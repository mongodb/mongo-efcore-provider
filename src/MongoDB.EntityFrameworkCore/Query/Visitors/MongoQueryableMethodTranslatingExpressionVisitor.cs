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
internal sealed class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
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
        else if (mongoQueryExpression.Select.JoinScope is { } bareValueScope
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
                         bareValueScope, selector.Parameters[0], selector.Body, out var bareValueLeaf)
                     : NativeJoinScopeTranslator.TryTranslateSingleScope(
                         bareValueScope, selector.Parameters[0], selector.Body, valueMode: true, out bareValueLeaf))
                 // Read back whole by alias; see NativeProjectionBinder.IsMisreadWholeValueLeaf.
                 && !NativeProjectionBinder.IsMisreadWholeValueLeaf(bareValueLeaf!)
                 // A non-nullable Length/IndexOf over an unmatched (or null) inner string: throw on null, or decline
                 // where an operator may absorb the null; the same single classification as the wrapped arm.
                 && MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(
                     NativeSlotPopulator.UnwrapBoxingToObjectType(selector.Body), bareValueLeaf!) is var bareValueRead
                 && bareValueRead != NonNullableValueRead.Decline)
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

    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression)</c> nav-expansion generates for a
    /// single-level root collection <c>Include</c>. Its <c>$lookup</c> is registered later by
    /// <see cref="MongoProjectionBindingExpressionVisitor"/>, so the Select must not be marked non-representable.
    /// </summary>
    private static bool IsSingleLevelCollectionIncludeSelector(LambdaExpression selector)
        => selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
           && includeExpression.EntityExpression == selector.Parameters[0]
           && navigation.IsCollection
           && !navigation.IsEmbedded();

    /// <summary>
    /// Recognizes <c>Select(ti =&gt; IncludeExpression(ti.Outer, nav, ...))</c> (or the <c>ti.Inner</c> mirror, or a longer
    /// <c>Outer</c>/<c>Inner</c> hop chain) : a single-level non-embedded collection <c>Include</c> over one side of a user
    /// <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c>. Structural only: the caller resolves which scope the hop chain names and
    /// must gate the join through <see cref="NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope"/> before confirming it.
    /// </summary>
    internal static IncludeExpression? TryGetCollectionIncludeOverJoinScope(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType()
            || selector.Body is not IncludeExpression { Navigation: INavigation navigation } includeExpression
            || !navigation.IsCollection
            || navigation.IsEmbedded())
        {
            return null;
        }

        var hop = includeExpression.EntityExpression;
        if (hop is not MemberExpression)
        {
            return null;
        }

        while (hop is MemberExpression member && member.IsTransparentIdentifierOuterOrInnerAccess())
        {
            hop = member.Expression;
        }

        return hop == selector.Parameters[0] ? includeExpression : null;
    }

    /// <summary>
    /// Recognizes a pure chain of one or more reference <c>Include</c>s: siblings nest via
    /// <see cref="IncludeExpression.EntityExpression"/>, <c>ThenInclude</c>s via <see cref="IncludeExpression.NavigationExpression"/>.
    /// Any collection level returns <see langword="null"/> (see <see cref="TryGetMixedReferenceAndCollectionIncludeChain"/>);
    /// both share <see cref="TryWalkIncludeChain"/> so they can't disagree on structure.
    /// </summary>
    /// <remarks>
    /// The base may be any number of <c>.Outer</c> hops. A user join followed by an Include looks the same by hop count and is
    /// declined by <see cref="TryConfirmReferenceIncludeChain"/>'s <c>Joins.Count != chain.Count</c> check: this method only
    /// recognizes structure; all semantic declines live in the confirm step.
    /// </remarks>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
        => TryGetReferenceIncludeChain(selector, out _);

    /// <summary>
    /// As <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/>, also reporting which levels are transitive
    /// (<c>ThenInclude</c>-nested). Confirm checks the root declaring type only for non-transitive levels.
    /// </summary>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(
        LambdaExpression selector, out HashSet<IncludeExpression> transitiveLevels)
    {
        if (!TryWalkIncludeChain(selector, out var referenceLevels, out transitiveLevels, out var collectionLevel)
            || collectionLevel != null
            || referenceLevels.Count == 0)
        {
            transitiveLevels = [];
            return null;
        }

        return referenceLevels;
    }

    /// <summary>
    /// Recognizes a reference-Include chain with exactly one non-embedded collection <c>Include</c> mixed in, as a sibling
    /// or a terminal <c>ThenInclude</c>. Pure reference chains and a bare collection Include belong to the other
    /// recognizers; the three don't overlap. The collection level is returned unconfirmed (its <c>$lookup</c> registers during
    /// projection binding); only the reference levels go through <see cref="TryConfirmReferenceIncludeChain"/>.
    /// </summary>
    internal static bool TryGetMixedReferenceAndCollectionIncludeChain(
        LambdaExpression selector,
        out List<IncludeExpression> referenceLevels,
        out HashSet<IncludeExpression> transitiveLevels,
        out IncludeExpression? collectionLevel)
    {
        if (!TryWalkIncludeChain(selector, out referenceLevels, out transitiveLevels, out collectionLevel)
            || collectionLevel == null
            || referenceLevels.Count == 0)
        {
            referenceLevels = [];
            transitiveLevels = [];
            collectionLevel = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Shared structural walk for the reference/mixed Include-chain recognizers: follows the sibling spine
    /// (<see cref="IncludeExpression.EntityExpression"/>) and each sibling's linear <c>ThenInclude</c> chain, sorting
    /// navigations into <paramref name="referenceLevels"/> or a single <paramref name="collectionLevel"/>;
    /// <c>ThenInclude</c> levels are also recorded in <paramref name="transitiveLevels"/>.
    /// </summary>
    /// <remarks>
    /// An embedded navigation in a <c>ThenInclude</c> chain is transparent, provided nothing non-embedded is nested past it
    /// (<see cref="HasNonEmbeddedThenInclude"/>); an embedded sibling rejects the chain (see
    /// <see cref="IsOwnedEmbeddedIncludeSelector"/>). The base must be a pure <c>.Outer</c>* chain reaching the
    /// <c>TransparentIdentifier</c> parameter.
    /// </remarks>
    private static bool TryWalkIncludeChain(
        LambdaExpression selector,
        out List<IncludeExpression> referenceLevels,
        out HashSet<IncludeExpression> transitiveLevels,
        out IncludeExpression? collectionLevel)
    {
        referenceLevels = [];
        transitiveLevels = [];
        collectionLevel = null;

        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return false;
        }

        var parameter = selector.Parameters[0];
        var body = selector.Body;

        while (body is IncludeExpression { Navigation: INavigation navigation } include)
        {
            if (navigation.IsEmbedded())
            {
                return false;
            }

            if (navigation.IsCollection)
            {
                if (collectionLevel != null)
                {
                    // A second collection Include in the chain — out of scope, decline the whole shape.
                    return false;
                }

                collectionLevel = include;
            }
            else
            {
                referenceLevels.Add(include);
            }

            // Follow this level's linear ThenInclude chain (NavigationExpression axis).
            var thenIncludeLevel = include;
            var thenIncludeTargetType = navigation.TargetEntityType;
            while (thenIncludeLevel.NavigationExpression is IncludeExpression { Navigation: INavigation thenNav } thenInclude)
            {
                if (thenNav.IsEmbedded())
                {
                    // Owned auto-include: lives in the same document (EF-368). With nothing non-embedded nested past
                    // it, this level's ThenInclude chain ends here.
                    if (!HasNonEmbeddedThenInclude(thenInclude))
                    {
                        break;
                    }

                    // A reference Include declared on the owned type (Include(Buyer).ThenInclude(Address).ThenInclude(Region)): the
                    // confirm step verifies the Region $lookup's localField reads through the owned hop. Exactly one such reference.
                    IncludeExpression? behindOwned = null;
                    if (!TryFindReferenceBehindOwned(thenInclude, thenNav.DeclaringEntityType, ref behindOwned)
                        || behindOwned is null)
                    {
                        return false;
                    }

                    referenceLevels.Add(behindOwned);
                    transitiveLevels.Add(behindOwned);
                    thenIncludeLevel = behindOwned;
                    thenIncludeTargetType = ((INavigation)behindOwned.Navigation!).TargetEntityType;
                    continue;
                }

                if (thenInclude.EntityExpression is IncludeExpression
                    || thenNav.DeclaringEntityType != thenIncludeTargetType)
                {
                    // A sibling off a ThenInclude, or a hop not declared on the previous hop's target.
                    return false;
                }

                if (thenNav.IsCollection)
                {
                    // A collection ThenInclude off a reference level (Include(o => o.Customer.Orders)) is
                    // admitted as the chain's single collection level, but only as the terminal hop.
                    if (collectionLevel != null || thenInclude.NavigationExpression is IncludeExpression)
                    {
                        return false;
                    }

                    collectionLevel = thenInclude;
                    break;
                }

                referenceLevels.Add(thenInclude);
                transitiveLevels.Add(thenInclude);
                thenIncludeLevel = thenInclude;
                thenIncludeTargetType = thenNav.TargetEntityType;
            }

            body = include.EntityExpression;
        }

        var current = body;
        while (current is MemberExpression { Member.Name: "Outer" } outerAccess)
        {
            current = outerAccess.Expression;
        }

        return current == parameter;
    }

    /// <summary>
    /// Validates and confirms every navigation in a recognized reference-Include chain, all-or-nothing. A single Include
    /// builds and registers the forced-unwind <c>$lookup</c>; for siblings <c>TranslateJoinCore</c> already registered every
    /// join's, so this only confirms each. Registration makes <see cref="MongoQueryExpression.UsesDriverJoinFields"/> false so
    /// the lowerer, DOM shaper and driver-LINQ <c>StripJoinForLookup</c> agree on the <c>_lookup_&lt;Nav&gt;</c> field.
    /// </summary>
    /// <remarks>
    /// Registration also changes the fallback's pipeline at translation time, before <c>MongoQueryMode</c> is read, so a
    /// wrong admission is wrong in every mode; every conjunct below must hold on its own merits. <c>TryWalkIncludeChain</c>
    /// alone decides which <c>ThenInclude</c> nesting is admissible; don't re-check <c>HasNonEmbeddedThenInclude</c> per level
    /// (it would wrongly decline an admitted further level).
    /// </remarks>
    private static bool TryConfirmReferenceIncludeChain(
        MongoQueryExpression mongoQueryExpression,
        List<IncludeExpression> chain,
        HashSet<IncludeExpression> transitiveLevels)
    {
        // Joins are counted, not InnerCollections (keyed by entity type, which collapses same-target siblings). A join beyond
        // the chain's levels is admitted only as a FILTER join (TryAdmitFilterJoins).
        var extraJoins = mongoQueryExpression.Joins.Count - chain.Count;
        if (mongoQueryExpression.Select.HasTerminalOperator                // composed after a terminal
            || mongoQueryExpression.Select.SawNonBareJoinInner             // a filtered/non-bare-scan inner, any join
            || extraJoins < 0
            || (extraJoins > 0 && !TryAdmitFilterJoins(mongoQueryExpression, chain, extraJoins)))
        {
            return false;
        }

        var pendingByAlias = mongoQueryExpression.GetPendingLookups().ToDictionary(l => l.As);
        var newLookups = new List<LookupExpression>();

        foreach (var include in chain)
        {
            var navigation = (INavigation)include.Navigation!;

            // Query filters are caught by SawNonBareJoinInner above, not by checking the target's GetQueryFilter(): that misses
            // a filter declared on a TPH root and EF10 named filters, and the flat $lookup can't filter, so either would return
            // wrong rows in every mode. DeclaringEntityType is checked against the root only for non-transitive levels.
            if (navigation.ForeignKey.Properties.Count != 1                        // composite FK
                || navigation.ForeignKey.PrincipalKey.Properties.Count != 1        // composite PK
                || (!transitiveLevels.Contains(include)
                    && navigation.DeclaringEntityType != mongoQueryExpression.CollectionExpression.EntityType)
                || !mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType))
            {
                return false;
            }

            var alias = LookupExpression.GetLookupAlias(navigation);
            if (pendingByAlias.TryGetValue(alias, out var existing))
            {
                // Registered by TranslateJoinCore's multi-join flattening; confirm rather than re-register. A level whose own lookup
                // carries a join-inner filter is an explicit join, not an Include: declined (a query-filtered Include target is a
                // separate feature with its own required/optional-FK rules).
                if (!existing.ForceUnwind || existing.HasInnerPredicate)
                {
                    return false;
                }

                // A reference declared on an owned type (Include(Buyer).ThenInclude(Address).ThenInclude(Region)): the
                // lookup must read its key through exactly that owned path under the parent's alias.
                if (navigation.DeclaringEntityType.IsOwned()
                    && !OwnedHopLocalFieldMatches(chain, navigation, existing))
                {
                    return false;
                }
            }
            else if (navigation.DeclaringEntityType.IsOwned())
            {
                return false; // an owned hop always follows a prior join, so its lookup is already registered
            }
            else
            {
                // Single join: TranslateJoinCore never flattens a lone join, so register it here, using the recorded join's own
                // lookup: it carries the real left-outer flag from the operator EF chose. EF emits an inner Join only for a
                // dependent-side required navigation; a principal-side reference Include is always a LeftJoin even when the FK is
                // required, so guessing from ForeignKey.IsRequired dropped principals with no dependent.
                var join = mongoQueryExpression.Joins.FirstOrDefault(
                    j => j.Navigation == navigation && j.Lookup is { ForceUnwind: true });
                if (join is null || join.Lookup!.HasInnerPredicate)
                {
                    return false;
                }

                // Defensive: not known reachable, since the lookup's constructor never prefixes LocalField and the
                // sites that do mutate already-registered lookups.
                if (join.Lookup!.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                newLookups.Add(join.Lookup);
            }
        }

        foreach (var lookup in newLookups)
        {
            mongoQueryExpression.AddLookup(lookup);
        }

        // Candidates equal Joins.Count, so confirm every join; a filter join no earlier arm confirmed is confirmed here.
        var toConfirm = mongoQueryExpression.Joins.Count - mongoQueryExpression.Select.ConfirmedReferenceIncludeCount;
        for (var i = 0; i < toConfirm; i++)
        {
            mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
        }

        return true;
    }

    /// <summary>
    /// Whether the joins beyond a reference-Include chain's own levels are FILTER joins that may precede the Include: a Where
    /// that read a join's Inner left its predicate, and any paging/Distinct after it, in <c>PostJoinOps</c> (lowered after
    /// every <c>$lookup</c>). The Include's join is a left-outer reference (row-preserving), so that equals paging before it.
    /// Every filter join must be a resolved, flattened, eligible reference join, confirmed by an earlier arm or read by a
    /// native post-join predicate.
    /// </summary>
    private static bool TryAdmitFilterJoins(MongoQueryExpression query, List<IncludeExpression> chain, int extraJoins)
    {
        var select = query.Select;
        var alreadyConfirmed = select.ConfirmedReferenceIncludeCount;
        if (alreadyConfirmed != extraJoins && !(alreadyConfirmed == 0 && select.JoinInnerAccessConfirmed))
        {
            return false;
        }

        // Every join no Include level backs (matched by navigation, once each) must be a reference join.
        var remaining = chain.Select(include => (INavigation)include.Navigation!).ToList();
        foreach (var join in query.Joins)
        {
            if (join.Navigation is { } navigation && remaining.Remove(navigation))
            {
                continue;
            }

            if (join.Lookup is not { ForceUnwind: true }
                || join.Navigation is not { IsCollection: false }
                || !join.IsNativelyEligible)
            {
                return false;
            }
        }

        // Paging recorded after a join but still in PipelineOps lowers BEFORE every $lookup.
        return !(select.HasPaging && select.HasPagingRecordedAfterAJoin && !query.AreAllJoinsRowCountPreserving());
    }

    /// <summary>
    /// Whether <paramref name="lookup"/> (the registered <c>$lookup</c> of <paramref name="navigation"/>, declared on an owned
    /// type) reads its foreign key at <c>&lt;parent alias&gt;.&lt;owned path&gt;.&lt;fk element&gt;</c>: the parent is the one chain
    /// level targeting the owned path's root owner, and every owned hop between is a single reference.
    /// </summary>
    private static bool OwnedHopLocalFieldMatches(
        List<IncludeExpression> chain, INavigation navigation, LookupExpression lookup)
    {
        var segments = new List<string>();
        var type = navigation.DeclaringEntityType;
        while (type.IsOwned())
        {
            var ownership = type.FindOwnership();
            if (ownership is null
                || ownership.PrincipalToDependent is not { IsCollection: false }
                || type.GetContainingElementName() is not { } segment)
            {
                return false;
            }

            segments.Insert(0, segment);
            type = ownership.PrincipalEntityType;
        }

        var parents = chain
            .Select(include => (INavigation)include.Navigation!)
            .Where(parent => parent.TargetEntityType == type)
            .ToList();
        if (parents.Count != 1)
        {
            return false; // ambiguous: sibling includes of the same target type
        }

        var expected = $"{LookupExpression.GetLookupAlias(parents[0])}.{string.Join('.', segments)}."
                       + LookupExpression.GetFieldPath(navigation.ForeignKey.Properties[0]);
        return lookup.LocalField == expected;
    }

    /// <summary>
    /// Finds the one non-embedded reference <see cref="IncludeExpression"/> behind a chain of embedded single-reference
    /// (owned) hops, searching both axes (EF nests the owned sibling holding the reference on either). <see langword="false"/>
    /// declines the whole chain: a second non-embedded include, a collection behind the hop, or a non-embedded include not
    /// declared on an owned type. <paramref name="owner"/> declares the embedded navigations being examined.
    /// </summary>
    private static bool TryFindReferenceBehindOwned(Expression expression, IEntityType owner, ref IncludeExpression? found)
    {
        if (expression is not IncludeExpression include)
        {
            return true;
        }

        if (include.Navigation is not INavigation navigation)
        {
            return false;
        }

        if (navigation.IsEmbedded())
        {
            // A $lookup key read through an array has no element-level correlation; only a plain owned auto-include
            // (nothing non-embedded under it) may be a collection.
            if (navigation.IsCollection)
            {
                return !HasNonEmbeddedThenInclude(include);
            }

            // The same owner's sibling auto-includes, then the owned type's own includes one hop down.
            return TryFindReferenceBehindOwned(include.EntityExpression, owner, ref found)
                   && TryFindReferenceBehindOwned(include.NavigationExpression, navigation.TargetEntityType, ref found);
        }

        // A non-embedded include is admitted only as a reference declared on an owned type, and only one of them; the
        // owner's sibling auto-includes that follow it on the entity axis are walked with the same rule.
        if (navigation.IsCollection || found is not null || !owner.IsOwned() || navigation.DeclaringEntityType != owner)
        {
            return false;
        }

        found = include;
        return TryFindReferenceBehindOwned(include.EntityExpression, owner, ref found);
    }

    /// <summary>
    /// Whether <paramref name="navigationExpression"/> is, or contains through further embedded hops, an
    /// <see cref="IncludeExpression"/> with a non-embedded navigation: a real <c>ThenInclude</c> past the looked-up document,
    /// not an owned auto-include living in the same document. Recurses both axes; known shapes are already rejected by the
    /// <c>Joins.Count != chain.Count</c> check, so the recursion is defence in depth.
    /// </summary>
    private static bool HasNonEmbeddedThenInclude(Expression navigationExpression)
    {
        if (navigationExpression is not IncludeExpression nested)
        {
            return false;
        }

        if (nested.Navigation is not INavigation nestedNavigation || !nestedNavigation.IsEmbedded())
        {
            return true;
        }

        return HasNonEmbeddedThenInclude(nested.EntityExpression) || HasNonEmbeddedThenInclude(nested.NavigationExpression);
    }

    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression(...))</c> nav-expansion generates for
    /// auto-included owned (embedded) navigations, possibly nested. Owned data lives in the owner's document, so there is
    /// nothing to push down and the Select must not be marked non-representable; any non-embedded navigation falls back.
    /// Owned collections whose elements have further navigations are kept off the streaming shaper by
    /// <see cref="StreamingEligibility"/>.
    /// </summary>
    private static bool IsOwnedEmbeddedIncludeSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1)
        {
            return false;
        }

        var body = selector.Body;
        var sawInclude = false;

        while (body is IncludeExpression { Navigation: INavigation navigation } include)
        {
            // Embedded arrays and sub-documents need no extra pipeline stage.
            if (!navigation.IsEmbedded())
            {
                return false;
            }

            sawInclude = true;
            body = include.EntityExpression;
        }

        return sawInclude && body == selector.Parameters[0];
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
        // Built outer-to-inner: s.SetProperty(a).SetProperty(b) parses as (s.SetProperty(a)).SetProperty(b), so walk Object
        // inward, inserting at the front to preserve the authored order.
        while (body is MethodCallExpression { Method.Name: "SetProperty" } call)
        {
            var selector = call.Arguments[0].UnwrapLambdaFromQuote();
            // For the self-referencing SetProperty overload the value arg is a quoted Func<T,TProp>
            // lambda; for the constant overload it is the value expression directly.
            var value = call.Arguments[1];
            parsed.Insert(0, BuildSetter(mongoQueryExpression, selector, value));
            body = call.Object!;
        }

        // EF10 validates "at least one SetProperty" before the provider, but EF9 hands the raw lambda through, so a setter
        // lambda with no SetProperty call must be rejected here rather than silently running a no-op updateMany.
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
    /// Parses a <c>SetProperty(selector, value)</c> into a <see cref="MongoNonQueryExpression.Setter"/>. The selector must
    /// target a mapped root scalar property; anything else produces EF's canonical non-query translation failure.
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

        // EF9 self-referencing: value is a quoted Func<T,TProp> lambda; unwrap and detect a reference to its parameter.
        // EF10 (and EF9 constants): value is the raw expression; detect a reference to the setter's lambda parameter.
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

    /// <summary>Whether an expression references any of the given parameters (a self-referencing setter value).</summary>
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
    /// Classifies the captured source chain of a bulk delete/update. Only <c>Where</c> is the single-command atomic path;
    /// <c>OrderBy</c>/<c>ThenBy</c>/<c>Skip</c>/<c>Take</c>/<c>Distinct</c> require the two-phase path (query target
    /// <c>_id</c>s, then act by <c>$in</c>). Anything else produces EF's canonical non-query translation failure.
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

    protected override ShapedQueryExpression? TranslateGroupJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, innerKeySelector, resultSelector, isLeftOuter: true);

    protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Intersect);

    protected override ShapedQueryExpression? TranslateLeftJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, innerKeySelector, resultSelector, isLeftOuter: true);

#if !EF8 && !EF9
    protected override ShapedQueryExpression? TranslateRightJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        null;
#endif

    protected override ShapedQueryExpression? TranslateJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector)
        => TranslateJoinCore(outer, inner, outerKeySelector, innerKeySelector, resultSelector, isLeftOuter: false);

    // isLeftOuter carries the LINQ operator's join semantics to the emitted $lookup/$unwind: Join is inner, LeftJoin/
    // GroupJoin are left-outer. EF lowers a REQUIRED reference navigation to Queryable.Join, which drops principals with
    // a dangling foreign key, matching relational EF Core.
    private static ShapedQueryExpression? TranslateJoinCore(
        ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector, bool isLeftOuter)
    {
        var outerQueryExpression = (MongoQueryExpression)outer.QueryExpression;
        var innerQueryExpression = (MongoQueryExpression)inner.QueryExpression;

        // A join over a grouped source can't be native, and its driver-LINQ fallback silently returns empty joined
        // entities, so mark it fallback-unsafe to fail cleanly.
        if (outerQueryExpression.Select.IsGroupBy || innerQueryExpression.Select.IsGroupBy)
        {
            outerQueryExpression.Select.MarkGroupByFallbackUnsafe();
        }
        // A join over a projected Distinct can't be native either (the lowerer's group branch would drop the
        // join), but its fallback is correct, so just mark non-native. See MongoSelectDefinition.IsDistinct.
        else if (outerQueryExpression.Select.IsDistinct || innerQueryExpression.Select.IsDistinct)
        {
            outerQueryExpression.Select.MarkNotNativelyRepresentable();
        }

        // The gate only reads the outermost MongoQueryExpression, so propagate a wrong-data verdict from an inner
        // subquery (EF-344).
        outerQueryExpression.Select.PropagateFallbackWrongDataFrom(innerQueryExpression.Select);

        var innerEntityType = innerQueryExpression.CollectionExpression.EntityType;
        outerQueryExpression.AddInnerCollection(innerEntityType);

        // Record THIS join up front. One entry per join rather than per target entity type, so a second join onto an
        // already-joined type still triggers the flattening below (EF-375), and a later hop can find this one by position
        // (AnalyzeKeySelectorTarget, EF-372).
        var joinInfo = outerQueryExpression.AddJoin(innerEntityType, isLeftOuter);

        // An operator composed between two cross-collection joins isn't declined here: the driver-LINQ path's
        // StripInterleavedJoinChain emits each $lookup at its own boundary (MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs).

        // Migrate the inner entity's projection (bound to the inner MongoQueryExpression) onto the outer query so the
        // entity shaper can read inner properties from the $lookup result field.
        var reboundInnerShaper = RebindInnerShaperToOuterQuery(
            inner.ShaperExpression, innerQueryExpression, outerQueryExpression, outerKeySelector, innerKeySelector, joinInfo);

        // Paging/dedup already in PostJoinOps (a Where/OrderBy reaching an earlier join's Inner side flipped ActiveOps) lowers
        // after EVERY $lookup/$unwind, including this join's. Unless this join provably neither drops nor multiplies rows, that
        // would page/dedup its joined rows instead of the rows the op was written over. Checked once Navigation/Lookup are
        // resolved, so it covers every consuming Select arm. Fails closed: no Lookup is not row-count-preserving.
        if (outerQueryExpression.Select.HasNonCommutingPostJoinOp && !joinInfo.IsRowCountPreserving)
        {
            outerQueryExpression.Select.MarkNotNativelyRepresentable();
        }

        // Computed for every join so a later join can tell whether every join so far is eligible. This only
        // builds JoinScope; confirming ($lookup registration) is deferred to the consuming Select arm.
        var eligibleApartFromInnerShape =
            !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
            && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
            && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);

        // A filter-only inner rides on this join's own $lookup instead of being dropped (M22). Only for a join that is
        // otherwise eligible, so an ineligible join never carries a predicate that a fallback would silently lose.
        var innerFilterCarried = eligibleApartFromInnerShape
            && TryCarryInnerFilterOnLookup(innerQueryExpression.Select, joinInfo);

        // The reference-Include path emits a flat $lookup with no sub-pipeline, so it can only stand in for a join whose inner
        // is a bare collection scan; record that here, the only point the inner's select is in hand. An inner whose filter this
        // join's own lookup carries is not recorded: that lookup is an explicit join (an Include level carrying a predicate is
        // declined separately via LookupExpression.HasInnerPredicate).
        if (!innerQueryExpression.Select.IsBareCollectionScan && !innerFilterCarried)
        {
            outerQueryExpression.Select.MarkSawNonBareJoinInner();
        }

        joinInfo.IsNativelyEligible =
            eligibleApartFromInnerShape
            && (innerQueryExpression.Select.IsBareCollectionScan || innerFilterCarried);

        // JoinScope covers only an unbroken run of eligible joins; one ineligible join stops it.
        if (outerQueryExpression.Joins.All(j => j.IsNativelyEligible))
        {
            outerQueryExpression.Select.JoinScope = new MongoJoinScope(
                outerQueryExpression.CollectionExpression.EntityType,
                outerQueryExpression.Joins
                    .Select(j => new MongoJoinScopeLevel(j.InnerEntityType, j.Alias, j.IsLeftOuter))
                    .ToList());
        }

        var newResultSelector = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], outer.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], reboundInnerShaper!,
                resultSelector.Body));

        return outer.UpdateShaperExpression(newResultSelector);
    }

    /// <summary>
    /// Moves a filter-only join inner's predicates onto <paramref name="joinInfo"/>'s own <c>$lookup</c>
    /// (<see cref="LookupExpression.InnerPredicate"/>), evaluated inside the lookup sub-pipeline relative to the joined document.
    /// </summary>
    /// <returns>
    /// Whether the filter is now carried; <see langword="false"/> (nothing changed) when the inner is not
    /// <see cref="MongoSelectDefinition.IsFilterOnlyCollectionScan"/>, the join has no lookup, or the lookup can't hold a plain
    /// predicate (a composite-key <c>let</c>/<c>$expr</c> lookup, or an already-stamped, write-once <see cref="LookupPipelineKind"/>).
    /// </returns>
    private static bool TryCarryInnerFilterOnLookup(MongoSelectDefinition innerSelect, JoinInfo joinInfo)
    {
        // A TPH-derived root is normally rewritten to OfType<TDerived>(root) by the preprocessor (DerivedEntityRootNarrower), so
        // the discriminator is part of the carried predicate. A derived collection type can only reach here un-narrowed (the
        // preprocessor skips a vector search), and a filter with no discriminator would join sibling-type documents: decline.
        if (!innerSelect.IsFilterOnlyCollectionScan
            || joinInfo.InnerEntityType.BaseType is not null
            || joinInfo.Lookup is not { CompositeKey: null, PipelineKind: LookupPipelineKind.None } lookup)
        {
            return false;
        }

        MongoExpression? predicate = null;
        foreach (var op in innerSelect.PipelineOps.Cast<MongoMatchOp>())
        {
            predicate = predicate is null
                ? op.Predicate
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, predicate, op.Predicate);
        }

        lookup.InnerPredicate = predicate;
        lookup.PipelineKind = LookupPipelineKind.FilteredJoinInner;
        return true;
    }

    /// <summary>
    /// Whether the <c>$lookup</c> built for <paramref name="joinInfo"/> implements the join condition the user wrote: its
    /// <c>localField</c>/<c>foreignField</c> match the key properties' field paths.
    /// </summary>
    /// <remarks>
    /// A wrong-data guard that makes <see cref="MongoSelectDefinition.JoinScope"/> safe to consume.
    /// <see cref="RebindInnerShaperToOuterQuery"/> falls back to any navigation targeting the joined type, so
    /// <c>Owners.Join(Orders, o =&gt; o.Region, r =&gt; r.Region, ...)</c> can resolve <c>Owner.Orders</c> and a lookup on the
    /// wrong fields. For simple keys that method discards such a navigation and builds a raw-key lookup instead
    /// (<c>NativeJoinTests.Join_on_non_key_properties_between_navigation_related_types_joins_on_the_written_keys</c>);
    /// composite or other non-simple keys decline here.
    /// </remarks>
    private static bool JoinLookupImplementsKeySelectors(
        JoinInfo joinInfo,
        MongoQueryExpression outerQueryExpression,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector)
    {
        if (joinInfo.Lookup is not { } lookup)
        {
            return false;
        }

        // An anonymous-type key (EF-436): only the navigation-less branch builds its Lookup, from the same recognizer,
        // with one key pair per member, so it matches by construction.
        if (TryGetAnonymousJoinKeyMembers(outerKeySelector, innerKeySelector, out var outerMembers, out _))
        {
            return joinInfo.Navigation == null
                && (lookup.CompositeKey?.Count ?? 1) == outerMembers.Count;
        }

        if (outerKeySelector.Body.TryGetSimplePropertyName() == null
            || innerKeySelector.Body.TryGetSimplePropertyName() == null)
        {
            return false;
        }

        if (joinInfo.Navigation == null)
        {
            // Navigation-less: the Lookup was built directly from these key selectors, so it matches by
            // construction.
            return true;
        }

        return LookupImplementsKeySelectors(
            lookup, joinInfo.Navigation, joinInfo.InnerEntityType, outerKeySelector, innerKeySelector);
    }

    /// <summary>
    /// The navigation-backed half of <see cref="JoinLookupImplementsKeySelectors"/>, shared with
    /// <see cref="RebindInnerShaperToOuterQuery"/>'s decision to discard a navigation whose <c>$lookup</c> would not
    /// implement the written key equality.
    /// </summary>
    private static bool LookupImplementsKeySelectors(
        LookupExpression lookup,
        INavigation navigation,
        IEntityType innerEntityType,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector)
    {
        var outerKeyName = outerKeySelector.Body.TryGetSimplePropertyName();
        var innerKeyName = innerKeySelector.Body.TryGetSimplePropertyName();
        if (outerKeyName == null || innerKeyName == null)
        {
            return false;
        }

        // Resolve the outer property on the navigation's declaring type, not the root: for a chained join
        // (`e.r.Id`) that's the prior hop's inner type, and the root could hold a wrong same-named property.
        var outerAnchorEntityType = navigation.DeclaringEntityType;
        var outerProperty = outerAnchorEntityType.FindProperty(outerKeyName);
        var innerProperty = innerEntityType.FindProperty(innerKeyName);
        if (outerProperty == null || innerProperty == null)
        {
            return false;
        }

        // A transitive hop's LocalField is prefixed with the prior join's alias, so match by suffix. Compare via
        // LookupExpression.GetFieldPath so composite-PK components ("_id.<ElementName>") match too.
        var outerElementPath = LookupExpression.GetFieldPath(outerProperty);
        var outerFieldMatches = lookup.LocalField == outerElementPath
            || lookup.LocalField.EndsWith("." + outerElementPath, StringComparison.Ordinal);

        return outerFieldMatches && lookup.ForeignField == LookupExpression.GetFieldPath(innerProperty);
    }

    /// <summary>
    /// Builds the forced-unwind <c>$lookup</c> for a navigation-backed join hop, with its <c>localField</c> prefixed by a
    /// transitive hop's alias and/or an owned/embedded path (EF-380). Shared by <see cref="RebindInnerShaperToOuterQuery"/>'s
    /// registration and its key-equality check so the two can't disagree on the joined fields.
    /// </summary>
    private static LookupExpression BuildNavigationJoinLookup(
        INavigation navigation, string alias, bool isLeftOuter, JoinInfo? throughJoin, string? embeddedPath)
    {
        var lookup = new LookupExpression(navigation, forceUnwind: true)
        {
            As = alias,
            PreserveNullAndEmptyArrays = isLeftOuter
        };
        if (throughJoin != null)
        {
            // Transitive join: match against the already-unwound intermediate document; an owned/embedded navigation (EF-380)
            // inserts its path between the intermediate's alias and the field.
            var throughAlias = embeddedPath != null ? $"{throughJoin.Alias}.{embeddedPath}" : throughJoin.Alias;
            lookup.LocalField = $"{throughAlias}.{lookup.LocalField}";
        }
        else if (embeddedPath != null)
        {
            // Direct from root, but through an owned/embedded navigation (EF-380).
            lookup.LocalField = $"{embeddedPath}.{lookup.LocalField}";
        }

        return lookup;
    }

    /// <summary>
    /// Resolves the raw outer/inner key properties a navigation-less (EF-377) join <c>$lookup</c> is built from:
    /// <paramref name="fkPropertyName"/> on <paramref name="fkOwnerEntityType"/> (the root, or a transitive hop's target) and
    /// the inner key selector's simple property on <paramref name="innerEntityType"/>.
    /// </summary>
    private static bool TryResolveRawKeyJoinProperties(
        IEntityType fkOwnerEntityType,
        IEntityType innerEntityType,
        string? fkPropertyName,
        LambdaExpression innerKeySelector,
        [NotNullWhen(true)] out IProperty? outerProperty,
        [NotNullWhen(true)] out IProperty? innerProperty)
        => TryResolveRawKeyJoinProperties(
            fkOwnerEntityType, innerEntityType, fkPropertyName, innerKeySelector.Body.TryGetSimplePropertyName(),
            out outerProperty, out innerProperty);

    private static bool TryResolveRawKeyJoinProperties(
        IEntityType fkOwnerEntityType,
        IEntityType innerEntityType,
        string? fkPropertyName,
        string? innerKeyPropertyName,
        [NotNullWhen(true)] out IProperty? outerProperty,
        [NotNullWhen(true)] out IProperty? innerProperty)
    {
        outerProperty = fkPropertyName != null ? fkOwnerEntityType.FindProperty(fkPropertyName) : null;
        innerProperty = innerKeyPropertyName != null ? innerEntityType.FindProperty(innerKeyPropertyName) : null;
        return outerProperty != null && innerProperty != null;
    }

    /// <summary>
    /// Recognizes an anonymous-type join key of simple properties. Both sides construct the same anonymous type, so members
    /// pair by position (<c>new { a.X, Y = a.Z } equals new { b.X, Y = b.W }</c>): each is a property read (<c>x.P</c> or
    /// <c>EF.Property(x, "P")</c>), optionally lifted to its nullable type; each inner member reads directly off the inner
    /// parameter. Hop, scalar-ness and storage are checked by <see cref="TryResolveAnonymousKeyJoinProperties"/>.
    /// </summary>
    /// <remarks>
    /// Anything else declines: constant or computed members, nested anonymous types, non-anonymous constructions (they compare
    /// by the type's own <c>Equals</c>), and narrowing or representation-changing conversions.
    /// </remarks>
    private static bool TryGetAnonymousJoinKeyMembers(
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        out IReadOnlyList<Expression> outerMembers,
        out IReadOnlyList<string> innerPropertyNames)
    {
        outerMembers = [];
        innerPropertyNames = [];

        if (outerKeySelector.Body is not NewExpression { Arguments.Count: > 0 } outerNew
            || innerKeySelector.Body is not NewExpression innerNew
            || outerNew.Type != innerNew.Type
            || !IsAnonymousType(outerNew.Type)
            || innerNew.Arguments.Count != outerNew.Arguments.Count)
        {
            return false;
        }

        var outer = new List<Expression>(outerNew.Arguments.Count);
        var inner = new List<string>(innerNew.Arguments.Count);
        for (var i = 0; i < outerNew.Arguments.Count; i++)
        {
            var outerMember = StripNullableLift(outerNew.Arguments[i]);
            var innerMember = StripNullableLift(innerNew.Arguments[i]);
            if (outerMember is null
                || innerMember is null
                || outerMember.TryGetSimplePropertyName() == null
                || GetKeySelectorTargetObject(innerMember)?.RemoveConvert() is not { } innerTarget
                || !ReferenceEquals(innerTarget, innerKeySelector.Parameters[0]))
            {
                return false;
            }

            outer.Add(outerMember);
            inner.Add(innerMember.TryGetSimplePropertyName()!);
        }

        outerMembers = outer;
        innerPropertyNames = inner;
        return true;

        // `(int?)x.P` over an `int` P preserves equality; any other conversion (narrowing, enum-to-int, boxing) may
        // not, so it declines. A member that's already a bare read passes through.
        static Expression? StripNullableLift(Expression member)
            => member switch
            {
                UnaryExpression { NodeType: ExpressionType.Convert, Operand: var operand } convert
                    when Nullable.GetUnderlyingType(convert.Type) == operand.Type => operand,
                UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } => null,
                _ => member
            };

        static bool IsAnonymousType(Type type)
            => type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
                && type.Name.Contains("AnonymousType", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the entity type a join key selector's outer side reads off: the root when <paramref name="isDirectFromRoot"/>,
    /// else the inner side of the prior join at <paramref name="throughLevel"/> (1-based), returned as
    /// <paramref name="throughJoin"/>. Declines any other hop.
    /// </summary>
    private static bool TryResolveKeyHopAnchor(
        MongoQueryExpression outerQueryExpression,
        bool isDirectFromRoot,
        int? throughLevel,
        int priorJoinCount,
        [NotNullWhen(true)] out IEntityType? anchorEntityType,
        out JoinInfo? throughJoin)
    {
        throughJoin = null;

        if (isDirectFromRoot)
        {
            anchorEntityType = outerQueryExpression.CollectionExpression.EntityType;
            return true;
        }

        if (throughLevel is { } level && level >= 1 && level <= priorJoinCount)
        {
            throughJoin = outerQueryExpression.Joins[level - 1];
            anchorEntityType = throughJoin.InnerEntityType;
            return true;
        }

        anchorEntityType = null;
        return false;
    }

    /// <summary>
    /// Builds a navigation-less raw-key <c>$lookup</c> (EF-377/EF-436) for <paramref name="joinInfo"/> over the given
    /// outer/inner key-property pairs, forced to unwind and preserving unmatched rows for a left-outer join. One pair keeps
    /// the localField/foreignField form; more render as <c>let</c> + <c>$and</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="LookupExpression.GetFieldPath"/>, not a bare <c>GetElementName()</c>: a composite primary key component is
    /// stored under <c>_id</c> (<c>"_id.ProductId"</c>), so the bare name matches nothing in every mode (pinned by
    /// <c>NativeCompositeKeyJoinTests</c>). A through-hop's local fields are scoped by its alias.
    /// </remarks>
    private static LookupExpression BuildRawKeyJoinLookup(
        IEntityType innerEntityType,
        IReadOnlyList<(IProperty Outer, IProperty Inner)> pairs,
        JoinInfo? throughJoin,
        JoinInfo joinInfo)
    {
        var keyPairs = pairs
            .Select(p =>
            {
                var outerFieldPath = LookupExpression.GetFieldPath(p.Outer);
                return new LookupKeyPair(
                    throughJoin != null ? $"{throughJoin.Alias}.{outerFieldPath}" : outerFieldPath,
                    LookupExpression.GetFieldPath(p.Inner),
                    p.Outer.IsNullable || p.Inner.IsNullable);
            })
            .ToList();

        return keyPairs.Count == 1
            ? new LookupExpression(
                innerEntityType, innerEntityType.GetCollectionName(), keyPairs[0].LocalField, keyPairs[0].ForeignField,
                joinInfo.Alias, forceUnwind: true)
            {
                PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
            }
            : new LookupExpression(
                innerEntityType, innerEntityType.GetCollectionName(), keyPairs, joinInfo.Alias, forceUnwind: true)
            {
                PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
            };
    }

    /// <summary>
    /// Resolves an anonymous join key (<see cref="TryGetAnonymousJoinKeyMembers"/>) to its property pairs: every outer member
    /// must read off the same hop (the root, or one prior join's inner side; no owned-navigation segment) and name a mapped
    /// property. Each pair must also compare in the database as in C# (<see cref="IsStoredEqualityFaithfulKeyPair"/>), else the
    /// <c>$lookup</c> silently answers differently.
    /// </summary>
    private static bool TryResolveAnonymousKeyJoinProperties(
        MongoQueryExpression outerQueryExpression,
        IEntityType innerEntityType,
        LambdaExpression outerKeySelector,
        IReadOnlyList<Expression> outerMembers,
        IReadOnlyList<string> innerPropertyNames,
        int priorJoinCount,
        out JoinInfo? throughJoin,
        [NotNullWhen(true)] out List<(IProperty Outer, IProperty Inner)>? pairs)
    {
        throughJoin = null;
        pairs = null;

        (bool IsDirectFromRoot, int? ThroughLevel)? hop = null;
        foreach (var member in outerMembers)
        {
            // RemoveConvert: EF.Property(x, "P") takes its entity as `(object)x`.
            var (target, embeddedSegments) = PeelEmbeddedSegments(
                GetKeySelectorTargetObject(member)?.RemoveConvert(), outerKeySelector.Parameters[0]);
            if (embeddedSegments.Count > 0)
            {
                return false;
            }

            var memberHop = AnalyzeKeySelectorTarget(target, outerKeySelector.Parameters[0], priorJoinCount);
            if (hop != null && hop != memberHop)
            {
                return false;
            }

            hop = memberHop;
        }

        if (!TryResolveKeyHopAnchor(
                outerQueryExpression, hop!.Value.IsDirectFromRoot, hop.Value.ThroughLevel, priorJoinCount,
                out var fkOwnerEntityType, out throughJoin))
        {
            return false;
        }

        var resolved = new List<(IProperty Outer, IProperty Inner)>(outerMembers.Count);
        for (var i = 0; i < outerMembers.Count; i++)
        {
            if (!TryResolveRawKeyJoinProperties(
                    fkOwnerEntityType, innerEntityType, outerMembers[i].TryGetSimplePropertyName(),
                    innerPropertyNames[i], out var outerProperty, out var innerProperty)
                || !IsStoredEqualityFaithfulKeyPair(outerProperty, innerProperty))
            {
                return false;
            }

            resolved.Add((outerProperty, innerProperty));
        }

        pairs = resolved;
        return true;
    }

    /// <summary>
    /// Whether stored-value equality of an anonymous-key pair agrees with the member-wise C# equality.
    /// </summary>
    /// <remarks>
    /// Only scalar members (value types and <see cref="string"/>): a primitive collection would match by element overlap, and
    /// other reference types (<c>byte[]</c>, lists) compare by reference in C#. Both sides must be stored alike
    /// (<see cref="StoredSerialization.StoredAlike"/>, shared with the projected set op): an enum stored as a string on one side
    /// and an int on the other never matches, nor do <c>BoolToStringConverter</c>s with different constants. Stricter than the
    /// single-key path, which isn't gated.
    /// </remarks>
    private static bool IsStoredEqualityFaithfulKeyPair(IProperty outerProperty, IProperty innerProperty)
    {
        return IsScalarKeyType(outerProperty.ClrType)
               && IsScalarKeyType(innerProperty.ClrType)
               && !outerProperty.IsPrimitiveCollection
               && !innerProperty.IsPrimitiveCollection
               && StoredSerialization.StoredAlike(outerProperty, innerProperty);

        static bool IsScalarKeyType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsValueType || underlying == typeof(string);
        }
    }

    /// <summary>
    /// Migrates the inner entity's projection onto the outer <see cref="MongoQueryExpression"/> and registers the
    /// <c>$lookup</c>(s) that stand in for the join.
    /// </summary>
    /// <param name="innerShaper">The inner entity's shaper.</param>
    /// <param name="innerQueryExpression">The inner query.</param>
    /// <param name="outerQueryExpression">The outer query.</param>
    /// <param name="outerKeySelector">The outer key selector.</param>
    /// <param name="innerKeySelector">The inner key selector.</param>
    /// <param name="joinInfo">This join's <see cref="JoinInfo"/>, so a later hop can find it by position (see <see cref="AnalyzeKeySelectorTarget"/>).</param>
    /// <returns>
    /// The rebound shaper, or <see langword="null"/> (nothing registered) for a transitive hop whose intermediate sub-document
    /// can't be identified. Null rather than a flag so a caller can't use an un-rebound shaper.
    /// </returns>
    private static Expression? RebindInnerShaperToOuterQuery(
        Expression innerShaper,
        MongoQueryExpression innerQueryExpression,
        MongoQueryExpression outerQueryExpression,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        JoinInfo joinInfo)
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
        var innerEntityType = innerEntityProjection.EntityType;
        var outerEntityType = outerQueryExpression.CollectionExpression.EntityType;
        var fkPropertyName = outerKeySelector.Body.TryGetSimplePropertyName();

        // joinInfo was already appended to Joins (by AddJoin, in TranslateJoinCore), so exclude it to get
        // the count of PRIOR joins only.
        var priorJoinCount = outerQueryExpression.Joins.Count - 1;

        // The outer key selector's FK-access target says whether this join reads off the root entity (a pure ".Outer"* chain)
        // or a previously-joined intermediate (a chain ending in ".Inner", e.g. the second ".Manager" in
        // Employee.Manager.Manager). Structural, not by entity type, since self-referencing chains share target and through
        // types. Owned navigation segments (Buyer.Address.RegionId, EF-380) are peeled first so the walk sees a pure chain.
        var (outerInnerTarget, embeddedSegments) = PeelEmbeddedSegments(
            GetKeySelectorTargetObject(outerKeySelector.Body), outerKeySelector.Parameters[0]);
        var (isDirectFromRoot, throughLevel) = AnalyzeKeySelectorTarget(
            outerInnerTarget, outerKeySelector.Parameters[0], priorJoinCount);

        INavigation? navigation = null;
        JoinInfo? throughJoin = null;
        string? embeddedPath = null;

        // A transitive join resolves the navigation on the join hop the key selector actually reaches through (found by
        // position, not IEntityType) and remembers it so the $lookup's localField can be prefixed with that intermediate's alias.
        if (TryResolveKeyHopAnchor(
                outerQueryExpression, isDirectFromRoot, throughLevel, priorJoinCount,
                out var anchorEntityType, out throughJoin))
        {
            // Walk embedded segments via the navigation graph (not CLR type) so sibling owned navigations sharing a CLR type
            // (ShippingAddress/BillingAddress) resolve to the right one, using each segment's mapped element name for the path.
            var searchEntityType = anchorEntityType;
            if (embeddedSegments.Count > 0)
            {
                var elementSegments = new List<string>();
                foreach (var segment in embeddedSegments)
                {
                    if (searchEntityType.FindNavigation(segment) is not { } segmentNavigation)
                    {
                        // Unresolvable embedded path: search the anchor itself, as if there were no segments.
                        searchEntityType = anchorEntityType;
                        elementSegments = null;
                        break;
                    }

                    searchEntityType = segmentNavigation.TargetEntityType;
                    elementSegments.Add(searchEntityType.GetContainingElementName() ?? segment);
                }

                if (elementSegments is { Count: > 0 })
                {
                    embeddedPath = string.Join(".", elementSegments);
                }
            }

            if (fkPropertyName != null)
            {
                // IsOnDependent disambiguates a self-referencing relationship declared with both a reference nav (Manager) and its
                // inverse collection nav (DirectReports): both share the same IForeignKey, and picking the collection nav flips the
                // $lookup's join direction (LookupExpression branches on IsOnDependent).
                navigation = searchEntityType.GetNavigations()
                    .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                         && n.IsOnDependent
                                         && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
            }

            navigation ??= searchEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == innerEntityType);
        }

        // A navigation resolved above (by FK match or the loose "any navigation onto the joined type" fallback) is usable only
        // if its $lookup reproduces the key equality the user WROTE. For a join on other properties between navigation-related
        // types (`Owners.Join(Orders, o => o.Region, r => r.Region, ...)`), or a principal-key-to-FK join whose model only has
        // the navigation pointing the OTHER way (EF Core's No_orderby_added_for_fully_translated_manually_constructed_LOJ), the
        // navigation's $lookup joins on different fields. Discard it and build a navigation-less raw-key $lookup (EF-377 branch
        // below), but only when both key selectors are simple properties that branch can resolve; anything else keeps the
        // navigation, and JoinLookupImplementsKeySelectors still declines it natively.
        if (navigation != null
            && !LookupImplementsKeySelectors(
                BuildNavigationJoinLookup(navigation, alias: "", joinInfo.IsLeftOuter, throughJoin, embeddedPath),
                navigation, innerEntityType, outerKeySelector, innerKeySelector)
            && TryResolveRawKeyJoinProperties(
                throughJoin?.InnerEntityType ?? outerEntityType, innerEntityType, fkPropertyName, innerKeySelector,
                out _, out _))
        {
            navigation = null;
        }

        // Document-shape decision (single source of truth): the driver's native LeftJoin ({ _outer, _inner }) is viable only for
        // a SINGLE reference join; a second cross-collection join flattens to root-level $lookup + $unwind fields
        // ("_lookup_<Navigation>"). Rather than toggle a flag, register the forced-unwind lookups and let
        // MongoQueryExpression.UsesDriverJoinFields derive from that state, so it never contradicts the emitted pipeline.
        // Each cross-collection projection carries its OWNING navigation and a stable alias; the shaper derives the field it
        // reads from that plus UsesDriverJoinFields, so the projection is never retroactively rewritten.
        // An anonymous-type key (EF-436) never resolves a navigation (GetKeySelectorTargetObject of a `new` is null) and is built
        // here as a navigation-less raw-key $lookup over every member pair.
        List<(IProperty Outer, IProperty Inner)>? anonymousKeyPairs = null;
        JoinInfo? anonymousKeyThroughJoin = null;
        if (navigation == null
            && TryGetAnonymousJoinKeyMembers(outerKeySelector, innerKeySelector, out var outerMembers, out var innerNames)
            && TryResolveAnonymousKeyJoinProperties(
                outerQueryExpression, innerEntityType, outerKeySelector, outerMembers, innerNames, priorJoinCount,
                out anonymousKeyThroughJoin, out var resolvedPairs))
        {
            anonymousKeyPairs = resolvedPairs;
        }

        joinInfo.Navigation = navigation;
        joinInfo.Alias = UniquifyLookupAlias(
            navigation != null
                ? Expressions.LookupExpression.GetLookupAlias(navigation)
                : $"_lookup_{innerEntityType.ShortName()}",
            outerQueryExpression,
            joinInfo);

        if (navigation != null)
        {
            joinInfo.Lookup = BuildNavigationJoinLookup(
                navigation, joinInfo.Alias, joinInfo.IsLeftOuter, throughJoin, embeddedPath);
        }
        else if (anonymousKeyPairs != null)
        {
            joinInfo.Lookup = BuildRawKeyJoinLookup(innerEntityType, anonymousKeyPairs, anonymousKeyThroughJoin, joinInfo);
        }
        else if (fkPropertyName != null)
        {
            // Bare key-equality Join hop with no model navigation (EF-377): build the $lookup from the raw outer/inner key property
            // paths. The FK-owning type is the root when isDirectFromRoot, else the through-hop's target.
            if (TryResolveRawKeyJoinProperties(
                    throughJoin?.InnerEntityType ?? outerEntityType, innerEntityType, fkPropertyName, innerKeySelector,
                    out var outerProperty, out var innerProperty))
            {
                joinInfo.Lookup = BuildRawKeyJoinLookup(
                    innerEntityType, [(outerProperty, innerProperty)], throughJoin, joinInfo);
            }
        }

        // The trigger counts JOINS, not distinct target entity types: two joins onto the same type must flatten just like two
        // onto different ones (EF-375), and a chained hop through a prior join always needs its own field too.
        var isSecondOrLaterJoin = outerQueryExpression.Joins.Count > 1;
        if (isSecondOrLaterJoin)
        {
            // Register this join's $lookup, then every prior join's: each carries its own lookup built from what it resolved when IT
            // was processed, so same-typed siblings, self-referencing chains and navigation-less hops (EF-377) are never confused.
            // AddLookup dedupes by alias, so re-adding a registered prior lookup is a no-op.
            if (joinInfo.Lookup != null)
            {
                outerQueryExpression.AddLookup(joinInfo.Lookup);
            }

            foreach (var priorJoin in outerQueryExpression.Joins)
            {
                if (priorJoin.Lookup != null && !ReferenceEquals(priorJoin, joinInfo))
                {
                    outerQueryExpression.AddLookup(priorJoin.Lookup);
                }
            }
        }

        // For the lone driver-native reference the shaper maps this alias to "_inner"; in flat mode it
        // reads this "_lookup_<Navigation>" field directly.
        var lookupAlias = joinInfo.Alias;

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

    /// <summary>
    /// The object an outer key selector's body reads a property FROM (the "x" in <c>x.Foo</c> or <c>EF.Property(x, "Foo")</c>).
    /// </summary>
    private static Expression? GetKeySelectorTargetObject(Expression body)
        => body.RemoveConvert() switch
        {
            MemberExpression member => member.Expression,
            MethodCallExpression call when call.Method.IsEFPropertyMethod() && call.Arguments.Count == 2 => call.Arguments[0],
            _ => null
        };

    /// <summary>
    /// Strips leading member/<c>EF.Property</c> hops through owned types (e.g. "Address" in <c>x.Inner.Address.RegionId</c>)
    /// that aren't transparent-identifier plumbing. Returns the remaining target and the stripped segment names, root to leaf.
    /// </summary>
    private static (Expression? RemainingTarget, List<string> EmbeddedSegments) PeelEmbeddedSegments(
        Expression? targetObject, ParameterExpression parameter)
    {
        var embeddedSegments = new List<string>();
        var current = targetObject;
        while (current != null && !ReferenceEquals(current, parameter))
        {
            var step = current.RemoveConvert();
            if ((step is MemberExpression member && member.IsTransparentIdentifierOuterOrInnerAccess())
                || !step.TryGetMemberOrEFProperty(out var next, out var name))
            {
                // Reached the Outer/Inner plumbing or an unrecognized shape; AnalyzeKeySelectorTarget takes it.
                break;
            }

            embeddedSegments.Insert(0, name);
            current = next;
        }

        return (current, embeddedSegments);
    }

    /// <summary>
    /// Walks the <c>.Outer</c>/<c>.Inner</c> chain from <paramref name="targetObject"/> to <paramref name="parameter"/> to tell
    /// whether the selector reads the root entity (a pure <c>.Outer</c>* chain, or the bare parameter) or a prior hop's result
    /// (a chain ending in <c>.Inner</c>, resolved to that step's join, 1-based, oldest first).
    /// </summary>
    private static (bool IsDirectFromRoot, int? ThroughLevel) AnalyzeKeySelectorTarget(
        Expression? targetObject, ParameterExpression parameter, int priorJoinCount)
    {
        if (targetObject == null)
        {
            return (false, null);
        }

        var steps = new List<string>();
        var cur = targetObject;
        while (cur is MemberExpression step && step.IsTransparentIdentifierOuterOrInnerAccess())
        {
            steps.Add(step.Member.Name);
            cur = step.Expression!;
        }

        if (!ReferenceEquals(cur, parameter))
        {
            // Not a recognizable Outer/Inner chain rooted at our parameter — only treat as direct when
            // the target object IS the parameter itself (no chain at all).
            return (ReferenceEquals(targetObject, parameter), null);
        }

        steps.Reverse();
        var level = priorJoinCount;
        foreach (var step in steps)
        {
            if (step == "Outer")
            {
                level--;
            }
            else
            {
                return (false, level);
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Disambiguates a join's <c>_lookup_&lt;Navigation&gt;</c> alias against registered joins: two joins can resolve the same
    /// navigation (a LINQ cross product), so each needs its own output field; the first keeps the unsuffixed name.
    /// </summary>
    private static string UniquifyLookupAlias(
        string baseAlias, MongoQueryExpression queryExpression, JoinInfo joinInfo)
    {
        var alias = baseAlias;
        var suffix = 0;
        while (queryExpression.Joins.Any(j => !ReferenceEquals(j, joinInfo) && j.Alias == alias))
        {
            alias = $"{baseAlias}_{++suffix}";
        }

        return alias;
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

    // Native whole-entity or projected Union/Concat/Intersect/Except, appended to source1's select. Union/Concat always
    // return source1: native when admissible, otherwise marked non-native for a graceful fallback. Intersect/Except return
    // null when inadmissible (see below).
    private ShapedQueryExpression? TryTranslateSetOperation(
        ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
    {
        var mongo1 = (MongoQueryExpression)source1.QueryExpression;
        var mongo2 = (MongoQueryExpression)source2.QueryExpression;

        if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            AppendSetOperationLink(mongo1, mongo2, kind);
            return source1;
        }

        // Nested whole-entity Concat/Union, on either side:
        //   Left-nested (A.Concat(B).Concat(C)): append another link to source1's chain. Each Union link's dedup is emitted right
        //     after its own $unionWith, which makes Concat(Union(A,B),C) correct.
        //   Right-nested (A.Concat(B.Union(C))): can't be flattened, since A.Concat(B).Union(C) would also dedup A. The operand
        //     keeps its own chain, emitted inside the outer $unionWith's pipeline.
        // These compose to any depth (IsWholeEntitySetOpOperandSelect recurses). Intersect/Except are excluded: they lower to
        // MongoSetDifferenceStage and have no driver-LINQ oracle, so they keep hard-failing below.
        if (kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
            && (IsPlainWholeEntitySelect(mongo1) || IsWholeEntitySetOpChainSelect(mongo1))
            && (IsPlainWholeEntitySelect(mongo2) || IsWholeEntitySetOpChainSelect(mongo2))
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            // IsSetOp is already true when mongo1 is itself a chain; setting it is needed when it is plain.
            AppendSetOperationLink(mongo1, mongo2, kind);
            return source1;
        }

        // Projected operands: each side may be a plain projected Select, a projected Distinct, or a GroupBy(key).Select(aggregate);
        // the combine compares flattened values by alias. ProjectionShapesMatch is a correctness guard (dedup/source-tagging
        // compare whole projected documents), and OperandSerializationsMatch requires each alias to be stored the same way on
        // both sides.
        //
        // source1's own pending lookups are admitted because the lowerer emits them ahead of source1's $project; source2's
        // operand select carries no lookup plumbing, so it must have none.
        //
        // source1's shaper is reused for every combined row, so a constant/parameter leaf baked into it would show source1's
        // value on source2's rows (HasShaperUnsafeConstantLeaf rejects that; the server pipeline is fine either way). Exception:
        // a bare `$literal`-rendered constant/parameter of a round-trip-safe type (CanRebindConstantLeafToDocument) has its shaper
        // rebound to read the value per document, after every check has passed.
        //
        // Bare scalar operands may differ in alias (a column by document path e.g. `_id`; a computed value, constant or grouped
        // aggregate by `_v`). CanAlignBareScalarAliases admits those; only then is mongo2's single projection re-aliased to
        // mongo1's, after every check has passed.
        var rebindConstantLeaf = false;
        var alignBareScalarAliases = false;
        if ((IsPlainProjectedSelect(mongo1, allowPreCombineLookups: true) || IsPlainDistinctSelect(mongo1, allowPreCombineLookups: true)
                || IsPlainGroupBySelect(mongo1, allowPreCombineLookups: true))
            && (!HasShaperUnsafeConstantLeaf(mongo1)
                || (rebindConstantLeaf = CanRebindConstantLeafToDocument(source1, mongo1)))
            && (IsPlainProjectedSelect(mongo2) || IsPlainDistinctSelect(mongo2) || IsPlainGroupBySelect(mongo2))
            && ((ProjectionShapesMatch(mongo1.Select.Projection, mongo2.Select.Projection)
                 && OperandSerializationsMatch(mongo1.Select, mongo2.Select))
                // Aligned bare scalars already require default serialization on both sides (ReadsOnlyDefaultSerializedFields).
                || (alignBareScalarAliases = CanAlignBareScalarAliases(source1, mongo1, source2, mongo2))))
        {
            if (rebindConstantLeaf)
            {
                // Admitted through CanRebindConstantLeafToDocument. Read the `$literal`-projected value by alias, so
                // source2's rows read their own value instead of source1's baked-in one.
                source1 = source1.UpdateShaperExpression(
                    BindProjectionByAlias(mongo1, mongo1.Select.Projection[0].Alias, source1.ShaperExpression));
            }

            if (alignBareScalarAliases)
            {
                // Admitted through CanAlignBareScalarAliases. Combined rows are read through source1's shaper (source1's alias); the
                // kind-shape check (NativeDateTimeKindReadBack.HasKindMismatch, at compile time) pairs operand leaves by alias, so it
                // now sees this pair too.
                mongo2.Select.ReplaceSingleProjectionAlias(mongo1.Select.Projection[0].Alias);
            }

            AppendSetOperationLink(mongo1, mongo2, kind, operandsProjected: true);
            return source1;
        }

        // Out of scope. Union/Concat fall back gracefully. Intersect/Except have no driver-LINQ fallback, so
        // return null to hard-fail cleanly in every mode.
        if (kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
        {
            return null;
        }

        mongo1.Select.MarkNotNativelyRepresentable();
        return source1;
    }

    // Appends mongo2 as the next set-op link of mongo1's select and marks mongo1 a set op.
    private static void AppendSetOperationLink(
        MongoQueryExpression mongo1, MongoQueryExpression mongo2, MongoSetOperationKind kind, bool operandsProjected = false)
    {
        mongo1.Select.AppendSetOperation(new MongoSetOperation(
            kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, mongo2.CollectionExpression.EntityType,
            operandsProjected));
        mongo1.Select.IsSetOp = true;
    }

    // Whether the select is already a whole-entity Concat/Union nesting, so it can take another link or serve as an operand.
    // IsWholeEntitySetOpOperandSelect adds the recursive structural checks; the rest mirrors IsPlainWholeEntitySelect.
    private static bool IsWholeEntitySetOpChainSelect(MongoQueryExpression mongo)
        => mongo.Select.IsSetOpTerminalOnly
           && mongo.Select.SetOperations.Count > 0
           && IsWholeEntitySetOpOperandSelect(mongo.Select)
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !mongo.Select.HasClientWrappedWholeEntityShaper
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // Recursive: a whole-entity Concat/Union tree the lowerer can emit as a self-contained sub-pipeline; a plain whole-entity
    // select is the base case. Each conjunct rejects something operand lowering doesn't emit, which would be silently dropped:
    // no $project/$group/$unwind for a whole-entity operand (a whole-entity Distinct is a MongoDistinctOp in PipelineOps, so
    // fine), and projected or Intersect/Except links lower differently.
    // TrailingOps may be non-empty: ops between two links (A.Union(B).OrderBy(..).Take(1).Union(C)) become the next link's
    // PrecedingOps (MongoSelectDefinition.AppendSetOperation) and are emitted before its stage.
    private static bool IsWholeEntitySetOpOperandSelect(MongoSelectDefinition select)
        => select.Projection.Count == 0
           && select.Grouping == null
           && select.Cardinality == null
           && select.UnwindSources.Count == 0
           && select.SetOperations.All(
               link => !link.OperandsProjected
                       && link.Kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
                       && IsWholeEntitySetOpOperandSelect(link.OperandSelect));

    // Filter/sort/paging only: no projection, grouping, scalar cardinality, own set op, SelectMany unwind, lookups, or
    // VectorSearch. UnwindSource: a whole-element owned SelectMany has Route == WholeEntity and no lookups, but operand
    // lowering emits no $unwind for mongo2 and source1's $unwind (plus its inner-element filter) would run after the
    // $unionWith over both sides, silently returning wrong rows. Pinned by
    // NativeSelectManyTests.Whole_owned_element_SelectMany_as_set_op_operand_does_not_go_native.
    private static bool IsPlainWholeEntitySelect(MongoQueryExpression mongo)
        => mongo.Select.Route == NativeRoute.WholeEntity
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.Projection.Count == 0
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !mongo.Select.HasClientWrappedWholeEntityShaper
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // The projected analogue of IsPlainWholeEntitySelect: a terminal anonymous/DTO Select is the only thing done.
    //
    // HasArrayProjectionLeaf: an owned-collection array leaf (Select(b => new { b.Title, b.Posts })) makes NativeProjectionBinder
    // emit the owner key into the projected document. Projected-operand dedup ($group{_id:"$$ROOT"}) and source-tagging compare
    // the whole document, so that key would turn value dedup into identity dedup (pinned by
    // Projected_operand_union_dedups_over_projected_values_not_whole_entities). Declining lets Union/Concat fall back;
    // Intersect/Except (no oracle) hard-fail. Bare projected operands are admitted (pinned by NativeBareProjectionTests).
    //
    // allowPreCombineLookups is passed only for source1, whose InjectAfterRoot projected-Count lookups (and a confirmed
    // single-level join's $lookup, IsPreCombineJoinScope) the lowerer emits ahead of its $project. Other lookups decline.
    private static bool IsPlainProjectedSelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => mongo.Select.Route == NativeRoute.Projection
           && mongo.Select.Projection.Count > 0
           && !mongo.Select.HasArrayProjectionLeaf
           // The raw case-mapping receiver would be what the combine dedups/compares.
           && !mongo.Select.HasClientCaseMappingProjectionLeaf
           // A client-evaluated leaf is never projected: the combine would dedup without it, and source1's shaper
           // would show source1's value on source2's rows.
           && !mongo.Select.HasClientEvaluatedProjectionLeaf
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && (allowPreCombineLookups
               ? (!mongo.IsJoinQuery || IsPreCombineJoinScope(mongo))
                 && mongo.Lookups.All(l => l.InjectAfterRoot || mongo.Joins.Any(j => ReferenceEquals(j.Lookup, l)))
               : !mongo.IsJoinQuery && mongo.Lookups.Count == 0)
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // A projected source1 over a single-level join scope whose confirming Select registered the join's $lookup.
    // HasConfirmedJoinLookup is set on a projected, ungrouped, unreduced select only by TranslateSelect's join-scope arms, each
    // gated by TryAdmitSingleEligibleNativeJoinScope, so this reuses that decision rather than re-running it (it may defer
    // PipelineOps). MongoSelectLowerer's OperandsProjected branch emits the join like the non-set-op path: $lookup/$unwind,
    // then PostJoinOps and PostLookupPagingOps, all ahead of source1's $project. The gate and that emission must move
    // together, else the inner-side $match is silently dropped.
    // Single-level only: a join chain is unvalidated here. The HasConfirmedJoinLookup conjunct is defence in depth (an
    // unconfirmed candidate already makes Route Fallback). Serves every set-op kind, Intersect/Except included
    // (NativeGroupByOverJoinTests.Intersect_/Except_of_a_join_scope_operand_...).
    private static bool IsPreCombineJoinScope(MongoQueryExpression mongo)
        => mongo.Select.HasConfirmedJoinLookup
           && mongo.Select.JoinScope is { Levels.Count: 1 };

    // A projected Distinct (Select(new {...}).Distinct(), Route == GroupBy) as a set-op operand; its $group + flattening
    // $project become its pre-combine pipeline. IsDistinct and IsGroupBy are mutually exclusive (a real GroupBy is
    // IsPlainGroupBySelect); PriorGrouping excludes a GroupBy nested on the Distinct.
    private static bool IsPlainDistinctSelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsDistinct
           && !mongo.Select.IsGroupBy
           // Same reason as IsPlainProjectedSelect: the Distinct keeps the flag of the Select it deduplicates.
           && !mongo.Select.HasClientEvaluatedProjectionLeaf;

    // A real GroupBy(key).Select(aggregate) as a set-op operand. The join wrong-data hazard of grouped sources doesn't apply
    // to Union/Concat, and the lowerer emits a Grouping-bearing operand's $group + $project the same way for Distinct and
    // GroupBy (AppendSetOpChainStages).
    private static bool IsPlainGroupBySelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsGroupBy
           // Operand lowering emits only the operand's $group + flattening $project, so post-group Skip/Take, HAVING, or OrderBy
           // would be silently dropped; such operands fall through to the out-of-scope decline.
           && mongo.Select.GroupPagingOps.Count == 0
           && mongo.Select.GroupHavingPredicate == null
           && mongo.Select.GroupOrderOp == null;

    // The conjuncts IsPlainDistinctSelect and IsPlainGroupBySelect share: a Grouping-bearing GroupBy-route select with no
    // prior grouping, scalar cardinality, SelectMany unwind, join or VectorSearch, and no lookups beyond (source1 only)
    // InjectAfterRoot ones.
    private static bool IsPlainGroupedOperandSelect(MongoQueryExpression mongo, bool allowPreCombineLookups)
        => mongo.Select.Route == NativeRoute.GroupBy
           && mongo.Select.Grouping != null
           && mongo.Select.PriorGrouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && (allowPreCombineLookups ? mongo.Lookups.All(l => l.InjectAfterRoot) : mongo.Lookups.Count == 0)
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // mongo1's shaper is reused for every combined row, so a constant/parameter leaf (never read from the document) would
    // repeat mongo1's value. Only unsafe in that context (standalone it is fine: NativeComputedBareProjectionTests); the one
    // exception, a bare round-trip-safe leaf on an ungrouped select, is CanRebindConstantLeafToDocument.
    // A projected Distinct moves the projected values into its key parts but leaves the shaper as built, constant baked in, so
    // its key parts are checked too. Scoped to IsDistinct: a real GroupBy's constant key is read back from `_id`.
    private static bool HasShaperUnsafeConstantLeaf(MongoQueryExpression mongo)
        => mongo.Select.Projection.Any(p => IsConstantOrParameterLeaf(p.Expression))
           || mongo.Select is { IsDistinct: true, Grouping: { } grouping }
           && grouping.Key.Any(k => IsConstantOrParameterLeaf(k.FieldRef));

    private static bool IsConstantOrParameterLeaf(MongoExpression expression)
        => expression is MongoConstantExpression or MongoParameterExpression;

    // Whether source1's constant/parameter leaf can be read from the document instead of the shaper: a bare scalar whose single
    // projection is a server-rendered (`$literal`) constant or parameter of a type whose BSON round-trip returns the C# value.
    // The gate and the shaper rebind in TryTranslateSetOperation both use this (internal for
    // NativeSetOperationConstantLeafRebindTests).
    // - Bare scalar only: a construction's members are read by name from its own shaper.
    // - Constant/parameter leaf, trial-rendered by TryProbeBareValueRenders: never a client-evaluated one (defence in depth).
    // - Round-trip-safe CLR type: a DateTime reads back UTC-kind, a Guid depends on the representation, an enum on its
    //   storage type; those decline.
    // - Ungrouped select only: the rebind binds a plain projection member, which a grouped shaper doesn't read (it throws
    //   "Document element '_v0' is missing"). A projected Distinct is grouped too.
    // - Nothing already bound under the leaf's alias in the read-side projection list: the rebind goes through AddToProjection,
    //   which renames a colliding alias (`_v` -> `_v0`), a field the pipeline never emits. Overlaps the ungrouped conjunct, so
    //   neither is killable by mutation alone (removing both fails
    //   Union_of_grouped_constant_result_and_column_declines and Grouped_constant_result_does_not_rebind).
    internal static bool CanRebindConstantLeafToDocument(ShapedQueryExpression source1, MongoQueryExpression mongo1)
        => IsBareScalarOperand(source1, mongo1)
           && mongo1.Select.Grouping == null
           && !mongo1.Projection.Any(
               pe => string.Equals(pe.Alias, mongo1.Select.Projection[0].Alias, StringComparison.OrdinalIgnoreCase))
           && mongo1.Select.Projection[0].Expression is MongoConstantExpression or MongoParameterExpression
           && NativeSlotPopulator.TryProbeBareValueRenders(mongo1.Select.Projection[0].Expression, source1.ShaperExpression.Type)
           && IsRoundTripSafeConstantLeafType(source1.ShaperExpression.Type);

    private static bool IsRoundTripSafeConstantLeafType(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(bool)
           || type == typeof(string);

    // A bare scalar operand (Select(x => x.Id), GroupBy(..).Select(g => g.Count())): a single projected value whose shaper
    // isn't a construction. Detected by shape; IsBareProjection isn't set for a grouped g => g.Count().
    private static bool IsBareScalarOperand(ShapedQueryExpression source, MongoQueryExpression mongo)
        => mongo.Select.Projection is [{ Expression: not MongoDocumentConstructionExpression }]
           && source.ShaperExpression.RemoveConvert() is not (NewExpression or MemberInitExpression);

    // Whether two bare scalar operands with different aliases can be combined by re-aliasing source2's single projection to
    // source1's. The gate and the rewrite in TryTranslateSetOperation both use this (internal for
    // NativeSetOperationAliasAlignmentTests).
    // - Only a bare scalar: a construction's members are read by name, so its aliases must match (ProjectionShapesMatch).
    // - No post-group ops on source2: they read its own flattened alias, which the re-alias would orphan (a Where would
    //   silently match nothing, dropping every source2 row).
    // - Default serialization on both sides: every combined row is read through source1's shaper, so a converted or
    //   non-default-representation column on either side would be read with the other side's serializer.
    internal static bool CanAlignBareScalarAliases(
        ShapedQueryExpression source1, MongoQueryExpression mongo1, ShapedQueryExpression source2, MongoQueryExpression mongo2)
        => IsBareScalarOperand(source1, mongo1)
           && IsBareScalarOperand(source2, mongo2)
           && mongo2.Select.PostGroupOps.Count == 0
           && ReadsOnlyDefaultSerializedFields(mongo1.Select)
           && ReadsOnlyDefaultSerializedFields(mongo2.Select);

    // A grouped operand's flattening projection reads the $group output, so its key parts and accumulator operands are what
    // reach the stored fields. The key-part conjunct is defence in depth (StoredSerialization.HasDefaultKeySerialization).
    private static bool ReadsOnlyDefaultSerializedFields(MongoSelectDefinition select)
        => select.Projection.All(p => MongoExpressionTranslator.AllFieldsDefaultSerialized(p.Expression))
           && (select.Grouping is not { } grouping
               || (grouping.Key.All(k => MongoExpressionTranslator.AllFieldsDefaultSerialized(k.FieldRef))
                   && grouping.Accumulators.All(
                       a => a.Operand is null || MongoExpressionTranslator.AllFieldsDefaultSerialized(a.Operand))));

    // Operands must have the same top-level alias set: dedup and source-tagging compare whole projected documents. Field
    // refs may differ. EF already requires a common anonymous type, so this is defence in depth.
    private static bool ProjectionShapesMatch(
        IReadOnlyList<MongoProjection> p1, IReadOnlyList<MongoProjection> p2)
    {
        if (p1.Count != p2.Count)
        {
            return false;
        }

        var aliases = new HashSet<string>(p1.Count);
        foreach (var projection in p1)
        {
            aliases.Add(projection.Alias);
        }

        foreach (var projection in p2)
        {
            if (!aliases.Contains(projection.Alias))
            {
                return false;
            }
        }

        return true;
    }

    // Each alias's values must be stored the same way on both operands: dedup and Intersect/Except compare stored BSON, and
    // source1's shaper reads every combined row through source1's property serializer. Without this, Converted.Select(o =>
    // o.Status) (stored "Shipped") against Other.Select(o => o.Status) (stored 1) dedups nothing, intersects to empty, or throws
    // reading 1 as a string. Covers bare, wrapped and projected-Distinct operands (key part read back from "_id.<alias>"); a
    // real GroupBy key is already default-serialized. DateTimeKind is checked separately (NativeDateTimeKindReadBack).
    private static bool OperandSerializationsMatch(MongoSelectDefinition select1, MongoSelectDefinition select2)
    {
        foreach (var projection1 in select1.Projection)
        {
            // MongoProjection is a struct: an absent alias is default, with a null Expression.
            var expression2 = select2.Projection.FirstOrDefault(p => p.Alias == projection1.Alias).Expression as MongoExpression;

            // A DateTime.TimeOfDay leaf stores milliseconds (a long), unlike any other TimeSpan (default string form): the combine
            // would dedup/intersect a long against a string, and the alias would be read through only source1's reader
            // (MongoSelectDefinition.IsTimeOfDayProjection). Both sides TimeOfDay store and read the same way.
            if (MongoDatePartExpression.IsTimeOfDay(projection1.Expression)
                != (expression2 is not null && MongoDatePartExpression.IsTimeOfDay(expression2)))
            {
                return false;
            }

            var field1 = StoredField(select1, projection1.Expression);
            var field2 = expression2 is not null ? StoredField(select2, expression2) : null;

            if (!StoredSerializationsMatch(field1, field2))
            {
                return false;
            }
        }

        return true;
    }

    // Both sides default-serialized (a computed value, null here, has no property and so no converter); or the same property;
    // or two properties stored identically (StoredSerialization.StoredAlike, shared with the anonymous-key join), e.g. two
    // HasConversion<string>() enums. A converter or representation against none, or two different ones, declines.
    private static bool StoredSerializationsMatch(MongoFieldExpression? field1, MongoFieldExpression? field2)
    {
        var default1 = field1 == null || StoredSerialization.HasDefaultKeySerialization(field1.Property);
        var default2 = field2 == null || StoredSerialization.HasDefaultKeySerialization(field2.Property);
        if (default1 && default2)
        {
            return true;
        }

        if (field1 == null || field2 == null)
        {
            return false;
        }

        return field1.Property == field2.Property
               || StoredSerialization.StoredAlike(field1.Property, field2.Property);
    }

    // The entity field whose stored value a projected operand's alias carries: the projected field itself, or, for a
    // projected Distinct/GroupBy operand, the key part its flattened "_id.<alias>" (or single-key "_id") reads.
    private static MongoFieldExpression? StoredField(MongoSelectDefinition select, MongoExpression value)
        => value switch
        {
            MongoFieldExpression field => field,
            // A marked Distinct key's flatten restores MISSING from the marker: $cond [marker, $$REMOVE, "_id.<alias>"]. Without
            // this a marked value-converted key (ClassifyMalformedFieldRead) would look default-serialized and match an operand
            // stored differently.
            MongoConditionalExpression { IfFalse: MongoElementRefExpression keyRef } when select is { IsDistinct: true, Grouping: not null }
                => StoredField(select, keyRef),
            MongoElementRefExpression { Path: var path } when select.Grouping is { } grouping
                => (path == "_id" && grouping.Key.Count == 1
                        ? grouping.Key[0]
                        : grouping.Key.FirstOrDefault(k => k.Name != null && path == "_id." + k.Name))
                    ?.FieldRef as MongoFieldExpression,
            _ => null
        };

    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        => null;

    #endregion
}
