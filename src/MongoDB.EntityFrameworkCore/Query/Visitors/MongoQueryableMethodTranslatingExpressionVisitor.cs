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

    private static readonly HashSet<string> OrderingMethodNames =
    [
        nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
        nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending)
    ];

    /// <summary>
    /// Elides a <c>ThenBy</c>/<c>ThenByDescending</c> ordering whose key selector matches an earlier ordering
    /// already established in the same <c>OrderBy</c>/<c>ThenBy</c> chain (see <see cref="KeySelectorsMatch"/>
    /// for what "matches" means here). Once a key fully determines the sort order, re-ordering by it again -
    /// in either direction - is a no-op; left untouched, the driver's LINQ provider renders both orderings
    /// into a single <c>$sort</c> stage and MongoDB rejects the resulting document for its duplicate field
    /// name (EF-253 / CSHARP-5690). Relational EF providers already drop this kind of redundant ordering from
    /// the generated SQL, so this mirrors their behavior rather than merely working around the driver
    /// limitation. Orderings that supply an explicit <see cref="IComparer{T}"/> are left untouched (in either
    /// role - as a candidate for elision, or as prior state a later ordering could match) since a custom
    /// comparer can make an otherwise-identical key selector not actually redundant.
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

        // Collect the contiguous ordering chain, outer (last-applied) to inner (first-applied), then
        // reverse it so it can be replayed in chronological order.
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
            // OrderBy/OrderByDescending establish a brand new ordering - even mid-chain (e.g.
            // .OrderBy(k).OrderByDescending(k)) - superseding whatever came before, so only ThenBy/
            // ThenByDescending calls continue an existing chain for duplicate-detection purposes.
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
    /// Whether two ordering key selectors are both a *direct*, single-hop access to the same member of the
    /// lambda parameter (e.g. <c>x =&gt; x.CustomerId</c>), modulo the identity of their (distinct) lambda
    /// parameters. Deliberately narrow to a single hop: the driver's LINQ provider renders a direct property
    /// access as-is against its raw field path, so two orderings on the *same* directly-accessed member are
    /// what collide into a single <c>$sort</c> document with a duplicate field name (EF-253 / CSHARP-5690).
    /// Anything requiring computation - a further member hop off that property (<c>x.Name.Length</c>), a
    /// method call (<c>Math.Truncate(x.Amount)</c>), etc. - instead gets materialized by EF into its own
    /// uniquely-named projected field even when repeated, so it never collides and must not be elided here.
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

    /// <summary>
    /// Visit the <see cref="MethodCallExpression"/> to capture the cardinality and final expression
    /// when found on a <see cref="Queryable"/> method.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="ShapedQueryExpression"/> if this method was on a <see cref="Queryable"/>,
    /// otherwise <see cref="QueryCompilationContext.NotTranslatedExpression"/>.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        _finalExpression ??= ElideRedundantOrderings(methodCallExpression);

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

            // The pushed-down bare collection-navigation `Count` body is null-coalesced here, one statement
            // after the capture, for a projection committed under ProjectionAliasTier.Synthetic. This can't
            // live in the projection binder's own commit block: the assignment on the line above overwrites
            // anything written there, because _finalExpression is the whole captured chain and is
            // re-assigned after EVERY translated Queryable call, including the Select whose translation runs
            // the binder. Applying it here keeps it at TRANSLATION time and unconditional, covering the
            // explicit-DriverLinq leg as well as the late-decline one.
            //
            // It is inert on the native route but not because "only the driver-LINQ bridge reads
            // CapturedExpression" (several native-routing sites also read it — ContainsVectorSearch,
            // GetOnZeroResultsAction, the bulk ExecuteUpdate/ExecuteDelete path, and exception-message
            // Print() sites); rather the rewrite's own reach is narrow — see
            // NullCoalesceSyntheticBareCountBody's remarks.
            var capturingQueryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;
            capturingQueryExpression.CapturedExpression =
                NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
                    _finalExpression, capturingQueryExpression.Select);
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
        // Any other (projecting) Select that this method can't lower into a native $project marks the
        // query as no longer natively representable.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        if (source.ShaperExpression is GroupByShaperExpression)
        {
            // GroupBy(key).Select(aggregate): bind the accumulators (and finalize MongoSelectDefinition.Grouping)
            // when the projection is a supported shape (g.Key parts + Count/Sum/Min/Max/Average accumulators);
            // otherwise mark non-native so the query falls back to driver-LINQ. Either way translation must
            // complete without hard-throwing.
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

        // The trailing projection of an explicit-result-selector / query-syntax owned SelectMany.
        // UnwindSource is set (by TranslateSelectMany's bare-nav bind) with no Projection yet;
        // bind ti.Outer/ti.Inner two-scope, build the projected shaper (by-alias, like the inner-Select form /
        // GroupBy), and skip the generic fold below (this Select's shaper is source.ShaperExpression's own
        // TransparentIdentifier(Outer, Inner) unfolded — the fold would just re-derive the same leaves we
        // already bound natively here). A projection this binder rejects (computed leaf, non-ti.Outer/Inner
        // shape) falls through unchanged to the existing guards below.
        if (mongoQueryExpression.Select.UnwindSource != null
            && mongoQueryExpression.Select.Projection.Count == 0
            && NativeSelectManyBinder.TryBindTransparentIdentifierProjection(
                mongoQueryExpression, selector, out var bareSelectManyLeafAlias))
        {
            // A BARE (non-`new {}`) computed body is bound under a single reserved alias the binder chose and
            // hands back; anything else is the wrapped anonymous/DTO shape BuildSelectManyResultShaper walks
            // member by member. The branch is keyed on the BINDER's own answer rather than on a restatement of
            // "which bodies are bare" here, so the shaper can never disagree with what was actually bound.
            var selectManyShaper = bareSelectManyLeafAlias != null
                ? BindSelectManyMember(mongoQueryExpression, bareSelectManyLeafAlias, selector.Body)
                : BuildSelectManyResultShaper(mongoQueryExpression, selector.Body);
            return source.UpdateShaperExpression(selectManyShaper);
        }

        if (TryGetReferenceIncludeChain(selector, out var transitiveLevels) is { } referenceIncludeChain)
        {
            if (!TryConfirmReferenceIncludeChain(mongoQueryExpression, referenceIncludeChain, transitiveLevels))
            {
                // Recognized the SHAPE but declined the case (composite key, post-terminal, transitive
                // hop, a mismatched join count, …). The candidate join(s) stay unconfirmed, so Route
                // computes Fallback (MongoSelectDefinition.HasUnconfirmedCandidateJoin).
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

            // On success: do NOT mark non-representable, mirroring IsSingleLevelCollectionIncludeSelector's
            // own "must not mark non-representable" rule below. The collection level's own $lookup is not
            // touched here at all — it registers unconditionally, later, during native shaper/
            // projection-binding compilation (MongoProjectionBindingExpressionVisitor's IncludeExpression
            // case), exactly as it already does for a bare single collection Include.
        }
        else if (IsSingleLevelCollectionIncludeSelector(selector) && mongoQueryExpression.Select.HasTerminalOperator
                 && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
        {
            // Post-terminal guard for a collection Include: EF Core's own
            // NavigationExpandingExpressionVisitor requires the SAME Include on both operands of a set
            // operation and, when that holds, HOISTS it to apply AFTER the combinator — i.e.
            // "A.Include(x).Union(B.Include(x))" reaches this Select as "Union(A, B).Select(x =>
            // Include(x))", with mongoQueryExpression.Select.IsSetOp (or IsGroupBy/IsDistinct for the
            // analogous GroupBy/Distinct cases) already set by the preceding TranslateUnion/Concat/
            // GroupBy/Distinct.
            //
            // EF-397 makes the SET-OP-ONLY terminal an exception, matching the exemption the projected-Select
            // branch below already carries. The reason this used to decline was NOT that the shape is
            // unrepresentable but that MongoSelectLowerer emitted the $lookup at step 2, i.e. BEFORE the
            // $unionWith — so rows contributed by the operand pipeline (which lowers from
            // setOp.OperandSelect.PipelineOps alone and carries no lookups) came back with an EMPTY Include
            // collection: silent wrong data, not a translation failure. The lowerer now DEFERS the whole
            // lookup block for a set-op query until after the set-op stage and TrailingOps, so the join runs
            // once over the combined (and, for Union, already-deduped) stream and every row is joined. See
            // the paired comments in MongoSelectLowerer.Lower — the gate here and that emission must move
            // together.
            //
            // Everything else stays declined, by construction rather than by restatement:
            // IsSetOpTerminalOnly is false for a GroupBy/Distinct/SelectMany terminal (whose $group/$unwind
            // the lookup block genuinely cannot be reconciled with) and false once a trailing projection has
            // been populated, so an Include composed after one of those still falls back. A REFERENCE
            // Include never reaches this branch at all — it is recognized above and declined by
            // TryConfirmReferenceIncludeChain's own post-terminal check, which this does not touch.
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
        else if (!IsTransparentIdentifierSelector(selector) && !IsSingleLevelCollectionIncludeSelector(selector)
                 && !IsTransparentIdentifierMemberAccessSelector(selector)
                 && !IsOwnedEmbeddedIncludeSelector(selector))
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
            // A set-op-ONLY terminal is EXEMPT — a trailing anonymous/DTO member-access Select
            // after a whole-entity set op pushes down a $project (emitted after the set-op stage by the lowerer's
            // Projection block, via the slice-B fall-through). IsSetOpTerminalOnly requires Projection.Count == 0,
            // so once this projection is populated a SECOND projection (or any post-projection operator) is no
            // longer set-op-terminal-only and correctly falls back here. A GroupBy/Distinct/SelectMany terminal
            // (IsSetOpTerminalOnly false) still marks non-native, exactly as before.
            if (mongoQueryExpression.Select.HasTerminalOperator && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // Native projection pushdown: a terminal anonymous-type / DTO projection whose leaves are
            // all top-level member accesses only is lowered to a $project stage. Anything else (bare scalar, computed
            // leaves, entity references, non-member bindings) is not natively representable and falls back.
            else if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }

        // A bare-nav owned/reference SelectMany (UnwindSource set by NativeSelectManyBinder.TryBindBareNavUnwind)
        // whose trailing selector is a whole-inner-entity `ti => ti.Inner` access — e.g. `from o in q from i in
        // o.Items select i`, `SelectMany(o => o.Items, (o, i) => i)`, or the bare 1-arg `SelectMany(o => o.Items)`
        // — bypasses both the pending-SelectMany projection branch above (which only binds an anonymous/DTO
        // construction) and the post-terminal guard above (skipped for a transparent-identifier member-access
        // selector). When representable (IsWholeElementRepresentable), it sets UnwindSource.WholeElement, which
        // drives the lowerer to emit $unwind(includeArrayIndex) + $replaceRoot — a $mergeObjects sentinel form
        // for Owned (carrying the owner key + array ordinal, see MongoReplaceRootStage) or a plain $replaceRoot
        // for Reference (the $lookup's unwound element is already a whole, independently-keyed document).
        // Control then falls through to the generic shaper fold below, which resolves
        // TransparentIdentifier(outer, item).Inner to the element shaper BuildBareNavWrappedShaper already built
        // — materialized by MongoShapedQueryCompilingExpressionVisitor's WholeElement branch, rooting the
        // standard DOM shaper at the element type instead of the collection root. Leaving Select.Projection
        // empty and Route falling through to WholeEntity without setting WholeElement would let the gate go
        // native for a bare owned/reference entity that was never actually materialized, crashing the DOM
        // shaper with an internal KeyNotFoundException instead of cleanly declining.
        //
        // TryGetWholeEntityMemberAccess(selector) distinguishes all whole-entity shapes (inner or outer) from a
        // computed-leaf selector (e.g. `ti => new { X = ti.Inner.Price * 2 }`, a NewExpression rather than a
        // bare member access), which must keep falling back gracefully via MarkNotNativelyRepresentable() in
        // the else branch below — its driver-LINQ fallback genuinely succeeds with correct results. The
        // whole-OUTER (`select o`) case and an unrepresentable element (an eager-loaded navigation for
        // Reference; a cross-collection nav / sentinel-wrapper collision / non-default-serialized shadow key
        // for Owned — see IsWholeElementRepresentable)
        // throw a plain NotSupportedException here at TRANSLATION time rather than
        // NativeTranslationNotSupportedException: this call site runs before the compile-time gate
        // (MongoShapedQueryCompilingExpressionVisitor) that would otherwise catch the latter and fall back, so
        // nothing downstream would ever catch it in any MongoQueryMode.
        if (mongoQueryExpression.Select.UnwindSource is { } wholeElementCandidateUnwind
            && mongoQueryExpression.Select.Projection.Count == 0)
        {
            var wholeEntityMember = TryGetWholeEntityMemberAccess(selector);

            if (wholeEntityMember is { Member.Name: "Inner" }
                && wholeElementCandidateUnwind.Kind is MongoUnwindSourceKind.Owned or MongoUnwindSourceKind.Reference
                && IsWholeElementRepresentable(wholeElementCandidateUnwind.InnerEntityType, wholeElementCandidateUnwind.Kind))
            {
                // Bare whole-inner-element SelectMany — owned (embedded) OR reference (cross-collection). The
                // lowerer emits $unwind → $replaceRoot (owned: $mergeObjects sentinel form; reference: plain,
                // after the $lookup+$unwind) and materializes the element from the re-rooted document; fall
                // through to the generic shaper fold below, which resolves TransparentIdentifier(outer, item).Inner
                // to the element shaper BuildBareNavWrappedShaper already built.
                wholeElementCandidateUnwind.WholeElement = true;

                // Re-root the query's ROOT ProjectionMember at the ELEMENT's own entity type. That member is
                // what BuildBareNavWrappedShaper's element shaper binds through, and until now it resolved to
                // the OUTER (owner) entity's EntityProjectionExpression — correct enough for the element's own
                // scalar leaves (which read straight off the root document either way) but wrong for anything
                // that has to bind a MEMBER against the projection's entity type: a nested owned navigation
                // reaches EF's auto-IncludeExpression machinery, which calls BindNavigation on the OWNER's
                // projection and throws. After $replaceRoot the element IS the root document, so a projection
                // rooted at the element type is the accurate description of it, and a nested owned member's
                // scalar leaves then read from direct dotted paths relative to that document exactly as they do
                // for an ordinary owned reference navigation on a normal query root.
                //
                // Safe to do unconditionally here: the trailing `ti => ti.Inner` selector drops the OUTER
                // shaper entirely (only the element shaper survives the fold below), and the mapping is
                // consumed — then replaced wholesale — by the _projectionBindingExpressionVisitor.Translate
                // call at the end of this method. This is also why it must happen HERE and not in
                // BuildBareNavWrappedShaper, which runs from TranslateSelectMany before it is known whether the
                // trailing selector projects the whole element at all.
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
    /// <c>TransparentIdentifier(Outer, Inner)</c> back down to the operator's real result type — the native
    /// inner-<c>Select</c> owned-collection SelectMany's mandatory unwrap Select is exactly this shape. This
    /// selector carries no projection of its own to push down — it is a pure field-of-a-
    /// freshly-built-object read that <see cref="ReplacingExpressionVisitor"/>'s own <c>NewExpression</c>-
    /// member fold resolves directly to whatever the wrapping Select/SelectMany already built for that slot —
    /// so it must bypass BOTH the post-terminal guard and <see cref="NativeProjectionBinder"/> here (mirroring
    /// <see cref="IsTransparentIdentifierSelector"/>/<see cref="IsSingleLevelCollectionIncludeSelector"/>).
    /// Without this, a SelectMany whose own binder set <see cref="MongoSelectDefinition.UnwindSource"/> (which
    /// makes <see cref="MongoSelectDefinition.HasTerminalOperator"/> true) would have this MANDATORY,
    /// EF-synthesized unwrap Select immediately trip the post-terminal guard and mark the query non-native —
    /// even though it is not a user-authored operator chained after a terminal, just EF's own internal
    /// TransparentIdentifier bookkeeping. Safe for Join/GroupJoin/LeftJoin too, though not for the reason
    /// once claimed here: <c>TranslateJoinCore</c> does NOT unconditionally mark the outer side non-native —
    /// it only does so for the GroupBy/Distinct hard-decline cases. A join is kept off the native pipeline by
    /// <see cref="NativeSlotPopulator"/>'s catch-all instead (<c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c> are
    /// not listed in <c>IsNativeRepresentableSlotOperator</c>), so skipping this guard for their own
    /// <c>ti.Inner</c>/<c>ti.Outer</c> unwrap changes nothing for them either way.
    /// </summary>
    private static bool IsTransparentIdentifierMemberAccessSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal))
        {
            return false;
        }

        // EF auto-Includes every owned navigation reachable from the projected result, so when the projected
        // side itself owns further navigations this MANDATORY unwrap Select arrives Include-WRAPPED
        // (IncludeExpression(ti.Inner, nav), possibly chained) rather than as a bare member access — exactly
        // the same unwrapping TryGetWholeEntityMemberAccess does for the same reason. It still carries no
        // projection of its own to push down, so it must still bypass the post-terminal guard; without this
        // unwrap a whole-inner-element SelectMany over an element with a nested owned member marks itself
        // non-native here and can never reach the WholeElement branch that supports it (EF-353).
        //
        // Only EMBEDDED (owned) navigations are unwrapped, mirroring IsOwnedEmbeddedIncludeSelector: a
        // cross-collection Include needs a $lookup this shape never emits, so it must keep tripping the guard
        // and fall back.
        var body = selector.Body;
        while (body is IncludeExpression { Navigation: INavigation navigation } include && navigation.IsEmbedded())
        {
            body = include.EntityExpression;
        }

        return body is MemberExpression { Member.Name: "Outer" or "Inner" } member
               && member.Expression == selector.Parameters[0];
    }

    /// <summary>
    /// Returns the underlying <c>ti.Outer</c>/<c>ti.Inner</c> <see cref="MemberExpression"/> of a bare-nav
    /// SelectMany's whole-entity trailing selector — the shape produced by all three equivalent user
    /// spellings <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; i)</c>, <c>from o in q from i in o.Items select i</c>,
    /// and the bare 1-arg <c>SelectMany(o =&gt; o.Items)</c> (all whole-INNER, <c>Member.Name == "Inner"</c>), or
    /// <c>select o</c>/<c>(o, i) =&gt; o</c> (whole-OUTER, <c>Member.Name == "Outer"</c>) — or <see langword="null"/>
    /// if <paramref name="selector"/> is not this shape. EF auto-Includes any owned navigation reachable from
    /// the projected result: when the referenced side (owner or owned element) itself owns further
    /// navigations, the selector body is Include-wrapped (<c>IncludeExpression(ti.Inner, nav)</c> — possibly
    /// chained for multiple navs) rather than a bare member access, so this unwraps through any
    /// <see cref="IncludeExpression"/> layers first (empirically confirmed necessary — a nested owned member
    /// under the element reaches exactly this shape; see <see cref="IsWholeElementRepresentable"/>'s
    /// nested-navigation guard note). A narrower, single-purpose predicate than
    /// <see cref="IsTransparentIdentifierMemberAccessSelector"/> — it does not check the parameter's own type
    /// name, since the caller already knows (from <c>UnwindSource != null</c>) that this Select is a SelectMany
    /// trailing selector.
    /// </summary>
    private static MemberExpression? TryGetWholeEntityMemberAccess(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1)
        {
            return null;
        }

        var body = selector.Body;
        while (body is IncludeExpression include)
        {
            body = include.EntityExpression;
        }

        return body is MemberExpression { Member.Name: "Outer" or "Inner" } member
               && member.Expression == selector.Parameters[0]
            ? member
            : null;
    }

    /// <summary>
    /// Whether <paramref name="innerEntityType"/> (the collection's element type) is within the shape the
    /// whole-element re-rooting mechanism (<c>$unwind</c> + <c>$replaceRoot</c>, see
    /// <see cref="Expressions.MongoUnwindSource.WholeElement"/>) actually supports.
    /// For <see cref="MongoUnwindSourceKind.Reference"/> the check narrows to a single guard — reject only an
    /// EAGER-LOADED navigation (<see cref="Microsoft.EntityFrameworkCore.Metadata.IReadOnlyNavigationBase.IsEagerLoaded"/>).
    /// A plain LAZY inverse back-reference (e.g. a reference element's own FK-owner navigation) is never
    /// auto-included and materializes fine as null, so it does not block this shape — only a navigation EF
    /// would try to auto-include (reaching EF's <c>IncludeExpression</c> machinery, which binds against the
    /// re-rooted shaper's wrong <see cref="Microsoft.EntityFrameworkCore.Query.ProjectionMember"/>) is rejected.
    /// For <see cref="MongoUnwindSourceKind.Owned"/> the full set of guards below applies; the
    /// sentinel-collision / complex-property / owned-key-serialization guards exist ONLY
    /// to protect the owned <c>$mergeObjects</c> sentinel merge and the synthesized owner-key/ordinal shadow
    /// keys — a reference element merges no sentinels and has no owned-type shadow keys, so those checks apply
    /// for <see cref="MongoUnwindSourceKind.Owned"/> only:
    /// <list type="bullet">
    /// <item>No NON-EMBEDDED navigation of its own. A nested OWNED (embedded) navigation — single reference or
    /// collection, at any depth — is fully supported since EF-353: the element shaper binds through a
    /// <see cref="Microsoft.EntityFrameworkCore.Query.ProjectionMember"/> that
    /// <see cref="Expressions.MongoQueryExpression.ReRootProjectionAt"/> has re-pointed at the ELEMENT's own
    /// entity type, so EF's auto-<c>IncludeExpression</c> machinery binds the nested member against the right
    /// projection and its scalar leaves read from direct dotted paths relative to the re-rooted document
    /// (after <c>$replaceRoot</c> the element IS that document). Before that, the element shaper bound through
    /// the OUTER (owner) entity's <c>EntityProjectionExpression</c> and a nested navigation threw
    /// <see cref="InvalidOperationException"/> ("Unable to bind 'navigation' … to an entity projection of
    /// &lt;owner&gt;"). What is still rejected is a navigation that is NOT embedded — a cross-collection
    /// reference from the owned element — because materializing it needs a <c>$lookup</c> this shape's lowering
    /// never emits; that keeps the same clean, translation-time <see cref="NotSupportedException"/> every other
    /// unsupported whole-entity shape gets. See
    /// NativeSelectManyTests.Bare_owned_whole_inner_element_with_nested_owned_reference_member_now_goes_native
    /// and ..._with_nested_owned_collection_member_now_goes_native.</item>
    /// <item>No property whose configured element name collides with the ONE reserved wrapper field the
    /// <c>$replaceRoot</c> merge adds, <see cref="MongoReplaceRootStage.ShadowField"/>. The lowerer's
    /// <c>$mergeObjects</c> merges the sentinel object AFTER the unwound element, so a same-named real stored
    /// field would be SILENTLY OVERWRITTEN (unlike the Intersect/Except source-tagging precedent, whose
    /// <c>_a</c>/<c>_b</c> tags live as siblings of a wrapping <c>_doc</c> field, this mechanism merges into
    /// the element's own top-level namespace). Since EF-428 the owner-key/ordinal sentinels are nested one
    /// level UNDER that wrapper, so a property named <c>__ownerKey</c>/<c>__ord</c> no longer collides with
    /// anything and goes native with its real value intact; only the wrapper name itself remains reserved.
    /// See NativeSelectManyTests.Bare_owned_whole_inner_element_with_sentinel_collision_now_goes_native and
    /// ..._colliding_with_the_shadow_wrapper_still_declines_cleanly.</item>
    /// <item>No <em>complex-type</em> property whose configured element name collides with that wrapper field
    /// either — the scalar-property scan above (<see cref="IEntityType.GetProperties"/>) does not see a
    /// complex property's own top-level document slot, so a <c>ComplexProperty</c> named/renamed
    /// <c>__mongoef_shadow</c> would otherwise slip past it and be silently overwritten the same way.
    /// There is no dedicated Mongo builder API for a complex property's own element name, so
    /// <see cref="GetComplexPropertyElementName"/> reads the same <c>Mongo:ElementName</c> annotation
    /// <see cref="MongoPropertyExtensions.GetElementName(IReadOnlyProperty)"/> reads for a plain property.</item>
    /// <item>Every owned-key property (<see cref="MongoPropertyExtensions.IsOwnedTypeKey"/> — the owner-FK
    /// shadow property and the array-ordinal shadow property) must have DEFAULT serialization
    /// (<see cref="NativeGroupByBinder.HasDefaultKeySerialization"/>). The <c>__ownerKey</c> sentinel is
    /// populated straight from the owner document's raw <c>$_id</c> through the DEFAULT type serializer,
    /// bypassing whatever value converter or non-default <c>BsonRepresentation</c> the owned key property
    /// itself is configured with; if the owned key (or the ordinal key) carries either, the raw sentinel read
    /// diverges from what the property's own serializer expects at materialization.</item>
    /// </list>
    /// What remains here is deliberately narrow: a cross-collection navigation off an owned element, a real
    /// element named <c>__mongoef_shadow</c>, and a value-converted/non-default-represented owned key. Each
    /// declines cleanly at translation time rather than emitting a pipeline that would return silently wrong
    /// data.
    /// </summary>
    private static bool IsWholeElementRepresentable(IEntityType innerEntityType, MongoUnwindSourceKind kind)
    {
        // Reference: a plain lazy inverse back-reference (e.g. RefItem.Owner) is never auto-included and
        // shapes fine as null — reject only an EAGER-LOADED navigation (which reaches EF's IncludeExpression
        // machinery and binds against the re-rooted shaper's wrong ProjectionMember, the owned-slice crash).
        // Owned: an EMBEDDED nested navigation (owned reference or owned collection) now materializes
        // correctly — the element shaper binds through a ProjectionMember re-rooted at the element type
        // (ReRootProjectionAt, EF-353) and an embedded nav lives inside the re-rooted document itself, so it
        // needs no extra pipeline stage. Only a NON-embedded (cross-collection) navigation is still rejected:
        // it would need a $lookup this shape's lowering never emits. The sentinel-collision /
        // shadow-key-serialization checks below exist ONLY to protect the owned $mergeObjects sentinel merge
        // + synthesized owner/ordinal shadow keys; reference merges no sentinels and has no owned-type shadow
        // keys, so they apply for Owned only.
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
    /// The document element name a <see cref="IReadOnlyComplexProperty"/> occupies at its own declaring type's
    /// top level — the same <c>Mongo:ElementName</c> annotation
    /// <see cref="MongoPropertyExtensions.GetElementName(IReadOnlyProperty)"/> reads for a plain
    /// <see cref="IReadOnlyProperty"/>, with the identical CLR-member-name fallback. There is no
    /// <c>IReadOnlyComplexProperty</c> overload of <c>GetElementName</c> in this provider (no builder surfaces
    /// a way to rename a complex property's own document slot), so this reads the shared annotation directly
    /// rather than duplicating a second, divergent default-name algorithm.
    /// </summary>
    private static string GetComplexPropertyElementName(IReadOnlyComplexProperty complexProperty)
        => (string?)complexProperty[MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames.ElementName]
           ?? complexProperty.Name;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="selector"/> is the synthetic
    /// <c>Select(x =&gt; IncludeExpression)</c> EF's nav-expansion generates for a single-level, root-level
    /// collection <c>Include</c> (e.g. <c>Customers.Include(c =&gt; c.Orders)</c>) — the body is an
    /// <see cref="IncludeExpression"/> directly over the lambda's own parameter (no further projection),
    /// for a non-embedded collection navigation. This shape carries no native-unrepresentable projection of
    /// its own: the actual <c>$lookup</c> registration happens later, during projection binding
    /// (<see cref="MongoProjectionBindingExpressionVisitor"/>), so this Select must not be marked
    /// non-natively-representable. Anything more complex — nested/ThenInclude chains, a reference
    /// navigation, or an Include composed with an actual projection — falls through to the existing
    /// catch-all and stays on the driver-LINQ path.
    /// </summary>
    private static bool IsSingleLevelCollectionIncludeSelector(LambdaExpression selector)
        => selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
           && includeExpression.EntityExpression == selector.Parameters[0]
           && navigation.IsCollection
           && !navigation.IsEmbedded();

    /// <summary>
    /// Recognizes <paramref name="selector"/> as a chain of one or more single-level reference
    /// <c>Include</c>s — stacked via <see cref="IncludeExpression.EntityExpression"/> nesting for SIBLING
    /// Includes (e.g. <c>Docs.Include(d =&gt; d.Author).Include(d =&gt; d.Editor)</c>, which nav-expansion
    /// compiles to <c>Include(Include(x, Author), Editor)</c>), or via
    /// <see cref="IncludeExpression.NavigationExpression"/> nesting for a <c>ThenInclude</c> chain (e.g.
    /// <c>Orders.Include(o =&gt; o.Buyer).ThenInclude(b =&gt; b.SomeRef)</c>) — including a single reference
    /// <c>Include</c> on its own (the N=1 case, e.g. <c>Orders.Include(o =&gt; o.Customer)</c>). The two
    /// nesting axes freely combine (a sibling can carry its own <c>ThenInclude</c> chain).
    /// <para>
    /// PURE reference chains only — a collection navigation at ANY level (e.g. the "reference + collection"
    /// combo, <c>Orders.Include(o =&gt; o.Buyer).Include(o =&gt; o.Lines)</c>, or a collection
    /// <c>ThenInclude</c>) returns <see langword="null"/> for the WHOLE chain here; the sibling combo is
    /// recognized separately by <see cref="TryGetMixedReferenceAndCollectionIncludeChain"/>, which this
    /// method defers to via the shared <see cref="TryWalkIncludeChain"/> walker so the two recognizers can
    /// never disagree on the underlying structure, only on which shape (pure vs. mixed) they each admit. A
    /// collection <c>ThenInclude</c>, and a sibling hanging off a <c>ThenInclude</c>, are declined outright
    /// by neither recognizer admitting them — out of scope for both.
    /// </para>
    /// <para>
    /// The walk bottoms out at a pure <c>.Outer</c>* member-access chain reaching the selector's own
    /// parameter — the TransparentIdentifier plumbing EF builds for N stacked joins (one <c>.Outer</c> hop
    /// per join beyond the innermost). This is deliberately NOT capped at one hop: a USER-authored join with
    /// a downstream Include, e.g.
    /// <c>Orders.Join(Customers, o =&gt; o.CustomerId, c =&gt; c.Id, (o, c) =&gt; o).Include(o =&gt; o.Customer)</c>,
    /// also produces a (single-level, not chained) trailing <c>IncludeExpression</c> whose
    /// <c>EntityExpression</c> is a multi-hop <c>ti.Outer.Outer</c> chain — indistinguishable from a genuine
    /// sibling chain by hop count alone, since both are "some number of <c>.Outer</c> hops reaching the
    /// parameter". What distinguishes them is not hop depth but JOIN COUNT versus RECOGNIZED-LEVEL COUNT:
    /// the user-join case recognizes exactly ONE level (one actual <c>.Include()</c> call) while the
    /// underlying query has TWO joins (the user's own, plus nav-expansion's synthesized one for the
    /// Include), so <see cref="TryConfirmReferenceIncludeChain"/>'s <c>Joins.Count != chain.Count</c> check —
    /// not this method — is what keeps that shape declining. This method's job is purely to recognize the
    /// STRUCTURE; every semantic/count-based decline lives in the confirm step.
    /// </para>
    /// </summary>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
        => TryGetReferenceIncludeChain(selector, out _);

    /// <summary>
    /// Overload of <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/> that also reports which
    /// recognized levels are TRANSITIVE — reached via a <c>ThenInclude</c> nested under an earlier level
    /// (<see cref="IncludeExpression.NavigationExpression"/>), rather than a root-level sibling reached
    /// directly off the query root (<see cref="IncludeExpression.EntityExpression"/> nesting). Needed by
    /// <see cref="TryConfirmReferenceIncludeChain"/>, which applies its root-<c>DeclaringEntityType</c>
    /// check only to non-transitive entries — a transitive entry's declaring type was already verified
    /// against its PARENT's target type, structurally, inside <see cref="TryWalkIncludeChain"/>.
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
    /// Recognizes <paramref name="selector"/> as a reference-Include chain (structurally identical to
    /// <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/>'s shape) with exactly one additional single-level,
    /// non-embedded COLLECTION <c>Include</c> mixed in anywhere among the <c>EntityExpression</c>-nested
    /// levels — the "reference + collection" combo, e.g.
    /// <c>Orders.Include(o =&gt; o.Buyer).Include(o =&gt; o.Lines)</c>. Returns <see langword="false"/> (with
    /// empty/null outputs) for a PURE reference-only chain (handled by
    /// <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/> instead) or a bare single collection <c>Include</c> with no
    /// reference sibling (handled by <see cref="IsSingleLevelCollectionIncludeSelector"/> instead) — the
    /// three recognizers partition the space without overlap.
    /// <para>
    /// The collection level is returned separately and deliberately UNCONFIRMED here — its own
    /// <c>$lookup</c> registers later, unconditionally, during native shaper/projection-binding compilation
    /// (<see cref="MongoProjectionBindingExpressionVisitor"/>'s <c>IncludeExpression</c> case), exactly as
    /// it already does for a bare single collection <c>Include</c>. Only the reference levels need
    /// confirming through the candidate/confirmed counter
    /// (<see cref="TryConfirmReferenceIncludeChain"/>, called with JUST the reference levels) — a collection
    /// <c>Include</c> never registers a <c>Join</c> (<see cref="MongoQueryExpression.Joins"/> is unaffected
    /// by it), so that method's existing <c>Joins.Count != chain.Count</c> check naturally continues to
    /// compare only against the reference-level count, unchanged.
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
    /// Shared structural walk behind <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/> and
    /// <see cref="TryGetMixedReferenceAndCollectionIncludeChain"/>. Walks the SIBLING spine — a chain of
    /// <see cref="IncludeExpression"/>s nested via <see cref="IncludeExpression.EntityExpression"/> — and,
    /// for each sibling level found, ALSO follows a linear <c>ThenInclude</c> chain hanging off it via
    /// <see cref="IncludeExpression.NavigationExpression"/>, partitioning every level's navigation
    /// (sibling or transitive) into <paramref name="referenceLevels"/> (non-collection, non-embedded) or
    /// <paramref name="collectionLevel"/> (non-embedded collection — at most ONE across the whole chain,
    /// and only reachable via the SIBLING axis, never via a <c>ThenInclude</c> — a collection
    /// <c>ThenInclude</c> rejects the whole chain). A transitive (<c>ThenInclude</c>-nested) level is also
    /// recorded in <paramref name="transitiveLevels"/>, since <see cref="TryConfirmReferenceIncludeChain"/>
    /// needs to know which entries are root-level (checked against the query's own root entity type) versus
    /// transitive (already verified against their PARENT's target type, structurally, right here).
    /// <para>
    /// An embedded navigation found while following a <c>ThenInclude</c> chain (an owned auto-include, or
    /// chain of them) is transparent — not added as its own entry — but is verified (via the existing
    /// <see cref="HasNonEmbeddedThenInclude"/>) to have nothing REAL nested past it; if it does (EF-407's
    /// shape, a real navigation reached THROUGH an embedded hop), the whole chain is rejected, unchanged
    /// scope. An embedded navigation found on the SIBLING axis directly (<c>IsEmbedded()</c> on the
    /// sibling's own navigation) rejects the whole chain outright, as before — that shape is recognized by
    /// <see cref="IsOwnedEmbeddedIncludeSelector"/> instead.
    /// </para>
    /// <para>
    /// Requires the base (once every sibling level's own <c>EntityExpression</c> is exhausted) to be a pure
    /// <c>.Outer</c>* member-access chain reaching the selector's own <c>TransparentIdentifier</c>-typed
    /// parameter. Callers apply their own shape-specific acceptance rule on top (pure-reference-only vs.
    /// exactly-one-collection-mixed-in) — this method only answers "is this the general
    /// nested-Include-chain-over-a-join-plumbing-base shape at all".
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
            || !selector.Parameters[0].Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal))
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

            // Follow a linear ThenInclude chain hanging off THIS level, via NavigationExpression — the
            // axis a sibling Include never uses (that's EntityExpression, the outer loop here).
            var thenIncludeLevel = include;
            var thenIncludeTargetType = navigation.TargetEntityType;
            while (thenIncludeLevel.NavigationExpression is IncludeExpression { Navigation: INavigation thenNav } thenInclude)
            {
                if (thenNav.IsEmbedded())
                {
                    // An owned auto-include (or chain of them) on the target — lives inside the same
                    // document, no lookup needed (EF-368). Verify nothing REAL is nested past it — the
                    // existing rule, unchanged — and stop following THIS level's ThenInclude chain
                    // either way (embedded or not, this is where it ends for this sibling).
                    if (HasNonEmbeddedThenInclude(thenInclude))
                    {
                        return false;
                    }

                    break;
                }

                if (thenNav.IsCollection
                    || thenInclude.EntityExpression is IncludeExpression
                    || thenNav.DeclaringEntityType != thenIncludeTargetType)
                {
                    // A collection ThenInclude, a sibling hanging off a ThenInclude, or a structural
                    // mismatch (this hop doesn't declare on the previous hop's target) — out of scope
                    // for this recognizer; decline the whole chain rather than mishandle it.
                    return false;
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
    /// Validates and confirms every navigation in a recognized reference-Include chain
    /// (<see cref="TryGetReferenceIncludeChain(LambdaExpression)"/>), all-or-nothing, or returns <see langword="false"/> to
    /// decline the WHOLE chain.
    /// <para>
    /// For N=1 (today's ordinary single reference Include) this constructs and registers the
    /// forced-unwind <c>$lookup</c> itself, exactly as before. For N&gt;=2 (sibling reference Includes),
    /// <c>TranslateJoinCore</c>/<c>RebindInnerShaperToOuterQuery</c> already registered every join's
    /// <c>$lookup</c> unconditionally once <c>Joins.Count &gt; 1</c> (see that method's
    /// <c>isSecondOrLaterJoin</c> branch) — independent of whether any Include ever confirms — so this
    /// method only needs to CONFIRM each navigation matches an already-registered lookup, not re-register
    /// it. Either way, registering/confirming a lookup makes
    /// <see cref="MongoQueryExpression.UsesDriverJoinFields"/> compute <see langword="false"/>, so the
    /// native lowerer, the DOM shaper and the driver-LINQ <c>StripJoinForLookup</c> fallback all agree on
    /// the <c>_lookup_&lt;Nav&gt;</c> field — which is why the shaper is correct whichever way the gate
    /// later decides.
    /// </para>
    /// <para>
    /// That safety property is about the SHAPER only, and the distinction is load-bearing for anyone widening
    /// admissibility here: registering the lookup ALSO changes the FALLBACK's emitted pipeline (the driver
    /// <c>LeftJoin</c> form becomes the flat <c>StripJoinForLookup</c> shape), and it happens at TRANSLATION
    /// time, before <c>MongoQueryMode</c> is read. So a wrong admission is wrong in EVERY mode — explicit
    /// <c>DriverLinq</c> is neither an escape hatch nor an independent oracle for a confirmed reference
    /// Include. Every conjunct below must therefore hold on its own merits, not "because the fallback would
    /// catch it".
    /// </para>
    /// <para>
    /// <c>ThenInclude</c> riding forward off any level in the chain (e.g.
    /// <c>Orders.Include(o =&gt; o.Customer).ThenInclude(c =&gt; c.SomeRef)</c> — EF nests the
    /// <c>ThenInclude</c> inside <c>NavigationExpression</c>, not as a further <c>EntityExpression</c>
    /// wrapper) is now ADMITTED as its own chain entry when it's a genuine non-embedded reference — the
    /// walker (<c>TryWalkIncludeChain</c>) is the sole authority on which <c>ThenInclude</c> nesting is
    /// admissible, so this method no longer re-checks <c>HasNonEmbeddedThenInclude</c> per level (checking
    /// it here too would wrongly decline a level whose <c>NavigationExpression</c> is a legitimately
    /// admitted further chain entry). A collection <c>ThenInclude</c>, or a sibling hanging off a
    /// <c>ThenInclude</c>, still declines — the walker never lets those shapes reach here at all. EF also
    /// auto-includes each level's target's OWN owned/embedded navigations the same way (e.g. <c>Buyer</c>
    /// owning an <c>Address</c> via <c>OwnsOne</c>) — the walker keeps admitting that too, unchanged,
    /// verifying (via <c>HasNonEmbeddedThenInclude</c>, still) that nothing REAL is nested past it.
    /// </para>
    /// </summary>
    private static bool TryConfirmReferenceIncludeChain(
        MongoQueryExpression mongoQueryExpression,
        List<IncludeExpression> chain,
        HashSet<IncludeExpression> transitiveLevels)
    {
        // Declines that apply to the WHOLE chain, not per-navigation. Joins.Count must equal chain.Count
        // EXACTLY — not InnerCollections.Count (entity-type-keyed, so two same-target sibling joins would
        // wrongly collapse to one entry, see MongoQueryExpression.Lookup.cs) — because Joins is a list, one
        // entry per join, so it correctly distinguishes N=1, N>=2 siblings (same or different target
        // types), AND a mismatched case like a user Join plus a downstream Include targeting the same
        // type (that shape recognizes only ONE level here but has TWO joins registered — see
        // TryGetReferenceIncludeChain's own remarks — so it declines here, not at the recognizer).
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

            // A metadata navigation.TargetEntityType.GetQueryFilter() != null test would consult only the
            // target's OWN anonymous filter and miss two reachable routes — a filter declared on the ROOT of
            // a TPH hierarchy (GetQueryFilter() returns null on a DERIVED target) and an EF10 NAMED filter
            // (which lives in GetDeclaredQueryFilters()) — each of which would admit a shape the flat
            // $lookup cannot filter, returning silently wrong rows in EVERY mode, DriverLinq included. EF
            // applies the filter as a Where on the JOIN'S INNER SEQUENCE regardless of spelling, so
            // SawNonBareJoinInner (checked once above, for the whole query) catches all of them
            // structurally, plus any other sub-pipeline-requiring inner (a filtered Include) for free.
            //
            // DeclaringEntityType is checked against the query ROOT only for a root-level (non-transitive)
            // entry — a transitive entry's declaring type was already verified against its PARENT's target
            // type, structurally, inside TryWalkIncludeChain.
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
                // Already registered by TranslateJoinCore's multi-join flattening — confirm it matches
                // this Include's own navigation rather than re-registering it.
                if (!existing.ForceUnwind)
                {
                    return false;
                }
            }
            else
            {
                // The single-join case (chain.Count == 1): TranslateJoinCore never flattens a lone join,
                // so nothing is registered yet. Build and add it here, exactly as the pre-chain
                // single-Include recognizer did.
                var lookup = new LookupExpression(navigation, forceUnwind: true)
                {
                    // Inner $unwind for a required navigation, left-outer for an optional one. Elsewhere
                    // the LINQ OPERATOR is the discriminator (isLeftOuter: Join => inner, LeftJoin/GroupJoin
                    // => left-outer) because ForeignKey.IsRequired alone is insufficient in general — a
                    // user-authored LeftJoin over a required FK must still preserve principals, and
                    // IsRequired cannot see that. No operator is in hand at THIS site (the confirm runs on
                    // the trailing Select, not the join), so IsRequired is read directly, and that is sound
                    // HERE SPECIFICALLY: the recognizer admits only EF's own nav-expansion shape for a
                    // single-level reference Include, and nav-expansion emits Queryable.Join for a required
                    // navigation and LeftJoin for an optional one — so for this one admitted shape the
                    // operator and IsRequired coincide by construction. It is not a general substitute for
                    // the operator; TranslateJoinCore keeps using isLeftOuter for every other join.
                    PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired
                };

                // Brief-mandated defence-in-depth, kept even though it is not known reachable at this call
                // site: LookupExpression's own constructor never prefixes LocalField. Other sites do
                // prefix it (TranslateJoinCore's retroactive multi-join flattening; three sites in
                // MongoProjectionBindingExpressionVisitor.cs; NativeSelectManyBinder.cs's
                // nested-reference-SelectMany scoping) but every one of them runs AFTER a lookup is
                // already registered on MongoQueryExpression, mutating the SAME LookupExpression instance
                // in place — none of them can affect this freshly-constructed local, above AddLookup.
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
    /// Whether <paramref name="navigationExpression"/> is (or contains, through a chain of further embedded
    /// hops) an <see cref="IncludeExpression"/> whose OWN navigation is non-embedded — a real
    /// <c>ThenInclude</c> reaching past the looked-up document, as opposed to an auto-included OWNED
    /// navigation on the reference-Include's target (which lives inside the same document the <c>$lookup</c>
    /// already reads, so admitting it is correct).
    /// <para>
    /// Recurses into both <see cref="IncludeExpression.EntityExpression"/> and
    /// <see cref="IncludeExpression.NavigationExpression"/> (where a further, deeper hop nests). A shape like
    /// <c>Include(o =&gt; o.Buyer).ThenInclude(b =&gt; b.Address).ThenInclude(a =&gt; a.Region)</c> (<c>Address</c>
    /// owned, <c>Region</c> a real cross-collection navigation) is actually filtered out earlier — adding a
    /// real cross-collection nav requires EF's nav-expansion to inject an additional join that no chain
    /// level's <see cref="IncludeExpression.EntityExpression"/> walk in <c>TryGetReferenceIncludeChain</c>
    /// ever counts (a <c>ThenInclude</c> nests via <c>NavigationExpression</c>, not <c>EntityExpression</c>),
    /// so <c>TryConfirmReferenceIncludeChain</c>'s <c>Joins.Count != chain.Count</c> check already rejects the
    /// whole chain before this method's own call, for THIS example, is ever reached.
    /// </para>
    /// <para>
    /// The recursion here is kept as defence-in-depth: if a future change to the join-count check, or to how
    /// EF nav-expands a nested real navigation, ever lets such a shape through to
    /// <see cref="TryConfirmReferenceIncludeChain"/>, this recursive walk is what stops it being silently
    /// admitted rather than declined.
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
    /// Returns <see langword="true"/> when <paramref name="selector"/> is the synthetic
    /// <c>Select(x =&gt; IncludeExpression(...))</c> EF's nav-expansion generates for one or more OWNED
    /// (embedded) navigations — a single reference OR a collection — auto-included eagerly by EF Core
    /// convention — e.g. <c>Blog { Address }</c> with <c>OwnsOne(b =&gt; b.Address)</c>, a nested chain
    /// <c>IncludeExpression(IncludeExpression(x, Address), Address.Geo)</c> for a further owned single-ref
    /// under the first, or <c>Blog { Tags }</c> with <c>OwnsMany(b =&gt; b.Tags)</c>.
    /// <para>
    /// Because owned data is embedded in the very same document as its owner, this auto-include carries NO
    /// projection of its own to push down to a native <c>$project</c> — the whole document (owner fields
    /// plus embedded sub-document or sub-array) is read back as-is by the ordinary whole-entity DOM/streaming
    /// shaper, which already recurses into owned single-refs and owned collections. So this <c>Select</c>
    /// must not be marked non-natively-representable (mirrors <see cref="IsSingleLevelCollectionIncludeSelector"/>
    /// for the NON-owned collection-Include case, and <see cref="IsTransparentIdentifierSelector"/>/
    /// <see cref="IsTransparentIdentifierMemberAccessSelector"/> more generally: all four predicates identify
    /// a synthetic Select that carries no projection of its own).
    /// </para>
    /// <para>
    /// Deliberately narrow: the ONLY navigations excluded are non-embedded ones — a non-owned/reference
    /// navigation (<c>!navigation.IsEmbedded()</c> — single-level reference <c>Include</c> has no native
    /// representation yet) — which keeps falling back to driver-LINQ exactly as before this predicate
    /// existed. An owned collection is admitted here on equal footing with an owned single reference; a
    /// collection whose ELEMENT itself carries further navigations is separately excluded from the
    /// *streaming* shaper (not this gate) by <see cref="StreamingEligibility"/>, routing it to the native
    /// DOM shaper instead.
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
            // Admit any EMBEDDED (owned) navigation — a single reference OR a collection. An owned
            // collection embeds as a BSON array in the same document, so the whole-entity DOM/streaming
            // shaper reads it back with no extra pipeline stage, exactly like an owned single reference.
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
                    // Post-terminal guard: OfType after a native terminal (Union/Concat, or GroupBy/
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
    /// has no discriminator property (non-TPH) or there are no discriminator values, in which case the caller
    /// should fall back to driver-LINQ.
    /// </returns>
    private static bool TryBuildDiscriminatorPredicate(IEntityType targetType, out MongoExpression predicate)
    {
        predicate = null!;
        var discriminatorProperty = targetType.FindDiscriminatorProperty();
        if (discriminatorProperty is null)
        {
            // Non-TPH / no discriminator → fall back. A non-TPH OfType has no native form at all. See NativeOfTypeTests.Non_TPH_OfType_falls_back_gracefully_and_works_across_modes.
            return false;
        }

        // This predicate serializes the discriminator value THROUGH the property serializer (via
        // MongoConstantExpression.ForSerialization → BsonSerializerFactory), which applies any value converter /
        // non-default BsonRepresentation configured on the discriminator property — the same transform
        // MongoEFDiscriminator now applies to the driver-LINQ filter (EF-349), and the same transform the write
        // path applies when the discriminator is persisted. So, unlike a grouping/distinct key (see
        // NativeGroupByBinder.HasDefaultKeySerialization), a represented discriminator does not need to be
        // rejected here: there is no generic flattened-_id readback involved — EF's own discriminator-based
        // MaterializationCondition reads the stored field back through the property's normal serializer — so
        // native and driver-LINQ agree for represented discriminators too.
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
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Except);

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
        // non-native so it falls back to driver-LINQ rather than hard-throwing.
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

        // A wrong-data verdict reached on the INNER select must reach the gate, which only ever reads the
        // OUTERMOST MongoQueryExpression. When the offending shape lives in a SUBQUERY used as this join's
        // inner, MarkGroupByFallbackUnsafe wrote to that intermediate select and the verdict would otherwise
        // be lost (EF-344).
        outerQueryExpression.Select.PropagateFallbackWrongDataFrom(innerQueryExpression.Select);

        // EF-368 finding 1. The reference-Include path emits a flat $lookup with NO sub-pipeline, so it can
        // only stand in for a join whose INNER side is the whole target collection and nothing else. Record
        // the inner's shape here — the only point at which the inner's translated MongoSelectDefinition is in
        // hand — and let TryConfirmReferenceIncludeChain decline on it. See
        // MongoSelectDefinition.IsBareCollectionScan / MarkSawNonBareJoinInner for why this replaced the
        // metadata GetQueryFilter() test that used to live at the confirm site.
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

        // EF-373: an operator composed BETWEEN two cross-collection joins is NOT declined here. The
        // driver-LINQ bridge's StripInterleavedJoinChain splits the join-replacing $lookup stages along the
        // join order and emits each at its own reattachment boundary, so the interleaved operator lands
        // between the two $lookup stages rather than above both - see
        // MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs and Query/AGENTS.md. Every join query routes
        // through that bridge (joins are not natively representable), so declining here would preempt it.
        // Shapes that bridge cannot split decline there instead, fail-closed.

        // Rebind the inner entity's projection to the outer MongoQueryExpression.
        // The inner shaper has a StructuralTypeShaperExpression bound to the inner MongoQueryExpression.
        // We need to migrate that projection to the outer query expression so the entity path
        // shaper can read inner entity properties from the $lookup result field.
        var reboundInnerShaper = RebindInnerShaperToOuterQuery(
            inner.ShaperExpression, innerQueryExpression, outerQueryExpression, outerKeySelector, innerKeySelector, joinInfo);

        var newResultSelector = ReplacingExpressionVisitor.Replace(
            resultSelector.Parameters[0], outer.ShaperExpression,
            ReplacingExpressionVisitor.Replace(
                resultSelector.Parameters[1], reboundInnerShaper!,
                resultSelector.Body));

        return outer.UpdateShaperExpression(newResultSelector);
    }

    /// <summary>
    /// Migrates the inner entity's projection onto the outer <see cref="MongoQueryExpression"/> and registers
    /// the <c>$lookup</c>(s) that stand in for the join.
    /// </summary>
    /// <param name="innerShaper">The inner side's shaper, bound to <paramref name="innerQueryExpression"/>.</param>
    /// <param name="innerQueryExpression">The join's inner query expression.</param>
    /// <param name="outerQueryExpression">The join's outer query expression, which the projection moves onto.</param>
    /// <param name="outerKeySelector">The join's outer key selector, used to identify the navigation.</param>
    /// <param name="innerKeySelector">The join's inner key selector, used to identify the joined-to key.</param>
    /// <param name="joinInfo">
    /// The <see cref="JoinInfo"/> recorded for THIS join by <c>TranslateJoinCore</c>. Carries the join's own
    /// left-outer/inner-ness, its resolved navigation, and its uniquified <c>$lookup</c> alias, so a later hop
    /// can find this join by position rather than by target entity type — see
    /// <see cref="AnalyzeKeySelectorTarget"/>.
    /// </param>
    /// <returns>
    /// The rebound shaper, or <see langword="null"/> when this join CANNOT be represented: a TRANSITIVE hop
    /// whose intermediate sub-document could not be identified, so the <c>$lookup</c>'s <c>localField</c>
    /// cannot be scoped under it. Nothing has been registered on <paramref name="outerQueryExpression"/> by
    /// this method in that case. A decline is signalled by the return value rather than an <c>out bool</c>
    /// beside a non-null-but-unusable shaper, so that a caller CANNOT go on to use an un-rebound shaper by
    /// simply not reading the flag.
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

        // The outer key selector's FK-access target tells us whether this join reads directly off the
        // root entity or off a previously-joined intermediate (e.g. the second ".Manager" in
        // Employee.Manager.Manager): a pure ".Outer"* chain reaches the root, one ending in ".Inner"
        // reaches a prior hop. Checking this structurally (not by comparing entity types) is required for
        // self-referencing chains, where the target and through entity types are the same.
        //
        // A key selector can also reach the FK property through an owned/embedded navigation nested
        // inside the root or the through-hop (e.g. Buyer.Address.RegionId, EF-380): PeelEmbeddedSegments
        // strips those leading member accesses (closest to the FK property) before the Outer/Inner walk
        // runs, so the walk still sees a pure Outer/Inner chain.
        var (outerInnerTarget, embeddedSegments) = PeelEmbeddedSegments(
            GetKeySelectorTargetObject(outerKeySelector.Body), outerKeySelector.Parameters[0]);
        var (isDirectFromRoot, throughLevel) = AnalyzeKeySelectorTarget(
            outerInnerTarget, outerKeySelector.Parameters[0], priorJoinCount);

        INavigation? navigation = null;
        JoinInfo? throughJoin = null;
        string? embeddedPath = null;

        IEntityType? anchorEntityType = null;
        if (isDirectFromRoot)
        {
            anchorEntityType = outerEntityType;
        }
        else if (throughLevel is { } level && level >= 1 && level <= priorJoinCount)
        {
            // Transitive join: resolve the navigation on the join hop the key selector actually reaches
            // through (found by position, not by IEntityType — see above) and remember it so the
            // $lookup's localField can be prefixed with that intermediate's alias.
            throughJoin = outerQueryExpression.Joins[level - 1];
            anchorEntityType = throughJoin.InnerEntityType;
        }

        if (anchorEntityType != null)
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
                        // Can't resolve this embedded path — fall back to searching the anchor itself, as
                        // if there were no embedded segments (matches pre-EF-380 behavior).
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
                // IsOnDependent disambiguates a self-referencing relationship declared with both a
                // reference nav (e.g. Manager) and its inverse collection nav (e.g. DirectReports):
                // both share the same IForeignKey, so matching on ForeignKey.Properties alone matches
                // either one, and picking the collection nav flips the $lookup's join direction
                // (LookupExpression branches on Navigation.IsOnDependent).
                navigation = searchEntityType.GetNavigations()
                    .FirstOrDefault(n => n.TargetEntityType == innerEntityType
                                         && n.IsOnDependent
                                         && n.ForeignKey.Properties.Any(p => p.Name == fkPropertyName));
            }

            navigation ??= searchEntityType.GetNavigations()
                .FirstOrDefault(n => n.TargetEntityType == innerEntityType);
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
        joinInfo.Navigation = navigation;
        joinInfo.Alias = UniquifyLookupAlias(
            navigation != null
                ? Expressions.LookupExpression.GetLookupAlias(navigation)
                : $"_lookup_{innerEntityType.ShortName()}",
            outerQueryExpression,
            joinInfo);

        if (navigation != null)
        {
            var lookup = new Expressions.LookupExpression(navigation, forceUnwind: true)
            {
                As = joinInfo.Alias,
                PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
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

            joinInfo.Lookup = lookup;
        }
        else if (fkPropertyName != null)
        {
            // Bare key-equality Join hop with no model navigation (EF-377): there's no navigation to
            // build a $lookup from, so build one directly from the raw outer/inner key property paths.
            // The FK-owning entity type is the root when isDirectFromRoot, or the through-hop's target
            // otherwise; the localField is scoped by the through-hop's alias the same way a
            // navigation-bearing transitive hop is above.
            var fkOwnerEntityType = throughJoin?.InnerEntityType ?? outerEntityType;
            var innerKeyPropertyName = innerKeySelector.Body.TryGetSimplePropertyName();
            var outerProperty = fkOwnerEntityType.FindProperty(fkPropertyName);
            var innerProperty = innerKeyPropertyName != null ? innerEntityType.FindProperty(innerKeyPropertyName) : null;
            if (outerProperty != null && innerProperty != null)
            {
                var localField = throughJoin != null
                    ? $"{throughJoin.Alias}.{outerProperty.GetElementName()}"
                    : outerProperty.GetElementName();

                joinInfo.Lookup = new Expressions.LookupExpression(
                    innerEntityType, innerEntityType.GetCollectionName(), localField, innerProperty.GetElementName(),
                    joinInfo.Alias, forceUnwind: true)
                {
                    PreserveNullAndEmptyArrays = joinInfo.IsLeftOuter
                };
            }
        }

        // The trigger counts JOINS, not distinct target entity types: two joins onto the same type must
        // flatten just like two joins onto different ones (EF-375), and a chained hop through a prior
        // join (!isDirectFromRoot) always needs its own field too.
        var isSecondOrLaterJoin = outerQueryExpression.Joins.Count > 1;
        if (isSecondOrLaterJoin)
        {
            // Register this join's $lookup, then every prior join's — each carries its own lookup built
            // (with its own left-outer/inner-ness, navigation-or-raw-key info, and any transitive
            // localField prefix) from what it actually resolved at the time IT was processed, so
            // same-typed sibling joins, self-referencing chains, and navigation-less hops (EF-377) are
            // never confused. AddLookup dedupes by alias, so re-adding an already-registered prior
            // lookup here is a no-op.
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
    /// Extracts the object an outer key selector's body reads a property FROM — the "x" in
    /// <c>x.Foo</c> or <c>EF.Property(x, "Foo")</c> — used to determine whether the selector reaches the
    /// root entity or a prior join's result. See <see cref="AnalyzeKeySelectorTarget"/>.
    /// </summary>
    private static Expression? GetKeySelectorTargetObject(Expression body)
        => body.RemoveConvert() switch
        {
            MemberExpression member => member.Expression,
            MethodCallExpression call when call.Method.IsEFPropertyMethod() && call.Arguments.Count == 2 => call.Arguments[0],
            _ => null
        };

    /// <summary>
    /// Strips leading member/<c>EF.Property</c> accesses off <paramref name="targetObject"/> that are
    /// NOT part of the join chain's synthetic <c>Outer</c>/<c>Inner</c> transparent-identifier plumbing —
    /// i.e. real navigation hops through an owned/embedded type nested inside the root or a prior join
    /// (e.g. the "Address" in <c>x.Inner.Address.RegionId</c>, EF-380). Returns what's left (handed to
    /// <see cref="AnalyzeKeySelectorTarget"/> to resolve against the root/prior-join chain) plus the
    /// stripped segment names in root-to-leaf order.
    /// </summary>
    private static (Expression? RemainingTarget, List<string> EmbeddedSegments) PeelEmbeddedSegments(
        Expression? targetObject, ParameterExpression parameter)
    {
        var embeddedSegments = new List<string>();
        var current = targetObject;
        while (current != null && !ReferenceEquals(current, parameter))
        {
            var (name, next) = current.RemoveConvert() switch
            {
                MemberExpression member when member.IsTransparentIdentifierOuterOrInnerAccess() => (null, null),
                MemberExpression member => (member.Member.Name, member.Expression),
                MethodCallExpression call when call.Method.IsEFPropertyMethod()
                    && call.Arguments.Count == 2
                    && call.Arguments[1] is ConstantExpression { Value: string propertyName } => (propertyName, call.Arguments[0]),
                _ => (null, null)
            };

            if (name == null || next == null)
            {
                // Either we've reached the Outer/Inner chain plumbing, or an unrecognized shape — either
                // way, hand off the rest to AnalyzeKeySelectorTarget as-is.
                break;
            }

            embeddedSegments.Insert(0, name);
            current = next;
        }

        return (current, embeddedSegments);
    }

    /// <summary>
    /// Walks a chain of <c>.Outer</c>/<c>.Inner</c> member accesses from <paramref name="targetObject"/>
    /// down to <paramref name="parameter"/> to determine whether the selector reads the root entity's own
    /// property (a pure <c>.Outer</c>* chain, or the bare parameter) or a prior hop's result (a chain
    /// ending in <c>.Inner</c>). Each <c>.Outer</c> step walks one hop back toward the root, and a
    /// terminal <c>.Inner</c> at some step resolves to that
    /// step's join (1-based, oldest first).
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
    /// Disambiguates a join's <c>_lookup_&lt;Navigation&gt;</c> alias against joins already registered.
    /// Two joins can resolve the same navigation (a LINQ cross product), so each needs its own
    /// <c>$lookup</c> output field; the first claimant keeps the unsuffixed name.
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
        // Only the INNER-Select owned-collection form (projection nested in the collection selector, e.g.
        // o => o.Items.Select(i => new {o.X, i.Y})) is handled here. EF's nav-expansion normalizes EVERY
        // SelectMany shape to this overload with resultSelector always the trivial
        // TransparentIdentifier(Outer=o, Inner=c) constructor. A subsequent .Select(ti => ti.Inner) always
        // immediately follows and reaches TranslateSelect, unwrapping the transparent identifier back down
        // to the SelectMany's real TResult (the nested Select's own projection, c) — this is how EF
        // materializes a 2-arg SelectMany's result type via nav-expansion's internal 3-arg rewrite. So the
        // shaper returned here must still be a TransparentIdentifier(Outer, Inner) shape (see
        // BuildSelectManyWrappedShaper) even though the underlying native pipeline has no "Outer" data of its
        // own — EF's own ReplacingExpressionVisitor.VisitMember NewExpression-member fold resolves ti.Inner
        // directly back to our projected shaper with no bespoke unwrap logic needed here.
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        // A narrow carve-out BEFORE the terminal guard below. Fires only when the sole terminal so far is a
        // single REFERENCE unwind source (IsSingleReferenceUnwindTerminalOnly) — i.e. this IS the second,
        // chained SelectMany of a nested reference shape, not some unrelated post-terminal operator (a 2nd
        // SelectMany after GroupBy/Distinct/a set-op/an owned unwind, or a query already 2+ levels deep,
        // still falls through unchanged to the guard below). On a structural match
        // (TryBindNestedReferenceNavUnwind), reuse the SAME wrapped-shaper builder the single-level bare-nav
        // bind uses — BuildBareNavWrappedShaper already reads Select.UnwindSource, which now resolves to
        // this SECOND source, so no new shaper code is needed: the result is the doubly-nested
        // TransparentIdentifier(Outer=<level-1 result>, Inner=<level-2 element>) shape EF's nav-expansion
        // expects.
        if (mongoQueryExpression.Select.IsSingleReferenceUnwindTerminalOnly
            && NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQueryExpression, collectionSelector))
        {
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);
        }

        // Post-terminal guard (composition-seam audit): a SelectMany composed AFTER a native terminal — a
        // Union/Concat (IsSetOp), GroupBy (IsGroupBy), projected Distinct (IsDistinct), or a prior SelectMany
        // (UnwindSource) — must NOT let its own UnwindSource coexist with the earlier terminal on the same
        // select. The lowerer (MongoSelectLowerer.Lower) selects exactly ONE terminal by fixed precedence
        // (SetOperation > UnwindSource > Grouping > Projection > Cardinality) and returns early, so a second
        // terminal is SILENTLY DROPPED: e.g. `Union(a,b).SelectMany(o => o.Items.Select(...))` emits only the
        // $unionWith and never the SelectMany's $unwind/$project — returning whole outer rows (wrong row count,
        // or a shaper crash when a projected alias is absent at top level) under BOTH Native and NativeOnly
        // (Route stays non-Fallback, so NativeOnly does not even throw). Every other own-Translate-override
        // operator (TranslateSelect/OfType/GroupBy) already gates on HasTerminalOperator; SelectMany's binders
        // set UnwindSource with no such gate and SelectManyWithCollectionSelector is whitelisted in
        // NativeSlotPopulator, so the catch-all does not back it up either — hence this dedicated guard.
        //
        // Decline by returning null (before any binder mutates the query), reaching EF Core's own
        // translation-failure path directly — the established SelectMany contract for an unsupported shape:
        // a clean hard-fail in EVERY MongoQueryMode, never silent wrong data. A GRACEFUL
        // MarkNotNativelyRepresentable() fallback is NOT viable here: the native SelectMany builds a by-index
        // ProjectionBindingExpression shaper that the driver-LINQ fallback cannot re-read ("'ProjectionBinding
        // Expression: 0' could not be translated") — the same shaper-rebuild limitation that makes operators
        // composed AFTER a SelectMany hard-fail in every mode (see NativeSelectManyTests). (DriverLinq MODE
        // succeeds on this chain only because it skips native slot population entirely and re-translates the raw
        // captured chain; that path is unavailable once the native binders have run under Native.)
        if (mongoQueryExpression.Select.HasTerminalOperator)
            return null;

        // The explicit-result-selector / query-syntax form arrives as a BARE owned nav
        // collection selector (o => o.Items.AsQueryable(), no nested Select) + a trivial
        // TransparentIdentifier(Outer,Inner) resultSelector; the real projection is the SEPARATE trailing
        // Select (see NativeSelectManyBinder.TryBindTransparentIdentifierProjection, bound from TranslateSelect).
        // Set UnwindSource here and hand EF the TransparentIdentifier(Outer, Inner) shape it expects — the item
        // (Inner) shaper is never itself read when the trailing Select binds natively (that path builds the
        // result shaper straight from Select.Projection by alias, bypassing this wrapper's Inner slot entirely);
        // it exists only so this method's return type-checks as resultSelector's own TransparentIdentifier<TOuter,
        // TInner> and so an unsupported trailing projection still folds through EF's ReplacingExpressionVisitor
        // NewExpression-member mechanism during driver-LINQ-fallback shaper construction.
        if (NativeSelectManyBinder.TryBindBareNavUnwind(mongoQueryExpression, collectionSelector))
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);

        // Cross-collection REFERENCE bare-nav — the collectionSelector is a correlated
        // Queryable.Where(EntityQueryRoot, o => c.pk==o.fk); same wrapped-shaper shape as owned bare-nav (the
        // item shaper here is likewise never itself read once the trailing Select binds natively).
        if (NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQueryExpression, collectionSelector))
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);

        if (!NativeSelectManyBinder.TryBind(mongoQueryExpression, collectionSelector))
            return null;

        return BuildSelectManyWrappedShaper(source, mongoQueryExpression, collectionSelector, resultSelector);
    }

    /// <summary>
    /// Builds the <see cref="ShapedQueryExpression"/> EF expects immediately after a bare-nav terminal
    /// SelectMany bind (<see cref="NativeSelectManyBinder.TryBindBareNavUnwind"/> — owned — or
    /// <see cref="NativeSelectManyBinder.TryBindReferenceNavUnwind"/> — reference), both of which set only
    /// <see cref="Expressions.MongoSelectDefinition.UnwindSource"/> and leave <see cref="Expressions.MongoSelectDefinition.Projection"/>
    /// empty. The item (Inner) shaper is never itself read when the trailing Select binds natively — see the
    /// <see cref="BuildSelectManyWrappedShaper"/> / <see cref="TranslateSelectMany(ShapedQueryExpression, LambdaExpression, LambdaExpression)"/>
    /// comments. It exists only so this return type-checks as resultSelector's own <c>TransparentIdentifier(Outer, Inner)</c>.
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

    // Native whole-entity, terminal Union/Concat/Intersect/Except -> a $unionWith (or source-tagging
    // $unionWith pipeline, for Intersect/Except) on source1's select. Union/Concat ALWAYS return a
    // non-null shaped query (source1): native when both operands are plain natively-lowerable whole-entity
    // selects of the same type, otherwise source1 marked non-native so the query falls back GRACEFULLY to
    // driver-LINQ (throws only under NativeOnly) -- mirrors TranslateGroupBy's always-non-null contract.
    // Intersect/Except differ on the guard-decline path -- see the comment below.
    private ShapedQueryExpression? TryTranslateSetOperation(
        ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
    {
        var mongo1 = (MongoQueryExpression)source1.QueryExpression;
        var mongo2 = (MongoQueryExpression)source2.QueryExpression;

        if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            mongo1.Select.SetOperation = new MongoSetOperation(
                kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, mongo2.CollectionExpression.EntityType);
            mongo1.Select.IsSetOp = true;
            return source1;
        }

        // Projected operands. Both operands are plain projected selects (a Select-projection is the SOLE
        // terminal on each). The EntityType-equality gate above does NOT apply — projected operands may be
        // different collections that project to the same shape; ProjectionShapesMatch guards the shape
        // compatibility instead (a correctness guard, not just an optimization: the dedup / source-tagging
        // compare whole projected documents by value, so mismatched alias sets would mis-compare). EF Core
        // rejects incompatible operand shapes upstream, so a mismatch is defense-in-depth.
        if (IsPlainProjectedSelect(mongo1) && IsPlainProjectedSelect(mongo2)
            && ProjectionShapesMatch(mongo1.Select.Projection, mongo2.Select.Projection))
        {
            mongo1.Select.SetOperation = new MongoSetOperation(
                kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, mongo2.CollectionExpression.EntityType,
                operandsProjected: true);
            mongo1.Select.IsSetOp = true;
            return source1;
        }

        // Out of scope. Union/Concat have a working driver-LINQ fallback, so mark non-native and return
        // source1 -> graceful fallback (throws only under NativeOnly). Intersect/Except have NO driver-LINQ
        // fallback (the driver's LINQ v3 provider does not translate a cross-view Intersect/Except), so
        // returning source1 would route to a fallback that then fails at execution; instead return null so
        // the shape reaches EF's NotTranslatedExpression path and hard-fails cleanly in every mode (mirroring
        // how reference SelectMany declines its no-baseline shapes).
        if (kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
        {
            return null;
        }

        mongo1.Select.MarkNotNativelyRepresentable();
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

    // A plain projected select: a terminal anonymous/DTO member-access Select is the SOLE thing done
    // (Projection populated, Route == Projection) — no grouping, scalar cardinality, its own set op,
    // SelectMany ($unwind), cross-collection lookups (Include), join, or a lifted-out VectorSearch. The
    // projected analogue of IsPlainWholeEntitySelect. Note this checks UnwindSource == null, which the
    // whole-entity sibling currently omits (a documented latent gap) — this predicate is deliberately
    // stricter.
    //
    // HasArrayProjectionLeaf: an owned entity-COLLECTION array leaf (Select(b => new { b.Title, b.Posts }))
    // is DECLINED as a set-op OPERAND. This is a CORRECTNESS guard on the set operation's own semantics, and
    // it is about the owner key the array leaf drags along, not about arrays as such. An array leaf forces
    // NativeProjectionBinder to emit the root key into the projected document (a shadow-key element
    // materializes its owner's key out of the row it is handed — see
    // NativeProjectionBinder.TryPopulateNativeProjection's owner-key block), and a PROJECTED-OPERAND set op
    // is exactly the shape whose dedup ($group{_id:"$$ROOT"}) / source-tagging ($group{_id:"$_doc"}) compares
    // that WHOLE projected document by value. So the leaked _id joins the comparison key and turns the
    // intended contract — dedup over the PROJECTED VALUES, pinned by
    // NativeSetOpsTests.Projected_operand_union_dedups_over_projected_values_not_whole_entities — into dedup
    // by document IDENTITY (a false Union duplicate; a false-negative Intersect; a false-positive Except).
    // Intersect/Except have NO driver-LINQ oracle at all (the driver's LINQ v3 provider throws for a
    // cross-view Intersect/Except), so a flipped answer there would be the ONLY answer available in any
    // mode. Declining here means Union/Concat fall back gracefully to driver-LINQ (which dedups over the
    // projected values, the documented semantics), and Intersect/Except hard-fail in every mode via
    // TryTranslateSetOperation's null return.
    //
    // This does NOT touch a TRAILING projection after a whole-entity set op (Union(A,B).Select(b => new {
    // b.Title, b.Posts })), which stays native: that path never consults this predicate, and its dedup runs
    // over whole entities BEFORE the trailing $project, so neither the array nor the owner key reaches the
    // comparison.
    //
    // EF-395: a BARE projected operand (Select(b => b.Title), as opposed to a wrapped Select(b => new {
    // b.Title})) is ADMITTED here, on par with a wrapped one — this predicate no longer conjoins
    // !IsBareProjection. The hazard this used to guard against is real but is fully covered by the SEPARATE
    // HasArrayProjectionLeaf conjunct just above: an array leaf drags its owner's shadow key into the
    // projected document (see NativeProjectionBinder's owner-key block), which is what corrupts the
    // WHOLE-PROJECTED-DOCUMENT dedup/source-tagging key ($group{_id:"$$ROOT"} / $group{_id:"$_doc"}) — and
    // that flag is set identically for a bare array leaf (Select(b => b.Posts)) and a wrapped one, so it
    // still declines the array case regardless of which door it arrives through. For every OTHER admitted
    // leaf kind (a non-array scalar or computed leaf) the projected document IS exactly the value being
    // compared — {Title: "..."} for Select(b => b.Title), same as the wrapped Select(b => new { b.Title })
    // — so dedup-by-whole-document and dedup-by-value coincide and admitting it changes nothing about what
    // $$ROOT means. This also means Intersect/Except (no driver-LINQ baseline at all) now answer correctly
    // for a bare operand instead of hard-failing, which is a strict improvement for those two: there was
    // never a working fallback to preserve. Pinned by NativeBareProjectionTests.
    private static bool IsPlainProjectedSelect(MongoQueryExpression mongo)
        => mongo.Select.Route == NativeRoute.Projection
           && mongo.Select.Projection.Count > 0
           && !mongo.Select.HasArrayProjectionLeaf
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !ContainsVectorSearch(mongo.CapturedExpression);

    // The two operands' projected shapes must have identical top-level alias SETS (same count, same alias names).
    // The output documents' fields are exactly these aliases, and Union dedup / Intersect-Except source-tagging
    // compare whole projected documents by value — mismatched alias sets would compare structurally-different
    // documents and silently mis-dedup / mis-tag. Compares alias sets only, NOT the underlying field-refs, so
    // e.g. new {N = a.Name} and new {N = b.Title} correctly match (both produce {N: ...}); each operand's own
    // $project maps its own source field to the shared alias. EF Core rejects incompatible operand shapes
    // upstream (a shared common anonymous type is required for the set op to compile), so a mismatch here is
    // defense-in-depth against that guarantee ever weakening.
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
