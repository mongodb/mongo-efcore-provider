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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

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
#if !EF8
        // ExecuteDelete / ExecuteUpdate marker methods are declared on EntityFrameworkQueryableExtensions.
        // Let them through to the base, which dispatches to TranslateExecuteDelete / TranslateExecuteUpdate.
        if (method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            return base.VisitMethodCall(methodCallExpression);
#endif
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

            // Native-slot population: delegate to NativeSlotPopulator on the already-visited source so we
            // always operate on the correct MongoQueryExpression instance — never re-traverse.
            NativeSlotPopulator.PopulateNativeSlots(shapedQueryExpression, methodDefinition, methodCallExpression);

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

        // TransparentIdentifier types are used by Join/LeftJoin/GroupJoin - allow them through.
        // Any other (projecting) Select cannot be expressed as a native pipeline stage (SP3 work),
        // so mark the query as no longer natively representable.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        if (source.ShaperExpression is GroupByShaperExpression)
        {
            // GroupBy(key).Select(aggregate): bind the accumulators (and finalize MongoSelectDefinition.Grouping)
            // when the projection is a supported shape (g.Key parts + Count/Sum/Min/Max/Average accumulators);
            // otherwise mark non-native so the query falls back to driver-LINQ. Either way translation must
            // complete without hard-throwing (the behavior change of this sub-project).
            //
            // Build the grouped-row result shaper: rewrite the projection's members onto ProjectionBinding
            // reads of top-level result aliases. When the grouping bound natively (Grouping finalized +
            // flatten projection populated) the gate emits the $group + flattening $project and this shaper
            // reads each alias from the grouped output document. When it did not bind (computed key/operand),
            // the query is marked non-native and this same anonymous-shaper (no GroupByShaperExpression left)
            // lets the driver-LINQ push-down path run the GroupBy server-side and pass its objects straight
            // through — CanPushDown succeeds because there is no entity reference in the shaper.
            if (!NativeGroupByBinder.TryBindGroupProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }

            var groupShaper = TryBuildGroupResultShaper(mongoQueryExpression, selector);

            // A projection shape we cannot rewrite (not an anonymous/DTO construction) keeps the placeholder
            // GroupByShaperExpression; the gate rejects it under NativeOnly and the driver reports it under
            // Native — matching a bare IGrouping.
            return groupShaper == null ? source : source.UpdateShaperExpression(groupShaper);
        }

        if (!IsTransparentIdentifierSelector(selector) && !IsSingleLevelCollectionIncludeSelector(selector))
        {
            // Native projection pushdown (SP3): a terminal anonymous-type / DTO projection whose leaves are
            // all top-level member accesses only is lowered to a $project stage. Anything else (bare scalar, computed
            // leaves, entity references, non-member bindings) is not natively representable and falls back.
            if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }

        var newSelectorBody =
            ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body);
        var newShaper = _projectionBindingExpressionVisitor.Translate(mongoQueryExpression, newSelectorBody);

        return source.UpdateShaperExpression(newShaper);
    }

    /// <summary>
    /// Builds the result shaper for a <c>GroupBy(key).Select(aggregate)</c> projection by rewriting each
    /// anonymous-type / DTO member onto a <see cref="ProjectionBindingExpression"/> that reads the member's
    /// top-level result alias from the grouped output document. The alias is the member name — matching the
    /// flattening <c>$project</c> the lowerer emits after <c>$group</c> — so the standard DOM binding-removing
    /// shaper reads each value by name. Returns <see langword="null"/> for a shape that is not an
    /// anonymous/DTO construction (kept as the placeholder <see cref="Microsoft.EntityFrameworkCore.Query.GroupByShaperExpression"/>).
    /// </summary>
    private static Expression? TryBuildGroupResultShaper(MongoQueryExpression mongoQueryExpression, LambdaExpression selector)
    {
        switch (selector.Body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                var arguments = new Expression[newExpression.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = BindGroupMember(mongoQueryExpression, newExpression.Members[i].Name, newExpression.Arguments[i]);
                return newExpression.Update(arguments);

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                var bindings = new MemberBinding[memberInit.Bindings.Count];
                for (var i = 0; i < bindings.Length; i++)
                {
                    if (memberInit.Bindings[i] is not MemberAssignment assignment)
                        return null;
                    bindings[i] = assignment.Update(
                        BindGroupMember(mongoQueryExpression, assignment.Member.Name, assignment.Expression));
                }

                return memberInit.Update((NewExpression)memberInit.NewExpression, bindings);

            default:
                return null;
        }
    }

    // Registers a projection for one grouped-result member and returns a ProjectionBindingExpression reading
    // it by index. The stored source expression (the original g.Key / g.Count() / g.Sum(...) argument) is kept
    // only for its distinctness (AddToProjection dedups by expression) and CLR type; the DOM shaper reads the
    // value raw by the alias (the member name) since these sources resolve to no IProperty.
    private static Expression BindGroupMember(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        var index = mongoQueryExpression.AddToProjection(valueExpression, alias);
        return new ProjectionBindingExpression(mongoQueryExpression, index, valueExpression.Type);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="selector"/> is a transparent-identifier
    /// selector produced by a Join/GroupJoin/LeftJoin rewrite — i.e. the body constructs an anonymous
    /// object whose fields are the outer and inner parameters without further transformation.
    /// Projecting selects (e.g. <c>Select(c =&gt; c.Name)</c>) return <see langword="false"/>.
    /// </summary>
    private static bool IsTransparentIdentifierSelector(LambdaExpression selector)
    {
        // EF generates transparent-identifier selectors as NewExpression nodes constructing an
        // anonymous "TransparentIdentifier" type.  All other selectors project or transform.
        if (selector.Body is not NewExpression newExpr)
            return false;

        var typeName = newExpr.Type.Name;
        if (!typeName.StartsWith("TransparentIdentifier", StringComparison.Ordinal)
            && !typeName.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            return false;

        // The compiler-generated type-name prefix alone is ambiguous: EF's Join/GroupJoin/LeftJoin rewrite
        // and a user's own two-member anonymous-type projection (e.g. Select(c => new { c.Name, c.Age }))
        // both produce a "<>f__AnonymousType..." NewExpression. A genuine transparent identifier has exactly
        // two members, literally named "Outer"/"Inner", each bound directly to one of the lambda's own
        // parameters with no further transformation — that shape is what actually distinguishes it.
        return newExpr.Members is { Count: 2 } members
               && members[0].Name == "Outer" && members[1].Name == "Inner"
               && newExpr.Arguments[0] is ParameterExpression
               && newExpr.Arguments[1] is ParameterExpression;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="selector"/> is the synthetic
    /// <c>Select(x =&gt; IncludeExpression)</c> EF's nav-expansion generates for a single-level, root-level
    /// collection <c>Include</c> (e.g. <c>Customers.Include(c =&gt; c.Orders)</c>) — the body is an
    /// <see cref="IncludeExpression"/> directly over the lambda's own parameter (no further projection),
    /// for a non-embedded collection navigation. This shape carries no native-unrepresentable projection of
    /// its own: the actual <c>$lookup</c> registration happens later, during projection binding
    /// (<see cref="MongoProjectionBindingExpressionVisitor"/>), so this Select must not be marked
    /// non-natively-representable (EF-339). Anything more complex — nested/ThenInclude chains, a reference
    /// navigation, or an Include composed with an actual projection — falls through to the existing
    /// catch-all and stays on the driver-LINQ path.
    /// </summary>
    private static bool IsSingleLevelCollectionIncludeSelector(LambdaExpression selector)
        => selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
           && includeExpression.EntityExpression == selector.Parameters[0]
           && navigation.IsCollection
           && !navigation.IsEmbedded();

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

#if !EF8
    protected override Expression? TranslateExecuteDelete(ShapedQueryExpression source)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        var strategy = ClassifyBulkSource(mongoQueryExpression);
        return new MongoNonQueryExpression(mongoQueryExpression, strategy);
    }

#if EF10
    protected override Expression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        IReadOnlyList<ExecuteUpdateSetter> setters)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        var strategy = ClassifyBulkSource(mongoQueryExpression);
        var parsed = setters
            .Select(s => BuildSetter(mongoQueryExpression, s.PropertySelector, s.ValueExpression))
            .ToList();
        return new MongoNonQueryExpression(mongoQueryExpression, parsed, strategy);
    }
#else
    protected override Expression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        LambdaExpression setPropertyCalls)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        var strategy = ClassifyBulkSource(mongoQueryExpression);

        var parsed = new List<MongoNonQueryExpression.Setter>();
        var body = setPropertyCalls.Body;
        // The chain is built outer-to-inner: s.SetProperty(a).SetProperty(b) parses as
        // (s.SetProperty(a)).SetProperty(b) — so walk Object inward, inserting at the front
        // to preserve the user's authored order.
        while (body is MethodCallExpression { Method.Name: "SetProperty" } call)
        {
            var selector = call.Arguments[0].UnwrapLambdaFromQuote();
            // For the self-referencing SetProperty overload the value arg is a quoted Func<T,TProp>
            // lambda; for the constant overload it is the value expression directly.
            var value = call.Arguments[1];
            parsed.Insert(0, BuildSetter(mongoQueryExpression, selector, value));
            body = call.Object!;
        }

        // EF10 validates "at least one SetProperty" before reaching the provider, but EF9 hands the raw
        // lambda straight through — so a setter lambda with no SetProperty call (e.g. an empty body or an
        // unrelated invocation) must be rejected here rather than silently running a no-op updateMany.
        if (parsed.Count == 0)
        {
            AddTranslationErrorDetails(
                "An 'ExecuteUpdate' call must specify at least one 'SetProperty' invocation, "
                + "to indicate the properties to be updated.");
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
        }

        return new MongoNonQueryExpression(mongoQueryExpression, parsed, strategy);
    }
#endif

    /// <summary>
    /// Parses a single <c>SetProperty(selector, value)</c> into a <see cref="MongoNonQueryExpression.Setter"/>.
    /// The selector must target a mapped root scalar property of the entity; the value is classified as
    /// self-referencing (references the entity) or a constant. Unsupported targets (owned / navigation /
    /// unmapped) produce EF's canonical non-query translation failure.
    /// </summary>
    private MongoNonQueryExpression.Setter BuildSetter(
        MongoQueryExpression mongoQueryExpression,
        LambdaExpression propertySelector,
        Expression valueExpression)
    {
        var entityType = mongoQueryExpression.CollectionExpression.EntityType;

        var selectorBody = propertySelector.Body;
        while (selectorBody is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            selectorBody = convert.Operand;
        }

        IProperty? property = null;
        if (selectorBody is MemberExpression { Expression: var memberSource } member
            && memberSource != null
            && propertySelector.Parameters.Count == 1
            && IsParameterReference(memberSource, propertySelector.Parameters[0]))
        {
            property = entityType.FindProperty(member.Member.Name);
        }
        // Also accept an EF.Property<TProperty>(entity, "Name") selector, e.g.
        // SetProperty(c => EF.Property<string>(c, "ContactName"), ...).
        else if (selectorBody is MethodCallExpression efPropertyCall
                 && efPropertyCall.Method.IsEFPropertyMethod()
                 && propertySelector.Parameters.Count == 1
                 && IsParameterReference(efPropertyCall.Arguments[0], propertySelector.Parameters[0])
                 && efPropertyCall.Arguments[1] is ConstantExpression { Value: string efPropertyName })
        {
            property = entityType.FindProperty(efPropertyName);
        }

        if (property == null)
        {
            AddTranslationErrorDetails(
                "Only mapped root scalar properties can be updated by a bulk update. The setter target "
                + $"'{propertySelector.Body}' is not a mapped scalar property of '{entityType.DisplayName()}'.");
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
        }

        // Classify and normalize the value expression.
        // EF9 self-referencing: value is a quoted Func<T,TProp> lambda; unwrap and detect a reference to its parameter.
        // EF10 (and EF9 constants): value is the raw value/aggregate expression; detect a reference to the
        // setter's lambda parameter.
        bool isSelfReferencing;
        Expression value;
        if (IsQuotedLambda(valueExpression))
        {
            var valueLambda = valueExpression.UnwrapLambdaFromQuote();
            value = valueLambda.Body;
            isSelfReferencing = ParameterFinder.ContainsAny(value, valueLambda.Parameters);
        }
        else
        {
            value = valueExpression;
            isSelfReferencing = ParameterFinder.ContainsAny(value, propertySelector.Parameters);
        }

        if (isSelfReferencing && property.GetTypeMapping().Converter != null)
        {
            AddTranslationErrorDetails(
                $"Self-referencing ExecuteUpdate on property '{property.Name}' is not supported because it uses a value converter.");
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
        }

        return new MongoNonQueryExpression.Setter(property, value, isSelfReferencing);
    }

    private static bool IsQuotedLambda(Expression expression)
        => expression is LambdaExpression
           || expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression };

    private static bool IsParameterReference(Expression expression, ParameterExpression parameter)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression == parameter;
    }

    /// <summary>
    /// Scans an expression for a reference to any of the supplied <see cref="ParameterExpression"/>s,
    /// used to classify a bulk-update setter value as self-referencing (depends on the entity being updated).
    /// </summary>
    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly IReadOnlyCollection<ParameterExpression> _parameters;
        private bool _found;

        private ParameterFinder(IReadOnlyCollection<ParameterExpression> parameters)
            => _parameters = parameters;

        public static bool ContainsAny(Expression expression, IReadOnlyCollection<ParameterExpression> parameters)
        {
            var finder = new ParameterFinder(parameters);
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (_parameters.Contains(node))
            {
                _found = true;
            }

            return base.VisitParameter(node);
        }
    }

    /// <summary>
    /// Classifies the captured source chain of a bulk delete/update. A chain of only
    /// <see cref="Queryable.Where{TSource}(IQueryable{TSource},Expression{Func{TSource,bool}})"/> is the
    /// single-command atomic path. Adding <c>OrderBy</c>/<c>OrderByDescending</c>/<c>ThenBy</c>/
    /// <c>ThenByDescending</c>/<c>Skip</c>/<c>Take</c>/<c>Distinct</c> requires the two-phase
    /// (query target <c>_id</c>s, then act by <c>$in</c>) path. Any other operator is not expressible as
    /// a server-side bulk scope and produces EF's canonical non-query translation failure. A TPH
    /// discriminator filter rides along as a Where.
    /// </summary>
    private MongoNonQueryExpression.BulkStrategy ClassifyBulkSource(MongoQueryExpression mongoQueryExpression)
    {
        var expression = MongoNonQueryExpression.UnwrapBulkOperator(mongoQueryExpression.CapturedExpression);
        var strategy = MongoNonQueryExpression.BulkStrategy.SingleCommand;

        while (expression is MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType != typeof(Queryable))
            {
                ThrowBulkSourceNotSupported(mongoQueryExpression, methodCallExpression.Method.Name);
            }

            switch (methodCallExpression.Method.Name)
            {
                case nameof(Queryable.Where):
                    break;

                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                case nameof(Queryable.Skip):
                case nameof(Queryable.Take):
                case nameof(Queryable.Distinct):
                    strategy = MongoNonQueryExpression.BulkStrategy.TwoPhase;
                    break;

                default:
                    ThrowBulkSourceNotSupported(mongoQueryExpression, methodCallExpression.Method.Name);
                    break;
            }

            expression = methodCallExpression.Arguments[0];
        }

        return strategy;
    }

    [DoesNotReturn]
    private void ThrowBulkSourceNotSupported(MongoQueryExpression mongoQueryExpression, string operatorName)
    {
        AddTranslationErrorDetails(
            $"The '{operatorName}' operator is not supported in a bulk delete or update. Only 'Where' predicates "
            + "and the 'OrderBy', 'OrderByDescending', 'ThenBy', 'ThenByDescending', 'Skip', 'Take', and 'Distinct' "
            + "operators can scope a bulk operation.");
        throw new InvalidOperationException(
            CoreStrings.NonQueryTranslationFailedWithDetails(
                mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
    }
#endif

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
                // OfType<TDerived>() narrows a TPH hierarchy by a discriminator predicate that is applied by
                // the driver-LINQ path (via the captured chain / shaper), not captured into the native Predicate
                // slot. The native pipeline would therefore return every document and the DOM shaper would fail
                // to materialize a sibling type, so the query is not natively representable.
                ((MongoQueryExpression)source.QueryExpression).Select.MarkNotNativelyRepresentable();
                return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
            }
        }

        return null;
    }

    #region Methods that just require shaper reshaping

    protected override ShapedQueryExpression TranslateAll(ShapedQueryExpression source, LambdaExpression predicate)
        => BindAggregateOrFallback(source, MongoAggregateOperator.All, null, predicate, typeof(bool));

    protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Any, null, predicate, typeof(bool));

    protected override ShapedQueryExpression TranslateAverage(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Average, selector, null, resultType);

    protected override ShapedQueryExpression TranslateCast(ShapedQueryExpression source, Type castType)
        => ReshapeShaperExpression(source, castType);

    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
        => ReshapeShaperExpression(source, typeof(bool)); // We don't support but a later step has a better error message

    protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Count, null, predicate, typeof(int));

    protected override ShapedQueryExpression TranslateLongCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => BindAggregateOrFallback(source, MongoAggregateOperator.LongCount, null, predicate, typeof(long));

    protected override ShapedQueryExpression TranslateMax(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Max, selector, null, resultType);

    protected override ShapedQueryExpression TranslateMin(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Min, selector, null, resultType);

    protected override ShapedQueryExpression TranslateSum(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType)
        => BindAggregateOrFallback(source, MongoAggregateOperator.Sum, selector, null, resultType);

    /// <summary>
    /// Attempts to bind a scalar aggregate terminal operator to <see cref="MongoSelectDefinition.Cardinality"/>
    /// via <see cref="NativeCardinalityBinder.TryBindAggregate"/>, marking the query non-native on failure, and
    /// reshapes the result to <paramref name="resultType"/> either way.
    /// </summary>
    private static ShapedQueryExpression BindAggregateOrFallback(ShapedQueryExpression source, MongoAggregateOperator op,
        LambdaExpression? selector, LambdaExpression? predicate, Type resultType)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeCardinalityBinder.TryBindAggregate(mongoQ, op, selector, predicate, resultType))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return ReshapeShaperExpression(source, resultType);
    }

    private static ShapedQueryExpression ReshapeShaperExpression(ShapedQueryExpression source, Type returnType)
        => source.UpdateShaperExpression(
            Expression.Convert(
                new ProjectionBindingExpression(
                    source.QueryExpression, new ProjectionMember(), returnType.MakeNullable()), returnType));

    #endregion

    // The Translate* overrides below remain dead code (never called via base) but are kept as
    // clean implementations for potential future use. Native-slot population lives in NativeSlotPopulator;
    // native projection binding lives in NativeProjectionBinder.

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
    {
        // The base QueryableMethodTranslatingExpressionVisitor.TranslateGroupBy is abstract, so there is no base
        // implementation to delegate to — the grouped shaped query is constructed here directly. The native $group
        // path supports only GroupBy(key).Select(aggregate): no element selector shaping and no fused result
        // selector (EF normalizes GroupBy-with-result-selector into GroupBy followed by Select, so a non-null
        // resultSelector here is a shape we do not natively bind). When the key binds via TryBindGroupKey, the query
        // routes native (Route becomes GroupBy once the Select finalizes the grouping); otherwise it is marked
        // non-native so it falls back to driver-LINQ rather than hard-throwing (the behavior change of this
        // sub-project — GroupBy previously produced NotTranslatedExpression and failed translation outright).
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // Record GroupBy provenance unconditionally so a later Join/GroupJoin/LeftJoin over this grouped
        // source can be recognized as the wrong-data-on-fallback shape (see TranslateJoinCore).
        mongoQueryExpression.Select.IsGroupBy = true;

        if (elementSelector != null || resultSelector != null
            || !NativeGroupByBinder.TryBindGroupKey(mongoQueryExpression, keySelector))
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }

        // Carry a GroupByShaperExpression so a subsequent Select over the IGrouping is recognized (grouped branch
        // in TranslateSelect). The definitive grouped-row shaper is compiled in the gate (Task 6); the key shaper
        // here is a lightweight placeholder (the ungrouped source represents each group's elements).
        var keyShaper = ReplacingExpressionVisitor.Replace(keySelector.Parameters[0], source.ShaperExpression, keySelector.Body);
        var groupByShaper = new GroupByShaperExpression(keyShaper, source);
        return source.UpdateShaperExpression(groupByShaper);
    }

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

        // A Join/GroupJoin/LeftJoin whose outer (or inner) is a grouped source is a shape the native path
        // cannot represent AND whose driver-LINQ fallback silently returns wrong data (the joined entity is
        // empty for every grouped row). Mark it fallback-unsafe so the gate fails cleanly instead of routing
        // to the wrong-data fallback. Non-grouped joins fall back to driver-LINQ as before (correct results).
        if (outerQueryExpression.Select.IsGroupBy || innerQueryExpression.Select.IsGroupBy)
        {
            outerQueryExpression.Select.MarkGroupByFallbackUnsafe();
        }

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
        var fkPropertyName = outerKeySelector.Body.TryGetSimplePropertyName();
        INavigation? navigation = null;

        if (fkPropertyName != null)
        {
            navigation = outerEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                     && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
        }

        navigation ??= outerEntityType.GetNavigations()
            .FirstOrDefault(n => n.TargetEntityType == innerEntityType);

        // Transitive join: the inner entity is reached not directly from the root but THROUGH a
        // previously-joined intermediate (e.g. OrderDetail.Order.Customer — the join's outer key
        // selector is "o.Inner.CustomerID"). When no direct navigation exists, resolve the navigation
        // on a prior inner collection and remember the intermediate so the $lookup's localField can be
        // prefixed with that intermediate's "_lookup_<Intermediate>" path.
        INavigation? throughNavigation = null;
        if (navigation == null && fkPropertyName != null)
        {
            foreach (var priorInnerEntityType in outerQueryExpression.InnerCollections.Keys)
            {
                if (priorInnerEntityType == innerEntityType)
                {
                    continue;
                }

                var candidate = priorInnerEntityType.GetNavigations()
                    .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                         && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
                if (candidate != null)
                {
                    navigation = candidate;
                    throughNavigation = outerEntityType.GetNavigations()
                        .FirstOrDefault(n => n.TargetEntityType == priorInnerEntityType);
                    break;
                }
            }
        }

        // Document-shape decision (single source of truth): the driver's native LeftJoin
        // (producing { _outer, _inner }) is only viable for a SINGLE reference join. As soon
        // as a second cross-collection join appears we must flatten everything to root-level
        // $lookup + $unwind fields ("_lookup_<Navigation>") — the driver can't nest multiple
        // joins as _outer/_inner. Rather than toggle a mutable flag, we register the forced-unwind
        // lookups; MongoQueryExpression.UsesDriverJoinFields is then computed from that state and
        // never contradicts the emitted pipeline.
        //
        // Each cross-collection projection carries its OWNING navigation and a stable
        // "_lookup_<Navigation>" alias. The shaper derives the field it reads from that navigation
        // plus the computed UsesDriverJoinFields flag (driver-native => "_inner"; flat => the
        // "_lookup_<Navigation>" alias), so the projection is never retroactively rewritten.
        var isSecondOrLaterJoin = outerQueryExpression.InnerCollections.Count > 1;
        if (isSecondOrLaterJoin)
        {
            // Flatten: register a forced-unwind $lookup for THIS join...
            if (navigation != null)
            {
                var lookup = new Expressions.LookupExpression(navigation, forceUnwind: true);
                if (throughNavigation != null)
                {
                    // Transitive join: match against the already-unwound intermediate document.
                    lookup.LocalField = $"{Expressions.LookupExpression.GetLookupAlias(throughNavigation)}.{lookup.LocalField}";
                }

                outerQueryExpression.AddLookup(lookup);
            }

            // ...and retroactively for every PRIOR inner collection so the whole document is flat.
            foreach (var priorInnerEntityType in outerQueryExpression.InnerCollections.Keys)
            {
                if (priorInnerEntityType == innerEntityType)
                {
                    continue;
                }

                var priorNavigation = outerEntityType.GetNavigations()
                    .FirstOrDefault(n => n.TargetEntityType == priorInnerEntityType);
                if (priorNavigation != null)
                {
                    outerQueryExpression.AddLookup(new Expressions.LookupExpression(priorNavigation, forceUnwind: true));
                }
            }
        }

        // Stable, navigation-derived alias. For the lone driver-native reference the shaper maps this
        // to "_inner"; in flat mode it reads this "_lookup_<Navigation>" field directly.
        var lookupAlias = navigation != null
            ? Expressions.LookupExpression.GetLookupAlias(navigation)
            : $"_lookup_{innerEntityType.ShortName()}";

        Expression parentAccess = new RootReferenceExpression(outerEntityType);
        ObjectAccessExpression lookupAccessExpression = navigation != null
            ? new NavigationObjectAccessExpression(navigation, parentAccess, false, lookupAlias)
            : new EntityTypeObjectAccessExpression(innerEntityType, parentAccess, false, lookupAlias);
        var newInnerProjection = new EntityProjectionExpression(innerEntityType, lookupAccessExpression);

        // Register on the outer query expression and create a new binding
        var projectionIndex = outerQueryExpression.AddToProjection(newInnerProjection);

        return structuralShaper.Update(
            new ProjectionBindingExpression(outerQueryExpression, projectionIndex, typeof(ValueBuffer)));
    }

    protected override ShapedQueryExpression? TranslateLastOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    // These QMTEV overrides are intentionally inert: native slot population is delegated to
    // NativeSlotPopulator.PopulateNativeSlots (see VisitMethodCall), because routing Where/OrderBy/ThenBy/
    // Skip/Take through base.VisitMethodCall rebuilds a fresh MongoQueryExpression per operator (slots don't
    // accumulate). Do NOT add these operators to the VisitMethodCall switch without first removing
    // their NativeSlotPopulator.PopulateNativeSlots handling, or slots will be double-populated.

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
        bool ascending)
        => null;

    protected override ShapedQueryExpression? TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => null;

    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    #endregion
}
