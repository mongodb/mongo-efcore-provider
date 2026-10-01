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
/// Captures the final query expression in the chain so it can be run against the MongoDB LINQ v3 provider while also
/// following the shape of the transformation so that the shaper may be correctly adjusted and early-terminates any
/// unsupported operations.
/// </summary>
internal sealed class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    private readonly MongoProjectionBindingExpressionVisitor _projectionBindingExpressionVisitor = new();
    private Expression? _finalExpression;

    /// <summary>Creates a <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/>.</summary>
    /// <param name="dependencies">The visitor dependencies.</param>
    /// <param name="queryCompilationContext">The query compilation context.</param>
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
    // EF8/EF9 nav-expansion flattens both a user GroupJoin+SelectMany(DefaultIfEmpty) and EF's own
    // optional-reference-navigation lowering onto this internal shim (there is no BCL Queryable.LeftJoin before
    // .NET 10). No per-call signal survives nav-expansion to tell the two origins apart (key-selector parameters
    // are rewritten uniformly), so the shim is admitted unconditionally by MethodInfo identity, as EF10 admits
    // Queryable.LeftJoin.
    internal static readonly MethodInfo Ef8Ef9LeftJoinMethod =
        typeof(Microsoft.EntityFrameworkCore.Internal.QueryableExtensions)
            .GetTypeInfo().GetDeclaredMethods("LeftJoin").Single(mi => mi.GetParameters().Length == 5);
#endif

    /// <summary>
    /// Whether <paramref name="method"/> is EF8/EF9's internal <c>LeftJoin</c> shim (see
    /// <c>Ef8Ef9LeftJoinMethod</c>), so it is treated like <c>Queryable.LeftJoin</c> here and in
    /// <see cref="MongoEFToLinqTranslatingExpressionVisitor"/>. Always <see langword="false"/> on EF10.
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
    /// <see cref="KeySelectorsMatch"/>). Otherwise the driver renders both into one <c>$sort</c> and MongoDB
    /// rejects the duplicate field (EF-253 / CSHARP-5690); relational providers drop these too. Orderings with an
    /// explicit <see cref="IComparer{T}"/> are left alone, since a custom comparer can make them non-redundant.
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
    /// Whether both key selectors are a direct single-hop access to the same member of their parameter
    /// (<c>x =&gt; x.CustomerId</c>). Only those collide in the driver's <c>$sort</c>; anything computed
    /// (<c>x.Name.Length</c>, a method call) gets its own uniquely-named projected field and must not be elided.
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
    /// <param name="methodCallExpression">The call to visit.</param>
    /// <returns>A <see cref="ShapedQueryExpression"/>, or <see cref="QueryCompilationContext.NotTranslatedExpression"/> for a non-<see cref="Queryable"/> method.</returns>
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

            // The native projection holds the raw receiver of a client-reapplied ToLower/ToUpper, so an operator that
            // reads projected values (e.g. Distinct) would see the wrong value. Marked after the switch because
            // base.VisitMethodCall re-translates the source into a new query expression.
            if (sourceHasCaseMappingLeaf && !IsProjectedValueFreeOperator(methodDefinition))
            {
                ((MongoQueryExpression)shapedQueryExpression.QueryExpression).Select.MarkNotNativelyRepresentable();
            }

            // A client-evaluated leaf (HasClientEvaluatedProjectionLeaf) is never projected, so an operator that reads
            // projected values would read a missing field. Distinct is exempt: the leaf's value is the same for every
            // row of one execution, so deduplicating by the projected leaves alone is exact. Set ops are exempt here
            // only because their own gate declines the flag (IsPlainProjectedSelect/IsPlainDistinctSelect), which
            // also covers a flagged source2. A client-evaluated ternary (HasClientConditionalProjectionLeaf) is not
            // exempt from Distinct: its value differs between rows, and deduplicating by the projected test and
            // branch reads is not proven equal to deduplicating the constructed values.
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
    /// Operators that never read projected values (paging, predicate-less cardinality/count terminals), so they stay
    /// native over a client-reapplied case-mapping projection (<see cref="MongoSelectDefinition.HasClientCaseMappingProjectionLeaf"/>).
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
        // Handle .Select(p => p) no-op/pass-thru
        if (selector.Body == selector.Parameters[0])
        {
            return source;
        }

        // An identity re-projection over an already-bound construction shaper, which nav-expansion appends after
        // Distinct/Union/Concat/Intersect/Except when the projection holds an entity reference (e.g. an owned navigation).
        // Substituting the shaper folds each member access back to the bound node; re-running projection binding over
        // those bound nodes would throw in every query mode.
        //
        // Not for a whole-entity leaf (`new { Customer = c, ... }`): driver-LINQ can't shape that under a Distinct/set op,
        // so it keeps failing translation cleanly.
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

        // Trailing projection of an explicit-result-selector / query-syntax owned SelectMany (UnwindSource set,
        // no Projection yet): bind ti.Outer/ti.Inner and build a by-alias shaper, skipping the generic fold
        // below. A rejected projection falls through to the guards below.
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
        // combinator, so it arrives as Union(A, B).Select(x => Include(x)).
        //
        // A set-op-only terminal is exempt: MongoSelectLowerer defers the lookup block until after the set-op stage and
        // TrailingOps, so operand rows are joined too. This gate and that emission must move together. GroupBy/Distinct/
        // SelectMany terminals, or a populated trailing projection, still decline. Reference Includes are handled by
        // TryConfirmReferenceIncludeChain above.
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        // A collection Include directly over a join scope, e.g. Customers.Include(c => c.Orders).Join(Orders, ...).
        // Gated by IsSingleEligibleNativeJoinScope; confirms and registers the join's $lookup. The Include's own $lookup
        // registers later during shaper compilation, so don't mark non-representable on success.
        //
        // Outer-rooted only. When the join resolves to the Include's navigation, both would claim one alias and AddLookup
        // would collapse them, but the join needs $unwind and the Include the bare array; renaming the join's alias is safe
        // only because nothing reads the join's Inner side here. An Inner-rooted Include is declined:
        // RebindInnerShaperToOuterQuery already baked the original alias into Inner's shaper.
        else if (TryGetCollectionIncludeOverJoinScope(selector) is { EntityExpression: MemberExpression { Member.Name: "Outer" } } collectionInclude)
        {
            if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var collectionJoin))
            {
                var includeNavigation = (INavigation)collectionInclude.Navigation!;
                if (collectionJoin.Lookup!.As == LookupExpression.GetLookupAlias(includeNavigation))
                {
                    var renamedAlias = $"{collectionJoin.Lookup.As}_join";
                    collectionJoin.Alias = renamedAlias;
                    collectionJoin.Lookup.As = renamedAlias;
                }

                mongoQueryExpression.AddLookup(collectionJoin.Lookup);
                mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
                mongoQueryExpression.Select.MarkJoinLookupConfirmed();
            }
            else
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
        // A bare `x.Outer`/`x.Inner` selector over an eligible single-level join, recorded as an unconfirmed candidate;
        // without this confirm/register step Route stays Fallback.
        //
        // Confirming here rather than in TranslateJoinCore defers AddLookup until the consuming Select is known;
        // registering eagerly would flip UsesDriverJoinFields and change the driver-LINQ fallback's document shape
        // (see Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL). No Projection entries: Route is
        // WholeEntity and the generic shaper fold below reads the entity.
        //
        // Depth-1 only (`Levels.Count: 1`); a bare root leaf over a chain is handled by the next arm.
        else if (IsTransparentIdentifierMemberAccessSelector(selector)
                 && mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } bareLeafScope
                 && IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var bareLeafJoin))
        {
            // Without the join-lookup confirmation, an operator composed after this Select could record a native op
            // that lowers before the $lookup and resolves against the outer entity type. See
            // MongoSelectDefinition.HasConfirmedJoinLookup.
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
        // multi-hop reference-navigation filter. Confirms every level. Root only: a non-root whole-entity leaf needs
        // HasBareJoinInnerEntityLeaf's read-side handling, which exists only at depth 1.
        //
        // Paging in PipelineOps recorded after a join may sit BETWEEN two joins of the chain, and confirming would defer
        // it past both $lookups (see the chain-paging gap at the bare-value arm below), so decline. Checked before
        // IsSingleEligibleNativeJoinScope, which may itself defer PipelineOps. This conservatively also declines paging
        // after BOTH joins while still in PipelineOps. Paging recorded after a Where flipped to PostJoinOps isn't seen
        // here; it lowers after every $lookup, correct only if no row-changing join follows, and TranslateJoinCore
        // declines when one does (HasNonCommutingPostJoinOp).
        else if (mongoQueryExpression.Select.JoinScope is { Levels.Count: > 1 } chainedLeafScope
                 && NativeJoinScopeTranslator.TryResolveBareScopeLeaf(
                     chainedLeafScope, selector.Parameters[0], selector.Body, out var chainedLeafScopeIndex)
                 && chainedLeafScopeIndex == 0
                 && !(mongoQueryExpression.Select.HasPaging && mongoQueryExpression.Select.HasPagingRecordedAfterAJoin)
                 && IsSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
        {
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, chainedLeafScope);
        }
        // A wrapped `new {...}`/MemberInit projection over the same eligible single-level join, every leaf resolvable
        // by NativeJoinScopeTranslator. TryBindProjection is the whole gate and mutates nothing on decline, so false
        // falls through to the projected-Select branch below.
        else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var wrappedLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindProjection(mongoQueryExpression, selector, wrappedLeafJoin))
        {
            // Build the result shaper by index, not via the generic fold below: ApplyProjection early-returns because a
            // join query's Projection is already non-empty, so members would never resolve and GetProjectionIndex would
            // throw at compile time. GroupBy and SelectMany bind by index for the same reason.
            //
            // Same post-confirmation gate as the bare-leaf arm: otherwise a later Take/Skip/First could page the
            // un-joined outer rows ahead of the $lookup.
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
        // A bare ternary null-checking a join scope's Inner side, e.g.
        // `ti => ti.Inner != null ? ti.Inner.City : null`. Bound as a single leaf under the synthetic "_v" alias
        // TryBindConditionalProjection staged, like the GroupBy/SelectMany bare-leaf branches.
        else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var conditionalLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindConditionalProjection(mongoQueryExpression, selector, conditionalLeafJoin))
        {
            return BindBareJoinScopeLeaf();
        }
        // A bare join scope null check, e.g. `ti => ti.Inner == null` (what nav-expansion leaves of
        // `o => o.Customer == null`), at any chain depth. A bool leaf under "_v", bound like the conditional arm above;
        // an inner join's degenerate check declines inside TryBindNullCheckProjection.
        else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out var nullCheckLeafJoin)
                 && NativeJoinScopeProjectionBinder.TryBindNullCheckProjection(mongoQueryExpression, selector, nullCheckLeafJoin))
        {
            return BindBareJoinScopeLeaf();
        }
        // A bare scalar/computed body over a single-level join scope, e.g. `ti => ti.Inner.City` (what EF's null-check
        // removal leaves of `nav != null ? nav.Member : null`). Tried after the whole-entity and conditional arms.
        //
        // Restricted to Levels.Count == 1; lifting that for chains is unvalidated.
        else if (IsSingleEligibleNativeJoinScope(mongoQueryExpression, out _)
                 && mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 }
                 && mongoQueryExpression.Select.Projection.Count == 0
                 && selector.Body is not ConditionalExpression
                 && !selector.Body.TryGetProjectionMembers(out _)
                 && NativeJoinScopeTranslator.TryTranslateValue(
                     mongoQueryExpression.Select.JoinScope, selector.Parameters[0], selector.Body, out var bareValueLeaf)
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
        // A parameter-free body over a single-level join scope, e.g. `Select(ti => "Foo")` after `Skip`/`Take`. The
        // bare-value arm above can't take it: NativeJoinScopeTranslator requires an Outer/Inner access. Staged as a
        // `$literal` `_v` projection; confirming the chain keeps the $lookup/$unwind, so inner-join-dropped rows stay
        // dropped and paging deferred by IsSingleEligibleNativeJoinScope lowers after the $unwind. Pure checks run
        // before that predicate (which may defer ops) so nothing is mutated on a decline.
        else if (mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } parameterFreeScope
                 && mongoQueryExpression.Select.Projection.Count == 0
                 && !selector.Body.ReferencesParameter(selector.Parameters[0])
                 && TryTranslateParameterFreeJoinProjection(parameterFreeScope, selector.Body, out var parameterFreeLeaf)
                 && IsSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
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
            // Post-terminal guard: a projected Select after a native GroupBy/Distinct/finalized Grouping reaches this
            // branch (the terminal already replaced the shaper), bypassing the guards in NativeSlotPopulator/
            // NativeCardinalityBinder. Appending field refs onto the populated Projection would reference fields that no
            // longer exist after $group, giving silent nulls (e.g. Select(new{Country,City}).Distinct().Select(x => new{Nation = x.Country})).
            // Mirrors TranslateGroupBy.
            //
            // A set-op-only terminal is exempt (the lowerer emits the $project after the set-op stage);
            // IsSetOpTerminalOnly requires Projection.Count == 0, so a second projection still falls back.
            if (mongoQueryExpression.Select.HasTerminalOperator && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // Native $project pushdown for an anonymous/DTO projection whose leaves the binder accepts.
            else if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // A multi-argument positional-ctor DTO needs the index-based shaper, not the generic fold below (see
            // MongoSelectDefinition.HasPositionalCtorProjectionShaper), unless it holds a client case mapping the
            // mixed reader can bind per argument (see DeclinePositionalCtorCaseMappingForMemberBinding).
            else if (mongoQueryExpression.Select.HasPositionalCtorProjectionShaper
                     && !DeclinePositionalCtorCaseMappingForMemberBinding(mongoQueryExpression, selector, source.ShaperExpression))
            {
                return source.UpdateShaperExpression(
                    BuildPositionalCtorProjectionShaper(mongoQueryExpression, selector.Body));
            }
            // A wrapped client-method call over a single-level join scope (e.g. Include(e => e.Manager) with
            // `ti => ti.Inner != null ? "..." + ClientMethod(ti.Outer) : ""`) needs the same confirm/register step as the
            // bare pass-through arm, else Route stays Fallback. Gated on HasClientWrappedWholeEntityShaper, which only that
            // binder arm sets.
            else if (mongoQueryExpression.Select.HasClientWrappedWholeEntityShaper
                     && mongoQueryExpression.Select.JoinScope is { Levels.Count: 1 } clientMethodScope
                     && IsSingleEligibleNativeJoinScope(mongoQueryExpression, out _))
            {
                NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQueryExpression, clientMethodScope);
            }
        }

        // A bare-nav owned/reference SelectMany whose trailing selector is a whole-inner-entity `ti => ti.Inner` bypasses
        // both the projection branch and the post-terminal guard above. UnwindSource.WholeElement makes the lowerer emit
        // $unwind + $replaceRoot (the $mergeObjects sentinel form for Owned, see MongoReplaceRootStage) and the gate root
        // the DOM shaper at the element type; without it the gate would go native for an element never materialized and
        // crash the shaper with KeyNotFoundException.
        //
        // A computed-leaf selector falls back via MarkNotNativelyRepresentable. A whole-outer (`select o`) or
        // unrepresentable element (see IsWholeElementRepresentable) throws NotSupportedException, not
        // NativeTranslationNotSupportedException: this runs before the compile-time gate, so nothing would catch the latter.
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

                // Re-root the ROOT ProjectionMember at the element type; it still pointed at the owner's projection, which
                // makes a nested owned navigation's auto-Include call BindNavigation on the owner and throw. Safe
                // unconditionally: `ti => ti.Inner` drops the outer shaper. Must be here, not in BuildBareNavWrappedShaper,
                // which runs before it is known whether the selector projects the whole element.
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
    /// Builds the <c>GroupBy(key).Select(aggregate)</c> result shaper, rebinding each anonymous/DTO member to
    /// its top-level alias (the member name) in the flattened <c>$group</c> output. Returns
    /// <see langword="null"/> for a non-construction body, which keeps the placeholder
    /// <see cref="Microsoft.EntityFrameworkCore.Query.GroupByShaperExpression"/>.
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

    // Registers a projection for one grouped-result member and returns an index-based binding. The stored
    // source expression matters only for dedup and CLR type; the DOM shaper reads the value raw by alias.
    //
    // Exception: a nested-construction member (`Container = new LastInChain { ..., Value = g.Sum(...) }`) was
    // already staged by NativeGroupByBinder as a MongoDocumentConstructionExpression, which must be registered
    // instead of the raw MemberInitExpression. Otherwise the shaper does a plain alias read of the whole nested
    // CLR type, which throws or silently misreads members (a decimal came back as Decimal128). Same carve-out
    // as BindResultMember.
    //
    // A client case mapping (`K = g.Key.ToUpper()`) staged only its receiver (NativeGroupByBinder
    // .PeelResultMemberCaseMapping), so the receiver is read and the call re-applied over it client-side, null
    // propagated. Never read on driver-LINQ, which declines such a selector (ThrowIfDriverLinqCaseMapsGroupedResult).
    private static Expression BindGroupMember(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        if (TryBindDocumentConstruction(mongoQueryExpression, alias, valueExpression, out var boundConstruction))
        {
            return boundConstruction;
        }

        var receiver = NativeGroupByBinder.PeelResultMemberCaseMapping(valueExpression);
        var index = mongoQueryExpression.AddToProjection(receiver, alias);
        var boundReceiver = new ProjectionBindingExpression(mongoQueryExpression, index, receiver.Type);
        return ReapplyCaseMapping(valueExpression, receiver, boundReceiver);
    }

    // Re-applies the case-mapping chain peeled off `original` down to `receiver` (NativeProjectionBinder.PeelCaseMapping,
    // or NativeGroupByBinder.PeelResultMemberCaseMapping, which also peels through a Convert) over `boundReceiver`,
    // null propagated as MongoProjectionBindingExpressionVisitor does for a named member. Answers `boundReceiver`
    // when nothing was peeled.
    private static Expression ReapplyCaseMapping(Expression original, Expression receiver, Expression boundReceiver)
        => original switch
        {
            _ when ReferenceEquals(original, receiver) => boundReceiver,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                => convert.Update(ReapplyCaseMapping(convert.Operand, receiver, boundReceiver)),
            MethodCallExpression call when NativeProjectionBinder.IsCaseMappingCall(call)
                => MongoProjectionBindingExpressionVisitor.NullPropagatingStringCall(
                    call, ReapplyCaseMapping(call.Object!, receiver, boundReceiver)),
            _ => throw new InvalidOperationException(
                $"Unexpected case-mapped projection shape '{original.GetType().Name}'.")
        };

    /// <summary>
    /// Builds the index-based result shaper for a multi-argument positional-ctor DTO <c>Select</c>
    /// (<c>x =&gt; new CustomerListItem(x.CustomerID, x.City)</c>) after
    /// <c>NativeProjectionBinder.TryPopulateNativeProjection</c> committed its <c>$project</c>. See
    /// <see cref="MongoSelectDefinition.HasPositionalCtorProjectionShaper"/> for why the generic fold can't be used.
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
    // Select.TryGetProjectionAlias because an owned-array/owned-nav-entity leaf may have registered a
    // document-path alias that differs from the synthetic "_ctorArg<N>" member name.
    //
    // A client case mapping (`x.S.ToUpper()`) was peeled by NativeProjectionBinder, which staged only its receiver
    // ($toUpper/$toLower are ASCII-only), so bind the receiver and re-apply the call chain client-side over it, null
    // propagated, as MongoProjectionBindingExpressionVisitor does for a named member.
    private static Expression BindPositionalCtorProjectionMember(MongoQueryExpression mongoQueryExpression, string memberName, Expression valueExpression)
    {
        var alias = mongoQueryExpression.Select.ResolveProjectionAlias(memberName);
        var receiver = NativeProjectionBinder.PeelCaseMapping(valueExpression);
        var index = mongoQueryExpression.AddToProjection(receiver, alias);
        return ReapplyCaseMapping(
            valueExpression, receiver, new ProjectionBindingExpression(mongoQueryExpression, index, receiver.Type));
    }

    /// <summary>
    /// For a positional-ctor projection holding a client case mapping (<c>new KeyValuePair&lt;string, string&gt;(
    /// x.S.ToUpper(), x.T)</c>) whose arguments the mixed reader can bind each under a member of their own, declines
    /// the native projection so the generic fold binds it that way; returns <see langword="true"/> when it declined.
    /// </summary>
    /// <remarks>
    /// The index-based positional shaper can only be read off the native <c>$project</c>: a whole-document read of a
    /// synthetic <c>_ctorArg&lt;N&gt;</c> alias finds nothing. So on driver-LINQ (explicitly, or a late decline) this
    /// shape would be pushed down, where <c>$toUpper</c>/<c>$toLower</c> are ASCII-only and map null to <c>""</c>. Bound
    /// per argument, the mixed reader applies the .NET call in every mode. When the arguments can't be bound per
    /// member (a constructor-only DTO whose parameters match no member), the native shaper stays: it re-applies the
    /// call itself (<see cref="BindPositionalCtorProjectionMember"/>), and there is no correct mixed reading to prefer.
    /// Calls the binder's own peel and the binding visitor's own member match, so neither can drift from this gate.
    /// </remarks>
    private static bool DeclinePositionalCtorCaseMappingForMemberBinding(
        MongoQueryExpression mongoQueryExpression, LambdaExpression selector, Expression sourceShaper)
    {
        if (selector.Body is not NewExpression positionalNew
            || positionalNew.Arguments.All(a => ReferenceEquals(NativeProjectionBinder.PeelCaseMapping(a), a))
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
    /// Shared admissibility gate for the join-scope <c>Select</c> arms in <see cref="TranslateSelect"/>, also
    /// used by <see cref="NativeTranslation.NativeCardinalityBinder.TryBindAggregate"/>. On success
    /// <paramref name="joinInfo"/> is the last join in the chain (<c>Joins[^1]</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>scope.Levels.Count == Joins.Count</c> means every join in the chain is individually eligible
    /// (<c>TranslateJoinCore</c> stops extending <c>JoinScope</c> at the first ineligible one). It also guards
    /// against resolving one join's Inner side against another join with coincident CLR types; chains are
    /// resolved structurally in <see cref="NativeJoinScopeProjectionBinder"/> (see
    /// <c>Binds_a_second_chained_join_reusing_the_first_joins_target_type_at_the_correct_alias</c>).
    /// </para>
    /// <para>
    /// A left-outer join over a collection navigation is admitted: the lowerer's <c>ForceUnwind</c> arm honors
    /// <see cref="LookupExpression.PreserveNullAndEmptyArrays"/> (from <see cref="JoinInfo.IsLeftOuter"/>).
    /// <see cref="NativeSelectManyBinder"/> sets it to <c>false</c> explicitly, since SelectMany is always inner.
    /// </para>
    /// <para>
    /// <c>HasUnsupportedOperator</c> prevents wrong rows: confirming registers the <c>$lookup</c> at translation
    /// time, which flips <c>UsesDriverJoinFields</c> and changes the driver-LINQ fallback's shape. For
    /// <c>Join(…).Where(x =&gt; …x.Inner…).Select(x =&gt; x.Inner)</c> whose <c>Where</c> declines, that fallback could
    /// return wrong rows; that failure is now also closed by <see cref="MongoSelectDefinition.HasBareJoinInnerEntityLeaf"/>
    /// and no test fails with this conjunct removed, so it is defence in depth (see
    /// <c>NativeJoinTests.Inner_side_Where_that_declines_natively_does_not_confirm_the_join</c>). Deliberately not
    /// <c>Route == Fallback</c>, which is also true merely because this join is unconfirmed.
    /// </para>
    /// <para>
    /// <c>HasTerminalOperator</c>/<c>UnwindSource</c> are excluded because the <c>$lookup</c> would be emitted at
    /// a different point than these arms assume.
    /// </para>
    /// <para>
    /// <c>TryBindAggregate</c> needs this too: a bare <c>Any()</c>/<c>Count()</c> after a join has no trailing
    /// <c>Select</c> (nav-expansion only adds one when row shape is needed), so without a second confirming site
    /// the chain would stay unconfirmed.
    /// </para>
    /// </remarks>
    internal static bool IsSingleEligibleNativeJoinScope(
        MongoQueryExpression mongoQueryExpression, [NotNullWhen(true)] out JoinInfo? joinInfo)
    {
        joinInfo = null;

        if (!HasCompleteJoinScope(mongoQueryExpression, out _))
        {
            return false;
        }

        // IsNativelyEligible already checked each join before JoinScope was built to Joins.Count levels.
        var candidate = mongoQueryExpression.Joins[^1];

        // Paging or a reducer recorded before this arm confirms is emitted ahead of the $lookup/$unwind. That is
        // sound only when every level's $unwind is 1:1; otherwise it pages the un-joined outer rows
        // (`Owners.Join(Orders, …).Take(2)` returned three rows). This ordering is the common one: nav-expansion
        // applies a join's result selector last. The mirror ordering is closed by
        // MongoSelectDefinition.HasConfirmedJoinLookup. A recorded $match/$sort commutes with the join (sorts
        // translate only against the root scope).
        //
        // Only a left-outer reference navigation is 1:1 (preserveNullAndEmptyArrays: true), e.g.
        // `Orders.OrderBy(o => o.OrderID).Take(10).Select(o => o.Customer.City)` (pinned by
        // Projection_take_projection and friends). Collection navigations multiply rows; inner reference joins
        // drop unmatched rows. Every level must be checked, since the ops precede the whole $lookup block.
        //
        // Reducers decline unless every join is 1:1. Paging instead defers the recorded PipelineOps past the
        // $lookup/$unwind block (DeferPipelineOpsPastConfirmedJoin).
        if (mongoQueryExpression.Select.Cardinality?.Reducer != null)
        {
            // Reducer case (First/FirstOrDefault/Single/...).
            if (!mongoQueryExpression.AreAllJoinsRowCountPreserving())
            {
                return false;
            }
        }
        else if (mongoQueryExpression.Select.HasPaging)
        {
            // PostJoinOps and PostLookupPagingOps aren't mutually exclusive. If an inner-predicate Where over a
            // required reference nav already populated PostJoinOps, deferring earlier paging would emit
            // filter-then-page for page-then-filter (e.g. `Skip(1).Take(2).Where(od => od.Order.CustomerID !=
            // "ALFKI")`), returning wrong rows. Decline.
            if (mongoQueryExpression.Select.JoinInnerAccessConfirmed)
            {
                return false;
            }

            var everyJoinPreLookupSafe = mongoQueryExpression.AreAllJoinsRowCountPreserving();

            // Keep paging before the $lookup when every join is 1:1-safe; otherwise defer it past the $lookup/$unwind.
            // Consequence: paging recorded before a required reference dereference in a projection pages the joined
            // result, which differs only for dangling references (pinned by
            // Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly).
            if (!everyJoinPreLookupSafe)
            {
                // Paging recorded before any join existed (e.g. `Customers.Take(1).GroupJoin(Orders, ...)
                // .SelectMany(g => g.DefaultIfEmpty())`) pages the outer sequence and must not be deferred past a
                // row-multiplying $unwind. HasPagingRecordedBeforeAnyJoin alone can't distinguish this from a
                // reference-nav dereference in a projection (whose join is synthesized just as late), which is at most 0:1
                // and still defers. "May multiply" is any join not provably a reference navigation: a collection OR a
                // navigation-less join (pinned by
                // NativeJoinTests.Outer_paging_before_a_navigation_less_one_to_many_join_pages_the_outer_rows). Such paging
                // is already ahead of the $lookup in PipelineOps, so it stays there, unless paging was also recorded
                // after a join, which leaves no correct single placement, so decline.
                if (mongoQueryExpression.Select.HasPagingRecordedBeforeAnyJoin
                    && mongoQueryExpression.Joins.Any(j => j.Lookup?.Navigation is not { IsCollection: false }))
                {
                    if (mongoQueryExpression.Select.HasPagingRecordedAfterAJoin)
                    {
                        return false;
                    }
                }
                // Paging written BETWEEN two joins of a chain (`Join(a, ...).Skip(1).Take(2).Join(b, ...)`) pages
                // the earlier join's rows, but the deferred snapshot is emitted after EVERY join's $lookup/$unwind
                // and would page the fully joined result (silently wrong rows when a later join drops or
                // multiplies rows). The lowerer has no per-join boundary to place it at, so decline; the
                // driver-LINQ fallback emits each $lookup at its own boundary. Checked before deferring, so a
                // decline mutates nothing. Pinned by Ef373InterleavedPagingTests.
                else if (mongoQueryExpression.Select.HasPagingRecordedBetweenJoins(mongoQueryExpression.Joins.Count))
                {
                    return false;
                }
                else
                {
                    mongoQueryExpression.Select.DeferPipelineOpsPastConfirmedJoin();
                }
            }
        }

        joinInfo = candidate;
        return true;
    }

    /// <summary>
    /// The structural core shared by <see cref="IsSingleEligibleNativeJoinScope"/> and
    /// <see cref="TryGetGroupByJoinScope"/>: a join scope with one level per join, no unsupported or terminal
    /// operator, no unwind source, and a resolved <c>Lookup</c> on every join. Pure.
    /// </summary>
    private static bool HasCompleteJoinScope(
        MongoQueryExpression mongoQueryExpression, [NotNullWhen(true)] out MongoJoinScope? scope)
    {
        var select = mongoQueryExpression.Select;
        scope = select.JoinScope;
        if (scope == null
            || scope.Levels.Count != mongoQueryExpression.Joins.Count
            || select.HasUnsupportedOperator
            || select.HasTerminalOperator
            || select.UnwindSource != null)
        {
            scope = null;
            return false;
        }

        // Every join needs a resolved Lookup, not just the last: ConfirmEntireChain confirms every level
        // unconditionally, so a null earlier Lookup would drop that $lookup while the projection still reads its
        // alias. JoinLookupImplementsKeySelectors should already prevent this; checked structurally anyway.
        foreach (var chainJoin in mongoQueryExpression.Joins)
        {
            if (chainJoin.Lookup is null)
            {
                scope = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Translates a <c>Select</c> body that doesn't reference its parameter to a bare constant or query parameter,
    /// which <c>RenderProject</c> <c>$literal</c>-wraps. Anything else declines, as does a value that would throw at
    /// pipeline-build time (<see cref="NativeSlotPopulator.TryProbeBareValueRenders"/>). Pure.
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
    /// The join scope a <c>GroupBy</c> may resolve through, or <see langword="null"/> to keep root-entity resolution.
    /// Pure: unlike <see cref="IsSingleEligibleNativeJoinScope"/> it never defers recorded ops, so it's safe to call
    /// before the grouping has bound. Called before <c>IsGroupBy</c> is set.
    /// </summary>
    /// <remarks>
    /// Paging recorded ahead of the grouped join is kept only when every join is 1:1 (a left-outer reference
    /// navigation), so it commutes with the <c>$lookup</c>. Any other paging declines instead of being deferred past
    /// the <c>$lookup</c>. Deferral is acceptable for a projection (it differs only for dangling references), but here
    /// it would silently change group counts. Chains decline on any paging (the snapshot gap at the bare-value arm of
    /// <see cref="TranslateSelect"/>). The key selector's parameter must be the join's <c>TransparentIdentifier</c>:
    /// an entity-typed parameter can't be routed by <see cref="MongoGroupElementTranslator"/>, and the root translator
    /// would resolve its members by name against the outer entity. Defence in depth: nav-expansion folds a result
    /// selector that projected one side (<c>(o, w) =&gt; w</c>) into the GroupBy as <c>ti =&gt; ti.Inner.X</c>, so no
    /// shape is known to reach a non-<c>TransparentIdentifier</c> parameter over a join scope.
    /// </remarks>
    internal static MongoJoinScope? TryGetGroupByJoinScope(MongoQueryExpression mongoQueryExpression, LambdaExpression keySelector)
    {
        var select = mongoQueryExpression.Select;
        if (!HasCompleteJoinScope(mongoQueryExpression, out var scope)
            || !keySelector.Parameters[0].Type.IsTransparentIdentifierType()
            || select.Cardinality != null
            // ConfirmEntireChain isn't idempotent (it counts confirmations), so a second confirm must be unreachable.
            || select.HasConfirmedJoinLookup)
        {
            return null;
        }

        if (select.HasPaging
            && (scope.Levels.Count > 1
                || select.JoinInnerAccessConfirmed
                || !mongoQueryExpression.AreAllJoinsRowCountPreserving()))
        {
            return null;
        }

        return scope;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="selector"/> is a transparent-identifier
    /// selector produced by a Join/GroupJoin/LeftJoin rewrite — i.e. the body constructs an anonymous
    /// object whose fields are the outer and inner parameters without further transformation.
    /// Projecting selects (e.g. <c>Select(c =&gt; c.Name)</c>) return <see langword="false"/>.
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
    /// Whether <paramref name="selector"/> is the <c>Select(ti =&gt; ti.Outer)</c>/<c>Select(ti =&gt; ti.Inner)</c>
    /// unwrap nav-expansion inserts after a Join/GroupJoin/LeftJoin/SelectMany. It projects nothing, so it must
    /// bypass the post-terminal guard and <see cref="NativeProjectionBinder"/>; otherwise a SelectMany that set
    /// <see cref="MongoSelectDefinition.UnwindSource"/> (a terminal operator) would be marked non-native by EF's
    /// own bookkeeping Select.
    /// </summary>
    private static bool IsTransparentIdentifierMemberAccessSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return false;
        }

        // EF auto-Includes owned navigations of the projected side, so this unwrap can arrive Include-wrapped;
        // unwrap it or a whole-element SelectMany over an element with a nested owned member never reaches the
        // WholeElement branch. Only embedded navigations are unwrapped (as in IsOwnedEmbeddedIncludeSelector): a
        // cross-collection Include needs a $lookup and must keep tripping the guard.
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
    /// Returns the <c>ti.Outer</c>/<c>ti.Inner</c> member access of a bare-nav SelectMany's whole-entity trailing
    /// selector (e.g. <c>(o, i) =&gt; i</c>, <c>select i</c>, 1-arg <c>SelectMany(o =&gt; o.Items)</c>, or
    /// <c>select o</c>), unwrapping any auto-Include layers first; otherwise <see langword="null"/>.
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
    /// Whether the element type is supported by whole-element re-rooting (<c>$unwind</c> + <c>$replaceRoot</c>,
    /// see <see cref="Expressions.MongoUnwindSource.WholeElement"/>). Each rejected case declines at translation
    /// time rather than returning silently wrong data.
    /// </summary>
    /// <remarks>
    /// For <see cref="MongoUnwindSourceKind.Reference"/>, only an eager-loaded navigation is rejected; it would
    /// reach the <c>IncludeExpression</c> machinery and bind against the wrong
    /// <see cref="Microsoft.EntityFrameworkCore.Query.ProjectionMember"/>. For
    /// <see cref="MongoUnwindSourceKind.Owned"/> (the <c>$mergeObjects</c> sentinel merge):
    /// <list type="bullet">
    /// <item>No non-embedded navigation: it would need a <c>$lookup</c> this shape never emits. Nested owned
    /// navigations work because <see cref="Expressions.MongoQueryExpression.ReRootProjectionAt"/> re-roots the
    /// projection at the element type.</item>
    /// <item>No property (scalar or complex) whose element name is <see cref="MongoReplaceRootStage.ShadowField"/>:
    /// the sentinel is merged after the element, so it would silently overwrite that field. Complex properties
    /// need a separate check (<see cref="GetComplexPropertyElementName"/>) because
    /// <see cref="IEntityType.GetProperties"/> doesn't include them.</item>
    /// <item>Every owned-key property (<see cref="MongoPropertyExtensions.IsOwnedTypeKey"/>) has default
    /// serialization (<see cref="NativeGroupByBinder.HasDefaultKeySerialization"/>): the <c>__ownerKey</c>
    /// sentinel is copied from the owner's raw <c>_id</c>, bypassing any converter or representation.</item>
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
                   .All(NativeGroupByBinder.HasDefaultKeySerialization);
    }

    /// <summary>
    /// The element name a complex property occupies, read from the same <c>Mongo:ElementName</c> annotation as
    /// <see cref="MongoPropertyExtensions.GetElementName(IReadOnlyProperty)"/>, falling back to the CLR name.
    /// There is no complex-property overload of <c>GetElementName</c>.
    /// </summary>
    private static string GetComplexPropertyElementName(IReadOnlyComplexProperty complexProperty)
        => (string?)complexProperty[MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames.ElementName]
           ?? complexProperty.Name;

    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression)</c> nav-expansion generates
    /// for a single-level root collection <c>Include</c> (e.g. <c>Customers.Include(c =&gt; c.Orders)</c>). Its
    /// <c>$lookup</c> is registered later by <see cref="MongoProjectionBindingExpressionVisitor"/>, so the Select
    /// must not be marked non-representable. ThenInclude chains, reference navigations, or an Include composed
    /// with a projection fall through to the catch-all.
    /// </summary>
    private static bool IsSingleLevelCollectionIncludeSelector(LambdaExpression selector)
        => selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
           && includeExpression.EntityExpression == selector.Parameters[0]
           && navigation.IsCollection
           && !navigation.IsEmbedded();

    /// <summary>
    /// Recognizes <c>Select(ti =&gt; IncludeExpression(ti.Outer, nav, ...))</c> (or the <c>ti.Inner</c> mirror):
    /// a single-level non-embedded collection <c>Include</c> over one side of a user
    /// <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c>, e.g. <c>Customers.Include(c =&gt; c.Orders).Join(Orders, ...)
    /// .Select(c =&gt; c)</c>. None of the other Include recognizers admit this shape.
    /// <para>
    /// The collection's <c>$lookup</c> registers later during projection binding; the caller must still gate
    /// the join itself through <see cref="IsSingleEligibleNativeJoinScope"/> before confirming it.
    /// </para>
    /// </summary>
    internal static IncludeExpression? TryGetCollectionIncludeOverJoinScope(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return null;
        }

        return selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
               && navigation.IsCollection
               && !navigation.IsEmbedded()
               && includeExpression.EntityExpression is MemberExpression member
               && member.IsTransparentIdentifierOuterOrInnerAccess()
               && member.Expression == selector.Parameters[0]
            ? includeExpression
            : null;
    }

    /// <summary>
    /// Recognizes a pure chain of one or more reference <c>Include</c>s: siblings nest via
    /// <see cref="IncludeExpression.EntityExpression"/> (<c>Include(d =&gt; d.Author).Include(d =&gt; d.Editor)</c>),
    /// <c>ThenInclude</c>s via <see cref="IncludeExpression.NavigationExpression"/>, and the two combine.
    /// <para>
    /// Any collection level makes this return <see langword="null"/>; those shapes belong to
    /// <see cref="TryGetMixedReferenceAndCollectionIncludeChain"/>. Both share <see cref="TryWalkIncludeChain"/>
    /// so they can't disagree on structure.
    /// </para>
    /// <para>
    /// The base may be any number of <c>.Outer</c> hops. A user join followed by an Include
    /// (<c>Orders.Join(Customers, ...).Include(o =&gt; o.Customer)</c>) looks the same by hop count; it is
    /// declined by <see cref="TryConfirmReferenceIncludeChain"/>'s <c>Joins.Count != chain.Count</c> check. This
    /// method only recognizes structure; all semantic declines live in the confirm step.
    /// </para>
    /// </summary>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
        => TryGetReferenceIncludeChain(selector, out _);

    /// <summary>
    /// As <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/>, also reporting which levels are
    /// transitive (<c>ThenInclude</c>-nested). <see cref="TryConfirmReferenceIncludeChain"/> checks the root
    /// declaring type only for non-transitive levels; transitive ones were verified against their parent in
    /// <see cref="TryWalkIncludeChain"/>.
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
    /// Recognizes a reference-Include chain with exactly one non-embedded collection <c>Include</c> mixed in,
    /// either as a sibling (<c>Include(o =&gt; o.Buyer).Include(o =&gt; o.Lines)</c>) or as a terminal
    /// <c>ThenInclude</c> (<c>Include(o =&gt; o.Customer.Orders)</c>). Pure reference chains and a bare collection
    /// Include belong to <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/> and
    /// <see cref="IsSingleLevelCollectionIncludeSelector"/>; the three don't overlap.
    /// <para>
    /// The collection level is returned unconfirmed: its <c>$lookup</c> registers later during projection
    /// binding. Only the reference levels go through <see cref="TryConfirmReferenceIncludeChain"/>; a collection
    /// Include adds no join, so its <c>Joins.Count != chain.Count</c> check still holds.
    /// </para>
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
    /// Shared structural walk for the reference/mixed Include-chain recognizers. Follows the sibling spine
    /// (<see cref="IncludeExpression.EntityExpression"/>) and each sibling's linear <c>ThenInclude</c> chain
    /// (<see cref="IncludeExpression.NavigationExpression"/>), sorting navigations into
    /// <paramref name="referenceLevels"/> or a single <paramref name="collectionLevel"/> (a sibling, or a
    /// terminal <c>ThenInclude</c>). <c>ThenInclude</c> levels are also recorded in
    /// <paramref name="transitiveLevels"/>.
    /// <para>
    /// An embedded navigation in a <c>ThenInclude</c> chain is transparent, provided nothing non-embedded is
    /// nested past it (<see cref="HasNonEmbeddedThenInclude"/>). An embedded sibling rejects the chain (see
    /// <see cref="IsOwnedEmbeddedIncludeSelector"/>). The base must be a pure <c>.Outer</c>* chain reaching the
    /// <c>TransparentIdentifier</c> parameter.
    /// </para>
    /// </summary>
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
                    // Owned auto-include: lives in the same document (EF-368). Reject if anything non-embedded
                    // is nested past it; either way this level's ThenInclude chain ends here.
                    if (HasNonEmbeddedThenInclude(thenInclude))
                    {
                        return false;
                    }

                    break;
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
    /// Validates and confirms every navigation in a recognized reference-Include chain, all-or-nothing.
    /// <para>
    /// For a single Include this builds and registers the forced-unwind <c>$lookup</c>. For sibling Includes,
    /// <c>TranslateJoinCore</c> already registered every join's <c>$lookup</c> once <c>Joins.Count &gt; 1</c>, so
    /// this only confirms each one. Registration makes <see cref="MongoQueryExpression.UsesDriverJoinFields"/>
    /// false, so the lowerer, DOM shaper and driver-LINQ <c>StripJoinForLookup</c> fallback all agree on the
    /// <c>_lookup_&lt;Nav&gt;</c> field.
    /// </para>
    /// <para>
    /// Registration also changes the fallback's emitted pipeline, at translation time before
    /// <c>MongoQueryMode</c> is read. So a wrong admission is wrong in every mode; <c>DriverLinq</c> is not an
    /// escape hatch. Every conjunct below must hold on its own merits.
    /// </para>
    /// <para>
    /// <c>TryWalkIncludeChain</c> alone decides which <c>ThenInclude</c> nesting is admissible, so this method
    /// doesn't re-check <c>HasNonEmbeddedThenInclude</c> per level (that would wrongly decline an admitted
    /// further level).
    /// </para>
    /// </summary>
    private static bool TryConfirmReferenceIncludeChain(
        MongoQueryExpression mongoQueryExpression,
        List<IncludeExpression> chain,
        HashSet<IncludeExpression> transitiveLevels)
    {
        // Joins.Count must equal chain.Count exactly. Not InnerCollections.Count, which is keyed by entity type
        // and collapses same-target siblings. This also declines a user Join plus a downstream Include (one
        // recognized level, two joins).
        if (mongoQueryExpression.Select.HasTerminalOperator                // composed after a terminal
            || mongoQueryExpression.Select.SawNonBareJoinInner             // a filtered/non-bare-scan inner, any join
            || mongoQueryExpression.Joins.Count != chain.Count)
        {
            return false;
        }

        var pendingByAlias = mongoQueryExpression.GetPendingLookups().ToDictionary(l => l.As);
        var newLookups = new List<LookupExpression>();

        foreach (var include in chain)
        {
            var navigation = (INavigation)include.Navigation!;

            // Query filters are caught by SawNonBareJoinInner above, not by checking the target's
            // GetQueryFilter(): that misses a filter declared on a TPH root and EF10 named filters, and the flat
            // $lookup can't filter, so either would return wrong rows in every mode. EF applies any filter as a
            // Where on the join's inner sequence, which SawNonBareJoinInner detects.
            //
            // DeclaringEntityType is checked against the root only for non-transitive levels.
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
                // Registered by TranslateJoinCore's multi-join flattening; confirm rather than re-register.
                if (!existing.ForceUnwind)
                {
                    return false;
                }
            }
            else
            {
                // Single join: TranslateJoinCore never flattens a lone join, so register it here.
                var lookup = new LookupExpression(navigation, forceUnwind: true)
                {
                    // Elsewhere the LINQ operator decides inner vs. left-outer, since IsRequired can't see a user
                    // LeftJoin over a required FK. No operator is available here, but for EF's own
                    // nav-expansion of a reference Include (Join for required, LeftJoin for optional) the two
                    // coincide.
                    PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired
                };

                // Defensive: not known reachable, since LookupExpression's constructor never prefixes LocalField
                // and the sites that do mutate already-registered lookups.
                if (lookup.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                newLookups.Add(lookup);
            }
        }

        foreach (var lookup in newLookups)
        {
            mongoQueryExpression.AddLookup(lookup);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="navigationExpression"/> is, or contains through further embedded hops, an
    /// <see cref="IncludeExpression"/> with a non-embedded navigation: a real <c>ThenInclude</c> past the
    /// looked-up document, as opposed to an owned auto-include that lives in the same document.
    /// <para>
    /// Recurses both axes. Known shapes like <c>...ThenInclude(b =&gt; b.Address).ThenInclude(a =&gt; a.Region)</c>
    /// are already rejected by the <c>Joins.Count != chain.Count</c> check; the recursion is defence in depth.
    /// </para>
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
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression(...))</c> nav-expansion
    /// generates for auto-included owned (embedded) navigations, reference or collection, possibly nested
    /// (e.g. <c>OwnsOne(b =&gt; b.Address)</c>, <c>OwnsMany(b =&gt; b.Tags)</c>).
    /// <para>
    /// Owned data lives in the owner's document, so the whole-entity shaper reads it back and there is nothing
    /// to push down; the Select must not be marked non-representable. Any non-embedded navigation falls back.
    /// Owned collections whose elements have further navigations are kept off the streaming shaper separately
    /// by <see cref="StreamingEligibility"/>.
    /// </para>
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
    /// Parses a <c>SetProperty(selector, value)</c> into a <see cref="MongoNonQueryExpression.Setter"/>. The selector
    /// must target a mapped root scalar property; owned / navigation / unmapped targets produce EF's canonical
    /// non-query translation failure.
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
    /// Classifies the captured source chain of a bulk delete/update. Only <c>Where</c> is the single-command atomic
    /// path; <c>OrderBy</c>/<c>ThenBy</c>/<c>Skip</c>/<c>Take</c>/<c>Distinct</c> require the two-phase path (query
    /// target <c>_id</c>s, then act by <c>$in</c>). Any other operator produces EF's canonical non-query translation
    /// failure. A TPH discriminator filter rides along as a Where.
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
    /// Builds the native <c>OfType&lt;TDerived&gt;()</c> discriminator conjunct: <c>$eq</c> for a single value,
    /// <c>$in</c> for a type with derived types. Returns <see langword="false"/> for a non-TPH type.
    /// </summary>
    private static bool TryBuildDiscriminatorPredicate(IEntityType targetType, out MongoExpression predicate)
    {
        predicate = null!;
        var discriminatorProperty = targetType.FindDiscriminatorProperty();
        if (discriminatorProperty is null)
        {
            // A non-TPH OfType has no native form; see
            // NativeOfTypeTests.Non_TPH_OfType_falls_back_gracefully_and_works_across_modes.
            return false;
        }

        // Values go through the discriminator property's serializer (converter/BsonRepresentation), as the write path and
        // MongoEFDiscriminator do. Unlike a grouping key (NativeGroupByBinder.HasDefaultKeySerialization) there is no
        // flattened-_id readback, so represented discriminators need not be rejected.
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
    /// A projected <c>Select(new {...}).Distinct()</c> becomes a zero-accumulator <c>$group</c>
    /// (<see cref="NativeGroupByBinder.TryBindDistinctFromProjection"/>); the existing shaper still reads the same
    /// aliases from the flattening <c>$project</c>. A whole-entity source records a plain
    /// <see cref="MongoDistinctOp"/> instead. Anything else falls back.
    /// </summary>
    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source)
    {
        var mongoQ = (MongoQueryExpression)source.QueryExpression;
        if (!NativeGroupByBinder.TryBindDistinctFromProjection(mongoQ) && !TryBindWholeEntityDistinct(mongoQ))
            mongoQ.Select.MarkNotNativelyRepresentable();
        return source; // shaper unchanged: either path leaves the existing shaper (projection or entity) valid
    }

    /// <summary>
    /// Records a <see cref="MongoDistinctOp"/> for a whole-entity <c>Distinct()</c>. Declines if a projection,
    /// grouping, cardinality or unwind source is already present.
    /// </summary>
    /// <remarks>
    /// Over a bare join leaf (<see cref="MongoSelectDefinition.BareJoinEntityLeaf"/>) the op must run after the
    /// <c>$lookup</c>/<c>$unwind</c> and dedup the selected side only: an inner row matched by several outer rows (or
    /// an outer row matching several inner rows) comes back once. Declines if any other <c>$lookup</c> is registered
    /// (its field would be dropped for Inner, compared for Outer), or if paging was deferred past the join (it
    /// lowers after <see cref="MongoSelectDefinition.PostJoinOps"/>, so it would wrongly run after this Distinct).
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
    /// Binds a scalar aggregate via <see cref="NativeCardinalityBinder.TryBindAggregate"/>, marking the query
    /// non-native on failure; reshapes to <paramref name="resultType"/> either way.
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
        // GroupBy(key).Select(aggregate), over the root collection or an eligible join scope
        // (TryGetGroupByJoinScope); a non-null resultSelector or an unbindable key marks non-native.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // A GroupBy over an existing grouping/distinct terminal must not rebind by default: TryBindGroupKey would
        // overwrite the Grouping and silently drop the Distinct (Select(new{a,b}).Distinct().GroupBy(x=>x.k) would count
        // all rows). GroupBy's own Translate override bypasses the guards in NativeSlotPopulator/NativeCardinalityBinder.
        // Read before IsGroupBy is set below, which would make HasTerminalOperator always true.
        var hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalOperator;

        // A completed set-op terminal has no Grouping to overwrite, so it is exempt (as in TranslateSelect).
        var wasSetOpTerminalOnly = mongoQueryExpression.Select.IsSetOpTerminalOnly;

        // A GroupBy over an already-finalized Grouping (Distinct().GroupBy(...) or
        // GroupBy(...).Select(...).GroupBy(...)) isn't the overwrite hazard either:
        // SnapshotPriorGroupingForNestedGroupBy moves the prior grouping into PriorGrouping, and the lowerer emits
        // both $group stages in order. Only TryBindGroupProjection/TryBindDistinctFromProjection leave Grouping
        // set here (TryBindGroupTerminalAggregate also sets Cardinality, ending the query).
        var hasFinalizedPriorGrouping = mongoQueryExpression.Select.Grouping != null;

        // Decided before IsGroupBy is set (which makes HasTerminalOperator true). A nested GroupBy over a finalized
        // grouping resolves against the prior stage's alias, never a join scope.
        var groupByJoinScope = hadTerminalGrouping || hasFinalizedPriorGrouping
            ? null
            : TryGetGroupByJoinScope(mongoQueryExpression, keySelector);

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

        // Carry a GroupByShaperExpression so a subsequent Select over the IGrouping is recognized (grouped branch
        // in TranslateSelect). The definitive grouped-row shaper is compiled in the gate; the key shaper
        // here is a lightweight placeholder (the ungrouped source represents each group's elements).
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

    // isLeftOuter carries the LINQ operator's join semantics to the emitted $lookup/$unwind: Join is inner,
    // LeftJoin/GroupJoin are left-outer. EF lowers a REQUIRED reference navigation to Queryable.Join, which
    // is what makes it drop principals with a dangling foreign key, matching relational EF Core.
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

        // The reference-Include path emits a flat $lookup with no sub-pipeline, so it can only stand in for a join
        // whose inner is a bare collection scan. Record that here, the only point the inner's select is in hand;
        // TryConfirmReferenceIncludeChain declines on it. See MongoSelectDefinition.IsBareCollectionScan.
        if (!innerQueryExpression.Select.IsBareCollectionScan)
        {
            outerQueryExpression.Select.MarkSawNonBareJoinInner();
        }

        var innerEntityType = innerQueryExpression.CollectionExpression.EntityType;
        outerQueryExpression.AddInnerCollection(innerEntityType);

        // Record THIS join up front. One entry per join rather than per target entity type, so a second
        // join onto an already-joined entity type still triggers the flattening below (EF-375), and so a
        // later hop can find this one by position - see AnalyzeKeySelectorTarget (EF-372).
        var joinInfo = outerQueryExpression.AddJoin(innerEntityType, isLeftOuter);

        // An operator composed between two cross-collection joins isn't declined here: on the driver-LINQ path,
        // StripInterleavedJoinChain emits each $lookup at its own boundary (see
        // MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs). Shapes it can't split decline there.

        // Migrate the inner entity's projection (bound to the inner MongoQueryExpression) onto the outer query so the
        // entity shaper can read inner properties from the $lookup result field.
        var reboundInnerShaper = RebindInnerShaperToOuterQuery(
            inner.ShaperExpression, innerQueryExpression, outerQueryExpression, outerKeySelector, innerKeySelector, joinInfo);

        // Paging/dedup already in PostJoinOps (a Where/OrderBy reaching an earlier join's Inner side flipped ActiveOps)
        // lowers after EVERY $lookup/$unwind, including this join's. Unless this join provably neither drops nor
        // multiplies rows, that would page/dedup its joined rows instead of the rows the op was written over
        // (`Join(a).Where(x => x.r.…).Take(1).Join(b)` returned one row instead of b's matches). Checked once
        // Navigation/Lookup are resolved, so it covers every consuming Select arm. Fails closed: no Lookup is not
        // row-count-preserving.
        if (outerQueryExpression.Select.HasNonCommutingPostJoinOp && !joinInfo.IsRowCountPreserving)
        {
            outerQueryExpression.Select.MarkNotNativelyRepresentable();
        }

        // Computed for every join so a later join can tell whether every join so far is eligible. This only
        // builds JoinScope; confirming ($lookup registration) is deferred to the consuming Select arm.
        joinInfo.IsNativelyEligible =
            innerQueryExpression.Select.IsBareCollectionScan
            && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
            && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
            && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);

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
    /// Whether the <c>$lookup</c> built for <paramref name="joinInfo"/> implements the join condition the user wrote:
    /// its <c>localField</c>/<c>foreignField</c> match the key properties' field paths.
    /// </summary>
    /// <remarks>
    /// A wrong-data guard that makes <see cref="MongoSelectDefinition.JoinScope"/> safe to consume.
    /// <see cref="RebindInnerShaperToOuterQuery"/> falls back to any navigation targeting the joined type, so
    /// <c>Owners.Join(Orders, o =&gt; o.Region, r =&gt; r.Region, …)</c> can resolve <c>Owner.Orders</c> and a lookup on
    /// <c>_id</c>/<c>OwnerId</c>, which would join on the wrong fields. For simple keys that method discards such a
    /// navigation and builds a raw-key lookup instead
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
    /// implement the written key equality: whether <paramref name="lookup"/>'s local/foreign fields are the element
    /// paths of the join's own simple outer/inner key properties.
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
        // LookupExpression.GetFieldPath (the helper the lookup was built with) so composite-PK components
        // ("_id.<ElementName>") match too.
        var outerElementPath = LookupExpression.GetFieldPath(outerProperty);
        var outerFieldMatches = lookup.LocalField == outerElementPath
            || lookup.LocalField.EndsWith("." + outerElementPath, StringComparison.Ordinal);

        return outerFieldMatches && lookup.ForeignField == LookupExpression.GetFieldPath(innerProperty);
    }

    /// <summary>
    /// Builds the forced-unwind <c>$lookup</c> for a navigation-backed join hop, with its <c>localField</c>
    /// prefixed by a transitive hop's alias and/or an owned/embedded path (EF-380). Shared by
    /// <see cref="RebindInnerShaperToOuterQuery"/>'s real registration and its key-equality check, so the two
    /// can never disagree about which fields the lookup joins on.
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
            // Transitive join: match against the already-unwound intermediate document; an
            // owned/embedded navigation (EF-380) inserts its path between the intermediate's alias
            // and the field.
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
    /// <paramref name="fkPropertyName"/> on <paramref name="fkOwnerEntityType"/> (the root, or a transitive hop's
    /// target) and the inner key selector's simple property on <paramref name="innerEntityType"/>.
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
    /// Recognizes an anonymous-type join key of simple properties. Both sides construct the same anonymous type, so
    /// members pair by position (<c>new { a.X, Y = a.Z } equals new { b.X, Y = b.W }</c> pairs <c>a.X</c> with
    /// <c>b.X</c> and <c>a.Z</c> with <c>b.W</c>): each member is a property read (<c>x.P</c> or
    /// <c>EF.Property(x, "P")</c>), optionally lifted to its own nullable type, and each inner member reads directly
    /// off the inner parameter. The outer members' hop, and each pair's scalar-ness and storage, are checked by
    /// <see cref="TryResolveAnonymousKeyJoinProperties"/>.
    /// </summary>
    /// <remarks>
    /// Anything else declines ("non-simple key declines"): constant or computed members, nested anonymous types,
    /// non-anonymous constructions (<c>new Key(...)</c> or <c>new Key { ... }</c> compare by the type's own
    /// <c>Equals</c>, reference equality for a plain class), and narrowing or representation-changing conversions.
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
    /// Resolves the entity type a join key selector's outer side reads off: the root entity when
    /// <paramref name="isDirectFromRoot"/>, else the inner side of the prior join at <paramref name="throughLevel"/>
    /// (1-based, at most <paramref name="priorJoinCount"/>), returned as <paramref name="throughJoin"/>. Declines any
    /// other hop.
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
    /// outer/inner key-property pairs, forced to unwind and preserving unmatched rows for a left-outer join. One pair
    /// keeps the localField/foreignField form; more render as <c>let</c> + <c>$and</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="LookupExpression.GetFieldPath"/>, not a bare <c>GetElementName()</c>: a component of a composite
    /// primary key is stored nested under <c>_id</c> (<c>"_id.ProductId"</c>), so the bare name names no field and the
    /// <c>$lookup</c> silently matches nothing in every query mode (pinned by <c>NativeCompositeKeyJoinTests</c>). A
    /// through-hop's local fields are scoped by its alias, as for a transitive navigation hop.
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
    /// Resolves an anonymous join key (<see cref="TryGetAnonymousJoinKeyMembers"/>) to its property pairs: every
    /// outer member must read off the same hop (the root, or one prior join's inner side; no owned-navigation segment)
    /// and every member must name a mapped property.
    /// </summary>
    /// <remarks>
    /// Each pair must also compare in the database as it does in C# (<see cref="IsStoredEqualityFaithfulKeyPair"/>):
    /// otherwise the <c>$lookup</c> silently answers differently (element overlap for arrays, no match across two
    /// stored forms).
    /// </remarks>
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
    /// <para>
    /// Only scalar members: value types and <see cref="string"/>. A primitive collection would match by element
    /// overlap (<c>localField</c>/<c>foreignField</c>) or array equality, and other reference types (<c>byte[]</c>,
    /// lists) compare by reference in C#, so none of them is reproducible.
    /// </para>
    /// <para>
    /// Both sides must be stored alike (<see cref="StoredSerialization.StoredAlike"/>, shared with the projected set
    /// op): the same <c>BsonRepresentation</c>, the same provider CLR type, and either no value converter or
    /// equivalent ones (the same instance, or the same type with structurally equal conversion expressions). An enum
    /// stored as a string on one side and as an int on the other never matches in the database; nor do
    /// <c>BoolToStringConverter("N", "Y")</c> and <c>("F", "T")</c>, whose constants differ in their expressions. This
    /// is stricter than the single-key path, which isn't gated.
    /// </para>
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
    /// <param name="innerShaper">The inner side's shaper.</param>
    /// <param name="innerQueryExpression">The join's inner query expression.</param>
    /// <param name="outerQueryExpression">The join's outer query expression, which the projection moves onto.</param>
    /// <param name="outerKeySelector">The outer key selector, used to identify the navigation.</param>
    /// <param name="innerKeySelector">The inner key selector, used to identify the joined-to key.</param>
    /// <param name="joinInfo">This join's <see cref="JoinInfo"/>, so a later hop can find it by position (see <see cref="AnalyzeKeySelectorTarget"/>).</param>
    /// <returns>
    /// The rebound shaper, or <see langword="null"/> (nothing registered) for a transitive hop whose intermediate
    /// sub-document can't be identified. Null rather than a flag so a caller can't use an un-rebound shaper.
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

        // The outer key selector's FK-access target says whether this join reads off the root entity or a previously-joined
        // intermediate (e.g. the second ".Manager" in Employee.Manager.Manager): a pure ".Outer"* chain reaches the root,
        // one ending in ".Inner" reaches a prior hop. Structural, not by entity type, since self-referencing chains have
        // the same target and through types.
        //
        // A key reached through an owned navigation (e.g. Buyer.Address.RegionId, EF-380) has those segments peeled
        // off first, so the walk still sees a pure Outer/Inner chain.
        var (outerInnerTarget, embeddedSegments) = PeelEmbeddedSegments(
            GetKeySelectorTargetObject(outerKeySelector.Body), outerKeySelector.Parameters[0]);
        var (isDirectFromRoot, throughLevel) = AnalyzeKeySelectorTarget(
            outerInnerTarget, outerKeySelector.Parameters[0], priorJoinCount);

        INavigation? navigation = null;
        JoinInfo? throughJoin = null;
        string? embeddedPath = null;

        // A transitive join resolves the navigation on the join hop the key selector actually reaches through (found
        // by position, not by IEntityType — see above) and remembers it so the $lookup's localField can be prefixed
        // with that intermediate's alias.
        if (TryResolveKeyHopAnchor(
                outerQueryExpression, isDirectFromRoot, throughLevel, priorJoinCount,
                out var anchorEntityType, out throughJoin))
        {
            // Walk any embedded segments via the navigation graph (not CLR type) so sibling owned
            // navigations sharing a CLR type (e.g. ShippingAddress/BillingAddress) resolve to the right
            // one, using each segment's mapped element name for the $lookup localField path.
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
                // IsOnDependent disambiguates a self-referencing relationship declared with both a reference nav
                // (Manager) and its inverse collection nav (DirectReports): both share the same IForeignKey, and picking
                // the collection nav flips the $lookup's join direction (LookupExpression branches on IsOnDependent).
                navigation = searchEntityType.GetNavigations()
                    .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                         && n.IsOnDependent
                                         && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
            }

            navigation ??= searchEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == innerEntityType);
        }

        // A navigation resolved above — by the FK match OR the loose "any navigation onto the joined type"
        // fallback — is only usable if its $lookup reproduces the key equality the user actually WROTE. For a join
        // on other properties between two navigation-related types (`Owners.Join(Orders, o => o.Region, r =>
        // r.Region, ...)`), or a principal-key-to-FK join whose model only has the navigation pointing the OTHER
        // way (`e1.EmployeeID equals e2.ReportsTo` with only Employee.Manager, EF Core's
        // No_orderby_added_for_fully_translated_manually_constructed_LOJ), the resolved navigation's $lookup joins
        // on completely different fields. Discard such a navigation and build the join as a navigation-less
        // raw-key $lookup (the EF-377 branch below) — but only when both key selectors are simple properties that
        // branch can actually resolve; anything else (composite keys, a key reached through an embedded hop) keeps
        // the navigation exactly as before, and TranslateJoinCore's JoinLookupImplementsKeySelectors conjunct
        // still declines it natively.
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

        // Document-shape decision (single source of truth): the driver's native LeftJoin ({ _outer, _inner }) is viable
        // only for a SINGLE reference join. A second cross-collection join flattens everything to root-level
        // $lookup + $unwind fields ("_lookup_<Navigation>"). Rather than toggle a flag, we register the forced-unwind
        // lookups and MongoQueryExpression.UsesDriverJoinFields is computed from that state, so it never contradicts
        // the emitted pipeline.
        //
        // Each cross-collection projection carries its OWNING navigation and a stable "_lookup_<Navigation>" alias; the
        // shaper derives the field it reads from that plus UsesDriverJoinFields (native => "_inner"; flat => the alias),
        // so the projection is never retroactively rewritten.
        // An anonymous-type key (EF-436) never resolves a navigation (GetKeySelectorTargetObject of a `new` is null,
        // so no anchor above) and is built here as a navigation-less raw-key $lookup over every member pair.
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
            // Bare key-equality Join hop with no model navigation (EF-377): build the $lookup directly from the raw
            // outer/inner key property paths. The FK-owning entity type is the root when isDirectFromRoot, else the
            // through-hop's target; localField is scoped by the through-hop's alias as for a transitive navigation hop.
            if (TryResolveRawKeyJoinProperties(
                    throughJoin?.InnerEntityType ?? outerEntityType, innerEntityType, fkPropertyName, innerKeySelector,
                    out var outerProperty, out var innerProperty))
            {
                joinInfo.Lookup = BuildRawKeyJoinLookup(
                    innerEntityType, [(outerProperty, innerProperty)], throughJoin, joinInfo);
            }
        }

        // The trigger counts JOINS, not distinct target entity types: two joins onto the same type must
        // flatten just like two joins onto different ones (EF-375), and a chained hop through a prior
        // join (!isDirectFromRoot) always needs its own field too.
        var isSecondOrLaterJoin = outerQueryExpression.Joins.Count > 1;
        if (isSecondOrLaterJoin)
        {
            // Register this join's $lookup, then every prior join's: each carries its own lookup (left-outer/inner-ness,
            // navigation or raw-key info, transitive localField prefix) built from what it resolved when IT was processed,
            // so same-typed siblings, self-referencing chains and navigation-less hops (EF-377) are never confused.
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
    /// Extracts the object an outer key selector's body reads a property FROM (the "x" in <c>x.Foo</c> or
    /// <c>EF.Property(x, "Foo")</c>). See <see cref="AnalyzeKeySelectorTarget"/>.
    /// </summary>
    private static Expression? GetKeySelectorTargetObject(Expression body)
        => body.RemoveConvert() switch
        {
            MemberExpression member => member.Expression,
            MethodCallExpression call when call.Method.IsEFPropertyMethod() && call.Arguments.Count == 2 => call.Arguments[0],
            _ => null
        };

    /// <summary>
    /// Strips leading member/<c>EF.Property</c> hops through owned types (e.g. "Address" in
    /// <c>x.Inner.Address.RegionId</c>) that aren't transparent-identifier <c>Outer</c>/<c>Inner</c> plumbing.
    /// Returns the remaining target (for <see cref="AnalyzeKeySelectorTarget"/>) and the stripped segment names,
    /// root to leaf.
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
    /// Walks the <c>.Outer</c>/<c>.Inner</c> chain from <paramref name="targetObject"/> down to
    /// <paramref name="parameter"/> to tell whether the selector reads the root entity (a pure <c>.Outer</c>* chain, or
    /// the bare parameter) or a prior hop's result (a chain ending in <c>.Inner</c>). Each <c>.Outer</c> step walks one
    /// hop back toward the root; a terminal <c>.Inner</c> resolves to that step's join (1-based, oldest first).
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
    /// Disambiguates a join's <c>_lookup_&lt;Navigation&gt;</c> alias against registered joins. Two joins can resolve the
    /// same navigation (a LINQ cross product), so each needs its own output field; the first keeps the unsuffixed name.
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
        // EF normalizes every SelectMany to this overload with a trivial TransparentIdentifier(Outer, Inner)
        // resultSelector, followed by a .Select(ti => ti.Inner) unwrap in TranslateSelect. So the returned shaper
        // must keep that TransparentIdentifier shape; EF's ReplacingExpressionVisitor member fold resolves
        // ti.Inner back to our projected shaper.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // Before the terminal guard: when the only terminal so far is a single reference unwind, this may be the
        // second SelectMany of a nested reference shape. BuildBareNavWrappedShaper reads the now-second
        // UnwindSource, producing the doubly-nested TransparentIdentifier EF expects.
        if (mongoQueryExpression.Select.IsSingleReferenceUnwindTerminalOnly
            && NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQueryExpression, collectionSelector))
        {
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);
        }

        // Post-terminal guard: after a set op, GroupBy, Distinct or prior SelectMany, the lowerer picks one terminal by
        // fixed precedence and silently drops the other (`Union(a,b).SelectMany(...)` emits only $unionWith and returns
        // whole outer rows, even under NativeOnly). SelectManyWithCollectionSelector is allowed in NativeSlotPopulator, so
        // the catch-all doesn't back this up.
        //
        // Return null (a hard fail in every mode) rather than MarkNotNativelyRepresentable: the native by-index shaper
        // can't be re-read by the driver-LINQ fallback. See NativeSelectManyTests.
        if (mongoQueryExpression.Select.HasTerminalOperator)
            return null;

        // Query-syntax / explicit-result-selector form: a bare owned nav collection selector
        // (o => o.Items.AsQueryable()), with the real projection in the trailing Select
        // (NativeSelectManyBinder.TryBindTransparentIdentifierProjection). The Inner item shaper exists only to
        // type-check as the TransparentIdentifier and to let an unsupported trailing projection fold during
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
    /// Builds the <c>TransparentIdentifier(Outer, Inner)</c> shaper after a bare-nav SelectMany bind
    /// (<see cref="NativeSelectManyBinder.TryBindBareNavUnwind"/> or
    /// <see cref="NativeSelectManyBinder.TryBindReferenceNavUnwind"/>), which set only
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
    /// Builds the shaper for a native inner-<c>Select</c> owned-collection <c>SelectMany</c>
    /// (<c>o.Items.Select(i =&gt; new {...})</c>) after <see cref="NativeSelectManyBinder.TryBind"/>. Each
    /// projected member binds to the alias the binder registered, as in <see cref="TryBuildGroupResultShaper"/>,
    /// and the result is wrapped in <paramref name="resultSelector"/>'s <c>TransparentIdentifier(Outer, Inner)</c>
    /// for the <c>.Select(ti =&gt; ti.Inner)</c> that follows.
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

    // Registers one projection leaf under `alias` and returns an index-based binding (SelectMany-result members, the
    // join-scope bare leaves, set-op operands; like BindGroupMember). The DOM shaper reads the value raw by alias (for
    // a SelectMany result, the member name, matching NativeSelectManyBinder.TryBind's alias).
    private static Expression BindProjectionByAlias(MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression)
    {
        var index = mongoQueryExpression.AddToProjection(valueExpression, alias);
        return new ProjectionBindingExpression(mongoQueryExpression, index, valueExpression.Type);
    }

    // BindProjectionByAlias for join results (EF-444). When the folded leaf (the join's shaper substituted in) is a
    // whole-entity StructuralTypeShaperExpression, rebind its EntityProjectionExpression under this alias; registering
    // the raw leaf would be a scalar alias read that throws "No known serializer for type '<Entity>'". Non-join callers
    // pass no folded expression, so this is inert for them.
    //
    // A whole-entity leaf that is also an Include target arrives wrapped in IncludeExpressions: unwrap, rebind, re-wrap
    // in order, and route through VisitIncludeExpression so the Include's $lookup registration runs (as
    // NativeJoinScopeProjectionBinder.TryBindProjection does). Otherwise the raw wrapped leaf would be registered under
    // an alias the native $project never emits, silently misreading it.
    private static Expression BindResultMember(
        MongoQueryExpression mongoQueryExpression, string alias, Expression valueExpression,
        MongoProjectionBindingExpressionVisitor projectionBindingExpressionVisitor, Expression? foldedExpression)
    {
        var includeWrappers = new List<IncludeExpression>();
        var unwrappedFoldedExpression = foldedExpression;
        // Two kinds of Include wrapper can be rebound:
        //
        //  * Collection: NavigationExpression is left as is; its $lookup is registered by
        //    MongoProjectionBindingExpressionVisitor via VisitIncludeExpression below.
        //  * Reference (e.g. Set<Order>().Include(o => o.Customer).Select(o => new { o, o.CustomerID }), lowered
        //    to a LeftJoin): its target is the join's Inner side, so its NavigationExpression is rebound by index
        //    like the leaf. NativeJoinScopeProjectionBinder.TryResolveReferenceIncludeLevel admits only that shape
        //    and has already staged the Inner document into the $project.
        //
        // Anything else (e.g. a nested ThenInclude) falls through to BindProjectionByAlias, which fails loudly
        // rather than yielding silent nulls. Every wrapper is checked before anything is rebound, since
        // AddToProjection mutates.
        var allIncludesRebindable = true;
        while (unwrappedFoldedExpression is IncludeExpression includeToUnwrap)
        {
            includeWrappers.Add(includeToUnwrap);
            if (includeToUnwrap.Navigation is not { IsCollection: true }
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
                var navigationExpression = includeWrappers[i].Navigation is { IsCollection: true }
                    ? includeWrappers[i].NavigationExpression
                    : RebindEntityShaper(
                        mongoQueryExpression, (StructuralTypeShaperExpression)includeWrappers[i].NavigationExpression, alias: null);
                rebound = includeWrappers[i].Update(rebound, navigationExpression);
            }

            return projectionBindingExpressionVisitor.VisitIncludeExpression(mongoQueryExpression, (IncludeExpression)rebound);
        }

        // A nested wrapped leaf from a join scope was already staged by NativeJoinScopeProjectionBinder.TryBindProjection
        // as a MongoDocumentConstructionExpression in Select.Projection. Register that, not the raw `new { ... }`, so the
        // shaper reads each member by dotted path; the raw form would be an alias read of an anonymous type and throw.
        // Uses the same lookup as MongoProjectionBindingExpressionVisitor.TryGetNativeDocumentConstructionLeaf so the two
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

    // Native whole-entity or projected Union/Concat/Intersect/Except, appended to source1's select. Union/Concat
    // always return source1: native when admissible, otherwise marked non-native for a graceful fallback.
    // Intersect/Except return null when inadmissible (see below).
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
        //
        //   Left-nested (A.Concat(B).Concat(C)): append another link to source1's chain. Each Union link's
        //     dedup is emitted right after its own $unionWith, which is what makes Concat(Union(A,B),C) correct.
        //
        //   Right-nested (A.Concat(B.Union(C))): can't be flattened, since A.Concat(B).Union(C) would also dedup
        //     A. The operand keeps its own chain and is emitted inside the outer $unionWith's pipeline.
        //
        // These compose to any depth (IsWholeEntitySetOpOperandSelect recurses). Intersect/Except are excluded:
        // they lower to MongoSetDifferenceStage and have no driver-LINQ oracle, so they keep hard-failing below.
        if (kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
            && (IsPlainWholeEntitySelect(mongo1) || IsWholeEntitySetOpChainSelect(mongo1))
            && (IsPlainWholeEntitySelect(mongo2) || IsWholeEntitySetOpChainSelect(mongo2))
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            // IsSetOp is already true when mongo1 is itself a chain; setting it is needed when it is plain.
            AppendSetOperationLink(mongo1, mongo2, kind);
            return source1;
        }

        // Projected operands: each side may be a plain projected Select, a projected Distinct, or a
        // GroupBy(key).Select(aggregate); the combine compares flattened values by alias regardless. Different
        // collections are fine; ProjectionShapesMatch is a correctness guard, since dedup/source-tagging compare
        // whole projected documents, and OperandSerializationsMatch requires each alias to be stored the same way
        // on both sides (a value converter on one side only would compare/read mismatched BSON).
        //
        // source1's own pending lookups (e.g. a projected Orders.Select(o => o.OrderDetails.Count())) are
        // admitted because the lowerer emits them ahead of source1's $project. source2's operand select carries
        // no lookup plumbing, so it must have none.
        //
        // source1's shaper is reused for every combined row, so a constant/parameter leaf in its projection
        // (baked into the shaper, not read per document) would show source1's value on source2's rows.
        // HasShaperUnsafeConstantLeaf rejects that; the server-side pipeline is fine either way. The exception is a
        // bare `$literal`-rendered constant/parameter of a round-trip-safe type (CanRebindConstantLeafToDocument): its
        // shaper is rebound to read the projected value per document, after every check has passed.
        //
        // Bare scalar operands may differ in alias (a column reads by its document path, e.g. `_id`; a computed value,
        // constant or grouped aggregate by `_v`). CanAlignBareScalarAliases admits those, and only then is mongo2's
        // single projection re-aliased to mongo1's, after every check has passed.
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
                // Admitted through CanAlignBareScalarAliases. The combined rows are read through source1's shaper, which
                // reads source1's alias; the kind-shape check (NativeDateTimeKindReadBack.HasKindMismatch, at compile
                // time) pairs operand leaves by alias, so it now sees this pair too.
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

    // Whether the select is already a whole-entity Concat/Union nesting, so it can take another link or serve as
    // an operand. IsWholeEntitySetOpOperandSelect adds the recursive structural checks; the rest mirrors
    // IsPlainWholeEntitySelect's query-level checks.
    private static bool IsWholeEntitySetOpChainSelect(MongoQueryExpression mongo)
        => mongo.Select.IsSetOpTerminalOnly
           && mongo.Select.SetOperations.Count > 0
           && IsWholeEntitySetOpOperandSelect(mongo.Select)
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !mongo.Select.HasClientWrappedWholeEntityShaper
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // Recursive: a whole-entity Concat/Union tree the lowerer can emit as a self-contained sub-pipeline. A plain
    // whole-entity select is the base case.
    //
    // Each conjunct rejects something operand lowering doesn't emit, which would otherwise be silently dropped:
    // no $project/$group/$unwind for a whole-entity operand (a whole-entity Distinct is a MongoDistinctOp in
    // PipelineOps, so it's fine), and projected or Intersect/Except links lower differently.
    //
    // TrailingOps may be non-empty: ops between two links (A.Union(B).OrderBy(..).Take(1).Union(C)) become the
    // next link's PrecedingOps (MongoSelectDefinition.AppendSetOperation) and are emitted before its stage.
    private static bool IsWholeEntitySetOpOperandSelect(MongoSelectDefinition select)
        => select.Projection.Count == 0
           && select.Grouping == null
           && select.Cardinality == null
           && select.UnwindSources.Count == 0
           && select.SetOperations.All(
               link => !link.OperandsProjected
                       && link.Kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
                       && IsWholeEntitySetOpOperandSelect(link.OperandSelect));

    // Filter/sort/paging only: no projection, grouping, scalar cardinality, own set op, SelectMany unwind, lookups,
    // or VectorSearch.
    //
    // UnwindSource: a whole-element owned SelectMany (SelectMany(o => o.Items)) has Route == WholeEntity and no
    // lookups, but operand lowering emits no $unwind for mongo2 and source1's $unwind (plus its inner-element
    // filter) would run after the $unionWith over both sides, silently returning wrong rows. Pinned by
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
    // HasArrayProjectionLeaf: an owned-collection array leaf (Select(b => new { b.Title, b.Posts })) makes
    // NativeProjectionBinder emit the owner key into the projected document. Projected-operand dedup
    // ($group{_id:"$$ROOT"}) and source-tagging compare the whole document, so that key would turn value
    // dedup (pinned by Projected_operand_union_dedups_over_projected_values_not_whole_entities) into identity
    // dedup. Declining lets Union/Concat fall back; Intersect/Except (no oracle) hard-fail. A trailing
    // projection after a whole-entity set op is unaffected: its dedup runs before the $project.
    //
    // Bare projected operands (Select(b => b.Title)) are admitted: apart from the array case above, the
    // projected document is exactly the compared value. Pinned by NativeBareProjectionTests.
    //
    // allowPreCombineLookups is passed only for source1, whose InjectAfterRoot projected-Count lookups the
    // lowerer emits ahead of its $project. So is a confirmed single-level join's $lookup (IsPreCombineJoinScope).
    // Other lookups still decline.
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

    // A projected source1 over a single-level join scope whose confirming Select registered the join's $lookup
    // (e.g. Orders.Where(o => o.Customer.City == "London").Select(o => new { o.OrderDate }), which nav-expands to a
    // join). HasConfirmedJoinLookup is set on a projected, ungrouped, unreduced select only by TranslateSelect's
    // join-scope arms, each gated by IsSingleEligibleNativeJoinScope, so this reuses that decision rather than
    // re-running it (it may defer PipelineOps). MongoSelectLowerer's OperandsProjected branch emits the join the way
    // the non-set-op path does: $lookup/$unwind, then PostJoinOps (the inner-side Where) and PostLookupPagingOps,
    // all ahead of source1's $project. The gate and that emission must move together: without the emission the
    // inner-side $match would be silently dropped.
    //
    // Single-level only (JoinScope.Levels.Count == 1): a join chain is unvalidated here.
    //
    // The HasConfirmedJoinLookup conjunct is defence in depth and can't be killed by mutation: an unconfirmed candidate
    // join already makes Route Fallback, so IsPlainProjectedSelect's Route check declines first.
    //
    // Serves every set-op kind: Intersect/Except over such a source1 lower to MongoSetDifferenceStage over the same
    // pre-combine stages (NativeGroupByOverJoinTests.Intersect_/Except_of_a_join_scope_operand_...).
    private static bool IsPreCombineJoinScope(MongoQueryExpression mongo)
        => mongo.Select.HasConfirmedJoinLookup
           && mongo.Select.JoinScope is { Levels.Count: 1 };

    // A projected Distinct (Select(new {...}).Distinct(), Route == GroupBy via TryBindDistinctFromProjection) as
    // a set-op operand; its $group + flattening $project become its pre-combine pipeline. IsDistinct and
    // IsGroupBy are mutually exclusive, so a real GroupBy is handled by IsPlainGroupBySelect. PriorGrouping
    // excludes a GroupBy nested on the Distinct. A whole-entity Distinct is covered by IsPlainWholeEntitySelect.
    private static bool IsPlainDistinctSelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsDistinct
           && !mongo.Select.IsGroupBy
           // Same reason as IsPlainProjectedSelect: the Distinct keeps the flag of the Select it deduplicates.
           && !mongo.Select.HasClientEvaluatedProjectionLeaf;

    // A real GroupBy(key).Select(aggregate) as a set-op operand. The join wrong-data hazard of grouped sources
    // doesn't apply to Union/Concat, and the lowerer emits a Grouping-bearing operand's $group + $project the
    // same way for Distinct and GroupBy (AppendSetOpChainStages). Mirrors IsPlainDistinctSelect.
    private static bool IsPlainGroupBySelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsGroupBy
           // Operand lowering emits only the operand's $group + flattening $project, so post-group
           // Skip/Take, HAVING, or OrderBy would be silently dropped. Such operands fall through to the
           // out-of-scope decline.
           && mongo.Select.GroupPagingOps.Count == 0
           && mongo.Select.GroupHavingPredicate == null
           && mongo.Select.GroupOrderOp == null;

    // The conjuncts IsPlainDistinctSelect and IsPlainGroupBySelect share: a Grouping-bearing GroupBy-route select with
    // no prior grouping, scalar cardinality, SelectMany unwind, join or VectorSearch, and no lookups beyond (for
    // source1 only) InjectAfterRoot ones.
    private static bool IsPlainGroupedOperandSelect(MongoQueryExpression mongo, bool allowPreCombineLookups)
        => mongo.Select.Route == NativeRoute.GroupBy
           && mongo.Select.Grouping != null
           && mongo.Select.PriorGrouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && (allowPreCombineLookups ? mongo.Lookups.All(l => l.InjectAfterRoot) : mongo.Lookups.Count == 0)
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // mongo1's shaper is reused for every combined row, so a constant/parameter leaf (never read from the
    // document) would repeat mongo1's value. Only unsafe in that context; standalone this shape is fine (see
    // NativeComputedBareProjectionTests), so it isn't folded into IsPlainProjectedSelect. The one exception, a bare
    // round-trip-safe leaf on an ungrouped select, is CanRebindConstantLeafToDocument.
    //
    // A projected Distinct (TryBindDistinctFromProjection) moves the projected values into its key parts and projects
    // element refs to them, but leaves the Select's shaper as built, constant baked in; so its key parts are checked
    // too. Scoped to IsDistinct: a real GroupBy's constant key (GroupBy(x => 8).Select(g => new { A = g.Key, ... }))
    // is read back from `_id` by the grouped shaper.
    private static bool HasShaperUnsafeConstantLeaf(MongoQueryExpression mongo)
        => mongo.Select.Projection.Any(p => IsConstantOrParameterLeaf(p.Expression))
           || mongo.Select is { IsDistinct: true, Grouping: { } grouping }
           && grouping.Key.Any(k => IsConstantOrParameterLeaf(k.FieldRef));

    private static bool IsConstantOrParameterLeaf(MongoExpression expression)
        => expression is MongoConstantExpression or MongoParameterExpression;

    // Whether source1's constant/parameter leaf can be read from the document instead of the shaper: a bare scalar
    // whose single projection is a server-rendered (`$literal`) constant or parameter of a type whose BSON round-trip
    // returns the C# value. The gate and the shaper rebind in TryTranslateSetOperation both go through this predicate
    // (internal for NativeSetOperationConstantLeafRebindTests).
    //
    // - Bare scalar only: a construction's members are read by name from its own shaper.
    // - Constant/parameter leaf, trial-rendered by TryProbeBareValueRenders: the rebind reads a server-rendered value,
    //   never a client-evaluated one. (Both are defence in depth: HasShaperUnsafeConstantLeaf already found such a
    //   leaf, and NativeProjectionBinder probed it when binding.)
    // - Round-trip-safe CLR type: the value is read back by the default serializer for the shaper's type. A DateTime
    //   reads back UTC-kind, a Guid depends on the representation, an enum on its storage type; those decline.
    // - Ungrouped select only: the rebind binds a plain projection member, which a grouped shaper
    //   (GroupBy(x => x.N).Select(g => 8)) doesn't read (it throws "Document element '_v0' is missing"). A projected
    //   Distinct is grouped too and its projection is an element ref, never a constant.
    // - Nothing already bound under the leaf's alias in the read-side projection list: the rebind registers the value
    //   through AddToProjection, which renames a colliding alias (`_v` -> `_v0`), a field the pipeline never emits. An
    //   arm that already bound the leaf there (the grouped shaper; the join-scope bare-value arm, if it ever admits a
    //   constant) would make this a double-bind. It overlaps the ungrouped conjunct: the grouped constant result is
    //   rejected by each alone, so neither is killable by mutation on its own (removing both fails
    //   Union_of_grouped_constant_result_and_column_declines and Grouped_constant_result_does_not_rebind). Beyond that
    //   it is defence in depth: no other reachable shape carries such an entry.
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

    // A bare scalar operand (Select(x => x.Id), Select(x => x.N + 1), GroupBy(..).Select(g => g.Count())): a single
    // projected value whose shaper isn't a construction. Detected by shape; IsBareProjection isn't set for a grouped
    // g => g.Count().
    private static bool IsBareScalarOperand(ShapedQueryExpression source, MongoQueryExpression mongo)
        => mongo.Select.Projection is [{ Expression: not MongoDocumentConstructionExpression }]
           && source.ShaperExpression.RemoveConvert() is not (NewExpression or MemberInitExpression);

    // Whether two bare scalar operands with different aliases can be combined by re-aliasing source2's single
    // projection to source1's. The gate and the rewrite in TryTranslateSetOperation both go through this predicate
    // (internal for NativeSetOperationAliasAlignmentTests).
    //
    // - Only a bare scalar: a construction's members are read by name, so its aliases must match (ProjectionShapesMatch).
    // - No post-group ops on source2: its post-Distinct/post-GroupBy ops (a filter/sort/page after a Distinct, or a
    //   Where after a GroupBy recorded via MongoProjectedAliasScope) read its own flattened alias, which the re-alias
    //   would orphan (such a Where would silently match nothing, dropping every source2 row).
    // - Default serialization on both sides: every combined row is read through source1's shaper, so a value-converted or
    //   non-default-representation column on either side would be read with the other side's serializer.
    internal static bool CanAlignBareScalarAliases(
        ShapedQueryExpression source1, MongoQueryExpression mongo1, ShapedQueryExpression source2, MongoQueryExpression mongo2)
        => IsBareScalarOperand(source1, mongo1)
           && IsBareScalarOperand(source2, mongo2)
           && mongo2.Select.PostGroupOps.Count == 0
           && ReadsOnlyDefaultSerializedFields(mongo1.Select)
           && ReadsOnlyDefaultSerializedFields(mongo2.Select);

    // A grouped operand's flattening projection reads the $group output, so its key parts and accumulator operands are
    // what reach the stored fields. The key-part conjunct is defence in depth: a grouping key over a converted or
    // non-default-representation field already declines (NativeGroupByBinder.HasDefaultKeySerialization).
    private static bool ReadsOnlyDefaultSerializedFields(MongoSelectDefinition select)
        => select.Projection.All(p => MongoExpressionTranslator.AllFieldsDefaultSerialized(p.Expression))
           && (select.Grouping is not { } grouping
               || (grouping.Key.All(k => MongoExpressionTranslator.AllFieldsDefaultSerialized(k.FieldRef))
                   && grouping.Accumulators.All(
                       a => a.Operand is null || MongoExpressionTranslator.AllFieldsDefaultSerialized(a.Operand))));

    // Operands must have the same top-level alias set: dedup and source-tagging compare whole projected
    // documents. Field refs may differ (new {N = a.Name} vs new {N = b.Title}). EF already requires a common
    // anonymous type, so this is defence in depth.
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

    // Each alias's values must be stored the same way on both operands: dedup and Intersect/Except compare stored
    // BSON, and source1's shaper reads every combined row through source1's property serializer. Without this,
    // Converted.Select(o => o.Status) (stored "Shipped") against Other.Select(o => o.Status) (stored 1) dedups
    // nothing, intersects to empty, or throws reading 1 as a string. Covers bare and wrapped projected operands and
    // projected Distinct operands (whose key part is read back from "_id.<alias>"); a real GroupBy key is already
    // default-serialized (HasDefaultKeySerialization gates it). DateTimeKind is checked separately
    // (NativeDateTimeKindReadBack).
    private static bool OperandSerializationsMatch(MongoSelectDefinition select1, MongoSelectDefinition select2)
    {
        foreach (var projection1 in select1.Projection)
        {
            // MongoProjection is a struct: an absent alias is default, with a null Expression.
            var expression2 = select2.Projection.FirstOrDefault(p => p.Alias == projection1.Alias).Expression as MongoExpression;

            // A DateTime.TimeOfDay leaf stores milliseconds (a long), unlike any other TimeSpan (a TimeSpan property's
            // default string form): the combine would dedup/intersect a long against a string, and the alias would be
            // read through only source1's reader (MongoSelectDefinition.IsTimeOfDayProjection). Both sides TimeOfDay
            // store and read the same way.
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

    // Both sides default-serialized (a computed value, null here, has no property and so no converter); or the same
    // property; or two properties stored identically (StoredSerialization.StoredAlike, shared with the anonymous-key
    // join: equal BsonRepresentation, equal provider type, equivalent converters), e.g. two HasConversion<string>()
    // enums or two [BsonRepresentation(String)] enums. Anything else (a converter or representation against none, two
    // different converters or representations) declines.
    private static bool StoredSerializationsMatch(MongoFieldExpression? field1, MongoFieldExpression? field2)
    {
        var default1 = field1 == null || NativeGroupByBinder.HasDefaultKeySerialization(field1.Property);
        var default2 = field2 == null || NativeGroupByBinder.HasDefaultKeySerialization(field2.Property);
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
