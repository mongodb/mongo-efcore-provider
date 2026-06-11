/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Captures the final query expression in the chain so it can be run against the MongoDB LINQ v3 provider while also
/// following the shape of the transformation so that the shaper may be correctly adjusted and early-terminates any
/// unsupported operations.
/// </summary>
internal sealed class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    private readonly MongoProjectionBindingExpressionVisitor _projectionBindingExpressionVisitor = new();
    private Expression? _finalExpression;

    /// <summary>
    /// Create a <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryableMethodTranslatingExpressionVisitorDependencies"/> this visitor depends upon.</param>
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> this visitor should use to correctly translate the expressions.</param>
    public MongoQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext, subquery: false)
    {
    }

    public override Expression? Visit(Expression? expression)
    {
        _finalExpression ??= expression;
        var result = base.Visit(expression);

        if (result == QueryCompilationContext.NotTranslatedExpression)
        {
            var originalExpression = ((MongoQueryCompilationContext)QueryCompilationContext).OriginalExpression;
            throw new InvalidOperationException(
                TranslationErrorDetails is null
                    ? CoreStrings.TranslationFailed(originalExpression?.Print())
                    : CoreStrings.TranslationFailedWithDetails(originalExpression?.Print(), TranslationErrorDetails));
        }

        return result;
    }

    private static readonly Type[] AllowedQueryableExtensions =
        [typeof(Queryable), typeof(MongoQueryableExtensions), typeof(Driver.Linq.MongoQueryable)];

    /// <summary>
    /// Visit the <see cref="MethodCallExpression"/> to capture the cardinality and final expression
    /// when found on a <see cref="Queryable"/> method.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="ShapedQueryExpression"/> if this method was on a <see cref="Queryable"/>,
    /// otherwise <see cref="QueryCompilationContext.NotTranslatedExpression"/>.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (!AllowedQueryableExtensions.Contains(method.DeclaringType))
            return QueryCompilationContext.NotTranslatedExpression;

        var source = Visit(methodCallExpression.Arguments[0]);
        if (source is ShapedQueryExpression shapedQueryExpression)
        {
            var methodDefinition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;
            switch (method.Name)
            {
                // Operations that need tweaks
                case nameof(Queryable.Select) when methodDefinition == QueryableMethods.Select:
                case nameof(Queryable.OfType) when methodDefinition == QueryableMethods.OfType:

                // Operations that only require reshaping
                case nameof(Queryable.Any) when methodDefinition == QueryableMethods.AnyWithoutPredicate:
                case nameof(Queryable.All) when methodDefinition == QueryableMethods.All:
                case nameof(Queryable.Cast) when methodDefinition == QueryableMethods.Cast:
                case nameof(Queryable.Count) when methodDefinition == QueryableMethods.CountWithoutPredicate:
                case nameof(Queryable.LongCount) when methodDefinition == QueryableMethods.LongCountWithoutPredicate:
                case nameof(Queryable.Average) when QueryableMethods.IsAverageWithSelector(methodDefinition)
                                                    || QueryableMethods.IsAverageWithoutSelector(methodDefinition):
                case nameof(Queryable.Sum) when QueryableMethods.IsSumWithSelector(methodDefinition)
                                                || QueryableMethods.IsSumWithoutSelector(methodDefinition):
                case nameof(Queryable.Min) when methodDefinition == QueryableMethods.MinWithoutSelector
                                                || methodDefinition == QueryableMethods.MinWithSelector:
                case nameof(Queryable.Max) when methodDefinition == QueryableMethods.MaxWithoutSelector
                                                || methodDefinition == QueryableMethods.MaxWithSelector:

                // Join operations - delegate to base class which calls our Translate* overrides
                case nameof(Queryable.Join) when methodDefinition == QueryableMethods.Join:
                case nameof(Queryable.GroupJoin) when methodDefinition == QueryableMethods.GroupJoin:
#if !EF8 && !EF9
                case nameof(Queryable.LeftJoin) when methodDefinition == QueryableMethods.LeftJoin:
#endif
                case nameof(Queryable.DefaultIfEmpty) when methodDefinition == QueryableMethods.DefaultIfEmptyWithArgument
                                                           || methodDefinition == QueryableMethods.DefaultIfEmptyWithoutArgument:

                // Operations not supported, but we want to bubble through for better error messages
#if !EF8 && !EF9
                case nameof(Queryable.RightJoin) when methodDefinition == QueryableMethods.RightJoin:
#endif
                case nameof(Queryable.GroupBy) when methodDefinition == QueryableMethods.GroupByWithKeySelector
                                                    || methodDefinition == QueryableMethods.GroupByWithKeyElementSelector:
                case nameof(Queryable.Contains) when methodDefinition == QueryableMethods.Contains:
                case nameof(Queryable.Except) when methodDefinition == QueryableMethods.Except:
                case nameof(Queryable.Intersect) when methodDefinition == QueryableMethods.Intersect:
                case nameof(Queryable.SelectMany) when methodDefinition == QueryableMethods.SelectManyWithCollectionSelector:
                    {
                        if (base.VisitMethodCall(methodCallExpression) is not ShapedQueryExpression visitedShapedQueryExpression)
                        {
                            return QueryCompilationContext.NotTranslatedExpression;
                        }

                        shapedQueryExpression = visitedShapedQueryExpression;
                        break;
                    }
            }

            var newCardinality = GetResultCardinality(method);
            if (newCardinality != shapedQueryExpression.ResultCardinality)
                shapedQueryExpression = shapedQueryExpression.UpdateResultCardinality(newCardinality);

            ((MongoQueryExpression)shapedQueryExpression.QueryExpression).CapturedExpression = _finalExpression;
            return shapedQueryExpression;
        }

        return QueryCompilationContext.NotTranslatedExpression;
    }

    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        // Handle .Select(p => p) no-op/pass-thru
        if (selector.Body == selector.Parameters[0])
        {
            return source;
        }

        // TransparentIdentifier types are used by Join/LeftJoin/GroupJoin - allow them through

        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        var newSelectorBody =
            ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body);
        var newShaper = _projectionBindingExpressionVisitor.Translate(mongoQueryExpression, newSelectorBody);

        return source.UpdateShaperExpression(newShaper);
    }

    protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
    {
        var queryExpression = new MongoQueryExpression(entityType);
        return new ShapedQueryExpression(
            queryExpression,
            shaperExpression: new StructuralTypeShaperExpression(
                entityType,
                new ProjectionBindingExpression(queryExpression, new ProjectionMember(), typeof(ValueBuffer)),
                false));
    }

    private static ResultCardinality GetResultCardinality(MethodInfo method)
    {
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
        switch (method.Name)
        {
            // Singles
            case nameof(Queryable.All)
                when genericMethod == QueryableMethods.All:
            case nameof(Queryable.Any)
                when genericMethod == QueryableMethods.AnyWithoutPredicate:
            case nameof(Queryable.Any)
                when genericMethod == QueryableMethods.AnyWithPredicate:
            case nameof(Queryable.Average)
                when QueryableMethods.IsAverageWithoutSelector(method) || QueryableMethods.IsAverageWithSelector(method):
            case nameof(Queryable.Contains)
                when genericMethod == QueryableMethods.Contains:
            case nameof(Queryable.Count)
                when genericMethod == QueryableMethods.CountWithoutPredicate:
            case nameof(Queryable.Count)
                when genericMethod == QueryableMethods.CountWithPredicate:
            case nameof(Queryable.ElementAt)
                when genericMethod == QueryableMethods.ElementAt:
            case nameof(Queryable.First)
                when genericMethod == QueryableMethods.FirstWithoutPredicate ||
                     genericMethod == QueryableMethods.FirstWithPredicate:
            case nameof(Queryable.Last)
                when genericMethod == QueryableMethods.LastWithoutPredicate ||
                     genericMethod == QueryableMethods.LastWithPredicate:
            case nameof(Queryable.LongCount)
                when genericMethod == QueryableMethods.LongCountWithoutPredicate ||
                     genericMethod == QueryableMethods.LongCountWithPredicate:
            case nameof(Queryable.Max)
                when genericMethod == QueryableMethods.MaxWithoutSelector || genericMethod == QueryableMethods.MaxWithSelector:
            case nameof(Queryable.Min)
                when genericMethod == QueryableMethods.MinWithoutSelector || genericMethod == QueryableMethods.MinWithSelector:
            case nameof(Queryable.Single)
                when genericMethod == QueryableMethods.SingleWithoutPredicate ||
                     genericMethod == QueryableMethods.SingleWithPredicate:
            case nameof(Queryable.Sum)
                when QueryableMethods.IsSumWithoutSelector(method) || QueryableMethods.IsSumWithSelector(method):

                return ResultCardinality.Single;

            // Single or defaults
            case nameof(Queryable.ElementAtOrDefault)
                when genericMethod == QueryableMethods.ElementAtOrDefault:
            case nameof(Queryable.FirstOrDefault)
                when genericMethod == QueryableMethods.FirstOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.FirstOrDefaultWithPredicate:
            case nameof(Queryable.LastOrDefault)
                when genericMethod == QueryableMethods.LastOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.LastOrDefaultWithPredicate:
            case nameof(Queryable.SingleOrDefault)
                when genericMethod == QueryableMethods.SingleOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.SingleOrDefaultWithPredicate:

                return ResultCardinality.SingleOrDefault;
        }

        return ResultCardinality.Enumerable;
    }

    protected override ShapedQueryExpression? TranslateOfType(ShapedQueryExpression source, Type resultType)
    {
        if (source.ShaperExpression is StructuralTypeShaperExpression entityShaperExpression)
        {
            if (entityShaperExpression.StructuralType is not IEntityType entityType)
            {
                throw new NotSupportedException($"Complex type '{entityShaperExpression.StructuralType.DisplayName()
                }' not supported in MongoDB.");
            }

            if (entityType.ClrType == resultType) return source;

            var resultEntityType = entityType.Model.FindEntityType(resultType);
            if (resultEntityType != null)
            {
                return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
            }
        }

        return null;
    }

    #region Methods that just require shaper reshaping

    protected override ShapedQueryExpression TranslateAll(ShapedQueryExpression source, LambdaExpression predicate) =>
        ReshapeShaperExpression(source, typeof(bool));

    protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate)
        => ReshapeShaperExpression(source, typeof(bool));

    protected override ShapedQueryExpression TranslateAverage(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType)
        => ReshapeShaperExpression(source, resultType);

    protected override ShapedQueryExpression TranslateCast(ShapedQueryExpression source, Type castType)
        => ReshapeShaperExpression(source, castType);

    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
        => ReshapeShaperExpression(source, typeof(bool)); // We don't support but a later step has a better error message

    protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => ReshapeShaperExpression(source, typeof(int));

    protected override ShapedQueryExpression TranslateLongCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => ReshapeShaperExpression(source, typeof(long));

    protected override ShapedQueryExpression TranslateMax(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => ReshapeShaperExpression(source, resultType);

    protected override ShapedQueryExpression TranslateMin(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => ReshapeShaperExpression(source, resultType);

    protected override ShapedQueryExpression TranslateSum(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => ReshapeShaperExpression(source, resultType);

    private static ShapedQueryExpression ReshapeShaperExpression(ShapedQueryExpression source, Type returnType)
        => source.UpdateShaperExpression(
            Expression.Convert(
                new ProjectionBindingExpression(
                    source.QueryExpression, new ProjectionMember(), returnType.MakeNullable()), returnType));

    #endregion

    #region Never called by visit as translation is handled by C# Driver LINQ (with some minor tweaks)

    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => throw new NotSupportedException("Subqueries are not supported by MongoDB.");

    protected override ShapedQueryExpression? TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => null;

    protected override ShapedQueryExpression? TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression? defaultValue)
        => null;

    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source)
        => null;

    protected override ShapedQueryExpression? TranslateElementAtOrDefault(ShapedQueryExpression source,
        Expression index, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => null;

    protected override ShapedQueryExpression? TranslateFirstOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateGroupBy(ShapedQueryExpression source, LambdaExpression keySelector,
        LambdaExpression? elementSelector, LambdaExpression? resultSelector)
        => null;

    protected override ShapedQueryExpression? TranslateGroupJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, resultSelector);

    protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => null;

    protected override ShapedQueryExpression? TranslateLeftJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, resultSelector);

#if !EF8 && !EF9
    protected override ShapedQueryExpression? TranslateRightJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        null;
#endif

    protected override ShapedQueryExpression? TranslateJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, resultSelector);

    private static ShapedQueryExpression? TranslateJoinCore(
        ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression resultSelector)
    {
        var outerQueryExpression = (MongoQueryExpression)outer.QueryExpression;
        var innerQueryExpression = (MongoQueryExpression)inner.QueryExpression;

        outerQueryExpression.AddInnerCollection(innerQueryExpression.CollectionExpression.EntityType);

        // Rebind the inner entity's projection to the outer MongoQueryExpression.
        // The inner shaper has a StructuralTypeShaperExpression bound to the inner MongoQueryExpression.
        // We need to migrate that projection to the outer query expression so the entity path
        // shaper can read inner entity properties from the $lookup result field.
        var reboundInnerShaper = RebindInnerShaperToOuterQuery(
            inner.ShaperExpression, innerQueryExpression, outerQueryExpression, outerKeySelector);

        var newResultSelector = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], outer.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], reboundInnerShaper,
                resultSelector.Body));

        return outer.UpdateShaperExpression(newResultSelector);
    }

    private static Expression RebindInnerShaperToOuterQuery(
        Expression innerShaper,
        MongoQueryExpression innerQueryExpression,
        MongoQueryExpression outerQueryExpression,
        LambdaExpression outerKeySelector)
    {
        if (innerShaper is not StructuralTypeShaperExpression structuralShaper
            || structuralShaper.ValueBufferExpression is not ProjectionBindingExpression innerBinding)
        {
            return innerShaper;
        }

        // Get the inner entity's projection from the inner query expression
        EntityProjectionExpression? innerEntityProjection = null;
        if (innerBinding.ProjectionMember is { } member)
        {
            innerEntityProjection = innerQueryExpression.GetMappedProjection(member) as EntityProjectionExpression;
        }

        if (innerEntityProjection == null)
        {
            return innerShaper;
        }

        // Find the navigation for this join using the FK property from the outer key selector.
        // This correctly handles self-joins where multiple navigations target the same entity type.
        var innerEntityType = innerEntityProjection.EntityType;
        var outerEntityType = outerQueryExpression.CollectionExpression.EntityType;
        var fkPropertyName = ExtractPropertyName(outerKeySelector);
        INavigation? navigation = null;

        if (fkPropertyName != null)
        {
            navigation = outerEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                     && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
        }

        navigation ??= outerEntityType.GetNavigations()
            .FirstOrDefault(n => n.TargetEntityType == innerEntityType);

        // Use "_inner" for the first LeftJoin to match the driver's Join pipeline.
        // For subsequent LeftJoins, register ALL as $lookup + $unwind to keep the
        // document flat (avoid nested _outer._outer depths).
        string lookupAlias;
        if (!outerQueryExpression.UsesDriverJoinFields)
        {
            outerQueryExpression.UsesDriverJoinFields = true;
            lookupAlias = "_inner";
        }
        else
        {
            lookupAlias = navigation != null ? $"_lookup_{navigation.Name}" : $"_lookup_{innerEntityType.ShortName()}";

            // Register all joins (including the first) as $lookup + $unwind.
            // Also register the first join's navigation so it becomes a $lookup too.
            if (navigation != null)
            {
                outerQueryExpression.AddLookup(new Expressions.LookupExpression(navigation, forceUnwind: true));
            }

            // Also register the FIRST join's navigation as a $lookup and update its projection alias
            var firstInnerEntityType = outerQueryExpression.InnerCollections.Keys.First();
            var firstNavigation = outerEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == firstInnerEntityType);
            if (firstNavigation != null)
            {
                outerQueryExpression.AddLookup(new Expressions.LookupExpression(firstNavigation, forceUnwind: true));

                // Update the first join's projection from "_inner" to "_lookup_*"
                var firstLookupAlias = $"_lookup_{firstNavigation.Name}";
                var rootAccess = new RootReferenceExpression(outerEntityType);
                var firstAccessExpr = new ObjectAccessExpression(firstNavigation, rootAccess, false, firstLookupAlias);
                var firstProjection = new EntityProjectionExpression(firstInnerEntityType, firstAccessExpr);
                for (var i = 0; i < outerQueryExpression.Projection.Count; i++)
                {
                    if (outerQueryExpression.Projection[i].Alias == "_inner")
                    {
                        outerQueryExpression.ReplaceProjectionAt(i, firstProjection);
                        break;
                    }
                }
            }

            // Switch off driver LeftJoin — all joins are now $lookup
            outerQueryExpression.UsesDriverJoinFields = false;
        }

        Expression parentAccess = new RootReferenceExpression(outerEntityType);
        ObjectAccessExpression lookupAccessExpression = navigation != null
            ? new ObjectAccessExpression(navigation, parentAccess, false, lookupAlias)
            : new ObjectAccessExpression(innerEntityType, parentAccess, false, lookupAlias);
        var newInnerProjection = new EntityProjectionExpression(innerEntityType, lookupAccessExpression);

        // Register on the outer query expression and create a new binding
        var projectionIndex = outerQueryExpression.AddToProjection(newInnerProjection);

        return structuralShaper.Update(
            new ProjectionBindingExpression(outerQueryExpression, projectionIndex, typeof(ValueBuffer)));
    }

    /// <summary>
    /// Extract the property name from a key selector lambda (e.g., o => o.CustomerId -> "CustomerId").
    /// Handles EF.Property calls and direct member access.
    /// </summary>
    private static string? ExtractPropertyName(LambdaExpression keySelector)
    {
        var body = keySelector.Body;

        // Unwrap Convert/ConvertChecked
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        // Direct member access: o => o.CustomerId
        if (body is MemberExpression memberExpression)
        {
            return memberExpression.Member.Name;
        }

        // EF.Property call: EF.Property(o, "CustomerId")
        if (body is MethodCallExpression methodCall && methodCall.Method.IsEFPropertyMethod()
            && methodCall.Arguments[1] is ConstantExpression constant)
        {
            return constant.Value as string;
        }

        return null;
    }

    protected override ShapedQueryExpression? TranslateLastOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateOrderBy(ShapedQueryExpression source, LambdaExpression keySelector,
        bool ascending)
        => null;

    protected override ShapedQueryExpression? TranslateReverse(ShapedQueryExpression source)
        => null;

    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source, LambdaExpression collectionSelector,
        LambdaExpression resultSelector)
        => null;

    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source, LambdaExpression selector)
        => null;

    protected override ShapedQueryExpression? TranslateSingleOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateSkip(ShapedQueryExpression source, Expression count)
        => null;

    protected override ShapedQueryExpression? TranslateSkipWhile(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    protected override ShapedQueryExpression? TranslateTake(ShapedQueryExpression source, Expression count)
        => null;

    protected override ShapedQueryExpression? TranslateTakeWhile(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    protected override ShapedQueryExpression? TranslateThenBy(ShapedQueryExpression source, LambdaExpression keySelector,
        bool ascending) => null;

    protected override ShapedQueryExpression? TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => null;

    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    #endregion
}
