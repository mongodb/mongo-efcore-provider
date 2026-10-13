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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Captures the final query expression for the driver-LINQ fallback while following the transformation's shape so the
/// shaper can be adjusted; early-terminates unsupported operations.
/// </summary>
internal sealed partial class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    private readonly MongoProjectionBindingExpressionVisitor _projectionBindingExpressionVisitor = new();
    private Expression? _finalExpression;

    /// <summary>Creates a <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/>.</summary>
    public MongoQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext, subquery: false)
    {
    }

    public override Expression? Visit(Expression? expression)
    {
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

#if EF8 || EF9
    // EF8/EF9 nav-expansion lowers both a user GroupJoin+SelectMany(DefaultIfEmpty) and EF's optional-reference-navigation
    // onto this internal shim (no BCL Queryable.LeftJoin before .NET 10). Nothing survives to tell the two origins apart,
    // so it is admitted unconditionally by MethodInfo identity, as EF10 admits Queryable.LeftJoin.
    internal static readonly MethodInfo Ef8Ef9LeftJoinMethod =
        typeof(Microsoft.EntityFrameworkCore.Internal.QueryableExtensions)
            .GetTypeInfo().GetDeclaredMethods("LeftJoin").Single(mi => mi.GetParameters().Length == 5);
#endif

    /// <summary>
    /// Whether <paramref name="method"/> is EF8/EF9's internal <c>LeftJoin</c> shim. Always <see langword="false"/> on EF10.
    /// </summary>
    internal static bool IsEf8Ef9LeftJoinShim(MethodInfo method)
#if EF8 || EF9
        => (method.IsGenericMethod ? method.GetGenericMethodDefinition() : method) == Ef8Ef9LeftJoinMethod;
#else
        => false;
#endif

    private static readonly HashSet<string> OrderingMethodNames =
    [
        nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
        nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending)
    ];

    /// <summary>
    /// Drops a <c>ThenBy</c>/<c>ThenByDescending</c> whose key matches an earlier key in the same chain (see
    /// <see cref="KeySelectorsMatch"/>); the driver would render both into one <c>$sort</c> and MongoDB rejects the
    /// duplicate field (EF-253 / CSHARP-5690). Orderings with an explicit <see cref="IComparer{T}"/> are left alone.
    /// </summary>
    internal static Expression ElideRedundantOrderings(Expression expression)
    {
        if (expression is not MethodCallExpression { Arguments.Count: > 0 } methodCall
            || !AllowedQueryableExtensions.Contains(methodCall.Method.DeclaringType))
        {
            return expression;
        }

        if (!OrderingMethodNames.Contains(methodCall.Method.Name))
        {
            var visitedSource = ElideRedundantOrderings(methodCall.Arguments[0]);
            if (ReferenceEquals(visitedSource, methodCall.Arguments[0]))
            {
                return methodCall;
            }

            var updatedArgs = methodCall.Arguments.ToArray();
            updatedArgs[0] = visitedSource;
            return methodCall.Update(methodCall.Object, updatedArgs);
        }

        // Collect the contiguous ordering chain outer-to-inner, then reverse it to replay chronologically.
        var chain = new List<MethodCallExpression>();
        var node = methodCall;
        while (AllowedQueryableExtensions.Contains(node.Method.DeclaringType) && OrderingMethodNames.Contains(node.Method.Name))
        {
            chain.Add(node);
            if (node.Arguments[0] is not MethodCallExpression next)
            {
                break;
            }

            node = next;
        }

        chain.Reverse();

        Expression current = ElideRedundantOrderings(chain[0].Arguments[0]);
        var seenKeys = new List<LambdaExpression>();
        foreach (var call in chain)
        {
            // OrderBy/OrderByDescending start a new ordering even mid-chain, superseding what came before.
            if (call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending))
            {
                seenKeys.Clear();
            }

            var keySelector = call.Arguments.Count == 2 ? call.Arguments[1].UnwrapLambdaFromQuote() : null;
            var isDuplicate = keySelector is not null && seenKeys.Any(seen => KeySelectorsMatch(seen, keySelector));

            if (keySelector is not null)
            {
                seenKeys.Add(keySelector);
            }

            if (isDuplicate)
            {
                continue;
            }

            if (ReferenceEquals(current, call.Arguments[0]))
            {
                current = call;
                continue;
            }

            var newArgs = call.Arguments.ToArray();
            newArgs[0] = current;
            current = call.Update(call.Object, newArgs);
        }

        return current;
    }

    /// <summary>
    /// Whether both key selectors are a direct single-hop access to the same member (<c>x =&gt; x.CustomerId</c>). Only
    /// those collide in the driver's <c>$sort</c>; computed keys get their own projected field and must not be elided.
    /// </summary>
    internal static bool KeySelectorsMatch(LambdaExpression a, LambdaExpression b)
    {
        if (a.Parameters.Count != 1 || b.Parameters.Count != 1)
        {
            return false;
        }

        var membersA = a.Parameters[0].MatchMemberAccess<MemberInfo>(a.Body);
        var membersB = b.Parameters[0].MatchMemberAccess<MemberInfo>(b.Body);

        return membersA is [var memberA] && membersB is [var memberB] && memberA == memberB;
    }

    /// <summary>Captures cardinality and the final expression when visiting a <see cref="Queryable"/> method.</summary>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        _finalExpression ??= ElideRedundantOrderings(methodCallExpression);

        var method = methodCallExpression.Method;
#if !EF8
        // ExecuteDelete/ExecuteUpdate markers live on EntityFrameworkQueryableExtensions; the base dispatches them.
        if (method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            return base.VisitMethodCall(methodCallExpression);
#endif
        if (!AllowedQueryableExtensions.Contains(method.DeclaringType) && !IsEf8Ef9LeftJoinShim(method))
            return QueryCompilationContext.NotTranslatedExpression;

        var source = Visit(methodCallExpression.Arguments[0]);
        if (source is ShapedQueryExpression shapedQueryExpression)
        {
            var methodDefinition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;
            var sourceHasCaseMappingLeaf =
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.HasClientCaseMappingProjectionLeaf;
            var sourceHasClientEvaluatedLeaf =
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.HasClientEvaluatedProjectionLeaf;
            var sourceHasClientConditionalLeaf =
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.HasClientConditionalProjectionLeaf;
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
#else
                // See Ef8Ef9LeftJoinMethod.
                case "LeftJoin" when methodDefinition == Ef8Ef9LeftJoinMethod:
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

            // The native projection holds the raw receiver of a client-reapplied ToLower/ToUpper, so value-reading operators
            // would see the wrong value. Marked after the switch because base.VisitMethodCall builds a new query expression.
            if (sourceHasCaseMappingLeaf && !IsProjectedValueFreeOperator(methodDefinition))
            {
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.MarkNotNativelyRepresentable();
            }

            // A client-evaluated leaf is never projected, so value-reading operators would read a missing field. Distinct is
            // exempt (the leaf is constant per execution); set ops are exempt because their own gate declines the flag. A
            // client-evaluated ternary is NOT exempt from Distinct: its value differs between rows.
            if (sourceHasClientEvaluatedLeaf
                && !IsProjectedValueFreeOperator(methodDefinition)
                && (methodDefinition != QueryableMethods.Distinct || sourceHasClientConditionalLeaf)
                && !IsSetOperation(methodDefinition))
            {
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.MarkNotNativelyRepresentable();
            }

            // Operates on the already-visited source; never re-traverse.
            NativeSlotPopulator.PopulateNativeSlots(shapedQueryExpression, methodDefinition, methodCallExpression);

            var newCardinality = GetResultCardinality(method);
            if (newCardinality != shapedQueryExpression.ResultCardinality)
                shapedQueryExpression = shapedQueryExpression.UpdateResultCardinality(newCardinality);

            // Null-coalesce a pushed-down bare collection-navigation Count body (ProjectionAliasTier.Synthetic). Must be
            // here, not in the projection binder: _finalExpression is re-captured after every translated call.
            var capturingQueryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;
            capturingQueryExpression.CapturedExpression =
                NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
                    _finalExpression, capturingQueryExpression.Select);
            return shapedQueryExpression;
        }

        return QueryCompilationContext.NotTranslatedExpression;
    }

    /// <summary>
    /// Operators that never read projected values, so they stay native over a client-reapplied case-mapping projection.
    /// </summary>
    private static bool IsProjectedValueFreeOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Take
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.FirstWithoutPredicate
           || methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.SingleWithoutPredicate
           || methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.LastWithoutPredicate
           || methodDefinition == QueryableMethods.LastOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.CountWithoutPredicate
           || methodDefinition == QueryableMethods.LongCountWithoutPredicate
           || methodDefinition == QueryableMethods.AnyWithoutPredicate;

    private static bool IsSetOperation(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Union
           || methodDefinition == QueryableMethods.Concat
           || methodDefinition == QueryableMethods.Intersect
           || methodDefinition == QueryableMethods.Except;

    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        // The selector the native projection binder sees: Tuple.Create(..) normalized to a construction. Equal to `selector`
        // unless that rewrite applies and native admits it (see the native projection branch below).
        var nativeSelector = selector;
        // Handle .Select(p => p) no-op/pass-thru
        if (selector.Body == selector.Parameters[0])
        {
            return source;
        }

        // An identity re-projection over an already-bound construction shaper, appended by nav-expansion after
        // Distinct/Union/Concat/Intersect/Except when the projection holds an entity reference. Substituting the shaper folds
        // member accesses back to the bound nodes; re-running projection binding over them would throw in every mode.
        // Not for a whole-entity leaf (`new { Customer = c, ... }`), which driver-LINQ can't shape under a set op either.
        if (source.ShaperExpression is NewExpression or MemberInitExpression
            && !ContainsBoundEntityShaper(source.ShaperExpression)
            && ExpressionEqualityComparer.Instance.Equals(
                ReplacingExpressionVisitor.Replace(selector.Parameters[0], source.ShaperExpression, selector.Body),
                source.ShaperExpression))
        {
            return source;
        }

        // Join/LeftJoin/GroupJoin TransparentIdentifier selectors pass through; any other projecting Select
        // this method can't lower natively marks the query non-representable.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // The join-scope bare-leaf arms below each stage one leaf under the synthetic "_v" alias and read it back whole.
        ShapedQueryExpression BindBareJoinScopeLeaf()
            => source.UpdateShaperExpression(
                BindProjectionByAlias(mongoQueryExpression, NativeProjectionBinder.SyntheticBareProjectionAlias, selector.Body));

        if (source.ShaperExpression is GroupByShaperExpression)
        {
            // Bind the accumulators and finalize Grouping for a supported shape, else mark non-native. Either way the
            // rewritten shaper reads top-level aliases with no entity reference, so driver-LINQ can still push GroupBy down.
            if (!NativeGroupByBinder.TryBindGroupProjection(mongoQueryExpression, selector, out var bareGroupLeafAlias))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }

            // Keyed on the binder's answer so the shaper can't disagree with what was bound: a bare result selector is
            // read under the alias the binder returned, otherwise walk the construction member by member.
            var groupShaper = bareGroupLeafAlias != null
                ? BindGroupMember(mongoQueryExpression, bareGroupLeafAlias, selector.Body)
                : TryBuildGroupResultShaper(mongoQueryExpression, selector);

            // A shape we cannot rewrite keeps the placeholder GroupByShaperExpression; the gate rejects it under
            // NativeOnly and the driver reports it under Native.
            return groupShaper == null ? source : source.UpdateShaperExpression(groupShaper);
        }

        // Trailing projection of an explicit-result-selector / query-syntax owned SelectMany: bind ti.Outer/ti.Inner into a
        // by-alias shaper, skipping the generic fold. A rejected projection falls through to the guards below.
        if (mongoQueryExpression.Select.UnwindSource != null
            && mongoQueryExpression.Select.Projection.Count == 0
            && NativeSelectManyBinder.TryBindTransparentIdentifierProjection(
                mongoQueryExpression, selector, out var bareSelectManyLeafAlias))
        {
            // Bare body: bound under the alias the binder returned. Wrapped: walk member by member.
            var selectManyShaper = bareSelectManyLeafAlias != null
                ? BindProjectionByAlias(mongoQueryExpression, bareSelectManyLeafAlias, selector.Body)
                : BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, _projectionBindingExpressionVisitor);
            return source.UpdateShaperExpression(selectManyShaper);
        }

        if (TryGetReferenceIncludeChain(selector, out var transitiveLevels) is { } referenceIncludeChain)
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, referenceIncludeChain, transitiveLevels))
            {
                // Recognized the shape but declined; the candidate join(s) stay unconfirmed, so Route computes
                // Fallback (HasUnconfirmedCandidateJoin).
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (TryGetMixedReferenceAndCollectionIncludeChain(
                     selector, out var mixedReferenceLevels, out var mixedTransitiveLevels, out _))
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, mixedReferenceLevels, mixedTransitiveLevels))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }

            // On success don't mark non-representable: the collection level's $lookup registers later, during
            // shaper compilation (MongoProjectionBindingExpressionVisitor's IncludeExpression case).
        }
        else if (IsSingleLevelCollectionIncludeSelector(selector) && mongoQueryExpression.Select.HasTerminalOperator
                 && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
        {
        // Collection Include after a terminal operator: EF hoists a matching Include on both set-op operands to after the
        // combinator (Union(A, B).Select(x => Include(x))). A set-op-only terminal is exempt because MongoSelectLowerer defers
        // the lookup block until after the set-op stage and TrailingOps; this gate and that emission must move together.
        // Reference Includes are handled by TryConfirmReferenceIncludeChain above.
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        // A collection Include directly over a join scope (Customers.Include(c => c.Orders).Join(Orders, ...)). Confirms and
        // registers the join's $lookup(s); the Include's own $lookup registers later during shaper compilation.
        // Root-rooted only: an Inner-rooted Include is declined because RebindInnerShaperToOuterQuery already baked the
        // original alias into Inner's shaper.
        //
        // Alias collision: a join resolving to the Include's navigation claims its `_lookup_<Nav>` alias, and AddLookup would
        // collapse them although the join needs $unwind and the Include the bare array.
        // - Single level: the join is renamed `<alias>_join`, safe only while nothing reads its Inner side, so an Inner
        //   Where/OrderBy (JoinInnerAccessConfirmed: its field refs carry the original alias) declines.
        // - Chain: no join is renamed; the Include binding sees the colliding lookup and renames the Include instead
        //   (MongoProjectionBindingExpressionVisitor's existingIncompatibleLookup), keeping later joins' localFields valid.
        //   Paging recorded between joins would be deferred past every $lookup, so it declines.
        else if (TryGetCollectionIncludeOverJoinScope(selector) is { } collectionInclude
                 && (collectionInclude.EntityExpression is MemberExpression { Member.Name: "Outer", Expression: ParameterExpression }
                     || mongoQueryExpression.Select.JoinScope is { Levels.Count: > 1 }))
        {
            BindCollectionIncludeOverJoinScope(mongoQueryExpression, selector, collectionInclude);
        }
        // A bare `x.Outer`/`x.Inner` selector over an eligible single-level join, recorded as an unconfirmed candidate.
        // Confirming here rather than in TranslateJoinCore defers AddLookup until the consuming Select is known; registering
        // eagerly would flip UsesDriverJoinFields and change the driver-LINQ fallback's document shape (see
        // Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL). Depth-1 only; a root leaf over a chain is the next arm.
        else if (IsTransparentIdentifierMemberAccessSelector(selector)
                 && mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } bareLeafScope
                 && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out var bareLeafJoin))
        {
            // Without confirmation, a later operator could record a native op that lowers before the $lookup and resolves against
            // the outer entity type (MongoSelectDefinition.HasConfirmedJoinLookup).
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, bareLeafScope);
            // The Inner spelling is read off the join's _lookup_<Nav> field of a whole document, which the driver-LINQ
            // fallback's pushed-down `_v` projection doesn't provide — see HasBareJoinInnerEntityLeaf.
            var isInnerLeaf = selector.Body is MemberExpression { Member.Name: "Inner" };
            if (isInnerLeaf)
            {
                mongoQueryExpression.Select.MarkBareJoinInnerEntityLeaf();
            }
            // A following Distinct must dedup the selected side only — see TryBindWholeEntityDistinct.
            mongoQueryExpression.Select.MarkBareJoinEntityLeaf(bareLeafJoin.Lookup!, isInnerLeaf);
        }
        // A bare ROOT-entity leaf over a chained join scope (`ti => ti.Outer.Outer`), what nav-expansion leaves after a
        // multi-hop reference-navigation filter. Root only: a non-root whole-entity leaf needs HasBareJoinInnerEntityLeaf's
        // read-side handling, which exists only at depth 1.
        //
        // Paging in PipelineOps recorded after a join may sit BETWEEN two joins, and confirming would defer it past both
        // $lookups, so decline. Checked before TryAdmitSingleEligibleNativeJoinScope, which may itself defer PipelineOps. Paging
        // already in PostJoinOps lowers after every $lookup, correct only if no row-changing join follows; TranslateJoinCore
        // declines when one does (HasNonCommutingPostJoinOp).
        else if (mongoQueryExpression.Select.JoinScope is { Levels.Count: > 1 } chainedLeafScope
                 && NativeJoinScopeTranslator.TryResolveBareScopeLeaf(
                     chainedLeafScope, selector.Parameters[0], selector.Body, out var chainedLeafScopeIndex)
                 && chainedLeafScopeIndex == 0
                 && !mongoQueryExpression.Select.HasPagingDeferredPastAChainJoin
                 && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
        {
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, chainedLeafScope);
        }
        // A wrapped `new {...}`/MemberInit projection over the same eligible single-level join. TryBindProjection is the
        // whole gate and mutates nothing on decline, so false falls through to the projected-Select branch.
        else if (NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out var wrappedLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindProjection(mongoQueryExpression, selector, wrappedLeafJoin))
        {
            // Build the shaper by index, not via the generic fold: ApplyProjection early-returns because a join query's
            // Projection is already non-empty, so members would never resolve and GetProjectionIndex would throw. Same
            // post-confirmation gate as the bare-leaf arm, else a later Take/Skip/First could page un-joined outer rows.
            mongoQueryExpression.Select.MarkJoinLookupConfirmed();

            // Fold the join's shaper into the body first, so a whole-entity leaf (x.Outer/x.Inner) arrives as the
            // join's StructuralTypeShaperExpression and is re-bound by index rather than as a scalar alias read.
            var foldedJoinBody = ReplacingExpressionVisitor.Replace(
                selector.Parameters.Single(), source.ShaperExpression, selector.Body);

            return source.UpdateShaperExpression(
                BuildSelectManyResultShaper(
                    mongoQueryExpression, selector.Body, _projectionBindingExpressionVisitor, foldedJoinBody,
                    allowContainerElements: true));
        }
        // A bare ternary null-checking a join scope's Inner side (`ti => ti.Inner != null ? ti.Inner.City : null`), bound as
        // a single leaf under the synthetic "_v" alias TryBindConditionalProjection staged.
        else if (NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out var conditionalLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindConditionalProjection(mongoQueryExpression, selector, conditionalLeafJoin))
        {
            return BindBareJoinScopeLeaf();
        }
        // A bare join scope null check (`ti => ti.Inner == null`) at any chain depth: a bool leaf under "_v", bound like the
        // conditional arm above; an inner join's degenerate check declines inside TryBindNullCheckProjection.
        else if (NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out var nullCheckLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindNullCheckProjection(mongoQueryExpression, selector, nullCheckLeafJoin))
        {
            return BindBareJoinScopeLeaf();
        }
        // A bare scalar/computed body over a join scope (`ti => ti.Inner.City`, what EF's null-check removal leaves of
        // `nav != null ? nav.Member : null`). A chain re-roots the leaf onto the one scope it resolves to, by hop chain and
        // never by CLR type; a leaf spanning scopes declines.
        else if (TryStageBareJoinScopeValue(mongoQueryExpression, selector, out var bareValueLeaf, out var bareValueRead))
        {
            mongoQueryExpression.Select.AddProjection(
                new MongoProjection(NativeProjectionBinder.SyntheticBareProjectionAlias, bareValueLeaf!,
                    ThrowsOnNull: bareValueRead == NonNullableValueRead.ThrowOnNull));
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, mongoQueryExpression.Select.JoinScope!);

            return BindBareJoinScopeLeaf();
        }
        // A parameter-free body over a join scope (`Select(ti => "Foo")` after Skip/Take), which the bare-value arm can't
        // take. Staged as a `$literal` `_v` projection; confirming keeps the $lookup/$unwind so inner-join-dropped rows stay
        // dropped. Pure checks run before the predicate that may defer ops, so nothing is mutated on a decline.
        else if (mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } parameterFreeScope
                 && mongoQueryExpression.Select.Projection.Count == 0
                 && !selector.Body.ReferencesParameter(selector.Parameters[0])
                 && TryTranslateParameterFreeJoinProjection(parameterFreeScope, selector.Body, out var parameterFreeLeaf)
                 && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
        {
            mongoQueryExpression.Select.AddProjection(
                new MongoProjection(NativeProjectionBinder.SyntheticBareProjectionAlias, parameterFreeLeaf));
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, parameterFreeScope);

            return BindBareJoinScopeLeaf();
        }
        else if (!IsTransparentIdentifierSelector(selector) && !IsSingleLevelCollectionIncludeSelector(selector)
                 && !IsTransparentIdentifierMemberAccessSelector(selector)
                 && !IsOwnedEmbeddedIncludeSelector(selector))
        {
            // Post-terminal guard: a projected Select after a native GroupBy/Distinct/finalized Grouping reaches this branch
            // (the terminal already replaced the shaper), bypassing the guards in NativeSlotPopulator/NativeCardinalityBinder.
            // Appending field refs would reference fields that no longer exist after $group (silent nulls). Mirrors
            // TranslateGroupBy. A set-op-only terminal is exempt (the lowerer emits the $project after the set-op stage);
            // IsSetOpTerminalOnly requires Projection.Count == 0, so a second projection still falls back.
            if (mongoQueryExpression.Select.HasTerminalOperator && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // Native $project pushdown. Tuple.Create(a, b, ..) is a static call, not a construction: normalize it to
            // `new Tuple<..>(a, b, ..)` for the positional-ctor arm; the original selector is kept for the fallback.
            else if (!NativeProjectionBinder.TryPopulateNativeProjection(
                         mongoQueryExpression, nativeSelector = NativeProjectionBinder.NormalizeTupleFactory(selector)))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // A positional-ctor DTO needs the index-based shaper (HasPositionalCtorProjectionShaper), unless it holds a client
            // case mapping the mixed reader can bind per argument (DeclinePositionalCtorCaseMappingForMemberBinding).
            else if (mongoQueryExpression.Select.HasPositionalCtorProjectionShaper
                     && !DeclinePositionalCtorCaseMappingForMemberBinding(mongoQueryExpression, nativeSelector, source.ShaperExpression))
            {
                return source.UpdateShaperExpression(
                    BuildPositionalCtorProjectionShaper(mongoQueryExpression, nativeSelector.Body));
            }
            // A wrapped client-method call over a join scope (`ti => ti.Inner != null ? "..." + ClientMethod(ti.Outer) : ""`)
            // needs the same confirm/register step as the bare pass-through arm, else Route stays Fallback.
            else if (mongoQueryExpression.Select.HasClientWrappedWholeEntityShaper
                     && mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } clientMethodScope
                     && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
            {
                NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, clientMethodScope);
            }
        }

        // A bare-nav owned/reference SelectMany whose trailing selector is a whole-inner-entity `ti => ti.Inner` bypasses both
        // the projection branch and the post-terminal guard above. UnwindSource.WholeElement makes the lowerer emit $unwind +
        // $replaceRoot and roots the DOM shaper at the element type; without it the gate would go native for an element never
        // materialized and crash with KeyNotFoundException. A computed-leaf selector falls back; a whole-outer or
        // unrepresentable element throws NotSupportedException (not the Native* one: this runs before the compile-time gate).
        if (mongoQueryExpression.Select.UnwindSource is { } wholeElementCandidateUnwind
            && mongoQueryExpression.Select.Projection.Count == 0)
        {
            var wholeEntityMember = TryGetWholeEntityMemberAccess(selector);

            if (wholeEntityMember is { Member.Name: "Inner" }
                && wholeElementCandidateUnwind.Kind is MongoUnwindSourceKind.Owned or MongoUnwindSourceKind.Reference
                && IsWholeElementRepresentable(wholeElementCandidateUnwind.InnerEntityType, wholeElementCandidateUnwind.Kind))
            {
                // Falls through to the generic fold below, which resolves TransparentIdentifier(outer, item).Inner
                // to the element shaper BuildBareNavWrappedShaper built.
                wholeElementCandidateUnwind.WholeElement = true;

                // Re-root the ROOT ProjectionMember at the element type; it still pointed at the owner's projection, which makes a
                // nested owned navigation's auto-Include call BindNavigation on the owner and throw. Must be here, not in
                // BuildBareNavWrappedShaper, which runs before it is known whether the selector projects the whole element.
                mongoQueryExpression.ReRootProjectionAt(wholeElementCandidateUnwind.InnerEntityType);
            }
            else if (wholeEntityMember != null)
            {
                throw new NotSupportedException(
                    "Projecting a whole entity other than an owned or reference collection element from a "
                    + "SelectMany (e.g. 'from o in q from i in o.Items select o', 'SelectMany(o => o.Items, "
                    + "(o, i) => o)', a reference collection element with an eager-loaded navigation, or an "
                    + "owned collection element with a cross-collection navigation or a real element name that "
                    + "collides with the provider's internal owned-key sentinel field) is not supported. Project "
                    + "members instead, e.g. 'from o in q from i in o.Items select new { o.Name, "
                    + "i.SomeProperty }', or project the owned element itself with 'select i'.");
            }
            else
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }

        var newSelectorBody =
            ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body);

        // Same replacement over each recorded leaf source, so the binding visitor can match a leaf against the
        // exact subtree it visits (structural equality) rather than by member name.
        mongoQueryExpression.Select.RebaseProjectionSources(
            leafSource => ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, leafSource));

        var newShaper = _projectionBindingExpressionVisitor.Translate(mongoQueryExpression, newSelectorBody);

        return source.UpdateShaperExpression(newShaper);
    }

    // A collection Include directly over a join scope: confirms and registers the join's $lookup(s), or marks the query non-representable.
    private static void BindCollectionIncludeOverJoinScope(
        MongoQueryExpression mongoQueryExpression, LambdaExpression selector, IncludeExpression collectionInclude)
    {
        var select = mongoQueryExpression.Select;
        var includeNavigation = (INavigation)collectionInclude.Navigation!;

        // A join level the Include binding would mistake for the Include's intermediate document (an unwound
        // reference/navigation-less hop targeting the root's type; matched by type) nests the root Include's $lookup under
        // that join's alias while the reader reads it at root. Same predicate as the binding, which has this known gap.
        if (mongoQueryExpression.Joins.Any(
                j => j.Lookup?.IsCollectionIncludeIntermediateFor(includeNavigation.DeclaringEntityType) == true))
        {
            select.MarkNotNativelyRepresentable();
        }
        else if (select.JoinScope is { Levels.Count: > 1 } includeChainScope)
        {
            if (NativeJoinScopeTranslator.TryResolveBareScopeLeaf(
                    includeChainScope, selector.Parameters[0], collectionInclude.EntityExpression, out var includeScopeIndex)
                && includeScopeIndex == 0
                && !select.HasPagingDeferredPastAChainJoin
                && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
            {
                NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, includeChainScope);
            }
            else
            {
                select.MarkNotNativelyRepresentable();
            }
        }
        else
        {
            var includeAlias = LookupExpression.GetLookupAlias(includeNavigation);
            var joinCollides = mongoQueryExpression.Joins is [{ Lookup: { } singleJoinLookup }]
                               && singleJoinLookup.As == includeAlias;
            if (!(joinCollides && select.JoinInnerAccessConfirmed)
                && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out var collectionJoin))
            {
                if (collectionJoin.Lookup!.As == includeAlias)
                {
                    var renamedAlias = mongoQueryExpression.GetUnusedLookupAlias($"{collectionJoin.Lookup.As}_join");
                    collectionJoin.Alias = renamedAlias;
                    collectionJoin.Lookup.As = renamedAlias;
                }

                mongoQueryExpression.AddLookup(collectionJoin.Lookup);
                select.MarkReferenceIncludeConfirmed();
                select.MarkJoinLookupConfirmed();
            }
            else
            {
                select.MarkNotNativelyRepresentable();
            }
        }
    }

    // The Select-over-join-scope arm for a bare scalar or computed body (`ti => ti.Inner.City`). TryAdmitSingleEligibleNativeJoinScope
    // may defer recorded ops before a later conjunct declines; that ordering is unchanged from when this was inline in TranslateSelect.
    private static bool TryStageBareJoinScopeValue(
        MongoQueryExpression mongoQueryExpression, LambdaExpression selector,
        [NotNullWhen(true)] out MongoExpression? bareValueLeaf, out NonNullableValueRead bareValueRead)
    {
        bareValueLeaf = null;
        bareValueRead = NonNullableValueRead.Plain;
        return mongoQueryExpression.Select.JoinScope is { } bareValueScope
             // Over a chain, paging or ordering recorded before/after/between joins may be placed past a row-dropping or
             // row-multiplying join (and the stage-position pins of Ef373InterleavedPagingTests expect the fallback layout): decline.
             && (bareValueScope.Levels.Count == 1
                 || (!mongoQueryExpression.Select.HasPaging && !mongoQueryExpression.Select.HasOrdering
                     && !mongoQueryExpression.Select.HasPagingRecordedBeforeAnyJoin
                     && !mongoQueryExpression.Select.HasPagingRecordedAfterAJoin
                     && mongoQueryExpression.Select.PostLookupPagingOps.Count == 0))
             && NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope(mongoQueryExpression, out _)
             && mongoQueryExpression.Select.Projection.Count == 0
             && selector.Body is not ConditionalExpression
             && !selector.Body.TryGetProjectionMembers(out _)
             && (bareValueScope.Levels.Count == 1
                 ? NativeJoinScopeTranslator.TryTranslateValue(
                     bareValueScope, selector.Parameters[0], selector.Body, out bareValueLeaf)
                 : NativeJoinScopeTranslator.TryTranslateSingleScope(
                     bareValueScope, selector.Parameters[0], selector.Body, valueMode: true, out bareValueLeaf))
             // Read back whole by alias; see NativeProjectionBinder.IsMisreadWholeValueLeaf.
             && !NativeProjectionBinder.IsMisreadWholeValueLeaf(bareValueLeaf!)
             // A non-nullable Length/IndexOf over an unmatched (or null) inner string: throw on null, or decline
             // where an operator may absorb the null; the same single classification as the wrapped arm.
             && MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(
                 NativeSlotPopulator.UnwrapBoxingToObjectType(selector.Body), bareValueLeaf!) is var read
             && (bareValueRead = read) != NonNullableValueRead.Decline;
    }

    // True when the shaper holds an entity shaper bound through a ProjectionBindingExpression (a whole-entity or
    // join-inner leaf), as opposed to an owned navigation shaper bound directly to its EntityProjectionExpression.
    private static bool ContainsBoundEntityShaper(Expression shaper)
    {
        var finder = new BoundEntityShaperFinder();
        finder.Visit(shaper);
        return finder.Found;
    }

    private sealed class BoundEntityShaperFinder : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (Found)
                return node;

            if (node is StructuralTypeShaperExpression { ValueBufferExpression: ProjectionBindingExpression })
            {
                Found = true;
                return node;
            }

            // Extension nodes don't all implement VisitChildren; descend only through the ones that wrap shapers.
            return node switch
            {
                StructuralTypeShaperExpression or ProjectionBindingExpression => node,
                IncludeExpression include => Visit(include.EntityExpression),
                CollectionShaperExpression collection => Visit(collection.InnerShaper),
                _ when node?.NodeType == ExpressionType.Extension => node,
                _ => base.Visit(node)
            };
        }
    }

    /// <summary>
    /// Builds the <c>GroupBy(key).Select(aggregate)</c> result shaper, rebinding each member to its top-level alias in the
    /// flattened <c>$group</c> output. <see langword="null"/> for a non-construction body.
    /// </summary>
    private static Expression? TryBuildGroupResultShaper(MongoQueryExpression mongoQueryExpression, LambdaExpression selector)
    {
        // Decide admissibility fully before any BindGroupMember call, so a decline never leaves the query
        // half-populated. Bare bodies (e.g. `g.Sum(...)`) are bound by the caller's bareGroupLeafAlias branch.
        if (!selector.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true))
            return null;

        var boundValues = new Expression[members.Count];
        for (var i = 0; i < boundValues.Length; i++)
        {
            boundValues[i] = BindGroupMember(mongoQueryExpression, members[i].MemberName, members[i].Value);
        }

        return selector.Body.RebuildProjectionMembers(boundValues);
    }

    // Registers a projection for one grouped-result member and returns an index-based binding. The stored source
    // expression matters only for dedup and CLR type; the DOM shaper reads the value raw by alias.
    // A nested-construction member was already staged by NativeGroupByBinder as a MongoDocumentConstructionExpression,
    // which must be registered instead of the raw MemberInitExpression (else a plain alias read of the whole nested type
    // misreads members; same carve-out as BindResultMember). A client case mapping (`K = g.Key.ToUpper()`) staged only its
    // receiver, which is read and the call re-applied client-side, null propagated.
    private static Expression BindGroupMember(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        if (TryBindDocumentConstruction(mongoQueryExpression, alias, valueExpression, out var boundConstruction))
        {
            return boundConstruction;
        }

        var receiver = NativeCaseMapping.PeelResultMemberCaseMapping(valueExpression);
        var index = mongoQueryExpression.AddToProjection(receiver, alias);
        var boundReceiver = new ProjectionBindingExpression(mongoQueryExpression, index, receiver.Type);
        return ReapplyCaseMapping(valueExpression, receiver, boundReceiver);
    }

    // Re-applies the case-mapping chain peeled off `original` down to `receiver` over `boundReceiver`, null propagated as
    // MongoProjectionBindingExpressionVisitor does for a named member.
    private static Expression ReapplyCaseMapping(Expression original, Expression receiver, Expression boundReceiver)
        => original switch
        {
            _ when ReferenceEquals(original, receiver) => boundReceiver,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                => convert.Update(ReapplyCaseMapping(convert.Operand, receiver, boundReceiver)),
            MethodCallExpression call when NativeCaseMapping.IsCaseMappingCall(call)
                => MongoProjectionBindingExpressionVisitor.NullPropagatingStringCall(
                    call, ReapplyCaseMapping(call.Object!, receiver, boundReceiver)),
            _ => throw new InvalidOperationException(
                $"Unexpected case-mapped projection shape '{original.GetType().Name}'.")
        };

    /// <summary>
    /// Builds the index-based result shaper for a positional-ctor DTO <c>Select</c> after the native binder committed its
    /// <c>$project</c>. See <see cref="MongoSelectDefinition.HasPositionalCtorProjectionShaper"/> for why the fold can't be used.
    /// </summary>
    private static Expression BuildPositionalCtorProjectionShaper(MongoQueryExpression mongoQueryExpression, Expression projectionBody)
    {
        // TryPopulateNativeProjection already validated this shape with the same reader; throw rather than
        // silently mis-shape if that ever stops holding.
        if (!projectionBody.TryGetProjectionMembers(
                out var members, allowPositionalConstructorArguments: true, allowContainerElements: true))
        {
            throw new InvalidOperationException(
                $"Unexpected positional-ctor projection shape '{projectionBody.GetType().Name}' after successful native binding.");
        }

        // A container element was staged without its boxing Convert (NativeProjectionBinder's container arm), so bind
        // the operand with its own type and re-box it. A constructor argument has no such layer to peel.
        var isContainer = projectionBody is NewArrayExpression;
        var boundValues = new Expression[members.Count];
        for (var i = 0; i < boundValues.Length; i++)
        {
            var value = members[i].Value;
            var operand = isContainer ? NativeProjectionBinder.UnwrapContainerElementBoxing(value) : value;
            var bound = BindPositionalCtorProjectionMember(mongoQueryExpression, members[i].MemberName, operand);
            boundValues[i] = ReferenceEquals(operand, value) ? bound : Expression.Convert(bound, value.Type);
        }

        return projectionBody.RebuildProjectionMembers(boundValues);
    }

    // Registers one positional-ctor-DTO member and returns an index-based binding. The alias goes through
    // Select.TryGetProjectionAlias because an owned leaf may have registered a document-path alias differing from the
    // synthetic "_ctorArg<N>" name. A peeled client case mapping is re-applied client-side over the bound receiver
    // ($toUpper/$toLower are ASCII-only).
    private static Expression BindPositionalCtorProjectionMember(MongoQueryExpression mongoQueryExpression, string memberName, Expression valueExpression)
    {
        var alias = mongoQueryExpression.Select.ResolveProjectionAlias(memberName);
        var receiver = NativeCaseMapping.PeelCaseMapping(valueExpression);
        var index = mongoQueryExpression.AddToProjection(receiver, alias);
        return ReapplyCaseMapping(
            valueExpression, receiver, new ProjectionBindingExpression(mongoQueryExpression, index, receiver.Type));
    }

    /// <summary>
    /// For a positional-ctor projection holding a client case mapping whose arguments the mixed reader can bind per member,
    /// declines the native projection so the generic fold binds it that way; <see langword="true"/> when it declined.
    /// </summary>
    /// <remarks>
    /// A whole-document read can't find the synthetic <c>_ctorArg&lt;N&gt;</c> alias, so on driver-LINQ this shape would
    /// push down ASCII-only <c>$toUpper</c>. Bound per argument, the mixed reader applies the .NET call in every mode. When
    /// arguments can't be bound per member, the native shaper stays and re-applies the call itself. Uses the binder's own
    /// peel and the binding visitor's own member match so neither drifts from this gate.
    /// </remarks>
    private static bool DeclinePositionalCtorCaseMappingForMemberBinding(
        MongoQueryExpression mongoQueryExpression, LambdaExpression selector, Expression sourceShaper)
    {
        if (selector.Body is not NewExpression positionalNew
            || positionalNew.Arguments.All(a => ReferenceEquals(NativeCaseMapping.PeelCaseMapping(a), a))
            || MongoProjectionBindingExpressionVisitor.TryMatchConstructorArgumentMembers(
                (NewExpression)ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), sourceShaper, positionalNew),
                memberInitBindingNames: null) is null)
        {
            return false;
        }

        mongoQueryExpression.Select.HasPositionalCtorProjectionShaper = false;
        mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        return true;
    }

    /// <summary>
    /// Translates a <c>Select</c> body that doesn't reference its parameter to a bare constant or query parameter, which
    /// <c>RenderProject</c> <c>$literal</c>-wraps. Declines anything else, or a value that would throw at pipeline-build
    /// time (<see cref="NativeSlotPopulator.TryProbeBareValueRenders"/>). Pure.
    /// </summary>
    private static bool TryTranslateParameterFreeJoinProjection(
        MongoJoinScope scope, Expression body, [NotNullWhen(true)] out MongoExpression? leaf)
    {
        leaf = null;

        if (!new MongoExpressionTranslator(scope.OuterEntityType).TryTranslateValue(body, out var translated)
            || translated is not (MongoConstantExpression or MongoParameterExpression)
            || !NativeSlotPopulator.TryProbeBareValueRenders(translated, NativeSlotPopulator.UnwrapBoxingToObjectType(body)))
        {
            return false;
        }

        leaf = translated;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="selector"/> is a transparent-identifier selector produced by a Join/GroupJoin/LeftJoin
    /// rewrite: an anonymous object whose fields are the outer and inner parameters untransformed.
    /// </summary>
    private static bool IsTransparentIdentifierSelector(LambdaExpression selector)
    {
        // EF generates transparent-identifier selectors as NewExpression nodes.
        if (selector.Body is not NewExpression newExpr)
            return false;

        var typeName = newExpr.Type.Name;
        if (!typeName.StartsWith("TransparentIdentifier", StringComparison.Ordinal)
            && !typeName.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            return false;

        // The type-name prefix alone is ambiguous with a user's two-member anonymous projection; a real
        // transparent identifier has exactly "Outer"/"Inner" members bound directly to parameters.
        return newExpr.Members is { Count: 2 } members
               && members[0].Name == "Outer" && members[1].Name == "Inner"
               && newExpr.Arguments[0] is ParameterExpression
               && newExpr.Arguments[1] is ParameterExpression;
    }

    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(ti =&gt; ti.Outer)</c>/<c>Select(ti =&gt; ti.Inner)</c> unwrap
    /// nav-expansion inserts after a Join/GroupJoin/LeftJoin/SelectMany. It projects nothing, so it must bypass the
    /// post-terminal guard and <see cref="NativeProjectionBinder"/>, else a SelectMany's <c>UnwindSource</c> would be marked
    /// non-native by EF's bookkeeping Select.
    /// </summary>
    private static bool IsTransparentIdentifierMemberAccessSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return false;
        }

        // EF auto-Includes owned navigations of the projected side, so this unwrap can arrive Include-wrapped; unwrap only
        // embedded navigations (as IsOwnedEmbeddedIncludeSelector does): a cross-collection Include needs a $lookup and must
        // keep tripping the guard.
        var body = selector.Body;
        while (body is IncludeExpression { Navigation: INavigation navigation } include && navigation.IsEmbedded())
        {
            body = include.EntityExpression;
        }

        return body is MemberExpression member
               && member.IsTransparentIdentifierOuterOrInnerAccess()
               && member.Expression == selector.Parameters[0];
    }

    /// <summary>
    /// The <c>ti.Outer</c>/<c>ti.Inner</c> member access of a bare-nav SelectMany's whole-entity trailing selector,
    /// unwrapping auto-Include layers first; otherwise <see langword="null"/>.
    /// </summary>
    private static MemberExpression? TryGetWholeEntityMemberAccess(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1)
        {
            return null;
        }

        var body = selector.Body.UnwrapIncludes();

        return body is MemberExpression { Member.Name: "Outer" or "Inner" } member
               && member.Expression == selector.Parameters[0]
            ? member
            : null;
    }

    /// <summary>
    /// Whether the element type is supported by whole-element re-rooting (<c>$unwind</c> + <c>$replaceRoot</c>, see
    /// <see cref="Expressions.MongoUnwindSource.WholeElement"/>). Each rejected case declines at translation time rather
    /// than returning silently wrong data.
    /// </summary>
    /// <remarks>
    /// <see cref="MongoUnwindSourceKind.Reference"/>: an eager-loaded navigation is rejected (it would bind against the wrong
    /// <see cref="Microsoft.EntityFrameworkCore.Query.ProjectionMember"/>). <see cref="MongoUnwindSourceKind.Owned"/> (the
    /// <c>$mergeObjects</c> sentinel merge) rejects:
    /// <list type="bullet">
    /// <item>A non-embedded navigation (needs a <c>$lookup</c> this shape never emits). Nested owned navigations work because
    /// <see cref="Expressions.MongoQueryExpression.ReRootProjectionAt"/> re-roots the projection at the element type.</item>
    /// <item>A property (scalar or complex) named <see cref="MongoReplaceRootStage.ShadowField"/>: the sentinel is merged
    /// after the element and would overwrite it. Complex properties need <see cref="GetComplexPropertyElementName"/>
    /// because <see cref="IEntityType.GetProperties"/> omits them.</item>
    /// <item>An owned-key property without default serialization (<see cref="StoredSerialization.HasDefaultKeySerialization"/>):
    /// the <c>__ownerKey</c> sentinel is copied from the owner's raw <c>_id</c>, bypassing any converter.</item>
    /// </list>
    /// </remarks>
    private static bool IsWholeElementRepresentable(IEntityType innerEntityType, MongoUnwindSourceKind kind)
    {
        // See the method remarks. Reference merges no sentinels, so only the eager-load check applies.
        if (kind == MongoUnwindSourceKind.Reference)
            return !innerEntityType.GetNavigations().Any(n => n.IsEagerLoaded);

        return !innerEntityType.GetNavigations().Any(n => !n.IsEmbedded())
               && innerEntityType.GetProperties().All(p => p.GetElementName() != MongoReplaceRootStage.ShadowField)
               && innerEntityType.GetComplexProperties().All(c =>
                   GetComplexPropertyElementName(c) != MongoReplaceRootStage.ShadowField)
               && innerEntityType.GetProperties().Where(p => p.IsOwnedTypeKey())
                   .All(StoredSerialization.HasDefaultKeySerialization);
    }

    /// <summary>
    /// The element name a complex property occupies, from the same <c>Mongo:ElementName</c> annotation as
    /// <see cref="MongoPropertyExtensions.GetElementName(IReadOnlyProperty)"/>, which has no complex-property overload.
    /// </summary>
    private static string GetComplexPropertyElementName(IReadOnlyComplexProperty complexProperty)
        => (string?)complexProperty[MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames.ElementName]
           ?? complexProperty.Name;

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
                // OfType<TDerived>() narrows a TPH hierarchy; the DOM shaper already materializes derived types
                // polymorphically, so only the discriminator $eq/$in conjunct is needed.
                var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
                if (mongoQueryExpression.Select.HasTerminalOperator)
                {
                    // After a terminal (set op, GroupBy, Distinct) the conjunct would land in the outer select's
                    // $match ahead of $unionWith/$group, leaving operand/grouped rows unfiltered.
                    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
                    return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
                }

                if (MongoDiscriminatorPredicate.TryBuild(resultEntityType, includeDerived: true, negated: false, out var predicate))
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
    /// A projected <c>Select(new {...}).Distinct()</c> becomes a zero-accumulator <c>$group</c>
    /// (<see cref="NativeGroupByBinder.TryBindDistinctFromProjection"/>); a whole-entity source records a plain
    /// <see cref="MongoDistinctOp"/>. Anything else falls back.
    /// </summary>
    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeGroupByBinder.TryBindDistinctFromProjection(mongoQ) && !TryBindWholeEntityDistinct(mongoQ))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return source; // shaper unchanged: either path leaves the existing shaper (projection or entity) valid
    }

    /// <summary>
    /// Records a <see cref="MongoDistinctOp"/> for a whole-entity <c>Distinct()</c>. Declines if a projection, grouping,
    /// cardinality or unwind source is already present.
    /// </summary>
    /// <remarks>
    /// Over a bare join leaf (<see cref="MongoSelectDefinition.BareJoinEntityLeaf"/>) the op must run after the
    /// <c>$lookup</c>/<c>$unwind</c> and dedup the selected side only. Declines if any other <c>$lookup</c> is registered (its
    /// field would be dropped for Inner, compared for Outer), or if paging was deferred past the join (it lowers after
    /// <see cref="MongoSelectDefinition.PostJoinOps"/>, so it would wrongly run after this Distinct).
    /// </remarks>
    private static bool TryBindWholeEntityDistinct(MongoQueryExpression mongoQ)
    {
        var select = mongoQ.Select;
        if (select.Projection.Count > 0 || select.Grouping != null || select.Cardinality != null
            || select.UnwindSource != null)
            return false;

        if (select.BareJoinEntityLeaf is var (lookup, isInner))
        {
            if (mongoQ.GetPendingLookups() is not [var only] || only.As != lookup.As
                || select.PostLookupPagingOps.Count > 0)
                return false;

            select.AppendPostJoinDistinct(isInner
                ? new MongoDistinctOp(KeepOnlyField: lookup.As)
                : new MongoDistinctOp(ExcludeField: lookup.As));
            return true;
        }

        select.AppendDistinct();
        return true;
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
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeCardinalityBinder.TryBindContains(mongoQ, source.ShaperExpression, item))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return ReshapeShaperExpression(source, typeof(bool));
    }

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
    /// Binds a scalar aggregate via <see cref="NativeCardinalityBinder.TryBindAggregate"/>, marking the query non-native on
    /// failure; reshapes to <paramref name="resultType"/> either way.
    /// </summary>
    private static ShapedQueryExpression BindAggregateOrFallback(ShapedQueryExpression source, MongoAggregateOperator op,
        LambdaExpression? selector, LambdaExpression? predicate, Type resultType)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        var bareSource = selector is null && predicate is null
            ? NativeCardinalityBinder.TryGetBareServerValueProjection(mongoQ, source.ShaperExpression)
            : null;
        if (!NativeCardinalityBinder.TryBindAggregate(mongoQ, op, selector, predicate, resultType, bareSource))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return ReshapeShaperExpression(source, resultType);
    }

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
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Concat);

    protected override ShapedQueryExpression? TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression? defaultValue)
        => null;

    protected override ShapedQueryExpression? TranslateElementAtOrDefault(ShapedQueryExpression source,
        Expression index, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Except);

    protected override ShapedQueryExpression? TranslateFirstOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    protected override ShapedQueryExpression? TranslateGroupBy(ShapedQueryExpression source, LambdaExpression keySelector,
        LambdaExpression? elementSelector, LambdaExpression? resultSelector)
    {
        // The base TranslateGroupBy is abstract, so the grouped query is built here. Native supports only
        // GroupBy(key).Select(aggregate), over the root collection or an eligible join scope; a non-null resultSelector or an
        // unbindable key marks non-native.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // A GroupBy over an existing grouping/distinct terminal must not rebind by default: TryBindGroupKey would overwrite the
        // Grouping and silently drop the Distinct. This override bypasses the guards in NativeSlotPopulator/
        // NativeCardinalityBinder. Read before IsGroupBy is set below, which would make HasTerminalOperator always true.
        var hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalOperator;

        // A completed set-op terminal has no Grouping to overwrite, so it is exempt (as in TranslateSelect).
        var wasSetOpTerminalOnly = mongoQueryExpression.Select.IsSetOpTerminalOnly;

        // A GroupBy over an already-finalized Grouping isn't the overwrite hazard: SnapshotPriorGroupingForNestedGroupBy moves
        // the prior grouping into PriorGrouping and the lowerer emits both $group stages in order. Only
        // TryBindGroupProjection/TryBindDistinctFromProjection leave Grouping set here (TryBindGroupTerminalAggregate also
        // sets Cardinality, ending the query).
        var hasFinalizedPriorGrouping = mongoQueryExpression.Select.Grouping != null;

        // Decided before IsGroupBy is set (which makes HasTerminalOperator true). A nested GroupBy over a finalized
        // grouping resolves against the prior stage's alias, never a join scope.
        var groupByJoinScope = hadTerminalGrouping || hasFinalizedPriorGrouping
            ? null
            : NativeJoinScopeGate.TryGetGroupByJoinScope(mongoQueryExpression, keySelector);

        // Set unconditionally so TranslateJoinCore can detect a join over a grouped source.
        mongoQueryExpression.Select.IsGroupBy = true;

        if (hadTerminalGrouping && !hasFinalizedPriorGrouping && !wasSetOpTerminalOnly)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        // The prior stage's Skip/Take lives in GroupPagingOps, which the snapshot doesn't move and the next
        // TryBindGroupProjection resets, so its paging would be silently lost. Decline.
        else if (hasFinalizedPriorGrouping && mongoQueryExpression.Select.GroupPagingOps.Count > 0)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        // A third grouping level: there is a single Prior* slot, so snapshotting again would overwrite it and the first
        // $group would silently vanish from the pipeline. N-level chains aren't supported; decline.
        else if (hasFinalizedPriorGrouping && mongoQueryExpression.Select.PriorGrouping != null)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        else
        {
            if (hasFinalizedPriorGrouping)
            {
                mongoQueryExpression.Select.SnapshotPriorGroupingForNestedGroupBy();

                // The prior stage's read-side projection entries (e.g. "Key"/"Count") would collide with this
                // stage's aliases.
                mongoQueryExpression.ClearReadProjectionForNestedGroupBy();
            }

            mongoQueryExpression.Select.GroupByJoinScope = groupByJoinScope;

            if (resultSelector != null
                || !NativeGroupByBinder.TryBindGroupKey(mongoQueryExpression, keySelector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }

        // Carry a GroupByShaperExpression so a subsequent Select over the IGrouping is recognized. The definitive grouped-row
        // shaper is compiled in the gate; this key shaper is a lightweight placeholder.
        var keyShaper = ReplacingExpressionVisitor.Replace(keySelector.Parameters[0], source.ShaperExpression, keySelector.Body);
        var groupByShaper = new GroupByShaperExpression(keyShaper, source);
        return source.UpdateShaperExpression(groupByShaper);
    }

    protected override ShapedQueryExpression? TranslateLastOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType, bool returnDefault)
        => null;

    // Translate{Where,OrderBy,ThenBy,Skip,Take} are inert: slots are populated in NativeSlotPopulator.PopulateNativeSlots
    // (see VisitMethodCall), since routing them through base.VisitMethodCall would build a fresh MongoQueryExpression per
    // operator. Don't add them to the VisitMethodCall switch without removing that handling, or slots double-populate.

    protected override ShapedQueryExpression? TranslateOrderBy(ShapedQueryExpression source, LambdaExpression keySelector,
        bool ascending)
        => null;

    protected override ShapedQueryExpression? TranslateReverse(ShapedQueryExpression source)
        => null;

    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source, LambdaExpression collectionSelector,
        LambdaExpression resultSelector)
    {
        // EF normalizes every SelectMany to this overload with a trivial TransparentIdentifier(Outer, Inner) resultSelector,
        // followed by a .Select(ti => ti.Inner) unwrap in TranslateSelect, so the returned shaper must keep that shape (EF's
        // ReplacingExpressionVisitor member fold resolves ti.Inner back to our projected shaper).
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // Before the terminal guard: when the only terminal so far is a single reference unwind, this may be the second
        // SelectMany of a nested reference shape; BuildBareNavWrappedShaper reads the now-second UnwindSource.
        if (mongoQueryExpression.Select.IsSingleReferenceUnwindTerminalOnly
            && NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQueryExpression, collectionSelector))
        {
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);
        }

        // Post-terminal guard: after a set op, GroupBy, Distinct or prior SelectMany, the lowerer picks one terminal by fixed
        // precedence and silently drops the other (`Union(a,b).SelectMany(...)` emits only $unionWith, even under NativeOnly).
        // SelectManyWithCollectionSelector is allowed in NativeSlotPopulator, so the catch-all doesn't back this up.
        // Return null (a hard fail in every mode) rather than MarkNotNativelyRepresentable: the native by-index shaper can't be
        // re-read by the driver-LINQ fallback. See NativeSelectManyTests.
        if (mongoQueryExpression.Select.HasTerminalOperator)
            return null;

        // Query-syntax / explicit-result-selector form: a bare owned nav collection selector (o => o.Items.AsQueryable()), with
        // the real projection in the trailing Select (NativeSelectManyBinder.TryBindTransparentIdentifierProjection). The Inner
        // item shaper only type-checks as the TransparentIdentifier and lets an unsupported trailing projection fold during
        // fallback shaper construction; it isn't read when the trailing Select binds natively.
        if (NativeSelectManyBinder.TryBindBareNavUnwind(mongoQueryExpression, collectionSelector))
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);

        // Cross-collection reference bare-nav: a correlated Queryable.Where(EntityQueryRoot, o => c.pk==o.fk)
        // collection selector. Same wrapped shape as owned bare-nav.
        if (NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQueryExpression, collectionSelector))
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);

        if (!NativeSelectManyBinder.TryBind(mongoQueryExpression, collectionSelector))
            return null;

        return BuildSelectManyWrappedShaper(source, mongoQueryExpression, collectionSelector, resultSelector, _projectionBindingExpressionVisitor);
    }

    /// <summary>
    /// Builds the <c>TransparentIdentifier(Outer, Inner)</c> shaper after a bare-nav SelectMany bind, which set only
    /// <see cref="Expressions.MongoSelectDefinition.UnwindSource"/>. The Inner item shaper exists only to type-check.
    /// </summary>
    private static ShapedQueryExpression BuildBareNavWrappedShaper(
        ShapedQueryExpression source, MongoQueryExpression mongoQueryExpression, LambdaExpression resultSelector)
    {
        var itemShaper = new StructuralTypeShaperExpression(
            mongoQueryExpression.Select.UnwindSource!.InnerEntityType,
            new ProjectionBindingExpression(mongoQueryExpression, new ProjectionMember(), typeof(ValueBuffer)),
            false);

        var wrapped = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], source.ShaperExpression,
            ReplacingExpressionVisitor.Replace(resultSelector.Parameters[1], itemShaper, resultSelector.Body));

        return source.UpdateShaperExpression(wrapped);
    }

    /// <summary>
    /// Builds the shaper for a native inner-<c>Select</c> owned-collection <c>SelectMany</c> after
    /// <see cref="NativeSelectManyBinder.TryBind"/>: each projected member binds to the alias the binder registered, wrapped in
    /// <paramref name="resultSelector"/>'s <c>TransparentIdentifier(Outer, Inner)</c> for the following <c>.Select(ti =&gt; ti.Inner)</c>.
    /// </summary>
    private static ShapedQueryExpression BuildSelectManyWrappedShaper(
        ShapedQueryExpression source, MongoQueryExpression mongoQueryExpression, LambdaExpression collectionSelector,
        LambdaExpression resultSelector, MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor)
    {
        // TryBind already validated collectionSelector.Body as Queryable.Select(<source>, innerLambda).
        var innerLambda = ((MethodCallExpression)collectionSelector.Body).Arguments[1].UnwrapLambdaFromQuote();
        var innerShaper = BuildSelectManyResultShaper(mongoQueryExpression, innerLambda.Body, projectionBindingExpressionVisitor);

        // Two nested single-argument Replace calls: EF8 lacks the list overload (a collection expression would
        // bind to the single-Expression overload and fail with CS9174).
        var wrappedShaper = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], source.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], innerShaper, resultSelector.Body));

        return source.UpdateShaperExpression(wrappedShaper);
    }

    // allowContainerElements: only the join-scope Select arm, whose binder (NativeJoinScopeProjectionBinder
    // .TryBindProjection) admits a positional container; the SelectMany callers keep rejecting one.
    private static Expression BuildSelectManyResultShaper(
        MongoQueryExpression mongoQueryExpression, Expression projectionBody,
        MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor, Expression? foldedBody = null,
        bool allowContainerElements = false)
    {
        // TryBind already validated this shape with the same reader; throw rather than silently mis-shape.
        if (!projectionBody.TryGetProjectionMembers(
                out var members, allowPositionalConstructorArguments: true, allowContainerElements: allowContainerElements))
        {
            throw new InvalidOperationException(
                $"Unexpected SelectMany projection shape '{projectionBody.GetType().Name}' after successful native binding.");
        }

        // The folded body (the join's shaper substituted in, EF-444) goes through the same reader, so members
        // pair up by index.
        IReadOnlyList<(string MemberName, Expression Value)>? foldedMembers = null;
        if (foldedBody is not null
            && foldedBody.TryGetProjectionMembers(
                out var readFolded, allowPositionalConstructorArguments: true, allowContainerElements: allowContainerElements))
        {
            foldedMembers = readFolded;
        }

        // A container element was staged without its boxing Convert (NativeJoinScopeProjectionBinder), so bind the
        // operand (a whole-entity element then arrives as the join's entity shaper and is rebound by index) and re-box it.
        var isContainer = projectionBody is NewArrayExpression;
        var boundValues = new Expression[members.Count];
        for (var i = 0; i < boundValues.Length; i++)
        {
            var value = members[i].Value;
            var folded = foldedMembers is not null && i < foldedMembers.Count ? foldedMembers[i].Value : null;
            var operand = value;
            if (isContainer)
            {
                operand = NativeProjectionBinder.UnwrapContainerElementBoxing(value);
                folded = folded is null ? null : NativeProjectionBinder.UnwrapContainerElementBoxing(folded);
            }

            var bound = BindResultMember(
                mongoQueryExpression, members[i].MemberName, operand, projectionBindingExpressionVisitor, folded);
            boundValues[i] = ReferenceEquals(operand, value) ? bound : Expression.Convert(bound, value.Type);
        }

        return projectionBody.RebuildProjectionMembers(boundValues);
    }

    // Registers one projection leaf under `alias` and returns an index-based binding (SelectMany-result members, join-scope
    // bare leaves, set-op operands; like BindGroupMember). The DOM shaper reads the value raw by alias.
    private static Expression BindProjectionByAlias(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        var index = mongoQueryExpression.AddToProjection(valueExpression, alias);
        return new ProjectionBindingExpression(mongoQueryExpression, index, valueExpression.Type);
    }

    // BindProjectionByAlias for join results (EF-444). When the folded leaf (the join's shaper substituted in) is a whole-entity
    // StructuralTypeShaperExpression, rebind its EntityProjectionExpression under this alias; registering the raw leaf would be a
    // scalar alias read that throws "No known serializer for type '<Entity>'". Inert for non-join callers (no folded expression).
    //
    // A whole-entity leaf that is also an Include target arrives wrapped in IncludeExpressions: unwrap, rebind, re-wrap in
    // order, and route through VisitIncludeExpression so the Include's $lookup registration runs (as
    // NativeJoinScopeProjectionBinder.TryBindProjection does); else the raw wrapped leaf would be registered under an alias the
    // native $project never emits, silently misreading it.
    private static Expression BindResultMember(
        MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression,
        MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor, Expression? foldedExpression)
    {
        var includeWrappers = new List<IncludeExpression>();
        var unwrappedFoldedExpression = foldedExpression;
        // Two kinds of Include wrapper can be rebound:
        //  * Collection: NavigationExpression is left as is; its $lookup is registered by MongoProjectionBindingExpressionVisitor
        //    via VisitIncludeExpression below.
        //  * Reference (Set<Order>().Include(o => o.Customer).Select(o => new { o, o.CustomerID }), lowered to a LeftJoin): its
        //    target is the join's Inner side, so its NavigationExpression is rebound by index like the leaf.
        //    NativeJoinScopeProjectionBinder.TryResolveReferenceIncludeLevel admits only that shape and has staged the Inner
        //    document into the $project.
        // Anything else (a nested ThenInclude) falls through to BindProjectionByAlias, which fails loudly rather than yielding
        // silent nulls. Every wrapper is checked before anything is rebound, since AddToProjection mutates.
        var allIncludesRebindable = true;
        while (unwrappedFoldedExpression is IncludeExpression includeToUnwrap)
        {
            includeWrappers.Add(includeToUnwrap);
            if (includeToUnwrap.Navigation is not { IsCollection: true } && !IsEmbeddedNavigation(includeToUnwrap.Navigation)
                && !IsRebindableEntityShaper(includeToUnwrap.NavigationExpression))
            {
                allIncludesRebindable = false;
            }

            unwrappedFoldedExpression = includeToUnwrap.EntityExpression;
        }

        if (allIncludesRebindable && IsRebindableEntityShaper(unwrappedFoldedExpression))
        {
            var rebound = RebindEntityShaper(
                mongoQueryExpression, (StructuralTypeShaperExpression)unwrappedFoldedExpression!, alias);

            if (includeWrappers.Count == 0)
            {
                return rebound;
            }

            for (var i = includeWrappers.Count - 1; i >= 0; i--)
            {
                var navigationExpression = includeWrappers[i].Navigation is { IsCollection: true } || IsEmbeddedNavigation(includeWrappers[i].Navigation)
                    ? includeWrappers[i].NavigationExpression
                    : RebindEntityShaper(
                        mongoQueryExpression, (StructuralTypeShaperExpression)includeWrappers[i].NavigationExpression, alias: null);
                rebound = includeWrappers[i].Update(rebound, navigationExpression);
            }

            return projectionBindingExpressionVisitor.VisitIncludeExpression(mongoQueryExpression, (IncludeExpression)rebound);
        }

        // A construction whose members are all whole-entity shapers (`o1 = new { o2 }`): the binder staged each level whole and
        // gave the wrapper no projection, so rebind each member's shaper by index (alias null: dedup onto the level's entry, the
        // same instance a sibling `o1.o2` leaf reads).
        if (foldedExpression is not null
            && foldedExpression.TryGetProjectionMembers(out var wrapperMembers)
            && wrapperMembers.Count > 0
            && wrapperMembers.All(m => IsRebindableEntityShaper(m.Value)))
        {
            return foldedExpression.RebuildProjectionMembers(
                wrapperMembers.Select(m => RebindEntityShaper(
                    mongoQueryExpression, (StructuralTypeShaperExpression)m.Value, alias: null)).ToArray());
        }

        // A nested wrapped leaf from a join scope was already staged by NativeJoinScopeProjectionBinder.TryBindProjection as a
        // MongoDocumentConstructionExpression; register that, not the raw `new { ... }` (an alias read of an anonymous type
        // would throw). Same lookup as MongoProjectionBindingExpressionVisitor.TryGetNativeDocumentConstructionLeaf so the two
        // can't diverge on admission rules.
        return TryBindDocumentConstruction(mongoQueryExpression, alias, valueExpression, out var boundConstruction)
            ? boundConstruction
            : BindProjectionByAlias(mongoQueryExpression, alias, valueExpression);
    }

    // Registers the MongoDocumentConstructionExpression already staged under `alias` (if any) in place of the raw
    // construction, so the shaper reads each member by path rather than alias-reading the whole CLR type.
    private static bool TryBindDocumentConstruction(
        MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression,
        [NotNullWhen(true)] out Expression? bound)
    {
        if (mongoQueryExpression.Select.TryGetDocumentConstructionProjection(
                alias, valueExpression.Type, out var construction))
        {
            var constructionIndex = mongoQueryExpression.AddToProjection(construction, alias);
            bound = new ProjectionBindingExpression(mongoQueryExpression, constructionIndex, valueExpression.Type);
            return true;
        }

        bound = null;
        return false;
    }

    // An embedded (owned) Include keeps its NavigationExpression as is: its data lives in the leaf's own document.
    private static bool IsEmbeddedNavigation(INavigationBase? navigation)
        => navigation is INavigation { } navigationWithOwnership && navigationWithOwnership.IsEmbedded();

    // Whether BindResultMember can rebind this folded expression by index: a whole-entity
    // StructuralTypeShaperExpression reading a ProjectionBindingExpression.
    private static bool IsRebindableEntityShaper(Expression? expression)
        => expression is StructuralTypeShaperExpression { ValueBufferExpression: ProjectionBindingExpression };

    // Rebinds a folded whole-entity shaper by index over its EntityProjectionExpression, registered under `alias`
    // (or deduplicated onto an existing entry).
    private static Expression RebindEntityShaper(
        MongoQueryExpression mongoQueryExpression, StructuralTypeShaperExpression shaper, string? alias)
    {
        var shaperBinding = (ProjectionBindingExpression)shaper.ValueBufferExpression;
        var entityProjection = shaperBinding.Index is int existingIndex
                               && shaperBinding.QueryExpression == mongoQueryExpression
            ? (EntityProjectionExpression)mongoQueryExpression.Projection[existingIndex].Expression
            : (EntityProjectionExpression)mongoQueryExpression.GetMappedProjection(shaperBinding.ProjectionMember!);

        var entityIndex = mongoQueryExpression.AddToProjection(entityProjection, alias);

        return shaper.Update(new ProjectionBindingExpression(mongoQueryExpression, entityIndex, typeof(ValueBuffer)));
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

    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    #endregion
}
