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
                case nameof(Queryable.Distinct) when methodDefinition == QueryableMethods.Distinct:
                case nameof(Queryable.Union) when methodDefinition == QueryableMethods.Union:
                case nameof(Queryable.Concat) when methodDefinition == QueryableMethods.Concat:

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

        if (IsSingleLevelCollectionIncludeSelector(selector) && mongoQueryExpression.Select.HasTerminalOperator)
        {
            // Post-terminal guard for a collection Include (EF-347 finding): EF Core's own
            // NavigationExpandingExpressionVisitor requires the SAME Include on both operands of a set
            // operation and, when that holds, HOISTS it to apply AFTER the combinator — i.e.
            // "A.Include(x).Union(B.Include(x))" reaches this Select as "Union(A, B).Select(x =>
            // Include(x))", with mongoQueryExpression.Select.IsSetOp (or IsGroupBy/IsDistinct for the
            // analogous GroupBy/Distinct cases) already set by the preceding TranslateUnion/Concat/
            // GroupBy/Distinct. Registering the $lookup here via the fall-through below would combine it
            // with a $unionWith/$group the lowerer does not know how to reconcile — empirically, rows
            // contributed by the $unionWith operand come back with an EMPTY Include collection (a silent
            // wrong-data bug, not a translation failure). Fall back to driver-LINQ instead.
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        else if (!IsTransparentIdentifierSelector(selector) && !IsSingleLevelCollectionIncludeSelector(selector)
                 && !IsTransparentIdentifierMemberAccessSelector(selector))
        {
            // Post-terminal guard: a projected Select applied AFTER a native terminal grouping/distinct — a
            // projected Distinct (IsDistinct, key-only Grouping), a prior GroupBy (IsGroupBy), or any finalized
            // Grouping — must NOT push down a native $project. This Select reaches the NON-grouped branch (it is
            // NOT a GroupByShaperExpression — the preceding native terminal already replaced the shaper with its
            // projection shaper), so it bypasses the IsGroupBy||IsDistinct guards in NativeSlotPopulator/
            // NativeCardinalityBinder. Without this guard TryPopulateNativeProjection would APPEND this Select's
            // entity field-refs onto the already-populated Projection while Grouping is still set; Route stays
            // GroupBy; the lowerer group branch then renders $group + a flatten $project referencing fields that
            // no longer exist after the $group — yielding silent NULL data (e.g.
            // Select(new{Country,City}).Distinct().Select(x => new{Nation = x.Country}) emits Nation:"$country"
            // after $group{_id:{Country,City}} → null). Mark non-native so it falls back to driver-LINQ under
            // Native (throws under NativeOnly), matching the correct driver-LINQ result. Mirrors the
            // TranslateGroupBy guard. The legit GroupBy(key).Select(aggregate) reaches the grouped branch above
            // (via GroupByShaperExpression) and is unaffected.
            if (mongoQueryExpression.Select.HasTerminalOperator)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // Native projection pushdown (SP3): a terminal anonymous-type / DTO projection whose leaves are
            // all top-level member accesses only is lowered to a $project stage. Anything else (bare scalar, computed
            // leaves, entity references, non-member bindings) is not natively representable and falls back.
            else if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
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
    /// <c>Select(ti =&gt; ti.Outer)</c> / <c>Select(ti =&gt; ti.Inner)</c> unwrap EF's nav-expansion ALWAYS
    /// inserts immediately after a Join/GroupJoin/LeftJoin/SelectMany rewrite to peel a
    /// <c>TransparentIdentifier(Outer, Inner)</c> back down to the operator's real result type (EF-347 slice
    /// 3 — the native inner-<c>Select</c> owned-collection SelectMany's mandatory unwrap Select is exactly
    /// this shape). This selector carries no projection of its own to push down — it is a pure field-of-a-
    /// freshly-built-object read that <see cref="ReplacingExpressionVisitor"/>'s own <c>NewExpression</c>-
    /// member fold resolves directly to whatever the wrapping Select/SelectMany already built for that slot —
    /// so it must bypass BOTH the post-terminal guard and <see cref="NativeProjectionBinder"/> here (mirroring
    /// <see cref="IsTransparentIdentifierSelector"/>/<see cref="IsSingleLevelCollectionIncludeSelector"/>).
    /// Without this, a SelectMany whose own binder set <see cref="MongoSelectDefinition.UnwindSource"/> (which
    /// makes <see cref="MongoSelectDefinition.HasTerminalOperator"/> true) would have this MANDATORY,
    /// EF-synthesized unwrap Select immediately trip the post-terminal guard and mark the query non-native —
    /// even though it is not a user-authored operator chained after a terminal, just EF's own internal
    /// TransparentIdentifier bookkeeping. Safe for Join/GroupJoin/LeftJoin too: <c>TranslateJoinCore</c>
    /// already unconditionally marks the outer side non-native at the join itself, independent of this
    /// selector, so skipping this guard for their own <c>ti.Inner</c>/<c>ti.Outer</c> unwrap changes nothing
    /// for them.
    /// </summary>
    private static bool IsTransparentIdentifierMemberAccessSelector(LambdaExpression selector)
        => selector.Parameters.Count == 1
           && selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal)
           && selector.Body is MemberExpression { Member.Name: "Outer" or "Inner" } member
           && member.Expression == selector.Parameters[0];

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
                // OfType<TDerived>() narrows a TPH hierarchy by a discriminator predicate. The native DOM
                // shaper already materializes TPH derived types polymorphically (via EF's own discriminator-
                // based MaterializationCondition), so all that is missing to keep this query natively
                // representable is the discriminator $eq/$in conjunct itself.
                var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
                if (mongoQueryExpression.Select.HasTerminalOperator)
                {
                    // Post-terminal guard (EF-347 slice 2): OfType after a native terminal (Union/Concat, or GroupBy/
                    // Distinct) is an own-Translate-override operator whose discriminator conjunct would be added to the
                    // OUTER select's Predicate — emitted as a pre-$unionWith/$group $match that filters only the outer
                    // rows, leaving the operand/grouped rows unfiltered (silent wrong data). Fall back to driver-LINQ.
                    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
                    return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
                }

                if (TryBuildDiscriminatorPredicate(resultEntityType, out var predicate))
                {
                    mongoQueryExpression.Select.AddPredicateConjunct(predicate);
                }
                else
                {
                    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
                }

                return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to build a discriminator predicate (<c>$eq</c> for a single value, <c>$in</c> for the
    /// subtree of a non-leaf type) that narrows a TPH hierarchy to <paramref name="targetType"/> and its
    /// derived types, for use as the native <c>OfType&lt;TDerived&gt;()</c> conjunct.
    /// </summary>
    /// <param name="targetType">The entity type <c>OfType</c> narrows to.</param>
    /// <param name="predicate">The built predicate, or a placeholder value when this method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when a predicate was built; <see langword="false"/> when <paramref name="targetType"/>
    /// has no discriminator property (non-TPH), the discriminator property has a value converter or a
    /// non-default <c>BsonRepresentation</c> (see the <c>HasDefaultKeySerialization</c> check below), or there
    /// are no discriminator values, in which case the caller should fall back to driver-LINQ.
    /// </returns>
    private static bool TryBuildDiscriminatorPredicate(IEntityType targetType, out MongoExpression predicate)
    {
        predicate = null!;
        var discriminatorProperty = targetType.FindDiscriminatorProperty();
        if (discriminatorProperty is null)
            return false; // Non-TPH / no discriminator → fall back.

        // The driver-LINQ discriminator filter (built from MongoEFDiscriminator.GetDiscriminatorsForTypeAndSubTypes
        // → BsonValue.Create(GetDiscriminatorValue())) uses the RAW discriminator value and bypasses any value
        // converter / non-default BsonRepresentation configured on the discriminator property. This native
        // predicate, by contrast, serializes the value THROUGH the property serializer (via
        // MongoConstantExpression.ForSerialization → BsonSerializerFactory), which applies that converter /
        // representation. For a value-converted or represented discriminator the two therefore produce DIFFERENT
        // discriminator BSON, so the native $eq/$in would return a different row set than the driver-LINQ path —
        // violating the Native == DriverLinq invariant (empirically: the write applies the conversion, the driver
        // filter does not, so the native and driver results diverge). Reject such a discriminator so the query
        // falls back to driver-LINQ (throwing only under NativeOnly), keeping native results identical to the
        // established driver-LINQ path — shared with NativeGroupByBinder.HasDefaultKeySerialization (same
        // generic-readback divergence risk).
        if (!NativeGroupByBinder.HasDefaultKeySerialization(discriminatorProperty))
            return false;

        var elementName = discriminatorProperty.GetElementName();
        var values = targetType.GetDerivedTypes().Prepend(targetType)
            .Select(t => t.GetDiscriminatorValue())
            .ToArray();
        if (values.Length == 0)
            return false;

        var field = new MongoFieldExpression(discriminatorProperty, elementName);
        predicate = values.Length == 1
            ? new MongoBinaryExpression(MongoBinaryOperator.Equal, field,
                new MongoConstantExpression(values[0], forSerialization: discriminatorProperty))
            : new MongoInExpression(field,
                new MongoConstantExpression(values, forSerialization: discriminatorProperty), negated: false);
        return true;
    }

    /// <summary>
    /// <c>Distinct()</c> over a terminal anonymous/DTO projection (<c>Select(new {...}).Distinct()</c>)
    /// translates to a degenerate <c>$group</c> — group by the projected value(s), zero accumulators — via
    /// <see cref="NativeGroupByBinder.TryBindDistinctFromProjection"/>. The shaper is unchanged: it was
    /// already built by the preceding <c>Select</c> to read the top-level result aliases, and those same
    /// aliases survive as the flattening <c>$project</c> that follows the <c>$group</c>. A bare-scalar
    /// projection (no native <c>Projection</c> populated) or a whole-entity source falls back to driver-LINQ.
    /// </summary>
    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeGroupByBinder.TryBindDistinctFromProjection(mongoQ))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return source; // shaper unchanged: the Select's projection shaper reads the flatten aliases
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
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Concat);

    protected override ShapedQueryExpression? TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression? defaultValue)
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

        // Guard: a GroupBy applied on top of a query that ALREADY terminates in a native grouping/distinct —
        // a projected Distinct (IsDistinct, which set a key-only Grouping), a prior GroupBy (IsGroupBy), or any
        // finalized Grouping — must NOT rebind. TryBindGroupKey would OVERWRITE the existing Grouping with this
        // GroupBy's own key, silently DROPPING the Distinct/prior-grouping (e.g.
        // Select(new{a,b}).Distinct().GroupBy(x=>x.k) would emit $group{_id:$k, $sum:1} counting ALL rows, not
        // distinct rows). GroupBy has its own Translate override, so it bypasses the IsGroupBy||IsDistinct
        // post-group guards in NativeSlotPopulator/NativeCardinalityBinder — hence this dedicated guard. Mark
        // the query non-native (so it falls back to driver-LINQ under Native, throws under NativeOnly, matching
        // the correct driver-LINQ result) and return a valid grouped shaped query WITHOUT rebinding.
        // The guard must read state as it stood BEFORE this GroupBy call — captured here, before the
        // unconditional IsGroupBy assignment below (both the guard branch and the normal-binding branch set
        // IsGroupBy, so it is hoisted above the if/else; reading Select.HasTerminalOperator AFTER that
        // assignment would always be true and defeat the guard).
        var hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalOperator;

        // Record GroupBy provenance unconditionally (both the guard branch below and the normal-binding branch
        // need it — see TranslateJoinCore) so a later Join/GroupJoin/LeftJoin over this grouped source can be
        // recognized as the wrong-data-on-fallback shape.
        mongoQueryExpression.Select.IsGroupBy = true;

        if (hadTerminalGrouping)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        else
        {
            if (elementSelector != null || resultSelector != null
                || !NativeGroupByBinder.TryBindGroupKey(mongoQueryExpression, keySelector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
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
        // A join over a projected-Distinct source is ALSO not natively representable — the lowerer's group
        // branch returns early after the $group + flatten $project, so allowing it native would silently DROP
        // the join. But unlike the GroupBy case its driver-LINQ fallback is CORRECT (Distinct produces a flat
        // set of rows the driver joins normally, no empty-join wrong-data hazard), so it must fall back
        // GRACEFULLY rather than hard-decline: mark it merely non-native (throws only under NativeOnly, runs
        // under Native/DriverLinq). Guarded on IsDistinct-and-not-IsGroupBy so a source that is somehow both
        // keeps the stricter GroupBy hard-decline above. See MongoSelectDefinition.IsDistinct.
        else if (outerQueryExpression.Select.IsDistinct || innerQueryExpression.Select.IsDistinct)
        {
            outerQueryExpression.Select.MarkNotNativelyRepresentable();
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
    {
        // Slice 3: only the INNER-Select owned-collection form (projection nested in the collection selector,
        // e.g. o => o.Items.Select(i => new {o.X, i.Y})) is handled here. EF's nav-expansion normalizes EVERY
        // SelectMany shape to this overload with resultSelector always the trivial
        // TransparentIdentifier(Outer=o, Inner=c) constructor. EMPIRICALLY (confirmed by running the actual
        // pipeline — the earlier assumption that this call is terminal was wrong) a subsequent
        // .Select(ti => ti.Inner) ALWAYS immediately follows and reaches TranslateSelect, unwrapping the
        // transparent identifier back down to the SelectMany's real TResult (the nested Select's own
        // projection, c) — this is how EF materializes a 2-arg SelectMany's result type via nav-expansion's
        // internal 3-arg rewrite. So the shaper returned here must still be a TransparentIdentifier(Outer,
        // Inner) shape (see BuildSelectManyWrappedShaper) even though the underlying native pipeline has no
        // "Outer" data of its own — EF's own ReplacingExpressionVisitor.VisitMember NewExpression-member fold
        // resolves ti.Inner directly back to our projected shaper with no bespoke unwrap logic needed here.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        if (!NativeSelectManyBinder.TryBind(mongoQueryExpression, collectionSelector))
            return null;

        return BuildSelectManyWrappedShaper(source, mongoQueryExpression, collectionSelector, resultSelector);
    }

    /// <summary>
    /// Builds the <see cref="ShapedQueryExpression"/> for a native inner-<c>Select</c> owned-collection
    /// <c>SelectMany</c> once <see cref="NativeSelectManyBinder.TryBind"/> has populated
    /// <see cref="MongoSelectDefinition.UnwindSource"/> and <see cref="MongoSelectDefinition.Projection"/>.
    /// The projected element (the nested <c>Select</c>'s own anonymous/DTO projection, <c>c</c> in
    /// <c>o.Items.Select(i => new {...})</c>) is built exactly like <see cref="TryBuildGroupResultShaper"/>/
    /// <see cref="BindGroupMember"/> (GroupBy's analogous projected shaper): each member is rewritten onto a
    /// <see cref="ProjectionBindingExpression"/> reading the member's top-level result alias — the SAME alias
    /// <see cref="NativeSelectManyBinder.TryBind"/> already registered on <c>Select.Projection</c> — from the
    /// flattened <c>$project</c> output document, so the existing DOM projection shaper
    /// (<see cref="MongoProjectionBindingRemovingExpressionVisitor"/>) reads it back by name with no bespoke
    /// shaper needed. That projected shaper is then wrapped into <paramref name="resultSelector"/>'s own
    /// <c>TransparentIdentifier(Outer=o, Inner=c)</c> shape (substituting <paramref name="source"/>'s
    /// EXISTING (unchanged) outer shaper for <c>o</c> and the projected shaper for <c>c</c>), because a
    /// subsequent <c>.Select(ti =&gt; ti.Inner)</c> always reaches <see cref="TranslateSelect"/> immediately
    /// after and expects that shape.
    /// </summary>
    private static ShapedQueryExpression BuildSelectManyWrappedShaper(
        ShapedQueryExpression source, MongoQueryExpression mongoQueryExpression, LambdaExpression collectionSelector,
        LambdaExpression resultSelector)
    {
        // TryBind already validated that collectionSelector.Body is Queryable.Select(<source>, innerLambda)
        // with a new{...}/MemberInit body — re-extract that same nested lambda body here rather than thread
        // the parsed member list through TryBind's bool-returning signature.
        var innerLambda = ((MethodCallExpression)collectionSelector.Body).Arguments[1].UnwrapLambdaFromQuote();
        var innerShaper = BuildSelectManyResultShaper(mongoQueryExpression, innerLambda.Body);

        // Replace both transparent-identifier parameters via two nested single-argument Replace calls. The
        // multi-argument ReplacingExpressionVisitor.Replace(IReadOnlyList<Expression>, IReadOnlyList<Expression>,
        // Expression) overload does not exist in EF8's EF Core, so a collection-expression argument there binds
        // to the single-Expression overload and fails to compile (CS9174). The params are distinct, so the
        // nesting order is immaterial.
        var wrappedShaper = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], source.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], innerShaper, resultSelector.Body));

        return source.UpdateShaperExpression(wrappedShaper);
    }

    private static Expression BuildSelectManyResultShaper(MongoQueryExpression mongoQueryExpression, Expression projectionBody)
    {
        switch (projectionBody)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                var arguments = new Expression[newExpression.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = BindSelectManyMember(mongoQueryExpression, newExpression.Members[i].Name, newExpression.Arguments[i]);
                return newExpression.Update(arguments);

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                var bindings = new MemberBinding[memberInit.Bindings.Count];
                for (var i = 0; i < bindings.Length; i++)
                {
                    var assignment = (MemberAssignment)memberInit.Bindings[i];
                    bindings[i] = assignment.Update(
                        BindSelectManyMember(mongoQueryExpression, assignment.Member.Name, assignment.Expression));
                }

                return memberInit.Update((NewExpression)memberInit.NewExpression, bindings);

            default:
                // NativeSelectManyBinder.TryBind already validated this shape (TryReadProjection accepts only
                // these two forms), so this is unreachable in practice — defensive rather than silently
                // mis-shaping the result.
                throw new InvalidOperationException(
                    $"Unexpected SelectMany projection shape '{projectionBody.GetType().Name}' after successful native binding.");
        }
    }

    // Registers a projection for one SelectMany-result member and returns a ProjectionBindingExpression
    // reading it by index — mirrors BindGroupMember (GroupBy's analogous helper). The stored source
    // expression (the original o.X / i.Y argument) is kept only for its distinctness (AddToProjection dedups
    // by expression) and CLR type; the DOM shaper reads the value raw by the alias (the member name), which
    // NativeSelectManyBinder.TryBind already used as the matching Select.Projection alias.
    private static Expression BindSelectManyMember(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        var index = mongoQueryExpression.AddToProjection(valueExpression, alias);
        return new ProjectionBindingExpression(mongoQueryExpression, index, valueExpression.Type);
    }

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
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Union);

    // Native whole-entity, terminal Union/Concat -> a $unionWith on source1's select. ALWAYS returns a
    // non-null shaped query (source1): native when both operands are plain natively-lowerable whole-entity
    // selects of the same type, otherwise source1 marked non-native so the query falls back GRACEFULLY to
    // driver-LINQ (throws only under NativeOnly). Never returns null (would become a hard NotTranslated in
    // the VisitMethodCall switch body). Mirrors TranslateGroupBy's always-non-null contract.
    private ShapedQueryExpression TryTranslateSetOperation(
        ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
    {
        var mongo1 = (MongoQueryExpression)source1.QueryExpression;
        var mongo2 = (MongoQueryExpression)source2.QueryExpression;

        if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            mongo1.Select.SetOperation = new MongoSetOperation(kind, mongo2.Select, mongo2.CollectionExpression.CollectionName);
            mongo1.Select.IsSetOp = true;
        }
        else
        {
            mongo1.Select.MarkNotNativelyRepresentable();
        }

        // Same-entity-type both sides -> source1's whole-entity shaper materializes every union row.
        return source1;
    }

    // A plain whole-entity select: filter/sort/paging slots only — no projection, grouping, scalar
    // cardinality, its own set op, cross-collection lookups (Include), or a lifted-out VectorSearch.
    private static bool IsPlainWholeEntitySelect(MongoQueryExpression mongo)
        => mongo.Select.Route == NativeRoute.WholeEntity
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.Projection.Count == 0
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !ContainsVectorSearch(mongo.CapturedExpression);

    // Local, minimal duplicate of MongoShapedQueryCompilingExpressionVisitor.ContainsVectorSearch (that
    // gate method is private and deliberately not made public — see the Query area AGENTS.md for the
    // rationale on why VectorSearch must be checked via the captured chain rather than a select-tree flag).
    // Walks the captured Queryable method chain looking for a VectorSearch call, descending through the
    // source argument of each call (VectorSearch sits at the root, optionally under a single pre-Where).
    private static bool ContainsVectorSearch(Expression? captured)
    {
        while (captured is MethodCallExpression call)
        {
            if (call.IsVectorSearch())
            {
                return true;
            }

            captured = call.Arguments.Count > 0 ? call.Arguments[0] : null;
        }

        return false;
    }

    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    #endregion
}
