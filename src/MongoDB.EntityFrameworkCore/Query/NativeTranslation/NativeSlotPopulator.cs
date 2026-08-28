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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native-translation ops (<see cref="Expressions.MongoSelectDefinition.PipelineOps"/> —
/// match/sort/skip/limit, recorded in arrival order) on a <see cref="Expressions.MongoQueryExpression"/> for
/// the seven slot-bearing LINQ operators, and owns the whitelist that suppresses the non-native catch-all.
/// </summary>
internal static class NativeSlotPopulator
{
    /// <summary>
    /// Populates the native-translation slots on the <see cref="MongoQueryExpression"/> for the
    /// seven slot-bearing operators: Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending,
    /// Skip, and Take.  Called from
    /// <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>'s VisitMethodCall
    /// on the already-evaluated source.
    /// </summary>
    internal static void PopulateNativeSlots(
        ShapedQueryExpression shapedQuery,
        MethodInfo methodDefinition,
        MethodCallExpression call)
    {
        var mongoQ = (MongoQueryExpression)shapedQuery.QueryExpression;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        // Post-group slot-operator guard. Once a GroupBy or projected Distinct has been seen on this query
        // (IsGroupBy / IsDistinct — both bind the same degenerate-$group machinery), a slot operator applied
        // after it — a Where (HAVING) / OrderBy / ThenBy / Skip / Take — operates over the grouped result,
        // not the entity. Every arm below resolves member accesses against the ENTITY type, so a post-group
        // predicate/sort whose member name collides with a real entity property (e.g. an aggregate alias
        // "Amount" shadowing Entity.Amount) would resolve and emit a pre-$group $match/$sort, running before
        // aggregation and silently returning wrong data. The native $group path does not support post-group
        // operators, so mark the query non-native to force a clean driver-LINQ fallback (throws only under
        // NativeOnly). Scoped to the seven slot operators only — the grouped Select/OfType and the
        // reducer/aggregate arms are excluded, so the supported GroupBy(key).Select(aggregate) still goes
        // native.
        //
        // A set-op-only terminal is exempt: the seven slot operators composed after a set op fall through to
        // their arms below and record into TrailingOps (MongoSelectDefinition.ActiveOps flips once
        // SetOperation is attached), filtering/sorting/paging the combined result and emitting after the
        // set-op stage. A GroupBy/Distinct/SelectMany terminal (or a mixed one) still trips this guard.
        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsSevenSlotOperator(methodDefinition))
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        // Post-CONFIRMED-JOIN slot-operator guard (EF-392). Structurally the same hazard as the post-group one
        // above, at a different point in the pipeline: once a Select-side arm has confirmed a genuine two-sided
        // join and registered its $lookup, Route is no longer Fallback and HasUnsupportedOperator is false, so
        // a slot operator composed AFTER that Select would record into PipelineOps — which MongoSelectLowerer
        // emits BEFORE the $lookup/$unwind. Over a 1:N collection-navigation $unwind that pages/filters the
        // UN-joined outer rows (Join(...).Take(5) limits to five OWNERS, then expands them into N joined rows),
        // and after the bare whole-entity-leaf arm it also resolves member names against the stale OUTER
        // CollectionExpression.EntityType used to build `translator` above. Decline so the query falls back to
        // driver-LINQ (throwing only under NativeOnly) instead of returning silently wrong rows.
        //
        // Deliberately NOT folded into HasTerminalOperator: that is evaluated at join-RECORDING time too,
        // where it gates TryConfirmReferenceIncludeChain's own precondition and would break native
        // reference-Include confirmation (tried and reverted earlier in EF-392, commit 4c7b852). This flag is
        // set only at CONFIRMATION, which the Include path never reaches.
        //
        // DEFENCE-IN-DEPTH, NOT A LIVE GUARD — measured, and stated here so nobody re-derives it as load-
        // bearing: EF Core's nav-expansion applies a join's result selector LAST (pending selector) and hoists
        // a trailing Where ahead of it, so a slot operator normally reaches this method BEFORE the confirming
        // Select. Instrumenting this exact branch across the whole functional suite (3018 tests) and the spec
        // suite in both query modes (4613 × 2) produced ZERO hits; the sibling gate in
        // NativeCardinalityBinder.TryBindReducer DID fire (twice). The forward ordering — the one that actually
        // happens — is closed by the HasPaging/Cardinality conjuncts in
        // MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope. Keep this branch
        // anyway: it costs one predicate, and it is what stops the hazard the moment the ordering changes
        // (a confirming arm that runs earlier, or an EF normalization change).
        //
        // Scoped to the seven slot operators, matching the guard above. Reverse needs no arm here: it declines
        // on its own unless the tail op is literally a $sort, and no sort can have been recorded before a
        // confirmed join (an OrderBy over a join scope isn't translatable by the single-scope arms below, so it
        // marks the query non-native, which in turn blocks confirmation via HasUnsupportedOperator). The
        // reducer arm's own gate lives in NativeCardinalityBinder.TryBindReducer; scalar AGGREGATES are
        // deliberately not gated, because their $count/$group stage is emitted AFTER the lookup block.
        if (mongoQ.Select.HasConfirmedJoinLookup && IsSevenSlotOperator(methodDefinition))
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps are emitted verbatim in arrival order: a Where (-> $match) applied after paging is
            // recorded after it too, and the lowerer emits ops in that same order — correct by MongoDB's
            // sequential pipeline semantics. No canonical-order guard.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            // Outer-side-only: PipelineOps ($match) always lower BEFORE the $lookup stage that materializes
            // the join's Inner side, so a Where reaching Inner would filter on a not-yet-joined field —
            // NativeJoinScopeTranslator.ReferencesInnerScope declines that here, deferring Inner access to
            // the Select-side binder (Task 5). See NativeJoinScopeTranslator.ReferencesInnerScope's remarks.
            //
            // WHY THIS GATE HAS FEWER CONJUNCTS THAN THE SELECT ARMS' SHARED ONE
            // (MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope, which adds
            // Joins.Count == 1, the key-selector/left-outer/collection-nav checks and !HasUnsupportedOperator):
            // those conjuncts all protect the act of REGISTERING the join's $lookup, and this arm registers
            // nothing — it only records a $match conjunct into PipelineOps. If the join is never confirmed the
            // query routes to Fallback and that conjunct is discarded unused; if it IS confirmed, it was
            // confirmed through the shared gate, so every one of those conjuncts held. The asymmetry is
            // therefore deliberate: do NOT copy this shorter set to a registering call site, and do not weaken
            // the Select arms' gate to match it.
            else if (mongoQ.Select.JoinScope is { } joinScope
                     && !NativeJoinScopeTranslator.ReferencesInnerScope(predicate.Parameters[0], predicate.Body)
                     && NativeJoinScopeTranslator.TryTranslatePredicate(
                         joinScope, predicate.Parameters[0], predicate.Body, out var joinPredicateNode))
                mongoQ.Select.AddPredicateConjunct(joinPredicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.ThenBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.AppendThenBy(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.AppendThenBy(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Skip)
        {
            // Repeated / non-canonical-order paging is natively representable: each Skip appends a $skip op
            // at its arrival position, and the lowerer emits ops verbatim.
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
                mongoQ.Select.MarkNotNativelyRepresentable();
            else
                mongoQ.Select.AppendSkip(count);
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
                mongoQ.Select.MarkNotNativelyRepresentable();
            else
                mongoQ.Select.AppendLimit(count);
        }
        else if (methodDefinition == QueryableMethods.Reverse)
        {
            // MQL has no "reverse row order" stage. The only sound native form is inverting an explicit
            // trailing sort (the exact complement of the original order — see
            // MongoSelectDefinition.TryFlipTrailingSortDirection). Reverse() over an otherwise-unordered
            // source has undefined result order in LINQ generally, so decline rather than invent an
            // unreliable $natural sort.
            if (!mongoQ.Select.TryFlipTrailingSortDirection())
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (TryGetReducerKind(methodDefinition, out var reducerKind))
        {
            // First/FirstOrDefault/Single/SingleOrDefault (no predicate — EF normalizes the predicate
            // overloads to Where(pred) followed by the no-arg terminal, so only the no-arg forms reach
            // here). Synthesize a $limit (1 for First*, 2 for Single*) and record the reducer kind; EF
            // Core's base cardinality reduction runs over the returned IEnumerable<T> to apply the actual
            // First/Single semantics (empty => throw/null, >1 => throw for Single*).
            if (!NativeCardinalityBinder.TryBindReducer(mongoQ, reducerKind, call.Method.ReturnType))
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#endif
                )
        {
            // Might be EF's nav-expansion of a single-level reference Include. Record a candidate rather
            // than marking non-native; TranslateSelect confirms it when the trailing IncludeExpression
            // matches the recognizer. Unconfirmed candidates route to Fallback, so this is default-deny and
            // a user join is unaffected. See MongoSelectDefinition §Reference-Include candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
        else if (call.IsVectorSearch())
        {
            // Binding the slot doubles as opening the native disposition: it reads
            // `ContainsVectorSearch(captured) && Select.VectorSearch is null`, so a bound slot means "native"
            // and "the lowerer has a $vectorSearch stage to emit" as one fact — bind or mark
            // non-representable are the only two exits, so a native route with the stage never emitted
            // (right row count, insertion order instead of score order, no exception) is unreachable by
            // construction.
            //
            // This branch sits above the catch-all rather than in IsNativeRepresentableSlotOperator because
            // that whitelist takes only a MethodInfo and there is no QueryableMethods constant for
            // VectorSearch — it's recognized via the internal IsVectorSearch() extension instead.
            if (!NativeVectorSearchBinder.TryBind(mongoQ, call))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (!IsNativeRepresentableSlotOperator(methodDefinition))
        {
            // Any other top-level operator (Distinct, Cast, DefaultIfEmpty, scalar aggregates, cardinality
            // reducers, Any/All, …) is not lowered into a native slot. Leaving the query "native-representable"
            // would silently drop the operator on the native pipeline (e.g. a Distinct executed as the bare
            // collection scan), so it is conservatively marked non-native. Select / OfType set the flag in their
            // own Translate overrides. This is correctness-safe: the worst case is a missed native optimization
            // and a fall back to the driver-LINQ path, never a wrong result.
            mongoQ.Select.MarkNotNativelyRepresentable();
        }
    }

    // The seven slot operators whose native lowering (a $match / $sort / $skip / $limit) would be emitted
    // BEFORE a stage they must run after — a $group when applied after a GroupBy, or the $lookup/$unwind when
    // applied after a CONFIRMED genuine join. Both post-terminal guards in PopulateNativeSlots key off this
    // same list. Deliberately excludes Select / OfType / GroupBy and the reducer / scalar-aggregate operators
    // so the supported grouped Select is not marked non-native.
    private static bool IsSevenSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take;

    // The operators PopulateNativeSlots lowers into a native slot. Everything else either sets the flag in its
    // own Translate override (Select/OfType) or must drop off the native path (handled by the catch-all above).
    //
    // VectorSearch is deliberately absent: this predicate takes a MethodInfo, and VectorSearch has no
    // QueryableMethods constant to compare against — it is recognized via the internal IsVectorSearch()
    // extension instead, whose explicit branch above runs before the catch-all, so this whitelist never needs
    // to hold it.
    internal static bool IsNativeRepresentableSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take
           || methodDefinition == QueryableMethods.Select
           || methodDefinition == QueryableMethods.OfType
           || methodDefinition == QueryableMethods.Distinct
           || methodDefinition == QueryableMethods.Union
           || methodDefinition == QueryableMethods.Concat
           || methodDefinition == QueryableMethods.Intersect
           || methodDefinition == QueryableMethods.Except
           || methodDefinition == QueryableMethods.SelectManyWithCollectionSelector
           || methodDefinition == QueryableMethods.GroupByWithKeySelector
           || methodDefinition == QueryableMethods.GroupByWithKeyElementSelector
           || methodDefinition == QueryableMethods.FirstWithoutPredicate
           || methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.SingleWithoutPredicate
           || methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.LastWithoutPredicate
           || methodDefinition == QueryableMethods.LastOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.Reverse
           || methodDefinition == QueryableMethods.CountWithoutPredicate
           || methodDefinition == QueryableMethods.LongCountWithoutPredicate
           || methodDefinition == QueryableMethods.AnyWithoutPredicate
           || methodDefinition == QueryableMethods.All
           || QueryableMethods.IsSumWithoutSelector(methodDefinition)
           || QueryableMethods.IsSumWithSelector(methodDefinition)
           || methodDefinition == QueryableMethods.MinWithoutSelector
           || methodDefinition == QueryableMethods.MinWithSelector
           || methodDefinition == QueryableMethods.MaxWithoutSelector
           || methodDefinition == QueryableMethods.MaxWithSelector
           || QueryableMethods.IsAverageWithoutSelector(methodDefinition)
           || QueryableMethods.IsAverageWithSelector(methodDefinition);

    // Maps the six no-predicate cardinality-reducer QueryableMethods to their MongoReducerKind. The
    // predicate-taking overloads are normalized by EF to Where(pred).First()/... before reaching here, so
    // they are intentionally not matched — leaving them off means the catch-all in PopulateNativeSlots
    // marks them non-native if one somehow arrives unnormalized.
    private static bool TryGetReducerKind(MethodInfo methodDefinition, out MongoReducerKind kind)
    {
        if (methodDefinition == QueryableMethods.FirstWithoutPredicate)
        {
            kind = MongoReducerKind.First;
            return true;
        }

        if (methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.FirstOrDefault;
            return true;
        }

        if (methodDefinition == QueryableMethods.SingleWithoutPredicate)
        {
            kind = MongoReducerKind.Single;
            return true;
        }

        if (methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.SingleOrDefault;
            return true;
        }

        if (methodDefinition == QueryableMethods.LastWithoutPredicate)
        {
            kind = MongoReducerKind.Last;
            return true;
        }

        if (methodDefinition == QueryableMethods.LastOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.LastOrDefault;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// Translates a Skip/Take count expression to a <see cref="MongoExpression"/>
    /// (either a <see cref="MongoConstantExpression"/> or a <see cref="MongoParameterExpression"/>).
    /// Returns <see langword="null"/> if the expression cannot be represented natively.
    /// </summary>
    private static MongoExpression? TranslateCountExpression(Expression count)
    {
        if (count is ConstantExpression constant)
            return new MongoConstantExpression(constant.Value, forSerialization: null);

        if (NativeQueryParameter.TryGetQueryParameterName(count, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization: null);

        return null;
    }

    /// <summary>
    /// Attempts to translate a computed (non-field) sort key. MQL <c>$sort</c> accepts field paths only, so
    /// <see cref="MongoSelectLowerer"/> materializes the result into a synthetic field with <c>$set</c> and
    /// removes it again with <c>$unset</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is <see cref="MongoAggregationExpressionRenderer.CanRender"/>, not
    /// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>: a <c>$set</c> body is an aggregation
    /// expression, so a node kind that exists only in the query dialect can serve a predicate but can never
    /// serve a computed sort key. Gating here turns that into a clean translate-time decline instead of a
    /// render-time throw. Any future slice that introduces a new node kind reachable as a sort key must add
    /// a matching arm to both <c>Render</c> and <c>CanRender</c> in
    /// <see cref="MongoAggregationExpressionRenderer"/> (that file's own contract requires the two be changed
    /// together) — not only to the query-dialect renderer.
    /// </para>
    /// <para>
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> brings its own guard: an operand whose
    /// property lacks default serialization is rejected, so a value-converted field cannot be sorted by its
    /// raw stored order via a computed key. (A plain field sort key on such a property has no equivalent
    /// guard.) It used to reject an integer-result division too; EF-434 replaced that with a truncating
    /// translation (<see cref="MongoBinaryOperator.IntegerDivide"/>), so <c>OrderBy(c =&gt; c.A / c.B)</c> now
    /// sorts by the C#-correct quotient natively.
    /// </para>
    /// <para>
    /// <b>A bare top-level constant/parameter is a separate, value-level hazard <c>CanRender</c> cannot see</b>
    /// — it is a node-kind check only. Two hazards are handled elsewhere/below: (1)
    /// <see cref="MongoPipelineFactory"/>'s <c>RenderAddFields</c> <c>$literal</c>-wraps a bare
    /// constant/parameter body, so an unwrapped <c>"$"</c>-prefixed string value can't render as a field path
    /// instead of a literal; (2) a bare constant whose CLR type
    /// <see cref="MongoDB.Bson.BsonValue.Create(object)"/> rejects (e.g. a custom struct) would otherwise
    /// throw at pipeline-build time, outside the translator's usual fallback path.
    /// <see cref="TryProbeBareValueRenders"/> below turns that into a clean decline by trial-rendering the
    /// actual constant value, or, for a parameter, a default instance of its declared (nullable-unwrapped)
    /// value type — a valid proxy because <c>BsonValue.Create</c>'s admission decision is keyed on the CLR
    /// type, not the value.
    /// </para>
    /// <para>
    /// A reference-type parameter is handled by a narrow allowlist rather than a probe: a default reference
    /// proxy is always <see langword="null"/>, which renders unconditionally and so can't discriminate, and
    /// <c>BsonValue.Create</c> admits some reference types (e.g. an array/<c>List&lt;T&gt;</c>, mapped
    /// structurally) but rejects others (<see cref="Uri"/>, <see cref="Version"/>, ordinary user classes) in
    /// a way that can't be recognized from the declared type alone. Admission is restricted to the reference
    /// types known to render for any value: <see cref="string"/> and <see cref="MongoDB.Bson.BsonValue"/>.
    /// Declining an admissible shape only costs nativeness, never correctness.
    /// </para>
    /// <para>
    /// <b>A filtered owned-collection count (<c>b.Posts.Count(p =&gt; ...)</c>) goes native as a sort key,</b>
    /// even though its element predicate is not passed through the operand-serialization guard that would
    /// catch a value-converted/non-default-represented comparison operand inside it (that guard only checks
    /// the outer expression). Over a property with a non-default <c>BsonRepresentation</c>, this can compare
    /// the raw stored representation rather than the CLR value inside the filter — but native and
    /// driver-LINQ agree in that case, because the driver's own LINQ provider serializes the same comparison
    /// constant through the same property serializer, so it is not a native-only divergence. An unfiltered
    /// count (<c>MongoSizeExpression</c>) has no such comparison at all and is unaffected.
    /// </para>
    /// </remarks>
    private static bool TryTranslateComputedSortKey(
        MongoExpressionTranslator translator,
        Expression keySelectorBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!translator.TryTranslateValue(keySelectorBody, out var translated))
            return false;

        if (!MongoAggregationExpressionRenderer.CanRender(translated))
            return false;

        if (!TryProbeBareValueRenders(translated, keySelectorBody.Type))
            return false;

        result = translated;
        return true;
    }

    /// <summary>
    /// Returns <see langword="false"/> only when <paramref name="translated"/> is a bare
    /// <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> whose value would make
    /// <see cref="MongoAggregationExpressionRenderer.Render"/> throw at pipeline-build time (see
    /// <see cref="TryTranslateComputedSortKey"/>'s remarks). Anything else (a binary/size/field-ref node —
    /// never a bare value) is trivially fine and returns <see langword="true"/> without probing.
    /// </summary>
    /// <remarks>
    /// Exact for a <see cref="MongoConstantExpression"/> — the value is known at translate time, so the real
    /// render path runs on the real value. For a <see cref="MongoParameterExpression"/> it is a type-keyed
    /// model of that same path rather than the value that will actually execute; it agrees with what
    /// actually runs (<c>MongoPipelineFactory.SerializeParameter</c>) because a bare value parameter carries
    /// no property serializer, so both paths reach <c>BsonValue.Create</c>, whose admission decision is keyed
    /// on the CLR type rather than the value.
    /// </remarks>
    private static bool TryProbeBareValueRenders(MongoExpression translated, Type declaredType)
    {
        switch (translated)
        {
            case MongoConstantExpression constant:
                return TryRender(constant);

            case MongoParameterExpression:
                // A default instance stands in for the (unknowable here) runtime value. Where the declared
                // type is looser than the runtime one (e.g. an `object`-typed parameter boxing an int) this
                // probe can over-decline a shape that would have rendered fine — costing nativeness only,
                // never correctness. If SerializeParameter ever stops routing a bare value through
                // BsonValue.Create, this probe must be re-pointed at whatever replaces it.
                var underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                if (underlying.IsValueType)
                {
                    var sample = Activator.CreateInstance(underlying);
                    return TryRender(new MongoConstantExpression(sample, forSerialization: null));
                }

                // A reference type can't be probed (a default proxy is always null, which renders
                // unconditionally) and BsonValue.Create maps a collection structurally element-by-element
                // (int[] renders, Uri[] throws), so admission is restricted to reference types known to
                // render for any value.
                return underlying == typeof(string) || typeof(BsonValue).IsAssignableFrom(underlying);

            default:
                return true;
        }

        static bool TryRender(MongoExpression node)
        {
            try
            {
                MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());
                return true;
            }
            catch (Exception)
            {
                // Broad catch deliberately: the question is exactly "does rendering this value throw", and
                // any throw means decline and let the query fall back to driver-LINQ (or throw cleanly under
                // NativeOnly) rather than crash uncaught at pipeline-build time.
                return false;
            }
        }
    }
}
