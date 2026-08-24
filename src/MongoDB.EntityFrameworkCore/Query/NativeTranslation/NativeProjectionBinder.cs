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
/// Attempts to populate the native <c>$project</c> slot (<see cref="MongoSelectDefinition"/> Projection)
/// from a terminal member-access anonymous/DTO selector.
/// </summary>
/// <remarks>
/// Returns <see langword="true"/> (and fills <c>Select.Projection</c>) only when every leaf is a plain
/// member access the translator resolves to a document field, or a projected collection-navigation
/// <c>Count</c>/<c>LongCount</c>; otherwise leaves the slot empty.
/// </remarks>
internal static class NativeProjectionBinder
{
    internal static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var projections = new List<MongoProjection>();
        // Parallel to projections: true at index i when that leaf is itself the owned array leaf. Used by the
        // sibling-readability check below, which skips the array leaf(s) (already proven whole-document-readable
        // by IsNativeArrayProjectionLeaf) and examines every other leaf.
        var leafIsArray = new List<bool>();
        // Lookups discovered by count-leaves are staged here rather than applied to mongoQ immediately, so a
        // later leaf failing native recognition (whole projection falls back) never leaves a half-registered
        // lookup behind.
        var pendingLookups = new List<LookupExpression>();
        // MongoQueryExpression.AddToProjection disambiguates aliases case-insensitively (appending a counter on
        // collision). If two members here differ only by case, the DOM shaper would read the disambiguated
        // alias while the native $project emits the un-disambiguated one, silently dropping a value. Bail to
        // driver-LINQ rather than risk that.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // True once any leaf accepted by TryTranslateLeaf was an owned array leaf. Drives the owner-key emission
        // below.
        var hasArrayLeaf = false;
        // The alias a BARE selector body was admitted under, or null when the body was not bare. Registered on
        // the select in the commit block below, in the same block as AddProjection, so "the emit gate opened for
        // a bare body" and "the alias override exists" are one event.
        string? bareProjectionAlias = null;
        // Which alias family the bare body was admitted under. Only meaningful when bareProjectionAlias is
        // non-null; carried alongside it rather than re-derived from the alias string at the commit block — see
        // AddProjectionAliasOverride's remarks for why the tier is data.
        var bareProjectionTier = ProjectionAliasTier.DocumentPath;
        // The (memberName, alias) pairs a WRAPPED body's leaves were admitted under, whenever the alias could
        // not be the member's own name. Registered on the select in the commit block below, alongside
        // AddProjection, for the same ordering reason as the bare override.
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

            // A BARE selector body — `b => b.Title`, `b => b.Posts`, `o => o.OrderID` — as opposed to the two
            // wrapped (anonymous-type / DTO) constructions above. It has no member name, so the alias cannot
            // come from the syntax the way a wrapped leaf's does; it is derived from the TRANSLATED LEAF and
            // registered as an override on the select, which every alias-reading site then reads instead of
            // deriving its own (see MongoSelectDefinition.AddProjectionAliasOverride).
            //
            // Two tiers are admitted here, tried in order, each correct for a different reason:
            //
            // TIER 1 (TryDeriveDocumentPathAlias) — a leaf with a root-relative document path, whose alias IS
            // that path. That equality is what makes the alias-addressed read and the document-path read the
            // same read, so the shaper stays correct when a late fallback hands it whole documents instead of
            // the projected ones (see ShouldStripBareProjectionOnFallback, which strips for this tier precisely
            // so that happens).
            //
            // TIER 2 (TryDeriveSyntheticAlias) — a COMPUTED leaf with no document path, under the reserved `_v`
            // alias. Correct for the mirror-image reason: `_v` is exactly what the driver names a bare
            // projection, so the late fallback is left un-stripped and the driver's own push-down writes the
            // element the shaper is already reading by.
            //
            // Tier 2 has two admitting arms with a deliberate asymmetry:
            //
            //   ARM 1a — a MongoSizeExpression or MongoFilteredSizeExpression as the TOP node, i.e. the bare
            //   body IS the count, AND a leaf whose un-stripped driver fallback cannot abort (see
            //   IsFallbackSafeBareSizeLeaf, which reconciles the gate's admitted set with the rewrite's reach by
            //   calling the rewrite's own matcher rather than restating it). No subtree check runs for this
            //   arm — "the body IS the count" is exactly what NullCoalesceSyntheticBareCountBody rewrites into
            //   its $ifNull form, so the driver's un-stripped push-down renders the same MQL native does.
            //
            //   ARM 1b — an arithmetic MongoBinaryExpression or a numeric-cast MongoConvertExpression as the
            //   top node, AND IsArrayFreeComputedSubtree over the whole subtree.
            //
            // Arm 1b's boundary is a SUBTREE fact, not a leaf-kind one: `b.Posts.Count * 2` is an arithmetic top
            // node, so a top-node-only gate would admit it, but its un-stripped driver fallback renders a bare
            // $size that aborts on a missing/null array. The rewrite doesn't save it either — it matches a body
            // that IS the count, never one that merely contains one. So arm 1b declines any subtree containing a
            // size node (`b.Posts.Count * 2`, `(int)(b.Posts.Count / 2.0)`), while `b.Posts.Count` and
            // `b.Posts.Count(pred)` are admitted by arm 1a. See TryDeriveSyntheticAlias, IsFallbackSafeBareSizeLeaf
            // and IsArrayFreeComputedSubtree for detail.
            default:
            {
                // A bare body appended onto an ALREADY-POPULATED projection is declined outright. Reaching here
                // with Projection.Count > 0 means a prior Select on this same select definition already pushed
                // a $project down: the emitted $project would then carry both projections' entries while the
                // single bare-body ProjectionMember can name only one alias, and the alias-override table can
                // hold only one bare entry. Declining keeps the bare override provably write-once, which
                // AddProjectionAliasOverride relies on (it uses Dictionary.Add, so a second write throws).
                if (mongoQ.Select.Projection.Count > 0)
                {
                    return false;
                }

                // A provisional alias, needed only because TryTranslateLeaf's owned-array branch takes the
                // alias as an input (IsNativeArrayProjectionLeaf's alias-agreement conjunct). For a bare array
                // body that conjunct is vacuous, since we choose the alias it demands; what actually admits the
                // leaf is its own root-path check. Every other leaf kind ignores this alias, so the placeholder
                // is never observable.
                var provisionalAlias = selector.Body is MaterializeCollectionNavigationExpression materializeBare
                    ? (materializeBare.Navigation as INavigation)?.TargetEntityType.GetContainingElementName()
                      ?? BareLeafProvisionalAlias
                    : BareLeafProvisionalAlias;

                if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], selector.Body, provisionalAlias,
                        pendingLookups, out var bareLeaf, out var bareIsArrayLeaf))
                {
                    return false;
                }

                // Derive the FINAL alias from the translated leaf rather than from the syntax.
                //
                // Tier 1 is tried first, and the ordering is load-bearing: a leaf with a root-relative document
                // path must take it, since that's what makes the alias-addressed read and the document-path
                // read the same read, letting the late-fallback strip work for it. Tier 2 answers only for a
                // leaf tier 1 cannot — a computed leaf backed by no document element — by choosing the alias the
                // driver would emit for a bare body, so leaving the driver's push-down in place is the correct
                // fallback (hence Synthetic, and hence the strip not firing).
                string derivedAlias;
                if (TryDeriveDocumentPathAlias(bareLeaf, out var documentPathAlias))
                {
                    derivedAlias = documentPathAlias;
                    bareProjectionTier = ProjectionAliasTier.DocumentPath;
                }
                else if (TryDeriveSyntheticAlias(bareLeaf, selector, pendingLookups, out var syntheticAlias))
                {
                    derivedAlias = syntheticAlias;
                    bareProjectionTier = ProjectionAliasTier.Synthetic;
                }
                else
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

        // An array leaf's own alias-agreement conjunct (see IsNativeArrayProjectionLeaf) proves ITS
        // alias-addressed read resolves correctly against a whole, un-projected document (the shape a late
        // DriverLinq fallback hands the shaper). But an array leaf's mere presence alongside any OTHER leaf
        // forces EF's own client-side "mixed" shaper the instant the projection executes via fallback — any
        // entity/collection-typed leaf makes ProjectionAnalyzer.CanPushDown refuse to hand the query to the
        // driver's LINQ v3 provider. So the same whole-document-readable invariant must hold for every other
        // leaf too: a plain top-level field whose alias equals its own document element name reads correctly
        // off a whole document; a renamed-alias field, a dotted (owned sub-property) field, or any computed leaf
        // (its alias names no document element at all) does not. Decline the whole projection when any sibling
        // fails this.
        if (hasArrayLeaf)
        {
            for (var i = 0; i < projections.Count; i++)
            {
                if (!leafIsArray[i] && !IsWholeDocumentReadableLeaf(projections[i].Alias, projections[i].Expression))
                    return false;
            }
        }

        // An owned element with a shadow key (no explicit HasKey) reads its owner's key out of the document the
        // shaper is handed — the element shaper resolves it through _ownerMappings, not anything stored on the
        // element itself. A $project that emits only the requested aliases has no _id, so materialization then
        // fails per row ("Document element is missing for required non-nullable property '<Key>'"). Emit the
        // root key alongside the requested aliases to fix that; it is inert for the result shape (the shaper
        // reads every result member by alias, and "_id" is never bound to a ProjectionMember) and it correctly
        // suppresses MongoPipelineFactory.RenderProject's default `_id : 0` exclusion. An explicit-HasKey
        // element never performs the owner-key read, so emitting _id alongside it is harmless too. Keyed on the
        // leaf kind (hasArrayLeaf) rather than the element's own key kind, since this binder has no cheap way to
        // tell those apart and doesn't need to.
        //
        // MongoQueryableMethodTranslatingExpressionVisitor.IsPlainProjectedSelect's set-op-operand decline is
        // gated on this same HasArrayProjectionLeaf flag, because the hazard it guards is this owner key leaking
        // into a set operation's whole-document comparison key — change both together if they ever diverge.
        if (hasArrayLeaf && seenAliases.Add("_id"))
        {
            // Properties[0] approximates the projection's CLR Type for a hypothetical composite-key root (a
            // composite key is stored nested under "_id", so no single property's ClrType describes it). Inert
            // today: nothing reads this Type, since the shaper resolves the owner key through _ownerMappings and
            // "_id" is never bound to a ProjectionMember. Re-derive the Type properly if anything starts reading it.
            var keyProperty = mongoQ.CollectionExpression.EntityType.FindPrimaryKey()!.Properties[0];
            projections.Add(new MongoProjection("_id", new MongoElementRefExpression("_id", keyProperty.ClrType)));
        }

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);
        foreach (var projection in projections)
            mongoQ.Select.AddProjection(projection);
        // Register the bare body's alias override in the same commit block as the projections it describes,
        // after every `return false` above, so a declined bare body leaves no override behind. The tier is
        // whichever of the two derivations above answered, carried in a local rather than re-derived here.
        if (bareProjectionAlias != null)
        {
            mongoQ.Select.AddProjectionAliasOverride(
                MongoSelectDefinition.BareProjectionMemberKey, bareProjectionAlias, bareProjectionTier);
        }

        // The same registration for a WRAPPED body's named members. DocumentPath by construction —
        // DeriveWrappedLeafAlias returns a non-member-name alias only when it IS the leaf's root-relative
        // document path.
        foreach (var (memberName, alias) in namedAliasOverrides)
        {
            mongoQ.Select.AddProjectionAliasOverride(memberName, alias, ProjectionAliasTier.DocumentPath);
        }

        // Record the array leaf's presence for the one consumer that must decline this projection — the
        // projected-set-op-operand scope gate. Set only here, alongside the commit, so a projection that
        // declined on any path above leaves no provenance behind.
        if (hasArrayLeaf)
            mongoQ.Select.HasArrayProjectionLeaf = true;
        return true;
    }

    /// <summary>
    /// Translates a single projection leaf: a plain top-level member access, an owned entity-collection leaf
    /// (<c>b.Posts</c> — a <see cref="MaterializeCollectionNavigationExpression"/>), or a projected
    /// collection-navigation <c>Count</c>/<c>LongCount</c> (see <see cref="TryTranslateProjectedCollectionCount"/>,
    /// which EF Core's nav-expansion lowers to a <see cref="MethodCallExpression"/>, not a member access).
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
        // field, a projected count, an arithmetic leaf) needs no owner-key emission.
        isArrayLeaf = false;

        // A plain top-level scalar leaf, in either spelling EF produces: a bare member (c.Foo) or the
        // shadow-safe EF.Property<T>(c, "Foo") call. Both are handed to TryTranslateField unconditionally — its
        // own TryResolveMember gate decides whether either shape resolves to a real document field, and the
        // leaf kinds every other branch below owns (an owned array leaf, a projected count, an arithmetic leaf,
        // the vector-search score) already decline cleanly through their own structural checks, so admitting
        // them here first is safe by construction.
        if ((leafExpression is MemberExpression
                || (leafExpression is MethodCallExpression efPropertyCall && efPropertyCall.Method.IsEFPropertyMethod()))
            && translator.TryTranslateField(leafExpression, out var field))
        {
            // A non-default-serialized leaf (a value converter, or a non-default BsonRepresentation) is only
            // read back correctly when the DOM shaper can resolve the leaf expression to its own IProperty and
            // therefore to its own serializer. Two spellings defeat that resolution and must decline here, or
            // the projection silently returns the raw stored value under the default Native mode:
            //
            //  (a) A DOTTED (owned single-ref) leaf — MongoProjectionBindingRemovingExpressionVisitor's
            //      field-access resolver is single-hop and cannot walk a nested owned chain.
            //
            //  (b) A `Nullable<T>.Value` leaf, even at top level. The emit side peels `.Value`
            //      (MongoExpressionTranslator.TryResolveMember), so `x.Score.Value` addresses the same field as
            //      `x.Score`; the read side does not — TryResolveFieldAccessSource recognises a
            //      StructuralTypeShaperExpression but not a MemberExpression wrapping one, so Property comes
            //      back null and the read falls to BsonBinding.GetElementValue<T>, which builds a default type
            //      serializer and discards the converter.
            //
            // The `.Value` disjunct is a decline, not a fix — the projection falls back to driver-LINQ, which
            // for this mapping throws exactly as the released packages do. Teaching TryResolveFieldAccess to
            // peel `.Value` so emit and read agree by construction is the real fix; remove this second disjunct
            // when that lands.
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

        // The synthetic vector-search relevance score:
        // `new { e.Author, Score = EF.Property<double>(e, "__score") }` and its Mql.Field spelling.
        //
        // Admitted only when this query actually emits the $addFields{__score} companion — see
        // TryRecognizeVectorScoreLeaf's remarks for why each guard is load-bearing. The leaf is a
        // MethodCallExpression, so this branch is structurally disjoint from the member-access branch above and
        // from the count branch below (which requires Queryable.Count/LongCount).
        if (mongoQ.Select.VectorSearch is not null
            && TryRecognizeVectorScoreLeaf(leafExpression, outerParameter, out var scoreType))
        {
            result = new MongoElementRefExpression(MongoVectorSearchScoreStage.ScoreField, scoreType);
            return true;
        }

        // An owned entity-collection leaf: `new { b.Title, b.Posts }`. EF's nav-expansion always wraps the
        // navigation in a MaterializeCollectionNavigationExpression whose Subquery is
        // `EF.Property(b, "Posts").AsQueryable()`, so this branch is structurally disjoint from the
        // MemberExpression branch above and from every branch below. A primitive collection (`b.Tags`) is a
        // mapped property, arrives as a plain member access, and is already handled by that branch. The
        // Subquery (not the wrapper) is what goes to the translator, which strips the AsQueryable() layer itself.
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

        // Arithmetic computed leaf: a numeric (+ - * / %) projection leaf renders as an aggregation operator
        // document (e.g. { $multiply: [...] }) via MongoAggregationExpressionRenderer, and the DOM shaper reads
        // it back raw by alias. Gated to a binary arithmetic top node only, so a bare constant/parameter leaf
        // stays on the fallback path; TryTranslateValue's numeric-type and divergence guards handle
        // string-concat / integer-division / converted operands.
        //
        // Widening this gate to admit any TryTranslateValue success would not silently misread a truthy
        // constant/parameter (folded client-side, correct value, just a junk field in the emitted $project) —
        // but a falsy (0/false) constant makes $project read it as an exclusion flag and aborts the aggregate.
        // See the count branch below for the same narrow-gate reasoning applied to counts.
        if (leafExpression is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo }
            && translator.TryTranslateValue(leafExpression, out var computed))
        {
            result = computed;
            return true;
        }

        // Owned (embedded) collection count leaf: `new { N = b.Posts.Count }`. Reaches TryTranslateValue via
        // the same path the arithmetic branch above already uses for a count nested inside arithmetic.
        //
        // Gate on the resulting NODE KIND, not on "TryTranslateValue succeeded" — a bare constant/parameter leaf
        // translates fine but renders as a bare value, and $project reads a bare value as an inclusion/exclusion
        // flag rather than a literal (a truthy constant folds client-side and is harmless beyond a junk emitted
        // field, but a 0/false constant hard-aborts the aggregate with "Cannot do exclusion on field ... in
        // inclusion projection"). { $size: ... } is a document, so it is safe exactly where a bare value is not.
        // See NativeOwnedCollectionCountTests.Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate.
        //
        // Running after the arithmetic branch is not load-bearing: `Count * 2` translates to a
        // MongoBinaryExpression regardless of order and reaches the arithmetic branch either way. The node-kind
        // test is what decides the binding; the order just avoids calling TryTranslateValue twice.
        //
        // MongoFilteredSizeExpression (a predicated owned-collection count, `b.Posts.Count(p => p.Rank > 0)`) is
        // admitted for the same reason as a plain MongoSizeExpression: it renders as a document
        // ({$size: {$filter: ...}}), never a bare value. It is a separate node kind from MongoSizeExpression,
        // never a flag on it, so this gate, the query-dialect renderer, the dialect classifier, and the negator
        // all keep failing closed for it by construction — see MongoFilteredSizeExpression's own remarks.
        //
        // A numeric CAST leaf (`new { X = (int)x.D }`) folds into this same call/gate rather than a second
        // TryTranslateValue call: MongoConvertExpression and MongoSizeExpression/MongoFilteredSizeExpression are
        // mutually exclusive outcomes of one translation attempt on the same leafExpression, so a single call
        // suffices. No structural pre-filter on leafExpression's own node kind is needed: MongoConvertExpression
        // has (with one irrelevant exception — MongoFieldPrefixRewriter only rewrites an existing instance's
        // operand, never originates one) exactly one construction site (TranslateOperand's Convert branch), so
        // `value is MongoConvertExpression` already implies leafExpression was Convert-shaped.
        //
        // A cast over a value-converted property (or a non-default BsonRepresentation) is kept off this path not
        // by this gate but by TryTranslateValue's own AllFieldsDefaultSerialized guard, which recurses through
        // the cast into the field and rejects a converter/non-default representation there — so
        // `$toInt`/`$toLong`/`$toDouble`/`$toDecimal` over a raw stored (converted) value is never emitted;
        // TryTranslateValue returns false before this line is reached. This is what makes the read side's
        // raw-alias bypass (MongoProjectionBindingRemovingExpressionVisitor's UnaryExpression{Convert} branch)
        // safe — it is reached only for a leaf this gate admitted, and this gate can never admit one backed by a
        // converted field. If a future edit relaxes that guard, or adds a cast-leaf path bypassing
        // TryTranslateValue, this dependency breaks silently. Pinned by
        // NativeCastTests.Cast_over_a_value_converted_property_declines_instead_of_reading_the_raw_stored_value
        // (there is no working driver-LINQ oracle for this shape either, so Native/DriverLinq both just throw).
        //
        // A WIDENING cast (`(long)x.I`, `(double)x.I`) is a deliberate, documented boundary, not a defect this
        // gate fails to close. TranslateOperand's Convert branch unwraps a widening conversion entirely rather
        // than wrapping it in a MongoConvertExpression, producing a bare MongoFieldExpression this gate correctly
        // declines (not one of the three admitted kinds); the whole wrapped projection then falls back gracefully
        // via driver-LINQ, like any other declined leaf. Admitting an unwrapped field ref here would mean
        // projecting the raw stored field under an alias whose declared CLR type is the cast's target type — a
        // read-back type question deliberately not taken up here. A widening Convert is what
        // the C# compiler inserts for ordinary numeric-cast projection shapes, so a fair fraction of them fall
        // back here. Pinned by NativeCastTests.Widening_cast_projection_leaf_still_falls_back_gracefully.
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
    /// <c>Mql.Field(e, "__score", DoubleSerializer.Instance)</c>, both rooted on the selector's own parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three guards, each load-bearing for a different reason:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The caller's <c>Select.VectorSearch is not null</c> check (at the call site, not here, since this method
    /// is deliberately free of query state) — only a query carrying a bound vector search emits the
    /// <c>$addFields{__score}</c> companion, so only there does the element this leaf names actually exist.
    /// </description></item>
    /// <item><description>
    /// The literal <c>"__score"</c> element name — keeps a general driver element-addressing capability out of
    /// the native projection binder. Admitting arbitrary <c>Mql.Field</c> names would open serializer-honouring
    /// and value-converter questions that belong to the projection long tail; note the read-back below ignores
    /// <c>Mql.Field</c>'s serializer argument entirely.
    /// </description></item>
    /// <item><description>
    /// The CLR type <c>double</c>/<c>double?</c> — the DOM shaper reads this leaf back raw by alias via a
    /// default type serializer, again ignoring any serializer passed to <c>Mql.Field</c>.
    /// <c>$meta: "vectorSearchScore"</c> always yields a BSON double, so <c>double</c> is exact.
    /// </description></item>
    /// </list>
    /// <para>
    /// Everything else — another element name, another CLR type, a receiver that is not the selector's own
    /// parameter — returns <see langword="false"/>, and the whole projection declines gracefully to driver-LINQ.
    /// </para>
    /// <para>
    /// The receiver is matched by reference against <paramref name="outerParameter"/>, never by type — the same
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
    /// The <see cref="IncludeExpression"/> peel is load-bearing: an entity owning an eager-loaded navigation
    /// (every owned navigation is, by EF Core convention) has its auto-include injected around the very
    /// expression the projection reads through, so the <c>Mql.Field</c> spelling arrives as
    /// <c>Mql.Field(IncludeExpression(e, Preface), "__score", …)</c> while the <c>EF.Property</c> spelling
    /// arrives with a bare parameter. Comparing the raw receiver by reference would admit one spelling and
    /// silently decline the other on models carrying owned data. Peeling mirrors
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.TryGetWholeEntityMemberAccess</c>; it is safe here
    /// because an include layer changes what is materialized from the row, never which document the element is
    /// read out of.
    /// <para>
    /// Matching is by reference against the parameter, never by type — the same identity-not-name rule the
    /// SelectMany binders' scope routing follows, which keeps a captured outer entity of the same CLR type from
    /// being mistaken for the selector's own row.
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
    /// The single admissibility rule for an owned entity-collection array projection leaf, shared by the emit
    /// side (<see cref="TryTranslateLeaf"/>) and the shaper side
    /// (<c>MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is one method, called from two places, deliberately — do not re-inline it. The two sides must admit
    /// exactly the same set of navigations, or the emitted <c>$project</c> and the shaper disagree about where
    /// the array lives: if the emit side accepts a navigation the shaper side rejects, the emitted <c>$project</c>
    /// flattens the array to a top-level alias while the shaper still reads it at the navigation's document path,
    /// silently producing an empty collection. Widening the rule is therefore a single edit here.
    /// </para>
    /// <para>
    /// The invariant this rule enforces — a correctness requirement, not a scope statement — is that the
    /// alias-addressed read and the navigation's own document-path read must resolve to the same place. The
    /// shaper is built at translation time and is alias-addressed from then on, but whether a <c>$project</c> is
    /// actually emitted is decided later, by the compile-time gate: an explicit
    /// <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> (or any other late fallback) executes <c>aggregate([])</c>
    /// and hands that same alias-addressed shaper a whole document. So the alias read has to be correct against
    /// an un-projected document too. It is, precisely when both conjuncts below hold:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <paramref name="rootEntityType"/> — the navigation's array is reachable from the query root by a dotted
    /// document path (<see cref="TryGetRootRelativeArrayPath"/>): every hop above it is a single embedded
    /// reference, so every segment resolves to a sub-document and the whole path is readable in one walk. An
    /// <c>OwnsOne</c> hop (<c>Home.Notes</c>) qualifies; a collection nested inside a collection
    /// (<c>Posts.Comments</c>) does not, because an array intermediate has no dotted read at all.
    /// </description></item>
    /// <item><description>
    /// <paramref name="alias"/> equals that path, so "read element &lt;alias&gt;" and "read the navigation at its
    /// document path" are literally the same read.
    /// </description></item>
    /// </list>
    /// <para>
    /// Without the alias conjunct, <c>Select(b =&gt; new { b.Title, P = b.Posts })</c> returns the correct 1
    /// element under <c>Native</c>/<c>NativeOnly</c> but 0 elements, silently, under explicit <c>DriverLinq</c> —
    /// the shaper looks for a top-level <c>P</c> in a whole document, finds nothing, and the empty-coalesce turns
    /// that into an empty collection. The plain <c>new { b.Title, b.Posts }</c> spelling masks this, because an
    /// anonymous type's
    /// implicit member name IS the property name and therefore happened to satisfy the invariant.
    /// </para>
    /// <para>
    /// Neither conjunct is intrinsic to the feature — both are what a mode-independent shaper costs. The
    /// nested-owner half is handled by keeping the invariant and making the alias a dotted path that satisfies it
    /// on both shapes, which needs a segment walk on the read side (<c>BsonBinding</c>) and a strip on the
    /// late-fallback route (<c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>).
    /// The renamed-alias half still declines: <c>DeriveWrappedLeafAlias</c> only ever replaces a member name that
    /// already agreed with the navigation's own containing element name.
    /// </para>
    /// <para>
    /// A fourth conjunct: the element type must carry no eager-loaded navigation of its own — no nested owned
    /// collection and no nested owned single reference (every owned navigation is eager-loaded by EF Core
    /// convention, so this is exactly "no nested owned navigation"). This is not an optimization gap; admitting
    /// such an element would not avoid a crash, it would relocate one — an element type with an eager-loaded
    /// navigation makes EF's nav-expansion emit the auto-include as an inner <c>Queryable.Select</c>, which
    /// <c>MongoProjectionBindingExpressionVisitor</c> rebuilds as an enumerable and <c>Expression.New</c>'s
    /// member-type validation then rejects at shaper-build time, in every <see cref="Infrastructure.MongoQueryMode"/>,
    /// before the mode is even read (a separate, unfixed defect). Declining here keeps
    /// the shape unaffected, leaving the actual fix (widening <c>MatchTypes</c> or the shaper) to that ticket.
    /// </para>
    /// <para>
    /// The test is <see cref="IReadOnlyNavigationBase.IsEagerLoaded"/>, not mere presence of any navigation: a
    /// bare <c>!GetNavigations().Any()</c> would also decline an element carrying only a lazy inverse
    /// back-reference to its own owner (<c>OwnsMany(b =&gt; b.Posts, p =&gt; p.WithOwner(x =&gt; x.Owner))</c>, an
    /// entirely ordinary model), keeping it off the native path unnecessarily. A lazy inverse back-reference is
    /// never auto-included by EF Core, so none of the crash mechanism above applies to it; only a navigation EF
    /// would try to auto-include does. This mirrors the sibling reference-kind guard in
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable</c>. See
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
           // The invariant expressed against the full path instead of a single hop: for a root-declared
           // navigation the path IS the containing element name; for an OwnsOne hop the path is dotted
           // ("Home.Notes") and the alias is the emit side's chosen dotted alias, which the shaper walks
           // segment by segment (BsonBinding.TryGetValueAtPath). The walk requires every intermediate hop to be
           // a single embedded reference, so a collection nested inside a collection ("Posts.Comments" — not
           // addressable by a dotted read) still declines.
           && TryGetRootRelativeArrayPath(navigation, rootEntityType, out var arrayPath)
           && alias == arrayPath
           // Decline an element type carrying an eager-loaded navigation of its own (a nested owned collection or
           // single reference) — EF's auto-include for it crashes shaper build. Admits any
           // non-eager-loaded navigation on the element, not just the lazy inverse back-reference to the owner
           // (WithOwner), on the identical reasoning: never auto-included, so none of the crash mechanism
           // applies. Matches the precedent at
           // MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable's Reference arm.
           && !navigation.TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded);

    /// <summary>
    /// The root-relative document path of an owned collection navigation's stored array — the dotted join of
    /// every containing element name from the query root down to <paramref name="navigation"/>'s own.
    /// <see langword="false"/> when the chain does not reach <paramref name="rootEntityType"/> through single
    /// embedded references only.
    /// </summary>
    /// <remarks>
    /// The intermediate-hop constraint is what keeps the path readable as a dotted name: every hop above the
    /// array must be a single embedded reference, so each segment resolves to a sub-document. A collection
    /// anywhere above it (an owned collection inside an owned collection) has no dotted read at all — the
    /// intermediate is an array, not a document — and declines here.
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
    /// The <c>$project</c> output alias a wrapped body's leaf is admitted under: the member's own name, except
    /// for an owned-collection array leaf reached through one or more <c>OwnsOne</c> hops, where it is the
    /// leaf's dotted root-relative document path instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member-name conjunct is what keeps the renamed-alias decline intact. Deriving a path alias for any
    /// array leaf would also admit <c>new { P = b.Posts }</c> (aliasing it <c>Posts</c> and reading it back
    /// correctly) — a real widening, but a different one, with its own tripwire test. Requiring the member name
    /// to equal the navigation's own containing element name means this method only ever replaces a name that
    /// already agreed with the last path segment, never one the user chose differently.
    /// </para>
    /// <para>
    /// For a root-declared array leaf the derived path equals the member name, so the alias is unchanged and no
    /// override is registered.
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
    /// Null-coalesces the pushed-down bare collection-navigation <c>Count</c> body inside
    /// <c>MongoQueryExpression.CapturedExpression</c> — <c>b.Posts.Count</c> becomes
    /// <c>(b.Posts ?? new List&lt;Post&gt;()).Count</c> — for a bare projection committed under
    /// <see cref="ProjectionAliasTier.Synthetic"/>. Returns <paramref name="captured"/> unchanged for every
    /// other shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProjectionAliasTier.Synthetic"/> is the tier for a computed bare leaf with no document path,
    /// so the late-fallback strip deliberately does not fire for it (see
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>): the alias-addressed
    /// shaper reads correctly on a fallback only because the driver's own <c>_v</c>-keyed push-down is left in
    /// place. But the driver renders a bare <c>{"$size": "$Posts"}</c> where native renders
    /// <c>{"$size": {"$ifNull": ["$Posts", []]}}</c>, and <c>$size</c> against a missing or explicitly-null array
    /// is a hard server error that aborts the whole aggregate — both under the default <c>Native</c> mode's
    /// late-decline route and under explicit <c>DriverLinq</c>. This rewrite closes both legs.
    /// </para>
    /// <para>
    /// The <c>??</c> spelling (not <c>?:</c>) matters: the driver renders
    /// <c>(b.Posts ?? new List&lt;Post&gt;()).Count</c> as <c>{"$size": {"$ifNull": ["$Posts", []]}}</c> —
    /// identical to native — while <c>b.Posts == null ? 0 : b.Posts.Count</c> renders <c>$cond</c> and still
    /// aborts, because MongoDB evaluates the untaken branch.
    /// </para>
    /// <para>
    /// This can't be applied in the commit block above, beside the alias-override registration:
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall</c> assigns
    /// <c>CapturedExpression = _finalExpression</c> immediately after every translated <c>Queryable</c> call,
    /// including the <c>Select</c> whose translation runs this binder, so a write from inside
    /// <see cref="TryPopulateNativeProjection"/> would be overwritten before anything can read it. The rewrite is
    /// therefore applied at that assignment, one statement later — still at translation time, still
    /// unconditional, and not at the decline site, which is what makes it cover the explicit-<c>DriverLinq</c>
    /// leg as well as the late-decline one.
    /// </para>
    /// <para>
    /// The rewrite is unconditional but has no effect outside its narrow reach: it fires only for a
    /// <see cref="ProjectionAliasTier.Synthetic"/> bare projection, and even then only replaces the pushed-down
    /// <c>Select</c>'s own selector body. Other <c>CapturedExpression</c> readers (<c>ContainsVectorSearch</c>,
    /// <c>GetOnZeroResultsAction</c>, the EF9+ bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> path, exception-
    /// message sites) are unaffected because none of them observe inside a <c>Count(AsQueryable(nav))</c> body.
    /// </para>
    /// <para>
    /// Scope is deliberately narrow: only the unfiltered collection-navigation <c>Count</c> is rewritten, and
    /// only as the body of the pushed-down bare <c>Select</c> itself. A filtered count renders
    /// <c>{$sum: {$map: …}}</c>, arithmetic renders <c>$multiply</c>, and a cast renders <c>$toInt</c> — none of
    /// which touches an array, so none aborts; a reference-collection count reads a <c>$lookup</c> output, which
    /// always exists. A primitive-collection bare <c>Count</c> (<c>b.Tags.Count</c>) never reaches this path at
    /// all: it is not natively representable, so the whole projection declines and the driver's bare
    /// <c>{"$size": "$Tags"}</c> still aborts on a missing/null array under both modes — a pre-existing,
    /// deliberately unclosed gap (see
    /// <c>NativeComputedBareProjectionTests.Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array</c>).
    /// A wrapped count leaf (<c>new { N = b.Posts.Count }</c>) is deliberately not rewritten by this method
    /// either, though it is coalesced by a different mechanism — see
    /// <c>NativeOwnedCollectionCountTests.Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike</c>.
    /// </para>
    /// <para>
    /// <c>StripPushedDownSelect</c> (called on the mixed-projection path and the tier-1 late-decline path) and
    /// this rewrite are mutually exclusive on any one <c>CapturedExpression</c> — the mixed path fires only for
    /// an entity-carrying projection, the tier-1 strip only for a <c>DocumentPath</c> bare leaf, and neither
    /// fires for a <c>Synthetic</c> bare leaf. This rewrite is applied earliest, at translation time, before
    /// either strip can run.
    /// </para>
    /// </remarks>
    internal static Expression? NullCoalesceSyntheticBareCountBody(
        Expression? captured, MongoSelectDefinition select)
    {
        if (captured is null || select.BareProjectionTier != ProjectionAliasTier.Synthetic)
        {
            return captured;
        }

        // The same two chain shapes StripPushedDownSelect navigates — the pushed-down Select is either the
        // outermost node or sits under a single no-arg cardinality terminator. Deliberately the same navigation
        // rather than a free-form tree walk: a tree walk would also reach a wrapped count leaf and a count in a
        // subquery, neither of which may be rewritten (see the scope paragraph above).
        //
        // Matching `Select` by name rather than by QueryableMethods.Select deliberately mirrors
        // StripPushedDownSelect: these two methods must navigate to the same node for the same captured chain,
        // since they are alternative mutations of it, and an independently-spelled matcher risks drifting from
        // that. A name match here cannot admit a shape the canonical constant would exclude: the index-selector
        // Select overload is ruled out by the arity check, and every body shape other than the recognized count
        // is ruled out by TryRewriteSelect. If StripPushedDownSelect is ever moved onto QueryableMethods, move
        // this with it.
        if (captured is not MethodCallExpression {Method.DeclaringType: var declaring} call
            || declaring != typeof(Queryable))
        {
            return captured;
        }

        if (call.Method.Name == nameof(Queryable.Select) && call.Arguments.Count == 2)
        {
            return TryRewriteSelect(call, out var rewrittenSelect) ? rewrittenSelect : captured;
        }

        if (call.Method.IsGenericMethod
            && call.Method.GetParameters().Length == 1
            && call.Method.Name is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)
                or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)
                or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault)
            && call.Arguments is [MethodCallExpression {Method: {Name: nameof(Queryable.Select), DeclaringType: var st}} innerSelect]
            && st == typeof(Queryable)
            && innerSelect.Arguments.Count == 2
            && TryRewriteSelect(innerSelect, out var rewrittenInner))
        {
            // The terminator is rebuilt with its generic argument unchanged, unlike StripPushedDownSelect (which
            // removes the Select and must retarget the terminator to the source element type): this rewrite keeps
            // the Select and only rewrites its selector body, whose type is unchanged (still an int/long after
            // being null-coalesced), so the Select still returns IQueryable<TResult> and First<TResult> is still
            // correct.
            return call.Update(call.Object, [rewrittenInner]);
        }

        return captured;
    }

    /// <summary>
    /// Rewrites <paramref name="selectCall"/>'s selector body when it is an unfiltered collection-navigation
    /// <c>Count</c>/<c>LongCount</c> over a navigation rooted at the selector's own parameter.
    /// </summary>
    /// <remarks>
    /// The captured (post-nav-expansion) spelling: EF lowers <c>Select(b =&gt; b.Posts.Count)</c> to
    /// <c>Select(b =&gt; Queryable.Count(Queryable.AsQueryable(EF.Property&lt;List&lt;Post&gt;&gt;(b, "Posts"))))</c>.
    /// Requiring the navigation to be rooted at the selector's own parameter is what excludes a
    /// reference-collection count, whose captured body is an <c>EntityQueryRoot</c> subquery instead — and which
    /// needs no rewrite, its <c>$lookup</c> output always being an array.
    /// </remarks>
    private static bool TryRewriteSelect(MethodCallExpression selectCall, out Expression rewritten)
    {
        rewritten = selectCall;

        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !TryMatchRewritableBareCountBody(selector, out var navigation, out var empty))
        {
            return false;
        }

        // Safe to re-cast: the matcher above validated both shapes and neither the count call nor the
        // AsQueryable call is rebuilt anywhere between here and there.
        var countCall = (MethodCallExpression)selector.Body;
        var asQueryableCall = (MethodCallExpression)countCall.Arguments[0];

        var coalesced = Expression.Coalesce(navigation, empty);
        var newBody = countCall.Update(null, [asQueryableCall.Update(null, [coalesced])]);

        var newSelector = Expression.Lambda(selector.Type, newBody, selector.Parameters);
        rewritten = selectCall.Update(
            selectCall.Object,
            [
                selectCall.Arguments[0],
                // Preserve the original argument's QUOTING rather than relying on Expression.Call's implicit
                // auto-quote, so the rewritten node is structurally identical to the original but for the body.
                selectCall.Arguments[1] is UnaryExpression {NodeType: ExpressionType.Quote}
                    ? Expression.Quote(newSelector)
                    : newSelector
            ]);
        return true;
    }

    /// <summary>
    /// The rewrite's OWN reachability test, factored out of <see cref="TryRewriteSelect"/> so the GATE can ASK
    /// it instead of describing it: <see langword="true"/> when <paramref name="selector"/>'s body is an
    /// unfiltered collection-navigation <c>Count</c>/<c>LongCount</c> that
    /// <see cref="NullCoalesceSyntheticBareCountBody"/> can null-coalesce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One function, one definition, two callers: <see cref="TryRewriteSelect"/> calls it to decide whether to
    /// rewrite; <see cref="IsFallbackSafeBareSizeLeaf"/> calls it to decide whether the tier-2 gate may admit the
    /// leaf at all. Those two decisions must agree — a paraphrase in either caller can drift from the other; a
    /// shared call cannot. Do not re-introduce a second spelling of this predicate anywhere.
    /// </para>
    /// <para>
    /// It answers only about the selector. The third reachability dimension —
    /// <see cref="NullCoalesceSyntheticBareCountBody"/>'s captured-chain shape — is deliberately not here,
    /// because the chain is not available where the gate runs; see <see cref="IsFallbackSafeBareSizeLeaf"/>'s
    /// remarks, which enumerate all three dimensions and say which two this covers.
    /// </para>
    /// </remarks>
    private static bool TryMatchRewritableBareCountBody(
        LambdaExpression selector,
        [NotNullWhen(true)] out Expression? navigation,
        [NotNullWhen(true)] out Expression? empty)
    {
        navigation = null;
        empty = null;

        if (selector.Parameters is not [var parameter]
            || selector.Body is not MethodCallExpression
            {
                Method: {DeclaringType: var countDeclaring} countMethod, Arguments: [var countArg]
            }
            || countDeclaring != typeof(Queryable)
            || countMethod.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount))
            || countArg is not MethodCallExpression
            {
                Method: {DeclaringType: var asQueryableDeclaring, Name: nameof(Queryable.AsQueryable)},
                Arguments: [var navigationArg]
            }
            || asQueryableDeclaring != typeof(Queryable)
            || !IsNavigationOnParameter(navigationArg, parameter))
        {
            return false;
        }

        if (!TryCreateEmptyCollection(navigationArg.Type, out var emptyCollection))
        {
            return false;
        }

        navigation = navigationArg;
        empty = emptyCollection;
        return true;
    }

    /// <summary>
    /// Builds the empty-collection expression the <c>??</c> substitutes for a null navigation, typed so it is
    /// assignable to <paramref name="navigationType"/>. Returns <see langword="false"/> when no such expression
    /// can be built, in which case the body is left un-rewritten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A constructible collection type (<c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>, a custom collection with a
    /// public parameterless constructor) is constructed as itself, rendering as
    /// <c>{"$ifNull": ["$Posts", []]}</c>.
    /// </para>
    /// <para>
    /// An interface-typed navigation — <c>ICollection&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
    /// <c>IEnumerable&lt;T&gt;</c> and the read-only pair — is not a hypothetical: EF's nav-expansion spells the
    /// navigation <c>EF.Property&lt;TNavClrType&gt;(b, "…")</c> with the declared property type, and this
    /// provider's own test suite models one (<c>OwnedEntityTests.PersonWithIEnumerableLocations</c>). Such a
    /// navigation is coalesced against <c>new List&lt;TElement&gt;()</c>, which
    /// <see cref="Expression.Coalesce(Expression, Expression)"/> accepts because <c>List&lt;TElement&gt;</c> is
    /// reference-assignable to the interface — the resulting node keeps the navigation's own declared type and
    /// renders identically to the <c>List&lt;T&gt;</c> case.
    /// </para>
    /// <para>
    /// Still declined: an abstract or interface type that <c>List&lt;TElement&gt;</c> is not assignable to —
    /// <c>ISet&lt;T&gt;</c>, <c>IReadOnlySet&lt;T&gt;</c>, a <c>Collection&lt;T&gt;</c>-derived abstract base —
    /// and any type with no element type at all.
    /// </para>
    /// <para>
    /// A decline here is not merely inert: <see cref="IsFallbackSafeBareSizeLeaf"/> consults this method
    /// (through <see cref="TryMatchRewritableBareCountBody"/>), so a decline here also declines the tier-2
    /// admission, keeping an un-rewritten bare <c>$size</c> (which aborts the aggregate on a missing or
    /// explicitly-null array) from ever being committed. Anything that widens the tier-2 gate must keep
    /// consulting this method, or that guarantee lapses silently.
    /// </para>
    /// </remarks>
    private static bool TryCreateEmptyCollection(Type navigationType, [NotNullWhen(true)] out Expression? empty)
    {
        if (!navigationType.IsInterface
            && !navigationType.IsAbstract
            && navigationType.GetConstructor(Type.EmptyTypes) is not null)
        {
            empty = Expression.New(navigationType);
            return true;
        }

        var elementType = TryGetEnumerableElementType(navigationType);
        if (elementType is not null)
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            if (navigationType.IsAssignableFrom(listType))
            {
                empty = Expression.New(listType);
                return true;
            }
        }

        empty = null;
        return false;
    }

    /// <summary>
    /// The <c>T</c> of the <c>IEnumerable&lt;T&gt;</c> <paramref name="type"/> is or implements, or
    /// <see langword="null"/>.
    /// </summary>
    private static Type? TryGetEnumerableElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> is a collection-navigation access on <paramref name="parameter"/>
    /// itself — either the shadow-safe <c>EF.Property&lt;T&gt;(b, "Posts")</c> spelling EF's nav-expansion
    /// produces or a plain <c>b.Posts</c> member access.
    /// </summary>
    private static bool IsNavigationOnParameter(Expression expression, ParameterExpression parameter)
        => expression switch
        {
            MethodCallExpression call when call.Method.IsEFPropertyMethod()
                => call.Arguments.Count == 2 && call.Arguments[0] == parameter,
            MemberExpression member => member.Expression == parameter,
            _ => false
        };

    /// <summary>
    /// True when <paramref name="leaf"/> would read back the same, correct value if looked up by
    /// <paramref name="alias"/> against a whole, un-projected document — the shape a late
    /// <see cref="Infrastructure.MongoQueryMode.DriverLinq"/> fallback (or EF's own client-side "mixed" shaper,
    /// forced by any entity/collection leaf in the same projection — see the call site's remarks) hands the
    /// shaper. Only a plain top-level (non-dotted) field whose alias equals its own stored document element
    /// name qualifies — a renamed alias, an owned sub-property's dotted path, or any computed leaf (a
    /// <see cref="MongoSizeExpression"/> count or an arithmetic <see cref="MongoBinaryExpression"/>, neither of
    /// which is backed by any single document element) all read back wrong or not at all and must decline.
    /// </summary>
    private static bool IsWholeDocumentReadableLeaf(string alias, MongoExpression leaf)
        => leaf is MongoFieldExpression field
           && !field.ElementName.Contains('.')
           && alias == field.ElementName;

    /// <summary>
    /// The placeholder alias handed to <see cref="TryTranslateLeaf"/> for a bare selector body whose leaf is
    /// not an owned-collection array. Only the array branch reads the alias argument, and for that branch the
    /// caller supplies the navigation's own containing element name instead — so this value is never observable
    /// in a pipeline or a shaper. The leading space makes it unrepresentable as a stored element name, so it
    /// cannot accidentally satisfy that branch's alias-agreement conjunct either.
    /// </summary>
    private const string BareLeafProvisionalAlias = " bare";

    /// <summary>
    /// Derives a bare selector body's projection alias from its translated leaf, admitting only a leaf that has
    /// a root-relative document path and taking that path as the alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alias == document path is the whole point: the DOM shaper is built alias-addressed at translation time,
    /// while whether a <c>$project</c> is really emitted is decided later (an explicit
    /// <see cref="Infrastructure.MongoQueryMode.DriverLinq"/>, or a late native-factory decline whose fallback
    /// is stripped back to whole documents — see
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>). When the alias
    /// is the leaf's path, "read top-level element &lt;alias&gt;" and "read the leaf at its document path" are
    /// literally the same read, so one shaper is correct against a projected document and an un-projected one
    /// alike. This is the same invariant <see cref="IsNativeArrayProjectionLeaf"/> enforces for a wrapped array
    /// leaf, reached from the other direction: there the alias is given and checked, here it is chosen.
    /// </para>
    /// <para>
    /// Hence the two admitted node kinds and the dotted exclusion. A <see cref="MongoFieldExpression"/> covers
    /// a plain top-level scalar and a primitive-collection property; a <see cref="MongoElementRefExpression"/>
    /// covers the owned-collection array leaf (whose path is the containing element name) and the synthetic
    /// vector-search <c>__score</c> element (which the <c>$addFields</c> companion really writes). A dotted
    /// path is declined because the alias would have to be dotted too, and a dotted alias is looked up by the
    /// shaper as a literal key while MongoDB's <c>$project</c> renders it as a nested document.
    /// </para>
    /// <para>
    /// Everything else — a count, a filtered count, an arithmetic leaf, a cast — is backed by no document
    /// element at all, so it has no path to use as an alias and is declined here. The caller then tries
    /// <see cref="TryDeriveSyntheticAlias"/>, a separate derivation with its own alias and its own
    /// (non-stripping) fallback disposition rather than a widening of this method — the two tiers cannot share
    /// one rule, because their correctness arguments are opposites.
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
    /// The reserved <c>$project</c> output element name for a computed bare selector body. Chosen to be exactly
    /// what the driver's LINQ provider names a bare projection, so the emitted alias and the driver's own are
    /// the same string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That coincidence is the tier's whole safety story, and it is the mirror image of tier 1's. A
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias survives a late fallback because the fallback is
    /// stripped back to whole documents and the alias is a real document path. A
    /// <see cref="ProjectionAliasTier.Synthetic"/> alias names no document element at all, so stripping it
    /// would be fatal (<c>Document element '_v' is missing but required</c>); it survives instead by not being
    /// stripped — the driver's own push-down stays in place and writes <c>_v</c>, which is what the
    /// alias-addressed shaper is already reading by.
    /// </para>
    /// <para>
    /// A collision with a real stored element named <c>_v</c> is unreachable: the emitted <c>$project</c>
    /// always replaces the document with a single computed <c>_v</c>, so nothing downstream can still be
    /// looking for the stored one.
    /// </para>
    /// <para>
    /// The tier's safety rests on the driver's own un-stripped push-down writing <c>_v</c> on a late fallback.
    /// If the driver-LINQ fallback route ever goes away, this arm needs a different answer, not a rename.
    /// </para>
    /// </remarks>
    private const string SyntheticBareProjectionAlias = "_v";

    /// <summary>
    /// Derives a computed bare selector body's projection alias, admitting exactly the leaf kinds that render
    /// as an aggregation-operator document, and taking <see cref="SyntheticBareProjectionAlias"/> as the alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a node-kind gate, not a "the leaf translated" gate: a <c>$project</c> reads a bare value as an
    /// inclusion/exclusion flag rather than a literal, so a constant or captured-parameter leaf is not a value
    /// the projection can carry at all. An arithmetic <see cref="MongoBinaryExpression"/> renders
    /// <c>{$multiply: […]}</c> and a <see cref="MongoConvertExpression"/> renders <c>{$toInt: …}</c>; both are
    /// documents, so both are safe exactly where a bare value is not. This mirrors the decision
    /// <see cref="TryTranslateLeaf"/>'s own count/cast gate makes for a wrapped leaf.
    /// </para>
    /// <para>
    /// The consequence of relaxing this gate differs from the wrapped spelling: a wrapped falsy leaf
    /// (<c>new { b.Title, X = 0 }</c>) hard-fails under the default <c>Native</c> mode (<c>$project</c> rejects
    /// mixing a falsy flag with an inclusion), but a bare falsy leaf has no sibling to mix with, so MongoDB
    /// accepts the pipeline — just not as a value projection any more (a pure exclusion returning whole
    /// documents minus fields). Pinned by
    /// <c>NativeComputedBareProjectionTests.Bare_constant_leaf_is_not_admitted_by_the_tier_2_node_kind_gate</c>,
    /// which asserts the emitted MQL since a values-only assertion would be vacuous for this shape.
    /// </para>
    /// <para>
    /// The operator test is on <see cref="MongoBinaryExpression.Operator"/>, deliberately — matching on
    /// <c>NodeType</c> instead would silently admit nothing, since every <see cref="MongoExpression"/> reports
    /// <see cref="ExpressionType.Extension"/> as its node type.
    /// </para>
    /// <para>
    /// An array-touching node is excluded from the whole subtree of the arithmetic/cast arm, not just its top:
    /// a bare collection <c>.Count</c> as the TOP node is admitted by arm 1a because
    /// <see cref="NullCoalesceSyntheticBareCountBody"/> rewrites the pushed-down body into its <c>$ifNull</c>
    /// form, and <see cref="IsFallbackSafeBareSizeLeaf"/> keeps arm 1a inside that rewrite's reach by asking
    /// <see cref="TryMatchRewritableBareCountBody"/> rather than restating it. The arithmetic/cast arm gets no
    /// such protection — the rewrite matches a body that IS the count, never one that merely contains one — so
    /// <c>$multiply</c> over <c>$size</c> must still decline, which <see cref="IsArrayFreeComputedSubtree"/>
    /// enforces over the whole subtree.
    /// </para>
    /// </remarks>
    private static bool TryDeriveSyntheticAlias(
        MongoExpression leaf,
        LambdaExpression selector,
        List<LookupExpression> pendingLookups,
        out string alias)
    {
        alias = null!;

        switch (leaf)
        {
            // Gate 1a — a size kind as the top node, i.e. the bare body IS the count, AND a leaf whose
            // un-stripped driver fallback cannot abort. Both halves are load-bearing; see
            // IsFallbackSafeBareSizeLeaf, which reconciles the gate's admitted set with the rewrite's reach by
            // calling the rewrite's own matcher rather than restating it.
            case MongoSizeExpression or MongoFilteredSizeExpression
                when IsFallbackSafeBareSizeLeaf(leaf, selector, pendingLookups):
                break;

            // Gate 1b — an arithmetic/cast top node. Gate 2 then re-checks the whole subtree, because gate 1b
            // alone is not the boundary: it excludes the size kinds only as the top node, so
            // `Select(b => b.Posts.Count * 2)` walks straight through it, and the null-coalesce rewrite does
            // not reach a nested count.
            case MongoConvertExpression
                or MongoBinaryExpression
                {
                    Operator: MongoBinaryOperator.Add or MongoBinaryOperator.Subtract
                    or MongoBinaryOperator.Multiply or MongoBinaryOperator.Divide or MongoBinaryOperator.Modulo
                } when IsArrayFreeComputedSubtree(leaf):
                break;

            default:
                return false;
        }

        alias = SyntheticBareProjectionAlias;
        return true;
    }

    /// <summary>
    /// Whether a bare size leaf is one whose un-stripped driver fallback cannot abort on a missing or
    /// explicitly-null array — the precondition gate 1a exists to enforce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place the gate's admitted set and the rewrite's reach are reconciled, and the
    /// reconciliation is a call, not a restatement. The unfiltered arm below does not describe what
    /// <see cref="NullCoalesceSyntheticBareCountBody"/> reaches; it asks it, through
    /// <see cref="TryMatchRewritableBareCountBody"/> — the same matcher <see cref="TryRewriteSelect"/> uses. One
    /// function, one definition, two callers, deliberately: a restatement of the rewrite's condition here has
    /// repeatedly drifted from what the rewrite actually covers. A call cannot drift.
    /// </para>
    /// <para>
    /// <see cref="TryRewriteSelect"/> requires all of:
    /// </para>
    /// <list type="number">
    /// <item><description>the array path / navigation rooting — <see cref="IsNavigationOnParameter"/> accepts
    /// only a navigation whose receiver IS the selector parameter, which is exactly the captured spelling that
    /// produces a non-dotted path (so a <c>b.Home.Notes.Count</c> hop is rejected);</description></item>
    /// <item><description>the empty-collection constructibility — <see cref="TryCreateEmptyCollection"/> must
    /// be able to build a substitute assignable to the navigation's declared CLR type. An <c>ISet&lt;Post&gt;</c>
    /// or <c>IReadOnlySet&lt;Post&gt;</c> navigation, though ordinary and EF-supported and carrying a perfectly
    /// non-dotted path, is one <c>List&lt;T&gt;</c> is NOT assignable to, so the rewrite declines
    /// it;</description></item>
    /// <item><description>the captured-chain shape — the pushed-down <c>Select</c> must be the outermost
    /// captured node or sit under a single no-arg cardinality terminator.</description></item>
    /// </list>
    /// <para>
    /// This method gates (1) and (2), because both are decidable from the selector, which is what it is handed.
    /// It cannot gate (3): the captured chain is not available at bind time. Read the result as "this leaf's
    /// fallback is safe as far as the selector can tell", never as "this shape is fully covered".
    /// </para>
    /// <para>
    /// Dimension (2) matters concretely: for an <c>ISet&lt;Post&gt;</c>-typed navigation, gating only the array
    /// path would admit <c>Select(b =&gt; b.Posts.Count)</c> into arm 1a even though the rewrite declines to
    /// coalesce it — leaving a bare <c>$size</c> that aborts on a missing/null array under the default
    /// <c>Native</c> mode's late-decline route and under explicit <c>DriverLinq</c>. Pinned by
    /// <c>NativeComputedBareProjectionTests.Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly</c>.
    /// </para>
    /// <para>
    /// The two arms that do not consult the rewrite are protected structurally instead:
    /// </para>
    /// <list type="table">
    /// <item>
    /// <term>reference collection via <c>$lookup</c> (alias <c>_lookup_Orders</c>)</term>
    /// <description>a <c>$lookup</c> always writes an array — never absent, never explicit null — so the
    /// driver's bare <c>$size</c> cannot abort (the rewrite could not reach it anyway: a reference-collection
    /// count is captured as an <c>EntityQueryRoot</c> subquery, not a navigation on the selector parameter). It
    /// is recognized by matching the leaf against the lookup this leaf's own translation registered, rather than
    /// by sniffing the <c>_lookup_</c> name.</description>
    /// </item>
    /// <item>
    /// <term>filtered count (<c>b.Posts.Count(pred)</c>)</term>
    /// <description>the driver renders it <c>{$sum: {$map: …}}</c>, and <c>$map</c> over a missing or
    /// explicitly-null array yields missing instead of aborting, so it needs no rewrite of its own (and
    /// <see cref="TryRewriteSelect"/> matches only the one-argument <c>Count</c>, so it could not have one). It
    /// is still held to a non-dotted rule for simplicity, pinned by
    /// <c>NativeComputedBareProjectionTests.Bare_FILTERED_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly</c>.</description>
    /// </item>
    /// </list>
    /// <para>
    /// Not gated here: a root-declared, rewrite-reachable count under a reducing operator —
    /// <c>Select(b =&gt; b.Posts.Count).Distinct()</c> / <c>.Sum()</c> / <c>.OrderBy(c =&gt; c)</c> — is admitted
    /// by the unfiltered arm and protected by nothing at THIS layer, because the selector cannot be lifted past
    /// an operator that consumes the projected value, so dimension (3) fails and the driver's un-stripped
    /// push-down renders a bare <c>$size</c> here. That no longer aborts end-to-end, though: EF-359's
    /// <c>MongoEFToLinqTranslatingExpressionVisitor.TryRewriteEmbeddedCollectionNavigationCount</c> coalesces the
    /// same shape independently of chain position, further down the residual-tree bridge — see
    /// <c>NativeComputedBareProjectionTests.Bare_count_leaf_under_a_REDUCING_operator_no_longer_aborts_on_a_ragged_array</c>.
    /// A composed slot operator (<c>.Skip(1)</c>) is not in that hole, since EF's nav-expansion applies the
    /// projection as a pending selector last, so the <c>Select</c> really is outermost and the rewrite fires.
    /// Closing the reducing-operator gap means widening the rewrite's chain navigation, at which point this
    /// method needs no edit at all.
    /// </para>
    /// <para>
    /// Every decline here is fail-closed, the same call <see cref="IsArrayFreeComputedSubtree"/>'s allow-list
    /// makes: the shape falls back gracefully with correct values in every mode, which is its base behaviour.
    /// </para>
    /// </remarks>
    private static bool IsFallbackSafeBareSizeLeaf(
        MongoExpression leaf, LambdaExpression selector, List<LookupExpression> pendingLookups)
        => leaf switch
        {
            // Structural: reads a $lookup output, which is always an array. Keyed on the lookup this leaf's own
            // translation just registered, so the read and the write cannot drift apart.
            MongoSizeExpression lookupSize
                when pendingLookups.Exists(l => l.As == lookupSize.FieldName) => true,

            // Structural: $sum/$map tolerates a missing array. Held to the same non-dotted rule anyway.
            MongoFilteredSizeExpression filtered => !filtered.ArrayPath.Contains('.'),

            // Everything else must be inside the rewrite's reach — and THE REWRITE'S OWN MATCHER is what says so.
            MongoSizeExpression => TryMatchRewritableBareCountBody(selector, out _, out _),

            _ => false
        };

    /// <summary>
    /// Whether every node in <paramref name="expression"/>'s subtree is one that renders without touching an
    /// array. An allow-list, so an unrecognised node kind is treated as unsafe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the top-node gate alone is not the boundary it looks like:
    /// <c>Select(b =&gt; b.Posts.Count * 2)</c> translates to an arithmetic <see cref="MongoBinaryExpression"/>
    /// over a <see cref="MongoSizeExpression"/>, so it satisfies a top-node-only gate while, on a late decline,
    /// the un-stripped driver fallback renders a bare <c>{"$size": "$Posts"}</c> that aborts on a
    /// missing/explicitly-null array — where native renders <c>$size</c> over <c>$ifNull</c>.
    /// <see cref="NullCoalesceSyntheticBareCountBody"/> cannot cover it: that rewrite matches only a
    /// pushed-down bare <c>Select</c> whose body IS the count, not one that merely contains one.
    /// </para>
    /// <para>
    /// This is an allow-list rather than a "contains a size node" deny-list so an unknown future node kind fails
    /// closed by default, the same call <see cref="MongoFilteredSizeExpression"/>'s "sibling, not a flag"
    /// argument makes elsewhere. The size kinds are not named here at all — they fall into the catch-all.
    /// </para>
    /// <para>
    /// The walk mirrors <c>MongoExpressionTranslator.AllFieldsDefaultSerialized</c>'s recursion (binary ⇒ both
    /// operands, convert ⇒ operand) but inverts its default: that one answers "nothing here objects" and ends in
    /// <c>_ =&gt; true</c>, while this one answers "everything here is known safe" and ends in <c>_ =&gt; false</c>.
    /// </para>
    /// </remarks>
    private static bool IsArrayFreeComputedSubtree(MongoExpression expression)
        => expression switch
        {
            MongoBinaryExpression binary
                => IsArrayFreeComputedSubtree(binary.Left) && IsArrayFreeComputedSubtree(binary.Right),
            MongoConvertExpression convert => IsArrayFreeComputedSubtree(convert.Operand),
            MongoFieldExpression or MongoConstantExpression or MongoParameterExpression => true,
            // Everything else, including MongoSizeExpression and MongoFilteredSizeExpression — see the remarks:
            // the size kinds are excluded by this catch-all rather than by an arm of their own, deliberately.
            _ => false
        };

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
    /// structurally via <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> (shared with the
    /// reference-<c>SelectMany</c> binder) plus an exactly-two-conjunct guard: any additional predicate conjunct
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
