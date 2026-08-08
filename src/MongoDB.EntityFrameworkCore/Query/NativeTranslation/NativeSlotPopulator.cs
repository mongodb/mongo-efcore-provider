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
/// match / sort / skip / limit, recorded in arrival order, EF-347) on a
/// <see cref="Expressions.MongoQueryExpression"/> for the seven slot-bearing LINQ operators, and owns the
/// whitelist that suppresses the non-native catch-all. Extracted from the QMTEV (EF-332) so
/// native-translation logic no longer lives inside the EF query dispatcher.
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

        // Post-group slot-operator guard. Once a GroupBy has been seen on this query (IsGroupBy), a slot
        // operator applied AFTER it — a Where (HAVING) / OrderBy / ThenBy / Skip / Take — operates over the
        // grouped result, NOT the entity. But every arm below resolves its member accesses against the ENTITY
        // type (the translator is built from CollectionExpression.EntityType), so a post-group predicate/sort
        // whose member name COLLIDES with a real entity property (e.g. an aggregate alias "Amount" shadowing
        // Entity.Amount) would resolve and emit a PRE-$group $match/$sort — the operator would run BEFORE
        // aggregation, silently returning wrong data. The native $group path does not support post-group
        // operators, so mark the query non-native to force a clean driver-LINQ fallback (throws only under
        // NativeOnly). Keyed on IsGroupBy (set unconditionally by TranslateGroupBy) rather than the finalized
        // Grouping so it also covers a post-group operator over a bare/unsupported grouping (which is already
        // Fallback anyway — no behavior change). Scoped to the seven slot operators only: the grouped
        // Select/OfType and the reducer/aggregate arms are excluded, so the SUPPORTED
        // GroupBy(key).Select(aggregate) (whose Select is dispatched here with IsGroupBy already true) still
        // goes native.
        // IsDistinct rides the same guard: a projected Distinct binds the same degenerate-$group machinery, so
        // a slot operator applied after it must fall back cleanly for the identical reason (it would otherwise
        // resolve against the entity type and emit a pre-$group $match/$sort). Only the Join-family decline
        // differs between GroupBy and Distinct (see MongoSelectDefinition.IsDistinct); this slot guard is shared.
        // (Centralized as HasTerminalOperator, EF-347 review follow-up — see MongoSelectDefinition.)
        //
        // EF-347 slice B: a set-op-only terminal is EXEMPT — the seven slot operators composed after a set op
        // fall through to their arms below and record into TrailingOps (MongoSelectDefinition.ActiveOps flips
        // once SetOperation is attached), so they filter/sort/page the COMBINED result and emit after the
        // set-op stage. Only a set-op-ONLY terminal is exempt: a GroupBy/Distinct/SelectMany terminal (or a
        // mixed one) still trips this guard and falls back. The deferred own-override operators (Select/
        // Distinct/GroupBy/SelectMany/OfType, chained set ops) each keep their own untouched HasTerminalOperator
        // guard, so they stay terminal after a set op.
        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsPostGroupSlotOperator(methodDefinition))
        {
            // TODO(CSHARP-6017): delete this MarkSawUnrecordedPaging call with the rest of the paging guard.
            // This return happens BEFORE the AppendSkip/AppendLimit arms below, so a Skip/Take reaching here is
            // never recorded as an op and MongoSelectDefinition.HasPagingAnywhere would not see it — yet the
            // Skip/Take IS still in the captured method chain the driver-LINQ fallback executes, so CSHARP-6017
            // still folds it into the correlated $lookup sub-pipeline if this sequence is used as a join inner.
            // MEASURED (EF-366): Orders.Join(Regions.Select(r => new {r.Country}).Distinct().Take(1), ...) —
            // TryBindDistinctFromProjection sets IsDistinct, so HasTerminalOperator is true here and the Take(1)
            // was swallowed — returned all 5 orders where at most 2 is correct, silently, under DEFAULT Native
            // as well as explicit DriverLinq, with the inner's $group/$replaceRoot/$limit:1 visibly folded into
            // the $lookup's own pipeline. Recording the fact here is exact: it says only "a Skip/Take was seen
            // and not lowered", which is precisely the condition under which the fold applies.
            if (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take)
            {
                mongoQ.Select.MarkSawUnrecordedPaging();
            }

            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps are emitted verbatim in arrival order (EF-347 Task 2): a Where (→ $match)
            // applied AFTER paging is recorded AFTER it too, and the lowerer emits ops in that same
            // order — correct by MongoDB's sequential pipeline semantics. No canonical-order guard.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // Same as Where above (EF-347 Task 2): a $sort recorded after paging is emitted after it,
            // verbatim — correct by sequential pipeline semantics. No canonical-order guard.
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
            // Same as OrderBy above (EF-347 Task 2): no canonical-order guard.
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
            // Repeated / non-canonical-order paging is natively representable (EF-347 Task 2): each
            // Skip appends a $skip op at its arrival position, and the lowerer emits ops verbatim.
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                // TODO(CSHARP-6017): delete MarkSawUnrecordedPaging with the rest of the paging guard. Same
                // reasoning as the post-terminal early return above — the Skip is declined rather than recorded,
                // but it stays in the captured chain the fallback executes. Unlike that path this one has not
                // been shown reachable from ordinary LINQ (EF parameterizes a captured/computed count, so
                // TranslateCountExpression essentially always succeeds), so it is defence-in-depth against a
                // silent-wrong-data hole, not a measured bug — see the design spec §2.9.
                mongoQ.Select.MarkSawUnrecordedPaging();
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
                mongoQ.Select.AppendSkip(count);
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            // Same as Skip above (EF-347 Task 2): repeated / non-canonical-order Take is representable.
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                // TODO(CSHARP-6017): same as the Skip arm immediately above — declined, not recorded, but still
                // in the captured chain. Defence-in-depth; not shown reachable from ordinary LINQ.
                mongoQ.Select.MarkSawUnrecordedPaging();
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
                mongoQ.Select.AppendLimit(count);
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
            // EF-368: might be EF's nav-expansion of a single-level reference Include. Record a candidate
            // rather than marking non-native; TranslateSelect confirms it when the trailing
            // IncludeExpression matches the recognizer. Unconfirmed candidates route to Fallback, so this
            // is default-deny and a user join is unaffected. See MongoSelectDefinition §Reference-Include
            // candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
        else if (call.IsVectorSearch())
        {
            // EF-322 VectorSearch slice. This is GATE 2, and binding the slot is what opens GATE 1 too: the
            // disposition reads `ContainsVectorSearch(captured) && Select.VectorSearch is null`, so a bound
            // slot means "native" AND "the lowerer has a $vectorSearch stage to emit" as ONE fact. There are
            // exactly two exits here — bind, or mark non-representable — which is what makes the
            // silently-wrong-data state (native route with the stage never emitted: the right row count, in
            // INSERTION order rather than score order, no exception) unreachable by construction.
            //
            // This branch sits ABOVE the catch-all rather than in IsNativeRepresentableSlotOperator because
            // that whitelist takes only a MethodInfo and there is no QueryableMethods constant for
            // VectorSearch — the recognizer is the internal MethodCallExpression extension IsVectorSearch().
            // The area's "native catch-all whitelist must stay in sync" pitfall is therefore satisfied by
            // construction here: the explicit branch means the catch-all is never reached for this operator,
            // so there is nothing for the whitelist to hold. See IsNativeRepresentableSlotOperator's note.
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
    // BEFORE a $group when applied after a GroupBy — so they must force fallback on a grouped query (see the
    // post-group guard in PopulateNativeSlots). Deliberately excludes Select / OfType / GroupBy and the
    // reducer / scalar-aggregate operators so the supported grouped Select is not marked non-native.
    private static bool IsPostGroupSlotOperator(MethodInfo methodDefinition)
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
    // VectorSearch is deliberately ABSENT, and its absence is not an omission to "fix": this predicate takes a
    // MethodInfo, and VectorSearch has no QueryableMethods constant to compare against — it is recognized by
    // the internal MethodCallExpression extension IsVectorSearch(), which needs the whole call node. Its own
    // explicit branch above runs BEFORE the catch-all, so the catch-all never sees it and this whitelist has
    // nothing to hold for it (EF-322 VectorSearch slice).
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

    // Maps the four no-predicate cardinality-reducer QueryableMethods to their MongoReducerKind. The
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
    /// Attempts to translate a COMPUTED (non-field) sort key — EF-401, stream 1 slice B. MQL <c>$sort</c>
    /// accepts field paths only, so <see cref="MongoSelectLowerer"/> materializes the result into a synthetic
    /// field with <c>$set</c> and removes it again with <c>$unset</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate is <see cref="MongoAggregationExpressionRenderer.CanRender"/>, NOT
    /// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>, and the difference is the whole point.</b>
    /// A <c>$set</c> body is an AGGREGATION expression, so a node kind that exists only in the query dialect
    /// can serve a predicate and can NEVER serve a computed sort key. Gating here turns that into a clean
    /// translate-time decline instead of a render-time throw.
    /// </para>
    /// <para>
    /// <b>Consequence for future capability-A slices, which no other document states:</b> a slice whose sort
    /// column is to count must add an arm to <see cref="MongoAggregationExpressionRenderer"/>
    /// (<c>Render</c> AND <c>CanRender</c>, which that file's own contract requires be changed together) —
    /// not only to <c>MongoQueryLanguageRenderer</c>/<c>IsQueryDialectRenderable</c>. `CanRender` today admits
    /// field/element refs, constants/parameters, binary operators over its 13 listed operators and the two
    /// size nodes — <b>not</b> <c>MongoInExpression</c>, <c>MongoRegexExpression</c>,
    /// <c>MongoElemMatchExpression</c> or <c>MongoUnaryExpression</c>. The stream-1 spike's §7 imposes this
    /// only on slices introducing a NEW node kind, so A6 (<c>Contains</c>) and A13 (<c>Not</c>) — whose node
    /// kinds already exist — fall outside it and would otherwise ship with their sort columns silently dead.
    /// </para>
    /// <para>
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> brings its own two guards with it: an
    /// integer-result division is rejected (MongoDB's <c>$divide</c> is non-truncating), and so is an operand
    /// whose property lacks default serialization — so a value-converted field cannot reach a computed sort
    /// key and be sorted by its RAW stored order. (A plain FIELD sort key on such a property has no equivalent
    /// guard and is unchanged by this slice — pre-existing, not introduced here.)
    /// </para>
    /// <para>
    /// <b><see cref="MongoAggregationExpressionRenderer.CanRender"/> is VACUOUS at this call site today —
    /// state this plainly so its presence is never later read as evidence a node kind was mutation-tested
    /// against it.</b> <see cref="MongoExpressionTranslator.TryTranslateValue"/> (via its private
    /// <c>TranslateOperand</c>) can only ever PRODUCE a node kind <c>CanRender</c> already admits (a field
    /// ref, a constant/parameter, a size, or an arithmetic binary over admitted operands) — so the check can
    /// never fail here, and forcing it to always return <see langword="true"/> turns no test red (mutation 2
    /// reproduces mutation 1's exact red set; it does not add to it). It is kept anyway as the correct
    /// FORWARD guard for the capability-A slices this file's remarks describe above (a count, a regex, an
    /// <c>$in</c> test, a quantifier) — each of those DOES introduce a node kind <c>CanRender</c> currently
    /// declines, and the gate is what turns that into a clean decline instead of a render-time throw once one
    /// of them lands. Until then, this call has no discriminating power of its own.
    /// </para>
    /// <para>
    /// <b>A bare top-level constant/parameter is a SEPARATE, value-level hazard <c>CanRender</c> cannot see
    /// (EF-401 fix round 1, Important 1) — it is a NODE-KIND check only.</b> Two failure modes live here,
    /// neither caught by <c>CanRender</c>: (1) <see cref="MongoPipelineFactory"/>'s <c>RenderAddFields</c> now
    /// <c>$literal</c>-wraps a bare constant/parameter body, closing the silent-wrong-order hole where an
    /// unwrapped <c>"$"</c>-prefixed string value renders as a field path instead of a literal — see that
    /// method's own remarks; nothing here needed to change for that fix. (2) A bare constant whose CLR type
    /// <see cref="MongoDB.Bson.BsonValue.Create(object)"/> rejects (a custom struct; an enum round-trips fine,
    /// MEASURED) throws <see cref="ArgumentException"/> at pipeline-BUILD time, OUTSIDE
    /// <c>MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline</c>'s typed
    /// <c>catch (NativeTranslationNotSupportedException)</c> — a hard failure under <c>Native</c>/
    /// <c>NativeOnly</c> where the pre-slice driver-LINQ fallback worked (unaffected: <c>DriverLinq</c> never
    /// reaches the lowerer for an explicit-fallback query). <see cref="TryProbeBareValueRenders"/> below turns
    /// that into a clean decline instead, by trial-rendering the actual constant value (exact — the render
    /// path is deterministic in the value) or, for a parameter, a default instance of its declared
    /// (nullable-unwrapped) VALUE type (a valid proxy — <c>BsonValue.Create</c>'s admission decision is keyed
    /// on the CLR type, not the value, MEASURED against both a real and a default instance of the same
    /// rejecting type).
    /// </para>
    /// <para>
    /// <b>A parameter of a REFERENCE type is NOT a value-type-only hazard, and is handled by a narrow
    /// ALLOWLIST rather than a probe (EF-401 Task 4).</b> An earlier revision of this remark claimed the
    /// reachable failure "is a value-type shape"; that is MEASURED FALSE —
    /// <c>BsonValue.Create</c> rejects <see cref="Uri"/>, <see cref="Version"/> and any ordinary user class
    /// exactly as it rejects a custom struct, and a bare reference-type parameter sort key
    /// (<c>OrderBy(x =&gt; capturedUri)</c>) therefore threw the same uncaught
    /// <see cref="ArgumentException"/> — at EXECUTION time, inside
    /// <c>MongoPipelineFactory.SerializeParameter</c>, i.e. outside ANY compile-time fallback — where the
    /// pre-slice driver-LINQ fallback returned correct rows under <c>Native</c> (MEASURED at this slice's
    /// base and at HEAD, all three modes). A default reference-type proxy is always <see langword="null"/>,
    /// which renders unconditionally, so it cannot discriminate; and the reference types
    /// <c>BsonValue.Create</c> DOES accept cannot be recognised from the declared type alone —
    /// MEASURED against driver 3.10.0, it admits an array / <c>List&lt;T&gt;</c> / <c>IDictionary</c>
    /// STRUCTURALLY, by mapping each element, so <c>int[]</c> renders while <c>Uri[]</c> throws on its
    /// element. Admission is therefore restricted to the reference types measured to render for ANY value:
    /// <see cref="string"/> and a <see cref="MongoDB.Bson.BsonValue"/>. Everything else declines, which
    /// restores exactly the pre-slice behaviour (a clean decline to the driver-LINQ fallback, or
    /// <see cref="NativeTranslationNotSupportedException"/> under <c>NativeOnly</c>). Declining an admissible
    /// shape only costs nativeness, never correctness — which is why the allowlist is deliberately narrower
    /// than the measured admitted set.
    /// </para>
    /// <para>
    /// <b>A FILTERED owned-collection count (<c>b.Posts.Count(p =&gt; ...)</c>) is DECLINED as a sort key —
    /// and READ THE SECOND HALF OF THIS PARAGRAPH BEFORE CITING IT, because the obvious reading of the first
    /// half is measured false.</b> The gap is real: <c>CanRender</c> admits
    /// <c>MongoFilteredSizeExpression</c> (it recurses into <c>ElementPredicate</c>), while
    /// <c>AllFieldsDefaultSerialized</c> (<see cref="MongoExpressionTranslator"/>, the operand-serialization
    /// guard <see cref="MongoExpressionTranslator.TryTranslateValue"/> already applies to the OUTER expression)
    /// recurses only through field and binary nodes — its catch-all returns <see langword="true"/> for a
    /// filtered size unconditionally, so it never looks INSIDE that node's own element predicate. Without the
    /// decline below, <c>OrderBy(b =&gt; b.Posts.Count(p =&gt; p.Code &gt; 5))</c> over a <c>Code</c> mapped
    /// <c>HasBsonRepresentation(BsonType.String)</c> lowers into the ordinary <c>$set</c>/<c>$sort</c>/
    /// <c>$unset</c> bracket and emits <c>cond: {$gt: ["$$e.Code", "5"]}</c>, comparing the RAW STORED STRINGS
    /// lexicographically — <c>"10"</c> does not exceed <c>"5"</c> but <c>"6"</c> does — so each owner's count
    /// diverges from CLR semantics and the rows come back in a different ORDER.
    /// </para>
    /// <para>
    /// <b>What that divergence is NOT: a native-vs-fallback one. MEASURED, and this refutes the review finding
    /// that prompted the decline.</b> Explicit <c>DriverLinq</c> returns the IDENTICAL server-side order
    /// (<c>[cA, cB, cC]</c> where in-memory LINQ answers <c>[cB, cC, cA]</c>), because the driver's own LINQ
    /// provider serializes the comparison constant through the very same property serializer. So native and the
    /// fallback agree with each other and both differ from the CLR — the EF-359 accepted-divergence family —
    /// and <b>this decline changes no value anywhere; it changes ROUTING only.</b> There is consequently no
    /// order-based assertion that can discriminate it: the tripwire
    /// (<c>NativeComputedSortTests.Filtered_owned_collection_count_sort_key_declines_instead_of_going_native</c>)
    /// pins the decline through its <c>NativeOnly</c> leg (which SUCCEEDS with the clause removed — measured),
    /// and carries the <c>DriverLinq</c> parity leg specifically so this paragraph cannot be re-read as a
    /// wrong-data fix.
    /// </para>
    /// <para>
    /// <b>Why decline at all, then.</b> The element predicate's operands are genuinely outside the guard this
    /// method already applies to every other computed sort key, so admitting them is an inconsistency rather
    /// than a decision; and declining costs NATIVENESS ONLY, never correctness — the query falls back to
    /// driver-LINQ, whose answer is byte-identical (<see cref="NativeTranslationNotSupportedException"/> only
    /// under <c>NativeOnly</c>). That is the same over-declining stance this method's reference-type allowlist
    /// already takes ("deliberately narrower than the measured admitted set"). <b>The underlying gap is
    /// PRE-EXISTING in predicate and projection position and is NOT closed here</b> — it is equally unguarded
    /// in <c>Where(b =&gt; b.Posts.Count(pred) &gt; 2)</c> and <c>Select(b =&gt; new { N = b.Posts.Count(pred)
    /// })</c>; closing it properly means guarding the element predicate's own operands at their one source,
    /// inside <c>MongoExpressionTranslator</c>'s filtered-size translation, for every position at once.
    /// <b>The decline is deliberately NARROW:</b> an UNFILTERED count is a <c>MongoSizeExpression</c>, carries
    /// no element predicate and no operand serialization to diverge on, and stays native
    /// (<c>OrderBy(x =&gt; x.Posts.Count)</c> — the paired control, case 17 of that test class). It does cost
    /// one legitimate shape: a filtered count over DEFAULT-serialized operands declines too, accepted as the
    /// price of a one-clause node-kind check over a new recursive serialization guard. The check recurses
    /// through binary nodes because <c>TranslateOperand</c> admits a filtered count as an ordinary arithmetic
    /// operand, so <c>OrderBy(b =&gt; b.Posts.Count(pred) * 2)</c> is the same shape one level down.
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

        // A FILTERED count's element predicate escapes AllFieldsDefaultSerialized, so a non-default-serialized
        // operand inside it is compared in its RAW stored form and the resulting count — hence the row ORDER —
        // diverges from CLR semantics. NOTE (measured): explicit DriverLinq diverges IDENTICALLY, so this is a
        // routing decline, not a wrong-data fix — see this method's remarks before citing it as one. An
        // UNFILTERED MongoSizeExpression is deliberately NOT caught here (no element predicate, nothing to
        // diverge on) and stays native.
        if (ContainsFilteredSize(translated))
            return false;

        if (!TryProbeBareValueRenders(translated, keySelectorBody.Type))
            return false;

        result = translated;
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="node"/> is, or contains anywhere beneath an arithmetic/comparison binary
    /// node, a <see cref="MongoFilteredSizeExpression"/> — the one node kind
    /// <see cref="TryTranslateComputedSortKey"/> declines. Mirrors
    /// <c>MongoExpressionTranslator.AllFieldsDefaultSerialized</c>'s own shape (field / binary / catch-all),
    /// because those are exactly the node kinds <c>TryTranslateValue</c> can produce here; a
    /// <see cref="MongoSizeExpression"/> falls into the catch-all deliberately.
    /// </summary>
    private static bool ContainsFilteredSize(MongoExpression node)
        => node switch
        {
            MongoFilteredSizeExpression => true,
            MongoBinaryExpression b => ContainsFilteredSize(b.Left) || ContainsFilteredSize(b.Right),
            _ => false
        };

    /// <summary>
    /// Returns <see langword="false"/> only when <paramref name="translated"/> is a bare
    /// <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> whose value would make
    /// <see cref="MongoAggregationExpressionRenderer.Render"/> throw at pipeline-build time (see
    /// <see cref="TryTranslateComputedSortKey"/>'s remarks). Anything else (a binary/size/field-ref node —
    /// never a bare value) is trivially fine and returns <see langword="true"/> without probing.
    /// </summary>
    /// <remarks>
    /// The probe is EXACT for a <see cref="MongoConstantExpression"/> only — the value is known at translate
    /// time, so the real render path runs on the real value. For a <see cref="MongoParameterExpression"/> it is
    /// a type-keyed MODEL of that same path, not the path that actually executes; the case comment below states
    /// precisely how far the two agree and where the model over-declines.
    /// </remarks>
    private static bool TryProbeBareValueRenders(MongoExpression translated, Type declaredType)
    {
        switch (translated)
        {
            case MongoConstantExpression constant:
                // The actual value is already known at translate time, so render it for real — exact,
                // no proxy needed.
                return TryRender(constant);

            case MongoParameterExpression:
                // NOT an exact proxy — a MODEL, and the difference matters to anyone editing either side.
                // What actually runs for this node at Build time is MongoPipelineFactory.SerializeParameter,
                // not this render call; the two agree today only because a BARE value parameter carries no
                // property serializer (ForSerialization is null => MongoValueRenderer records a
                // serializer-less placeholder => SerializeParameter takes its `serializer is null` arm, which
                // is BsonValue.Create — the same function the constant render below reaches). What is probed
                // is therefore the right FUNCTION but not the right VALUE: a default instance stands in for a
                // runtime value that is not knowable here, which is sound only because BsonValue.Create's
                // admission decision is keyed on the .NET TYPE (measured). Where the declared type is looser
                // than the runtime one (an `object`-typed parameter boxing an int, say) the probe DECLINES a
                // shape that would have rendered — an over-decline, which costs nativeness only.
                // If SerializeParameter ever stops routing a bare value through BsonValue.Create, this probe
                // must be re-pointed at whatever replaces it.
                var underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                if (underlying.IsValueType)
                {
                    var sample = Activator.CreateInstance(underlying);
                    return TryRender(new MongoConstantExpression(sample, forSerialization: null));
                }

                // A reference type cannot be probed (a default proxy is always null, which renders
                // unconditionally) and cannot be admitted by container kind either — BsonValue.Create maps a
                // collection STRUCTURALLY, element by element, so int[] renders and Uri[] throws. Admit only
                // the reference types measured to render for ANY value; decline the rest, which is the
                // pre-slice behaviour. See this method's caller's remarks.
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
                // The BROAD catch is deliberate, not laziness, and narrowing it would re-open the hole this
                // probe exists to close. The question being asked is exactly "does rendering this value throw",
                // and the answer for ANY throw is the same — decline, and let the query fall back. The two
                // types measured today are ArgumentException (BsonValue.Create rejecting the CLR type) and
                // NativeTranslationNotSupportedException (a property serializer refusing the value), but the
                // renderer and the driver's BsonValue.Create are both free to add others, and an exception type
                // this method did not anticipate would escape at TRANSLATE time — a crash where a decline is
                // correct — or, worse, be caught only later at pipeline-BUILD time, outside any fallback, which
                // is precisely the EF-401 fix-round-1 failure this probe was added for. Nothing is swallowed
                // that matters: the node is discarded either way, and the caller's decline is loud under
                // NativeOnly.
                return false;
            }
        }
    }
}
