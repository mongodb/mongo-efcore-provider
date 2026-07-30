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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;        // IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Attempts to populate the native <c>$project</c> slot (<see cref="MongoSelectDefinition"/>
/// Projection) from a terminal member-access anonymous/DTO selector. Extracted from the QMTEV (EF-332).
/// Returns <see langword="true"/> (and fills <c>Select.Projection</c>) only when every leaf is a plain
/// member access the translator resolves to a document field, or a projected collection-navigation
/// <c>Count</c>/<c>LongCount</c> (EF-339 Task 4); otherwise leaves the slot empty.
/// </summary>
internal static class NativeProjectionBinder
{
    internal static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var projections = new List<MongoProjection>();
        // Parallel to projections: true at index i when that leaf is itself the owned array leaf. Needed by
        // the sibling-readability check below, which must skip the array leaf(s) themselves (already proven
        // whole-document-readable by IsNativeArrayProjectionLeaf's own alias-agreement conjunct) and examine
        // every OTHER leaf instead.
        var leafIsArray = new List<bool>();
        // Lookups discovered by count-leaves are staged here rather than applied to mongoQ immediately,
        // so a later leaf failing native recognition (whole projection falls back) never leaves a
        // half-registered lookup behind on mongoQ.
        var pendingLookups = new List<LookupExpression>();
        // EF's MongoQueryExpression.AddToProjection disambiguates aliases case-insensitively (appending a
        // counter on collision). If two members here differ only by case, the DOM shaper would read the
        // disambiguated alias while the native $project emits the un-disambiguated one, silently dropping
        // a value. Bail to driver-LINQ rather than risk that.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // EF-322 owned-data slice 8, Task 4: true once any leaf accepted by TryTranslateLeaf was an owned
        // array leaf. Drives the owner-key emission below — see that block's comment for why.
        var hasArrayLeaf = false;

        switch (selector.Body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    var alias = newExpression.Members[i].Name;
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], newExpression.Arguments[i], alias, pendingLookups, out var leaf, out var isArrayLeaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                    leafIsArray.Add(isArrayLeaf);
                    hasArrayLeaf |= isArrayLeaf;
                }
                break;

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;

                    var alias = binding.Member.Name;
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], assignment.Expression, alias, pendingLookups, out var leaf, out var isArrayLeaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                    leafIsArray.Add(isArrayLeaf);
                    hasArrayLeaf |= isArrayLeaf;
                }
                break;

            default:
                return false;
        }

        // EF-322 owned-data slice 8, Task 5 fix round 1 (branch-review finding — a COMPLETENESS gap in the array-leaf
        // admissibility invariant, not a weakening of it; see IsNativeArrayProjectionLeaf's remarks for the
        // invariant this extends). An array leaf's own alias-agreement conjunct proves ITS alias-addressed
        // read resolves correctly against a WHOLE, un-projected document (the shape a late DriverLinq fallback
        // hands the shaper). But an array leaf's mere PRESENCE in a projection also containing any OTHER leaf
        // forces EF's own client-side "mixed" shaper the instant that projection ever executes via fallback —
        // any entity/collection-typed leaf makes EF's own ProjectionAnalyzer.CanPushDown refuse to hand the
        // query to the driver's LINQ v3 provider, exactly the same "mixed-whole-entity-plus-computed" hazard
        // Query/AGENTS.md's arithmetic-computed-projections note already documents for a whole-entity leaf.
        // So the identical whole-document-readable invariant has to hold for every OTHER leaf too, not just
        // the array leaf's own — a plain top-level field whose alias equals its own document element name
        // reads correctly off a whole document; a RENAMED-alias field, a DOTTED (owned sub-property) field, or
        // any COMPUTED leaf (a count or arithmetic expression — its alias names no document element at all)
        // does not. Decline the WHOLE projection (not just the array leaf) when any sibling fails this — this
        // is one admissibility rule applied uniformly across the projection's leaves, not two rules.
        if (hasArrayLeaf)
        {
            for (var i = 0; i < projections.Count; i++)
            {
                if (!leafIsArray[i] && !IsWholeDocumentReadableLeaf(projections[i].Alias, projections[i].Expression))
                    return false;
            }
        }

        // EF-322 owned-data slice 8, Task 4: an owned element with a SHADOW key (no explicit HasKey) reads
        // its OWNER's key out of the document the shaper is handed — the element shaper resolves it through
        // _ownerMappings, not through anything stored on the element itself. A $project that emits only the
        // requested aliases (e.g. { Title, Posts }) has no _id, so materialization then fails PER ROW with
        // "Document element is missing for required non-nullable property '<Key>'" (BsonBinding.GetPropertyValue,
        // via PopulateCollection). Emit the root key alongside the requested aliases to fix that. This is inert
        // for the RESULT SHAPE — the shaper reads every result member by alias, never positionally, and the
        // extra "_id" projection is never bound to any ProjectionMember — and it correctly suppresses
        // MongoPipelineFactory.RenderProject's default `_id : 0` exclusion, which fires only when the projection
        // does not itself emit _id (see RenderProject). An explicit-HasKey element never performs the owner-key
        // read at all, so emitting _id alongside it is harmless too; keying this on the LEAF KIND (hasArrayLeaf)
        // rather than on the element's OWN key kind (shadow vs. explicit) keeps one code path instead of two —
        // NativeProjectionBinder has no cheap way to tell those apart from here, and it doesn't need to.
        //
        // NOTE for a future slice that makes this emission CONDITIONAL (e.g. only for a shadow-key element):
        // MongoQueryableMethodTranslatingExpressionVisitor.IsPlainProjectedSelect's set-op-operand decline is
        // gated on HasArrayProjectionLeaf, i.e. it is named for the LEAF while the hazard it guards is this
        // OWNER KEY leaking into the set operation's whole-document comparison key. The two conditions are
        // identical TODAY, which is why one flag serves both; decouple them and that gate would be keyed on the
        // wrong signal while still, coincidentally, reading correctly. Change both together.
        if (hasArrayLeaf && seenAliases.Add("_id"))
        {
            // Properties[0] is a best-effort APPROXIMATION of the projection's CLR Type for a hypothetical
            // COMPOSITE-key root: a composite key is stored nested under "_id" (a sub-document), so no single
            // property's ClrType describes it. Inert today, because nothing reads this Type — the shaper resolves
            // the owner key through _ownerMappings, and "_id" is never bound to a ProjectionMember, so the
            // MongoElementRefExpression's Type is carried but never consulted. Whether a composite-key root can
            // reach here at all was NOT measured (no such fixture exists on this branch); the approximation is
            // recorded rather than defended. Re-derive the Type properly if anything ever starts reading it.
            var keyProperty = mongoQ.CollectionExpression.EntityType.FindPrimaryKey()!.Properties[0];
            projections.Add(new MongoProjection("_id", new MongoElementRefExpression("_id", keyProperty.ClrType)));
        }

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);
        foreach (var projection in projections)
            mongoQ.Select.AddProjection(projection);
        // EF-322 owned-data slice 8, Task 7: record the array leaf's PRESENCE for the one consumer that has to decline this
        // projection — the projected-set-op-operand scope gate. Set only here, alongside the commit, so a
        // projection that declined on any path above leaves no provenance behind.
        if (hasArrayLeaf)
            mongoQ.Select.HasArrayProjectionLeaf = true;
        return true;
    }

    /// <summary>
    /// Translates a single projection leaf: a plain top-level member access, an owned entity-collection leaf
    /// (<c>b.Posts</c> — a <see cref="MaterializeCollectionNavigationExpression"/>, EF-322 owned-data slice 8), or a projected
    /// collection-navigation <c>Count</c>/<c>LongCount</c> (see <see cref="TryTranslateProjectedCollectionCount"/>,
    /// which EF Core's nav-expansion lowers to a <see cref="MethodCallExpression"/>, NOT a member access).
    /// Anything else is not natively representable.
    /// </summary>
    private static bool TryTranslateLeaf(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        ParameterExpression outerParameter,
        Expression leafExpression,
        string alias,
        List<LookupExpression> pendingLookups,
        out MongoExpression result,
        out bool isArrayLeaf)
    {
        // Only the owned array-leaf branch below ever sets this true; every other accepted leaf kind (a plain
        // field, a projected count, an arithmetic leaf) needs no owner-key emission, so false is correct for
        // every early return in this method except that one.
        isArrayLeaf = false;

        if (leafExpression is MemberExpression && translator.TryTranslateField(leafExpression, out var field))
        {
            // A dotted (owned single-ref) leaf is read back RAW by the DOM shaper (the shaper's field-access
            // resolver is single-hop and cannot apply the converter for a nested owned chain), so a
            // value-converted or non-default-BsonRepresentation owned leaf would diverge from the CLR value.
            // Decline it → the projection falls back to driver-LINQ (which resolves it correctly). Top-level
            // leaves have no dot and are unaffected (they already round-trip converters correctly).
            if (field.ElementName.Contains('.')
                && !NativeGroupByBinder.HasDefaultKeySerialization(field.Property))
            {
                result = null!;
                return false;
            }
            result = field;
            return true;
        }

        // An owned entity-COLLECTION leaf (EF-322 owned-data slice 8): `new { b.Title, b.Posts }`.
        // EF's nav-expansion always wraps the navigation in a MaterializeCollectionNavigationExpression whose
        // Subquery is `EF.Property(b, "Posts").AsQueryable()` — MEASURED on this shape, not assumed — so this
        // branch is structurally disjoint from the MemberExpression branch above and from every branch below.
        // A PRIMITIVE collection (`b.Tags`) is a mapped PROPERTY, arrives as a plain member access, and is
        // already handled by that branch; it never reaches here. The Subquery (not the wrapper) is what goes to
        // the translator, which strips the AsQueryable() layer itself.
        if (leafExpression is MaterializeCollectionNavigationExpression materializeCollection
            && IsNativeArrayProjectionLeaf(
                materializeCollection.Navigation as INavigation, mongoQ.CollectionExpression.EntityType, alias)
            && translator.TryTranslateOwnedCollectionArray(materializeCollection.Subquery, out var arrayRef))
        {
            result = arrayRef;
            isArrayLeaf = true;
            return true;
        }

        if (TryTranslateProjectedCollectionCount(mongoQ, outerParameter, leafExpression, pendingLookups, out var sizeExpression))
        {
            result = sizeExpression!;
            return true;
        }

        // Arithmetic computed leaf (EF-347): a numeric (+ - * / %) projection leaf renders as an aggregation
        // operator document (e.g. { $multiply: [...] }) via MongoAggregationExpressionRenderer, and the DOM shaper
        // reads it back raw by alias. Gate to a BINARY arithmetic top node only, so a bare constant/parameter leaf
        // stays on the fallback path; TryTranslateValue's numeric-type and divergence guards handle
        // string-concat / integer-division / converted operands.
        //
        // This comment USED TO SAY a bare constant/parameter leaf "would render as a bare value that $project
        // misreads as an inclusion flag", which implies silently wrong results. That mechanism was MEASURED
        // FALSE — see the correction on the count branch ~20 lines below, whose mutation (relaxing the count gate
        // to plain TryTranslateValue success) admits exactly the set that dropping THIS branch's BinaryExpression
        // requirement would, so the same measurement covers this line: a truthy constant / captured parameter
        // returns CORRECT values (folded client-side, junk field in the emitted $project), while a 0/false
        // constant ABORTS the aggregate. Same narrow gate, different reason for it.
        if (leafExpression is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo }
            && translator.TryTranslateValue(leafExpression, out var computed))
        {
            result = computed;
            return true;
        }

        // Owned (embedded) collection count leaf (EF-322): `new { N = b.Posts.Count }`. The same
        // TryMatchCountExpression + TryResolveOwnedCollectionPath pair the arithmetic branch above already
        // reaches through TryTranslateValue — a count inside `Count * 2` has been native since the .Count
        // predicate slice — just with no arithmetic wrapped around it.
        //
        // Gate on the resulting NODE KIND, not on "TryTranslateValue succeeded" — a bare constant/parameter leaf
        // translates perfectly well but renders as a BARE VALUE, and $project reads a bare value as an
        // inclusion/exclusion FLAG rather than a literal. { $size: ... } is a document, so it is safe exactly
        // where a bare value is not.
        //
        // The CONSEQUENCE of widening is MEASURED, and it is not what this comment first claimed. It used to say
        // $project "misreads" the value as an inclusion flag ({X: 1}), which implies silently wrong results.
        // Mutation-tested (gate relaxed to plain TryTranslateValue success): `new { b.Title, X = 5 }` and a
        // captured-parameter leaf return CORRECT values — Visit's own constant/parameter cases fold them
        // client-side, and the only damage is a junk `X: 5` in the emitted $project — but `new { b.Title, X = 0 }`
        // HARD-FAILS under the default Native mode with
        //   MongoCommandException: Invalid $project :: caused by :: Cannot do exclusion on field X in inclusion projection
        // because $project reads 0/false as an EXCLUSION flag. So the gate is still right to be narrow; the
        // hazard is an abort on a 0/false constant, not a silent misread.
        //
        // Widening is also caught by a test now. It was NOT before: at the time that mutation was first run, the
        // whole functional Query namespace stayed green, 0 failed — nothing caught it. (Bare pass COUNTS are
        // deliberately not recorded here or in the sibling comments: three different counts from three different
        // points in this branch's life were irreconcilable to a later reader, and a fourth run of the same filter
        // gave a fifth number. The outcome plus what was run is the durable part.) See
        // NativeOwnedCollectionCountTests.Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate,
        // which pins the measured routing (the shape DECLINES today — NativeOnly throws) and the correct values.
        //
        // Ordering: this runs AFTER the arithmetic branch, but that ordering is NOT load-bearing — also measured:
        // swapping the two branches kept the full functional Query suite green, 0 failed (run on this branch under
        // "Debug EF10"; see the note above on why no pass count is quoted), including
        // Arithmetic_projection_leaf_containing_a_count_goes_native, because `Count * 2` translates to a
        // MongoBinaryExpression, fails the `is MongoSizeExpression` test, and reaches the arithmetic branch
        // regardless of order. What decides the binding is the node-kind test. The order only avoids calling
        // TryTranslateValue twice.
        if (translator.TryTranslateValue(leafExpression, out var value) && value is MongoSizeExpression size)
        {
            result = size;
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// THE single admissibility rule for an owned entity-collection array projection leaf
    /// (EF-322 owned-data slice 8), shared by the emit side (<see cref="TryTranslateLeaf"/>) and the shaper side
    /// (<c>MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is ONE method, called from two places, deliberately — do not re-inline it.</b> The two sides must
    /// admit exactly the same set of navigations, and the consequence of them disagreeing is SILENT WRONG DATA,
    /// not a decline: if the emit side accepts a navigation the shaper side rejects, the emitted <c>$project</c>
    /// flattens the array to a top-level alias while the shaper still reads it at the navigation's document path,
    /// producing an EMPTY collection with no exception anywhere. Widening the rule is therefore a single edit
    /// here, not two edits that have to be remembered together.
    /// </para>
    /// <para>
    /// <b>THE INVARIANT this rule enforces, and it is a correctness requirement rather than a scope statement:
    /// the alias-addressed read and the navigation's own DOCUMENT-PATH read must resolve to the same place.</b>
    /// The shaper is built at TRANSLATION time and is alias-addressed from then on, but whether a <c>$project</c>
    /// is actually emitted is decided LATER, by the compile-time gate — an explicit
    /// <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> (and, in principle, any other late fallback) executes
    /// <c>aggregate([])</c> and hands that same alias-addressed shaper a WHOLE document. So the alias read has to
    /// be correct against an un-projected document too. It is, precisely when both conjuncts below hold:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <paramref name="rootEntityType"/> — the navigation is declared on the query ROOT, so its document path is a
    /// single top-level element (no <c>Home.Notes</c> dotting) and the shaper's bare
    /// <c>RootReferenceExpression</c> resolves against the whole document rather than an embedded sub-document.
    /// </description></item>
    /// <item><description>
    /// <paramref name="alias"/> equals that element name, so "read top-level field &lt;alias&gt;" and "read the
    /// navigation at its document path" are literally the same read.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>This was MEASURED, not reasoned about after the fact.</b> Before the alias conjunct existed,
    /// <c>Select(b =&gt; new { b.Title, P = b.Posts })</c> returned the correct 1 element under <c>Native</c> and
    /// <c>NativeOnly</c> but <b>0 elements, silently, under explicit <c>DriverLinq</c></b> — the shaper looked for
    /// a top-level <c>P</c> in a whole document, found nothing, and the EF-358 coalesce turned that into an empty
    /// collection. The plain <c>new { b.Title, b.Posts }</c> spelling had masked it, because an anonymous type's
    /// implicit member name IS the property name and therefore happened to satisfy the invariant.
    /// </para>
    /// <para>
    /// Neither conjunct is intrinsic to the feature — both are what a mode-independent shaper costs. Lifting
    /// either one (a nested owner, or an arbitrary DTO/renamed alias) means giving the shaper a way to know
    /// whether the native pipeline will really be emitted, and is a later slice's work; the translator entry point
    /// this gates (<c>MongoExpressionTranslator.TryTranslateOwnedCollectionArray</c>) is already general enough
    /// for the nested path, so this predicate is the only thing narrowing it.
    /// </para>
    /// <para>
    /// <b>A fourth conjunct (EF-322 owned-data slice 8): the element type must carry no EAGER-LOADED navigation of
    /// its own</b> — no nested owned collection and no nested owned single reference (every owned navigation is
    /// eager-loaded by EF Core convention, so in practice this is exactly "no nested owned navigation"). This is
    /// NOT an optimization gap analogous to the two above; it is a decline that prevents this branch from MOVING a
    /// crash rather than fixing it. An element type with an eager-loaded navigation makes EF's nav-expansion emit
    /// the auto-include as an inner <c>Queryable.Select</c>, which <c>MongoProjectionBindingExpressionVisitor</c>
    /// rebuilds as an enumerable and <c>Expression.New</c>'s member-type validation then rejects
    /// (<c>ArgumentException</c>, "does not match the corresponding member type") at shaper-BUILD time, in EVERY
    /// <see cref="Infrastructure.MongoQueryMode"/>, before the mode is even read (<b>EF-360</b>, a separate,
    /// not-yet-fixed ticket). Admitting such an element here would not avoid that crash — the shaper-build code
    /// path is unconditional — it would only risk masking or relocating it behind this feature's own machinery.
    /// Declining keeps the shape BYTE-IDENTICAL to what it was before this slice, leaving the actual fix (widening
    /// <c>MatchTypes</c> or the shaper) to EF-360 itself.
    /// </para>
    /// <para>
    /// <b>Why <see cref="IReadOnlyNavigationBase.IsEagerLoaded"/> and not mere presence — a final-review
    /// correction.</b> This conjunct was originally written <c>!GetNavigations().Any()</c>, which ALSO declined an
    /// element carrying only the LAZY INVERSE back-reference to its own owner —
    /// <c>OwnsMany(b =&gt; b.Posts, p =&gt; p.WithOwner(x =&gt; x.Owner))</c>, an entirely ordinary model — so that
    /// model silently never went native (measured: <c>NativeOnly</c> threw). A lazy inverse back-reference is never
    /// auto-included by EF Core, so it produces no inner <c>Queryable.Select</c> and none of the crash mechanism
    /// above applies to it; only a navigation EF would try to auto-include does. The narrowing mirrors the sibling
    /// reference-kind guard in
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable</c>, which uses this exact
    /// <c>IsEagerLoaded</c> test for the same reason (see its own comment). The EF-360 decline itself is unchanged
    /// and still pinned byte-identically — see
    /// <c>NativeArrayProjectionTests.Element_with_its_own_navigation_is_declined_and_still_fails_identically_in_every_mode</c>
    /// for the nested-owned case that must keep declining, and
    /// <c>.Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native</c> for the owner
    /// back-reference that must not.
    /// </para>
    /// </remarks>
    /// <param name="navigation">
    /// The candidate navigation, or <see langword="null"/> when the node carried no <see cref="INavigation"/>
    /// (e.g. a skip navigation) — always declined.
    /// </param>
    /// <param name="rootEntityType">The query root's entity type (<c>CollectionExpression.EntityType</c>).</param>
    /// <param name="alias">
    /// The projection member's name — the alias the <c>$project</c> will emit under. Both sides derive it from the
    /// same <c>ProjectionMember</c>/member name, so they cannot disagree about it.
    /// </param>
    internal static bool IsNativeArrayProjectionLeaf(INavigation? navigation, IEntityType rootEntityType, string? alias)
        => navigation is not null
           && navigation.IsEmbedded()
           && navigation.IsCollection
           && navigation.DeclaringEntityType == rootEntityType
           && alias is not null
           && alias == navigation.TargetEntityType.GetContainingElementName()
           // Decline an element type carrying an EAGER-LOADED navigation of its own (a nested owned collection or
           // single reference) — EF's auto-include for it crashes shaper build, ticket EF-360. Admits ANY
           // non-eager-loaded navigation on the element, not just the lazy inverse back-reference to the owner
           // (WithOwner) — e.g. an owned element's own lazy reference to an unrelated, non-owned entity is
           // admitted too, on the identical reasoning: never auto-included, so none of the crash mechanism
           // applies. Matches the precedent at
           // MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable's Reference arm
           // (MongoQueryableMethodTranslatingExpressionVisitor.cs:582) exactly. See the remarks above.
           && !navigation.TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded);

    /// <summary>
    /// EF-322 owned-data slice 8, Task 5 fix round 1: true when <paramref name="leaf"/> would read back the SAME, correct value if
    /// looked up by <paramref name="alias"/> against a WHOLE, un-projected document — the shape a late
    /// <see cref="Infrastructure.MongoQueryMode.DriverLinq"/> fallback (or EF's own client-side "mixed" shaper,
    /// forced by ANY entity/collection leaf in the same projection — see the call site's remarks) hands the
    /// shaper. Only a plain top-level (non-dotted) field whose alias equals its own stored document element
    /// name qualifies — a renamed alias, an owned sub-property's dotted path, or any COMPUTED leaf (a
    /// <see cref="MongoSizeExpression"/> count or an arithmetic <see cref="MongoBinaryExpression"/>, neither of
    /// which is backed by any single document element) all read back wrong or not at all and must decline.
    /// </summary>
    private static bool IsWholeDocumentReadableLeaf(string alias, MongoExpression leaf)
        => leaf is MongoFieldExpression field
           && !field.ElementName.Contains('.')
           && alias == field.ElementName;

    /// <summary>
    /// Recognizes a projected collection-navigation <c>Count</c>/<c>LongCount</c> leaf
    /// (<c>select new { ..., OrderCount = c.Orders.Count }</c>) inside a terminal projection.
    /// </summary>
    /// <remarks>
    /// EF Core's nav-expansion rewrites <c>c.Orders.Count</c> directly to
    /// <c>Queryable.Count(Queryable.Where(DbSet&lt;Target&gt;(), predicate))</c> — mirroring the shape
    /// <see cref="Visitors.MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigationCount"/>
    /// recognizes on the driver-LINQ path (a bare <c>Count()</c>/<c>LongCount()</c> call with NO
    /// user-supplied predicate argument), but resolved here directly against
    /// <paramref name="outerParameter"/> (the selector's own lambda parameter) rather than a materialized
    /// outer shaper, because this binder runs on the raw selector before shaper substitution.
    /// <para>
    /// The <c>Where</c> predicate itself is EF's standard null-guarded correlation shape —
    /// <c>(outerKey != null) AndAlso Equals(Convert(outerKey, object), Convert(dependentKey, object))</c>
    /// (or, when the key type can't be null, the bare <c>Equals</c>/<c>==</c> form) — comparing the
    /// dependent-side FK property against <paramref name="outerParameter"/>'s key. This is recognized
    /// structurally via <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> (EF-347 slice 5 —
    /// extracted so a reference-<c>SelectMany</c> binder can share the same recognition) plus an
    /// exactly-two-conjunct guard: any ADDITIONAL predicate conjunct
    /// (e.g. <c>c.Orders.Where(o =&gt; o.Amount &gt; 5).Count()</c>) nests the null-guard/equality pair one
    /// level deeper as the left operand of an outer <c>AndAlso</c>, so the direct-conjunct match fails and
    /// the whole projection bails to driver-LINQ — never emitting a wrong-shape native count.
    /// </para>
    /// </remarks>
    private static bool TryTranslateProjectedCollectionCount(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression leafExpression,
        List<LookupExpression> pendingLookups,
        out MongoExpression? result)
    {
        result = null;

        if (leafExpression is not MethodCallExpression
            {
                Method: { DeclaringType: var countDeclaring } countMethod,
                Arguments: [var whereArg]
            }
            || countDeclaring != typeof(Queryable)
            || countMethod.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount)))
        {
            return false;
        }

        if (whereArg is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        // The Count binder wants a reference (non-embedded) collection navigation.
        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false, out var navigation))
        {
            return false;
        }

        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        if (!lookup.IsNativeCollectionLookup)
            return false;

        pendingLookups.Add(lookup);

        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), leafExpression.Type);
        return true;
    }
}
