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
using Microsoft.EntityFrameworkCore.Infrastructure;  // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;                                // Mql.Field
using MongoDB.EntityFrameworkCore.Extensions;        // IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

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
        // EF-322 step 3a: the alias a BARE selector body was admitted under, or null when the body was not
        // bare. Registered on the select in the commit block below, in the SAME block as AddProjection, so
        // "the emit gate opened for a bare body" and "the alias override exists" are one event rather than two
        // to keep ordered.
        string? bareProjectionAlias = null;
        // EF-362: the (memberName, alias) pairs a WRAPPED body's leaves were admitted under, whenever the
        // alias could not be the member's own name. Collected here and registered on the select in the commit
        // block below, alongside AddProjection, for the same reason the bare override is: "the emit gate
        // opened for this leaf" and "the override exists" have to be one event, not two to keep ordered.
        var namedAliasOverrides = new List<(string MemberName, string Alias)>();

        switch (selector.Body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    var memberName = newExpression.Members[i].Name;
                    var alias = DeriveWrappedLeafAlias(mongoQ, newExpression.Arguments[i], memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], newExpression.Arguments[i], alias, pendingLookups, out var leaf, out var isArrayLeaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                    if (alias != memberName)
                        namedAliasOverrides.Add((memberName, alias));
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

                    var memberName = binding.Member.Name;
                    var alias = DeriveWrappedLeafAlias(mongoQ, assignment.Expression, memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], assignment.Expression, alias, pendingLookups, out var leaf, out var isArrayLeaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                    if (alias != memberName)
                        namedAliasOverrides.Add((memberName, alias));
                    leafIsArray.Add(isArrayLeaf);
                    hasArrayLeaf |= isArrayLeaf;
                }
                break;

            // EF-322 step 3a: a BARE selector body — `b => b.Title`, `b => b.Posts`, `o => o.OrderID` — as
            // opposed to the two wrapped (anonymous-type / DTO) constructions above. It has no member name at
            // all, so the alias cannot come from the syntax the way a wrapped leaf's does; it is derived from
            // the TRANSLATED LEAF and registered as an override on the select, which every alias-reading site
            // then reads instead of deriving its own (see MongoSelectDefinition.AddProjectionAliasOverride).
            //
            // Only TIER 1 is admitted here — a leaf with a root-relative DOCUMENT PATH, whose alias IS that
            // path. That equality is what makes the alias-addressed read and the document-path read the SAME
            // read, so the shaper built here stays correct when a LATE fallback hands it whole documents
            // instead of the projected ones (see ShouldStripBareProjectionOnFallback). A COMPUTED leaf (a
            // count, a filtered count, arithmetic, OR — EF-322 slice A1, Task 6 — a numeric CAST) has no
            // document path and is declined below; admitting it needs the synthetic-alias tier, which is a
            // separate change (see NativeCastTests.Bare_cast_projection_leaf_declines_and_returns_correct_values
            // for the cast case specifically).
            default:
            {
                // A bare body appended onto an ALREADY-POPULATED projection is declined outright. Reaching
                // here with Projection.Count > 0 means a prior Select on this same select definition already
                // pushed a $project down (the composition-after-projection seam this file's callers document):
                // the emitted $project would then carry BOTH projections' entries while the single bare-body
                // ProjectionMember can name only one alias, and the alias-override table can hold only one
                // bare entry. Declining leaves the shape exactly as it was before step 3a — the whole
                // projection falls back — and it is what makes the bare override provably WRITE-ONCE, which
                // AddProjectionAliasOverride relies on (it uses Dictionary.Add, so a second write throws).
                if (mongoQ.Select.Projection.Count > 0)
                {
                    return false;
                }

                // Step 1 — the PROVISIONAL alias, needed only because TryTranslateLeaf's owned-array branch
                // takes the alias as an INPUT (IsNativeArrayProjectionLeaf's alias-agreement conjunct). For a
                // bare array body that conjunct is therefore vacuous — we choose the alias it demands — and
                // what actually admits the leaf is its DeclaringEntityType == rootEntityType sibling, which is
                // what forces the single top-level hop tier 1 requires. Every other leaf kind ignores the
                // alias argument entirely, so the placeholder is never observable.
                var provisionalAlias = selector.Body is MaterializeCollectionNavigationExpression materializeBare
                    ? (materializeBare.Navigation as INavigation)?.TargetEntityType.GetContainingElementName()
                      ?? BareLeafProvisionalAlias
                    : BareLeafProvisionalAlias;

                if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], selector.Body, provisionalAlias,
                        pendingLookups, out var bareLeaf, out var bareIsArrayLeaf))
                {
                    return false;
                }

                // Steps 2 and 3 — derive the FINAL alias from the translated leaf rather than from the syntax,
                // and decline anything that is not path-addressable. For the array branch the two agree by
                // construction (an owned-collection array leaf's path IS the containing element name).
                if (!TryDeriveDocumentPathAlias(bareLeaf, out var derivedAlias))
                {
                    return false;
                }

                bareProjectionAlias = derivedAlias;
                seenAliases.Add(derivedAlias);
                projections.Add(new MongoProjection(derivedAlias, bareLeaf));
                leafIsArray.Add(bareIsArrayLeaf);
                hasArrayLeaf |= bareIsArrayLeaf;
                break;
            }
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
        // EF-322 step 3a: register the bare body's alias override in the SAME commit block as the projections
        // it describes, after every `return false` above — so a declined bare body leaves no override behind
        // and the read side keeps behaving exactly as it did before this slice. The tier is DocumentPath by
        // construction: TryDeriveDocumentPathAlias admits nothing else.
        if (bareProjectionAlias != null)
        {
            mongoQ.Select.AddProjectionAliasOverride(
                MongoSelectDefinition.BareProjectionMemberKey, bareProjectionAlias, ProjectionAliasTier.DocumentPath);
        }

        // EF-362: the same registration for a WRAPPED body's named members. DocumentPath by construction —
        // DeriveWrappedLeafAlias returns a non-member-name alias only when it IS the leaf's root-relative
        // document path.
        foreach (var (memberName, alias) in namedAliasOverrides)
        {
            mongoQ.Select.AddProjectionAliasOverride(memberName, alias, ProjectionAliasTier.DocumentPath);
        }

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

        // A plain top-level scalar leaf, in either spelling EF produces: a bare member (c.Foo) or the
        // shadow-safe EF.Property<T>(c, "Foo") call (EF-322 slice A2). Both are handed to TryTranslateField
        // unconditionally — its own TryResolveMember gate is what decides whether either shape resolves to a
        // real document field, so no separate type check is needed here beyond ruling out the leaf kinds every
        // OTHER branch below owns (an owned array leaf, a projected count, an arithmetic leaf, the vector-search
        // score) — each of those already declines cleanly through TryResolveMember/TryResolveOwnedFieldPath's
        // own structural checks (see their remarks), so admitting them here first is safe by construction.
        if ((leafExpression is MemberExpression
                || (leafExpression is MethodCallExpression efPropertyCall && efPropertyCall.Method.IsEFPropertyMethod()))
            && translator.TryTranslateField(leafExpression, out var field))
        {
            // A non-default-serialized leaf (a value converter, or a non-default BsonRepresentation) is only
            // read back correctly when the DOM shaper can resolve the leaf expression to its own IProperty and
            // therefore to its own serializer. Two SPELLINGS defeat that resolution, and both must decline here
            // or the projection returns the RAW STORED value, silently, under the default Native mode:
            //
            //  (a) A DOTTED (owned single-ref) leaf — MongoProjectionBindingRemovingExpressionVisitor's
            //      field-access resolver is single-hop and cannot walk a nested owned chain.
            //
            //  (b) A `Nullable<T>.Value` leaf (EF-322 slice A5 / EF-400) — even at TOP level. The EMIT side
            //      peels `.Value` (MongoExpressionTranslator.TryResolveMember), so `x.Score.Value` addresses
            //      the same field as `x.Score`; the READ side does NOT — TryResolveFieldAccessSource
            //      recognises a StructuralTypeShaperExpression but not a MemberExpression wrapping one, so
            //      Property comes back null and the read falls to BsonBinding.GetElementValue<T>, which builds
            //      a DEFAULT type serializer and discards the converter. Measured with
            //      ValueConverter<int,int>(v => v*2, v => v/2), stored 14, correct CLR value 7: both
            //      `new { V = x.Converted.Value }` and the bare `x.Converted.Value` returned 14.
            //
            // This comment USED TO ASSERT that "top-level leaves have no dot and are unaffected (they already
            // round-trip converters correctly)". Slice A5 invalidated that premise: the `.Value` spelling is a
            // top-level leaf whose read side cannot find the property. Hence the second disjunct.
            //
            // The `.Value` disjunct is a DECLINE, not a fix — the projection falls back to driver-LINQ, which
            // for this mapping throws exactly as the released packages do. Teaching TryResolveFieldAccess to
            // peel `.Value` so emit and read agree BY CONSTRUCTION is the real answer and is tracked as
            // EF-402; REMOVE this second disjunct when that lands.
            if (!NativeGroupByBinder.HasDefaultKeySerialization(field.Property)
                && (field.ElementName.Contains('.')
                    || (leafExpression is MemberExpression {Member.Name: nameof(Nullable<int>.Value), Expression: { } valueReceiver}
                        && Nullable.GetUnderlyingType(valueReceiver.Type) is not null)))
            {
                result = null!;
                return false;
            }
            result = field;
            return true;
        }

        // The synthetic vector-search relevance score (EF-322 VectorSearch slice, Task 5):
        // `new { e.Author, Score = EF.Property<double>(e, "__score") }` and its Mql.Field spelling.
        //
        // Admitted ONLY when this query actually emits the $addFields{__score} companion — see
        // TryRecognizeVectorScoreLeaf's remarks for why each of the three guards is load-bearing. The leaf is a
        // MethodCallExpression, so this branch is structurally disjoint from the member-access branch above and
        // from the count branch below (which requires Queryable.Count/LongCount).
        if (mongoQ.Select.VectorSearch is not null
            && TryRecognizeVectorScoreLeaf(leafExpression, outerParameter, out var scoreType))
        {
            result = new MongoElementRefExpression(MongoVectorSearchScoreStage.ScoreField, scoreType);
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
        //
        // EF-359 widens the admitted node kind to ALSO accept MongoFilteredSizeExpression — a predicated owned-
        // collection count leaf, `new { N = b.Posts.Count(p => p.Rank > 0) }`. It is admitted here for the SAME
        // reason a plain MongoSizeExpression is: it renders as a DOCUMENT ({$size: {$filter: ...}}), so $project
        // cannot misread it as an inclusion/exclusion flag the way it would a bare value. It is a SEPARATE node
        // kind from MongoSizeExpression, never a flag on it, precisely so this gate, the query-dialect Tier-1
        // renderer (TryRenderSizeComparison), the dialect classifier (IsQueryDialectRenderable) and the negator
        // (MongoExpressionNegator) all keep failing CLOSED for it by construction — see MongoFilteredSizeExpression's
        // own remarks for the full "sibling, not a flag" argument. This is still a node-kind gate, not "translation
        // succeeded": that is what keeps a bare constant/parameter leaf out (see the measurement above).
        //
        // EF-322 slice A1, Task 6 (fix round 1) folds a numeric CAST leaf (`new { X = (int)x.D }`) into this
        // SAME call/gate, rather than giving it a second `TryTranslateValue` call — the two node kinds
        // (MongoConvertExpression here, MongoSizeExpression/MongoFilteredSizeExpression above) are mutually
        // exclusive outcomes of ONE translation attempt on the SAME leafExpression, so trying it twice would
        // silently reintroduce the "avoids calling TryTranslateValue twice" property this comment's own ordering
        // paragraph claims a few lines up — which a separate cast branch (this task's first-round shape) broke
        // without saying so. No structural pre-filter on leafExpression's own top-level node kind is needed for
        // the cast half either, for the identical reason it is not needed for the count half: MongoConvertExpression
        // has exactly ONE construction site that originates a genuinely NEW node — TranslateOperand's Convert
        // branch (MongoExpressionTranslator.cs) — so `value is MongoConvertExpression` already IMPLIES
        // leafExpression was Convert-shaped. (A SECOND call site, MongoFieldPrefixRewriter.cs, exists too, but it
        // only REWRITES an already-existing MongoConvertExpression's operand while prefixing a SelectMany scope —
        // it never originates one from an unrelated leaf shape, so it has no bearing on this gate. An earlier
        // draft of this comment said "exactly one construction site in the whole codebase", which is what the
        // second site makes literally false; corrected here rather than repeated.)
        //
        // GUARD B — NOT THIS GATE — IS WHAT KEEPS A CAST OVER A VALUE-CONVERTED PROPERTY (OR A NON-DEFAULT
        // BsonRepresentation) OFF THIS PATH, and that dependency belongs in this comment, not only in the read
        // side's. TryTranslateValue applies Guard B (AllFieldsDefaultSerialized) to whatever TranslateOperand
        // returns; its `MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand)` arm recurses THROUGH
        // the cast into the field, and HasDefaultKeySerialization rejects a converter or a non-default
        // representation there exactly as it does for the arithmetic/count/comparison gates elsewhere in this
        // file — so `$toInt`/`$toLong`/`$toDouble`/`$toDecimal` over a RAW STORED (converted) value is never
        // emitted; TryTranslateValue returns false and the leaf declines at TRANSLATE time, before this line is
        // even reached. This is the fact that makes the read side's raw-alias bypass
        // (MongoProjectionBindingRemovingExpressionVisitor's UnaryExpression{Convert} branch) safe: that bypass
        // is reached only for a leaf THIS gate admitted, and this gate can never admit one backed by a converted
        // field. If a future edit relaxes Guard B, or adds a cast-leaf path that does not route through
        // TryTranslateValue, this dependency breaks silently and reopens the EF-402 defect class (see the A5
        // `.Value`-peel note elsewhere in this file for that class's own read-side account) with no guard at
        // this site. Pinned functionally by
        // NativeCastTests.Cast_over_a_value_converted_property_declines_instead_of_reading_the_raw_stored_value,
        // which asserts the NativeOnly OUTCOME AS A STRING ("threw NativeTranslationNotSupportedException")
        // rather than a bare Assert.Throws — a regression that wrongly admits the leaf would print the WRONG
        // VALUE it returned (the raw stored value, unconverted) instead of merely failing on a missing
        // exception. (There is no working driver-LINQ oracle for this shape either — the same
        // ValueConverterSerializer/IHasRepresentationSerializer limitation cases 12/13 measure for a numeric
        // cast over a converted property in comparison position — so Native/DriverLinq are asserted only to
        // throw, not to return a value; the NativeOnly assertion is what proves the guard.) The A5 precedent
        // this mirrors is
        // NativeNullableMemberTests.Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value.
        //
        // A WIDENING cast (`(long)x.I`, `(double)x.I`) is a DELIBERATE, DOCUMENTED BOUNDARY, not a defect this
        // gate fails to close. TranslateOperand's Convert branch UNWRAPS a widening conversion entirely rather
        // than wrapping it in a MongoConvertExpression, so the translated result is a bare MongoFieldExpression
        // (or, for a widened arithmetic/constant/parameter operand, whatever THAT resolves to) — a node kind
        // this gate correctly declines, since it is not one of the three admitted kinds. The whole WRAPPED
        // projection then falls back gracefully (correct values via driver-LINQ), exactly like any other
        // declined leaf. Admitting an unwrapped field ref here would mean projecting the RAW STORED field under
        // an alias whose declared CLR type is the CAST's target type — a read-back type question distinct from
        // anything this task measured, and deliberately NOT taken up in this round (see the task report's
        // recommendation for a possible follow-up ticket). Pinned as a boundary, not a residual, by
        // NativeCastTests.Widening_cast_projection_leaf_still_falls_back_gracefully.
        if (translator.TryTranslateValue(leafExpression, out var value)
            && value is MongoSizeExpression or MongoFilteredSizeExpression or MongoConvertExpression)
        {
            result = value;
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Recognizes the synthetic vector-search relevance score as a projection leaf — exactly
    /// <c>EF.Property&lt;double&gt;(e, "__score")</c> or
    /// <c>Mql.Field(e, "__score", DoubleSerializer.Instance)</c>, both rooted on the selector's own parameter
    /// (EF-322 VectorSearch slice, Task 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three guards, each load-bearing for a DIFFERENT reason. None is decoration.</b>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The caller's <c>Select.VectorSearch is not null</c> check</b> (at the call site, not here, because
    /// this method is deliberately free of query state). Only a query carrying a bound vector search emits the
    /// <c>$addFields{__score}</c> companion, so only there does the element this leaf names actually exist. A
    /// plain query projecting <c>EF.Property&lt;double&gt;(e, "__score")</c> would otherwise emit a
    /// <c>$project</c> alias reading an element no stage writes. Today that shape falls back to driver-LINQ;
    /// with the guard it still does.
    /// </description></item>
    /// <item><description>
    /// <b>The literal <c>"__score"</c> element name.</b> This keeps a GENERAL driver element-addressing
    /// capability out of the native projection binder. Admitting arbitrary <c>Mql.Field</c> names opens
    /// serializer-honouring and value-converter questions that belong to the projection long tail, not here —
    /// note the read-back below IGNORES <c>Mql.Field</c>'s serializer argument entirely. Recognising exactly the
    /// one synthetic element this slice is about is inside the "targeted decline of <c>Mql.Field</c>" ruling,
    /// not an exception to it.
    /// </description></item>
    /// <item><description>
    /// <b>The CLR type <c>double</c>/<c>double?</c>.</b> The DOM shaper reads this leaf back RAW by alias
    /// (<c>BsonBinding.CreateGetElementValue</c> → <c>BsonSerializerFactory.CreateTypeSerializer(type)</c>),
    /// which — again — ignores any serializer the user passed to <c>Mql.Field</c>.
    /// <c>$meta: "vectorSearchScore"</c> always yields a BSON double, so <c>double</c> is exact; any other
    /// requested type could read back differently from what the driver's own serializer would have produced on
    /// the fallback path.
    /// </description></item>
    /// </list>
    /// <para>
    /// Everything else — another element name, another CLR type, a receiver that is not the selector's own
    /// parameter — returns <see langword="false"/>, and the WHOLE projection then declines gracefully to
    /// driver-LINQ (correct results under <c>Native</c>/<c>DriverLinq</c>, throwing only under
    /// <c>NativeOnly</c>), exactly as any other unrecognized leaf does.
    /// </para>
    /// <para>
    /// The receiver is matched by REFERENCE against <paramref name="outerParameter"/>, never by type — the same
    /// identity-not-name rule the SelectMany binders' scope routing follows.
    /// </para>
    /// </remarks>
    private static bool TryRecognizeVectorScoreLeaf(
        Expression leafExpression,
        ParameterExpression outerParameter,
        out Type scoreType)
    {
        scoreType = null!;

        if (leafExpression is not MethodCallExpression call)
        {
            return false;
        }

        Expression receiver;
        string elementName;

        if (call.Method.IsEFPropertyMethod()
            && call.Arguments is [var efReceiver, ConstantExpression { Value: string efName }])
        {
            receiver = efReceiver;
            elementName = efName;
        }
        else if (call.Method.IsGenericMethod
                 && call.Method.GetGenericMethodDefinition() == MqlFieldMethodInfo
                 && call.Arguments is [var mqlReceiver, ConstantExpression { Value: string mqlName }, _])
        {
            receiver = mqlReceiver;
            elementName = mqlName;
        }
        else
        {
            return false;
        }

        if (elementName != MongoVectorSearchScoreStage.ScoreField
            || !IsSelectorParameter(receiver, outerParameter))
        {
            return false;
        }

        if (call.Type != typeof(double) && call.Type != typeof(double?))
        {
            return false;
        }

        scoreType = call.Type;
        return true;
    }

    /// <summary>
    /// True when <paramref name="receiver"/> is the selector's own lambda parameter, possibly wrapped in
    /// auto-include layers EF's nav-expansion added.
    /// </summary>
    /// <remarks>
    /// <b>The <see cref="IncludeExpression"/> peel is load-bearing, and it was MEASURED, not anticipated.</b>
    /// An entity owning an eager-loaded navigation (every owned navigation is, by EF Core convention — the
    /// specification suite's <c>Book</c> owns a <c>Preface</c>) has its auto-include injected around the very
    /// expression the projection reads through, so the <c>Mql.Field</c> spelling arrives as
    /// <c>Mql.Field(IncludeExpression(e, Preface), "__score", …)</c> while the <c>EF.Property</c> spelling
    /// arrives with a bare parameter. Comparing the raw receiver by reference therefore admitted one spelling
    /// and silently declined the other on exactly the models that carry owned data. Peeling mirrors
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.TryGetWholeEntityMemberAccess</c>, which unwraps the
    /// same layers for the same reason; it is safe here because an include layer changes what is MATERIALIZED
    /// from the row, never which document the element is read out of.
    /// <para>
    /// Matching is by REFERENCE against the parameter, never by type — the same identity-not-name rule the
    /// SelectMany binders' scope routing follows, and the thing that keeps a captured outer entity of the same
    /// CLR type from being mistaken for the selector's own row.
    /// </para>
    /// </remarks>
    private static bool IsSelectorParameter(Expression receiver, ParameterExpression outerParameter)
    {
        var current = receiver.RemoveConvert();

        while (current is IncludeExpression include)
        {
            current = include.EntityExpression.RemoveConvert();
        }

        return ReferenceEquals(current, outerParameter);
    }

    /// <summary>
    /// The open generic definition of <c>Mql.Field&lt;TDocument, TField&gt;(TDocument, string, IBsonSerializer&lt;TField&gt;)</c>.
    /// Resolved by reflection because the driver exposes no canonical <see cref="MethodInfo"/> constant for it;
    /// matched by reference equality on the generic DEFINITION, never by name — mirroring
    /// <c>MongoProjectionBindingRemovingExpressionVisitor</c>'s own <c>Mql.Field</c> arm, which is the read-back
    /// side of the very same leaf.
    /// </summary>
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Mql.Field) && m.GetParameters().Length == 3);

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
    /// <paramref name="rootEntityType"/> — the navigation's array is reachable from the query ROOT by a DOTTED
    /// DOCUMENT PATH (<see cref="TryGetRootRelativeArrayPath"/>): every hop above it is a single embedded
    /// reference, so every segment resolves to a sub-document and the whole path is readable in one walk.
    /// <b>EF-362 widened this from "declared on the root" to "reachable by a dotted path from the root"</b>, so
    /// an <c>OwnsOne</c> hop (<c>Home.Notes</c>) now qualifies; a collection nested inside a collection
    /// (<c>Posts.Comments</c>) still does not, because an array intermediate has no dotted read at all.
    /// </description></item>
    /// <item><description>
    /// <paramref name="alias"/> equals that path, so "read element &lt;alias&gt;" and "read the navigation at its
    /// document path" are literally the same read.
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
    /// Neither conjunct is intrinsic to the feature — both are what a mode-independent shaper costs.
    /// <b>The nested-owner half was lifted by EF-362</b>, and NOT by giving the shaper a way to know whether the
    /// native pipeline will really be emitted (which is what this paragraph used to say it would take). It was
    /// lifted by keeping the invariant and making the alias a DOTTED path that satisfies it on both shapes —
    /// which needed a segment walk on the read side (<c>BsonBinding</c>) and a strip on the late-fallback route
    /// (<c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>, whose widening
    /// closed a MEASURED silent-empty-collection defect). The RENAMED-alias half is untouched and still
    /// declines: <c>DeriveWrappedLeafAlias</c> only ever replaces a member name that already agreed with the
    /// navigation's own containing element name.
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
           && alias is not null
           // EF-362 replaced the pair `DeclaringEntityType == rootEntityType && alias == GetContainingElementName()`
           // with this ONE test, which is the same invariant expressed against the FULL path instead of a single
           // hop. For a root-declared navigation the path IS the containing element name, so that case is
           // unchanged; for an OwnsOne hop the path is dotted ("Home.Notes") and the alias is the emit side's
           // chosen dotted alias, which the shaper walks segment by segment (BsonBinding.TryGetValueAtPath).
           // The walk requires every INTERMEDIATE hop to be a single embedded reference, so a collection nested
           // inside a collection ("Posts.Comments" — not addressable by a dotted read at all) still declines.
           && TryGetRootRelativeArrayPath(navigation, rootEntityType, out var arrayPath)
           && alias == arrayPath
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
    /// The root-relative DOCUMENT PATH of an owned collection navigation's stored array — the dotted join of
    /// every containing element name from the query root down to <paramref name="navigation"/>'s own
    /// (EF-362). <see langword="false"/> when the chain does not reach <paramref name="rootEntityType"/>
    /// through single embedded references only.
    /// </summary>
    /// <remarks>
    /// The intermediate-hop constraint is what keeps the path READABLE as a dotted name: every hop above the
    /// array must be a single embedded reference, so each segment resolves to a sub-document. A collection
    /// anywhere above it (an owned collection inside an owned collection) has no dotted read at all — the
    /// intermediate is an array, not a document — and declines here, exactly as it did before EF-362.
    /// </remarks>
    private static bool TryGetRootRelativeArrayPath(
        INavigation navigation, IEntityType rootEntityType, [NotNullWhen(true)] out string? path)
    {
        var segments = new List<string>();
        var current = navigation;

        // A bounded walk rather than `while (true)`: an owned chain is finite by construction, but a
        // translation-time infinite loop is not a failure mode worth risking on a model this code did not build.
        for (var depth = 0; depth < MaxOwnedChainDepth; depth++)
        {
            if (current.TargetEntityType.GetContainingElementName() is not { } segment)
            {
                break;
            }

            segments.Add(segment);

            if (current.DeclaringEntityType == rootEntityType)
            {
                segments.Reverse();
                path = string.Join(".", segments);
                return true;
            }

            if (current.DeclaringEntityType.FindOwnership()?.PrincipalToDependent is not INavigation owner
                || owner.IsCollection
                || !owner.IsEmbedded())
            {
                break;
            }

            current = owner;
        }

        path = null;
        return false;
    }

    /// <summary>The bound on <see cref="TryGetRootRelativeArrayPath"/>'s walk. Not a modelling limit.</summary>
    private const int MaxOwnedChainDepth = 32;

    /// <summary>
    /// The <c>$project</c> output alias a WRAPPED body's leaf is admitted under (EF-362): the member's own
    /// name, except for an owned-collection array leaf reached through one or more <c>OwnsOne</c> hops, where
    /// it is the leaf's dotted root-relative document path instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member-name conjunct is what keeps the RENAMED-alias decline intact. Deriving a path alias for any
    /// array leaf would also admit <c>new { P = b.Posts }</c> (aliasing it <c>Posts</c> and reading it back
    /// correctly) — a real widening, but a different one, with its own tripwire test. Requiring the member name
    /// to equal the navigation's own containing element name means this method only ever REPLACES a name that
    /// already agreed with the last path segment, never one the user chose differently.
    /// </para>
    /// <para>
    /// For a ROOT-declared array leaf the derived path equals the member name, so the alias is unchanged and no
    /// override is registered — every pre-EF-362 shape emits and reads exactly the same names as before.
    /// </para>
    /// </remarks>
    private static string DeriveWrappedLeafAlias(
        MongoQueryExpression mongoQ, Expression leafExpression, string memberName)
        => leafExpression is MaterializeCollectionNavigationExpression materializeCollection
           && materializeCollection.Navigation is INavigation navigation
           && memberName == navigation.TargetEntityType.GetContainingElementName()
           && TryGetRootRelativeArrayPath(navigation, mongoQ.CollectionExpression.EntityType, out var path)
            ? path
            : memberName;

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
    /// The placeholder alias handed to <see cref="TryTranslateLeaf"/> for a BARE selector body whose leaf is
    /// not an owned-collection array (EF-322 step 3a). Only the array branch reads the alias argument at all,
    /// and for that branch the caller supplies the navigation's own containing element name instead — so this
    /// value is never observable in a pipeline or a shaper. The leading space makes it unrepresentable as a
    /// stored element name, so it cannot accidentally satisfy that branch's alias-agreement conjunct either.
    /// </summary>
    private const string BareLeafProvisionalAlias = " bare";

    /// <summary>
    /// Derives a BARE selector body's projection alias from its TRANSLATED leaf (EF-322 step 3a), admitting
    /// only a leaf that has a root-relative document PATH and taking that path as the alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alias == document path is the whole point: the DOM shaper is built alias-addressed at TRANSLATION time,
    /// while whether a <c>$project</c> is really emitted is decided LATER (an explicit
    /// <see cref="Infrastructure.MongoQueryMode.DriverLinq"/>, or a late native-factory decline whose fallback
    /// is stripped back to whole documents — see
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>). When the alias
    /// IS the leaf's path, "read top-level element &lt;alias&gt;" and "read the leaf at its document path" are
    /// literally the same read, so one shaper is correct against a projected document and an un-projected one
    /// alike. This is the same invariant <see cref="IsNativeArrayProjectionLeaf"/> enforces for a wrapped array
    /// leaf, reached from the other direction: there the alias is given and checked, here it is chosen.
    /// </para>
    /// <para>
    /// Hence the two admitted node kinds and the DOTTED exclusion. A <see cref="MongoFieldExpression"/> covers
    /// a plain top-level scalar and a primitive-collection property; a <see cref="MongoElementRefExpression"/>
    /// covers the owned-collection array leaf (whose path is the containing element name) and the synthetic
    /// vector-search <c>__score</c> element (which the <c>$addFields</c> companion really writes). A DOTTED
    /// path is declined because the alias would have to be dotted too, and a dotted alias is looked up by the
    /// shaper as a LITERAL key while MongoDB's <c>$project</c> renders it as a NESTED document — that gap is
    /// EF-362's, and the decline here is its tripwire, not an oversight.
    /// </para>
    /// <para>
    /// Everything else — a count, a filtered count, an arithmetic leaf — is backed by no document element at
    /// all, so it has no path to use as an alias and is declined: the whole projection then falls back exactly
    /// as it did before step 3a. Admitting those needs a synthetic alias AND the matching (non-stripping)
    /// fallback disposition, which is deliberately a separate change rather than a widening of this method.
    /// </para>
    /// </remarks>
    private static bool TryDeriveDocumentPathAlias(MongoExpression leaf, out string alias)
    {
        switch (leaf)
        {
            case MongoFieldExpression field when !field.ElementName.Contains('.'):
                alias = field.ElementName;
                return true;

            case MongoElementRefExpression elementRef when !elementRef.Path.Contains('.'):
                alias = elementRef.Path;
                return true;

            default:
                alias = null!;
                return false;
        }
    }

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
