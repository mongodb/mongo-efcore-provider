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
using MongoDB.Bson;
using MongoDB.Driver;                                // Mql.Field
using MongoDB.EntityFrameworkCore.Extensions;        // IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native <c>$project</c> slot (<see cref="MongoSelectDefinition"/> Projection) from a selector whose
/// every leaf is natively translatable; otherwise leaves the slot empty and returns <see langword="false"/>.
/// </summary>
internal static class NativeProjectionBinder
{
    internal static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        // A reference upcast around a constructed DTO (`x => (BaseDto)new DerivedDto { ... }`, produced when a
        // covariant IQueryable<Derived> is consumed as IQueryable<Base>) changes nothing server-side: bind the
        // construction itself; the Convert stays on the shaper, which TranslateSelect builds from the original
        // selector. Method == null excludes a user-defined operator (C# forbids one to a base type anyway).
        if (selector.Body is UnaryExpression { NodeType: ExpressionType.Convert, Method: null, Operand: NewExpression or MemberInitExpression } upcast
            && !upcast.Type.IsValueType
            && !upcast.Operand.Type.IsValueType
            && upcast.Type.IsAssignableFrom(upcast.Operand.Type))
        {
            selector = Expression.Lambda(upcast.Operand, selector.Parameters);
        }

        // A wrapped body re-entered with Projection already populated (EF nav-expansion re-visiting a wrapped
        // nav-entity-leaf projection under Distinct/Union/Concat). Re-running would duplicate Projection entries and
        // throw from AddProjectionAliasOverride's write-once Dictionary.Add. Declines rather than claiming success,
        // since the re-entrant selector isn't verified to be equivalent. Currently defense-in-depth only: those shapes
        // already throw InvalidCastException in MongoProjectionBindingExpressionVisitor (see
        // NativeOwnedReferenceWholeEntityTests).
        if (selector.Body is NewExpression or MemberInitExpression && mongoQ.Select.Projection.Count > 0)
        {
            return false;
        }

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, selector.Parameters[0]);
        var projections = new List<MongoProjection>();
        // Parallel to projections: true where the leaf is an owned array leaf (skipped by the sibling-readability
        // check).
        var leafIsArray = new List<bool>();
        // Parallel to projections: true where the leaf is an owned single-reference navigation entity. Also skipped by
        // the sibling-readability check: TryTranslateLeaf already proved its alias equals its document path.
        var leafIsOwnedNavEntity = new List<bool>();
        // Staged rather than applied to mongoQ, so a later declining leaf leaves no half-registered lookup behind.
        var pendingLookups = new List<LookupExpression>();
        // Correlated-reducer leaves, staged for the same reason.
        var pendingReducerLeaves = new List<MongoCorrelatedReducerLeaf>();
        // Lookups a projected reference-collection Count leaf needs stamped IsBareCountSizeSource; staged for the same
        // reason (see NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup).
        var pendingBareCountStamps = new List<LookupExpression>();
        // AddToProjection disambiguates aliases case-insensitively, so two members differing only by case would make
        // the DOM shaper read a disambiguated alias the native $project never emits (a silently dropped value). Decline
        // instead.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Any accepted leaf was an owned array leaf; drives _id owner-key retention below.
        var hasArrayLeaf = false;
        // Any accepted leaf was an owned single-reference navigation entity (`new { b.Address, b.Title }`). It shares
        // the array leaf's hazard (drags the owner's shadow key into the projected document), so it is OR'd with
        // hasArrayLeaf below.
        var hasOwnedNavEntityLeaf = false;
        // Any accepted leaf was a string-to-char-sequence call (`e.City.AsEnumerable()`/ToList/ToArray). Committed to
        // HasStringSequenceProjectionLeaf, which keeps the projection away from the driver's LINQ v3 push-down (see
        // MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery). Derived syntactically from the leaf: only
        // TryTranslateLeaf's string-sequence arm can admit that shape.
        var hasStringSequenceLeaf = false;
        // Any leaf had a client-reapplied case mapping peeled off; committed to HasClientCaseMappingProjectionLeaf.
        var hasCaseMappingLeaf = false;
        // Alias a bare selector body was admitted under (null otherwise); registered in the commit block with
        // AddProjection.
        string? bareProjectionAlias = null;
        // Alias family for bareProjectionAlias, carried as data (see AddProjectionAliasOverride).
        var bareProjectionTier = ProjectionAliasTier.DocumentPath;
        // (memberName, alias) overrides for wrapped-body leaves whose alias differs from the member name, plus
        // owned-nav-entity leaves; registered in the commit block.
        var namedAliasOverrides = new List<(string MemberName, string Alias)>();
        // Set once the multi-argument ctor-only-DTO arm has translated every argument; committed to
        // HasPositionalCtorProjectionShaper in the commit block.
        var hasPositionalCtorProjection = false;

        switch (selector.Body)
        {
            // Wrapped body: anonymous type/DTO via NewExpression-with-Members or MemberInit. A construction that fails
            // TryGetProjectionMembers falls through to the bare-body case.
            case NewExpression or MemberInitExpression
                when selector.Body.TryGetProjectionMembers(out var wrappedMembers):
                foreach (var (memberName, member) in wrappedMembers)
                {
                    var memberValue = PeelCaseMapping(member);
                    hasCaseMappingLeaf |= !ReferenceEquals(memberValue, member);
                    var alias = DeriveWrappedLeafAlias(mongoQ, selector.Parameters[0], memberValue, memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], memberValue, alias, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var leaf, out var isArrayLeaf, out var isOwnedNavEntityLeaf, allowWholeRootEntityLeaf: true))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf, memberValue));
                    // The nav-entity leaf always registers a DocumentPath override, even though its alias equals the
                    // member name: the late-fallback strip it triggers is what supplies the retained _id.
                    if (alias != memberName || isOwnedNavEntityLeaf)
                        namedAliasOverrides.Add((memberName, alias));
                    leafIsArray.Add(isArrayLeaf);
                    hasArrayLeaf |= isArrayLeaf;
                    leafIsOwnedNavEntity.Add(isOwnedNavEntityLeaf);
                    hasOwnedNavEntityLeaf |= isOwnedNavEntityLeaf;
                    hasStringSequenceLeaf |= memberValue is MethodCallExpression wrappedStringSequenceCall
                                             && IsStringSequenceMaterializationCall(wrappedStringSequenceCall);
                }

                break;

            // Single-argument ctor-only DTO — `x => new CustomerDtoWithEntityInCtor(x)`; Members is null for a
            // named-type construction. The read side (MongoProjectionBindingExpressionVisitor.VisitNew) resolves every
            // argument of a Members-null body under the same ambient ProjectionMember, which is safe only with one
            // argument; the positional arm below handles more.
            case NewExpression { Members: null, Arguments: { Count: 1 } ctorArguments }:
            {
                var ctorArgument = ctorArguments[0];

                // Sub-case 1: the sole argument is the whole root entity. Leave Projection empty so Route resolves to
                // NativeRoute.WholeEntity and the DTO is constructed client-side.
                //
                // Must precede sub-case 2: TryTranslateLeaf's whole-root-entity arm yields a "$ROOT" element ref, which
                // TryDeriveDocumentPathAlias would accept as the $project alias — not a valid field name, and with no
                // bare-projection read-side wiring.
                if (IsWholeRootEntityLeaf(mongoQ, ctorArgument, selector.Parameters[0]))
                {
                    // To TranslateUnion/Concat's IsPlainWholeEntitySelect this looks like a plain entity fetch, but the
                    // per-row result is the DTO, so a native $unionWith would dedupe the wrong thing (see
                    // HasClientWrappedWholeEntityShaper).
                    mongoQ.Select.HasClientWrappedWholeEntityShaper = true;
                    break;
                }

                // Sub-case 2: any other bare-admissible leaf (scalar/computed field or owned-reference nav entity).
                // Reuses the bare-body alias derivation and registration, so VisitNew's ambient-member lookup reads it
                // exactly as for a bare body.
                if (!TryBindAsBareProjection(ctorArgument, BareLeafProvisionalAlias, allowWholeRootEntityLeafForThis: true))
                {
                    return false;
                }

                break;
            }

            // Multi-argument ctor-only DTO — `x => new CustomerListItem(x.CustomerID, x.City)`. Handled like the
            // wrapped arm, with member names taken positionally (TryGetProjectionMembers'
            // allowPositionalConstructorArguments). Committed via HasPositionalCtorProjectionShaper so TranslateSelect
            // avoids VisitNew's shared-ambient-member collision (see that flag's remarks).
            case NewExpression { Members: null, Arguments: { Count: > 1 } } when
                selector.Body.TryGetProjectionMembers(out var positionalMembers, allowPositionalConstructorArguments: true):
                foreach (var (memberName, member) in positionalMembers)
                {
                    var memberValue = PeelCaseMapping(member);
                    hasCaseMappingLeaf |= !ReferenceEquals(memberValue, member);
                    var alias = DeriveWrappedLeafAlias(mongoQ, selector.Parameters[0], memberValue, memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], memberValue, alias, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var leaf, out var isArrayLeaf, out var isOwnedNavEntityLeaf, allowWholeRootEntityLeaf: true))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf, memberValue));
                    if (alias != memberName || isOwnedNavEntityLeaf)
                        namedAliasOverrides.Add((memberName, alias));
                    leafIsArray.Add(isArrayLeaf);
                    hasArrayLeaf |= isArrayLeaf;
                    leafIsOwnedNavEntity.Add(isOwnedNavEntityLeaf);
                    hasOwnedNavEntityLeaf |= isOwnedNavEntityLeaf;
                    hasStringSequenceLeaf |= memberValue is MethodCallExpression positionalStringSequenceCall
                                             && IsStringSequenceMaterializationCall(positionalStringSequenceCall);
                }

                hasPositionalCtorProjection = true;
                break;

            // Client method wrapping the whole entity — `x => context.ClientMethod(x)`. Like the single-argument
            // ctor-only arm: fetch whole documents (Route stays WholeEntity) and let EF invoke the method client-side.
            // Requires exactly one entity-referencing operand; anything else falls through to the bare-body arm, which
            // declines.
            case MethodCallExpression methodCall
                // Excludes EF.Property(x, "Name"): same shape, but it's a field accessor the bare-body arm translates.
                // Admitting it here would leave Projection empty while the shaper reads a projected field — silently
                // mis-shaped results.
                when !methodCall.Method.IsEFPropertyMethod()
                     && TryGetSoleWholeRootEntityOperand(mongoQ, methodCall, selector.Parameters[0]):
                // Flag it so set ops decline a native $unionWith (see HasClientWrappedWholeEntityShaper).
                mongoQ.Select.HasClientWrappedWholeEntityShaper = true;
                break;

            // Bare body — `b => b.Title`, `b => b.Posts.Count`. With no member name, the alias is derived from the
            // translated leaf and registered as an override (MongoSelectDefinition.AddProjectionAliasOverride). Two
            // tiers, tried in order:
            //
            // Tier 1 (TryDeriveDocumentPathAlias): the leaf has a root-relative document path and the alias is that
            // path, so alias-addressed and document-path reads coincide and a late fallback can strip the projection
            // (ShouldStripBareProjectionOnFallback).
            //
            // Tier 2 (TryDeriveSyntheticAlias): a computed leaf under the driver's own bare alias `_v`, so an
            // un-stripped fallback push-down writes the element the shaper reads. Admits a size/filtered-size top node
            // accepted by IsFallbackSafeBareSizeLeaf, or an arithmetic/numeric-cast top node over an array-free subtree
            // (IsArrayFreeComputedSubtree). `b.Posts.Count * 2` declines: its fallback renders a bare $size that errors
            // on a missing array.
            default:
            {
                // Declined when a prior Select already populated Projection: the single bare ProjectionMember can name
                // only one alias, and the bare alias override is write-once (Dictionary.Add).
                if (mongoQ.Select.Projection.Count > 0)
                {
                    return false;
                }

                // Provisional alias; only IsNativeArrayProjectionLeaf's alias-agreement conjunct reads it (vacuous for
                // a bare array body). Other leaf kinds ignore it.
                var provisionalAlias = selector.Body is MaterializeCollectionNavigationExpression materializeBare
                    ? (materializeBare.Navigation as INavigation)?.TargetEntityType.GetContainingElementName()
                      ?? BareLeafProvisionalAlias
                    : BareLeafProvisionalAlias;

                // false keeps a true bare `b => b.Address` declining; the ctor-wrap arm passes true.
                if (!TryBindAsBareProjection(selector.Body, provisionalAlias, allowWholeRootEntityLeafForThis: false))
                {
                    // Untranslatable (e.g. embeds a client method). If every entity reference is the whole entity,
                    // fetch whole documents and evaluate the body client-side, as in the client-method arm above.
                    if (!IsClientOnlyWholeEntityExpression(mongoQ, selector.Body, selector.Parameters[0]))
                    {
                        return false;
                    }

                    mongoQ.Select.HasClientWrappedWholeEntityShaper = true;
                }

                break;
            }
        }

        // Shared by the bare-body and single-argument ctor-only arms; mutates this method's locals. Tier 1 (document
        // path) is tried before tier 2 (synthetic `_v`): a leaf with a document path must take it so the late-fallback
        // strip works.
        bool TryBindAsBareProjection(Expression bareLikeExpr, string provisionalAlias, bool allowWholeRootEntityLeafForThis)
        {
            var peeled = PeelCaseMapping(bareLikeExpr);
            hasCaseMappingLeaf |= !ReferenceEquals(peeled, bareLikeExpr);
            bareLikeExpr = peeled;
            if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], bareLikeExpr, provisionalAlias,
                    pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var bareLeaf, out var bareIsArrayLeaf, out _,
                    allowWholeRootEntityLeafForThis))
            {
                return false;
            }

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
            projections.Add(new MongoProjection(derivedAlias, bareLeaf, bareLikeExpr));
            leafIsArray.Add(bareIsArrayLeaf);
            hasArrayLeaf |= bareIsArrayLeaf;
            hasStringSequenceLeaf |= bareLikeExpr is MethodCallExpression bareStringSequenceCall
                                     && IsStringSequenceMaterializationCall(bareStringSequenceCall);
            // isOwnedNavEntityLeaf is discarded above: TryTranslateLeaf only produces it on the wrapped arm's
            // alias-equals-member-name path, never for a bare or ctor-wrap leaf.
            leafIsOwnedNavEntity.Add(false);
            return true;
        }

        // With an array or owned-nav-entity leaf present, a late fallback runs EF's client-side mixed shaper over whole
        // un-projected documents (ProjectionAnalyzer.CanPushDown refuses entity-typed leaves). So every other leaf must
        // read correctly off a whole document — a plain top-level field whose alias equals its element name. Renamed,
        // dotted and computed leaves don't, so the projection declines. The array/nav-entity leaves themselves are
        // skipped: their alias-equals-document-path check already proves them.
        if (hasArrayLeaf || hasOwnedNavEntityLeaf)
        {
            for (var i = 0; i < projections.Count; i++)
            {
                if (!leafIsArray[i] && !leafIsOwnedNavEntity[i]
                    && !IsWholeDocumentReadableLeaf(projections[i].Expression))
                    return false;
            }
        }

        // An owned element with a shadow key reads its owner's key from the shaped document (via _ownerMappings), so a
        // $project without _id fails per row ("Document element is missing for required non-nullable property"). Emit
        // the root key too; it is inert for the result shape and suppresses RenderProject's default `_id: 0`.
        // IsPlainProjectedSelect declines these as set-op operands (HasArrayProjectionLeaf) because this _id would leak
        // into the comparison key — change both together.
        if ((hasArrayLeaf || hasOwnedNavEntityLeaf) && seenAliases.Add("_id"))
        {
            // Properties[0] is only an approximate Type for a composite key (stored nested under _id). Inert: nothing
            // reads it.
            var keyProperty = mongoQ.CollectionExpression.EntityType.FindPrimaryKey()!.Properties[0];
            projections.Add(new MongoProjection("_id", new MongoElementRefExpression("_id", keyProperty.ClrType)));
        }

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);
        foreach (var reducerLeaf in pendingReducerLeaves)
            mongoQ.AddCorrelatedReducerLeaf(reducerLeaf);
        foreach (var lookupToStamp in pendingBareCountStamps)
            lookupToStamp.IsBareCountSizeSource = true;
        foreach (var projection in projections)
            mongoQ.Select.AddProjection(projection);
        // Registered after every `return false`, so a declined body leaves no override behind.
        if (bareProjectionAlias != null)
        {
            mongoQ.Select.AddProjectionAliasOverride(
                MongoSelectDefinition.BareProjectionMemberKey, bareProjectionAlias, bareProjectionTier);
        }

        // DocumentPath by construction: DeriveWrappedLeafAlias returns a non-member alias only when it is the document
        // path, and owned-nav-entity leaves register unconditionally (see above).
        foreach (var (memberName, alias) in namedAliasOverrides)
        {
            mongoQ.Select.AddProjectionAliasOverride(memberName, alias, ProjectionAliasTier.DocumentPath);
        }

        // Provenance for IsPlainProjectedSelect's set-op decline (leaked owner _id). This and the flags below are set
        // only on a successful commit.
        if (hasArrayLeaf || hasOwnedNavEntityLeaf)
            mongoQ.Select.HasArrayProjectionLeaf = true;
        if (hasStringSequenceLeaf)
            mongoQ.Select.HasStringSequenceProjectionLeaf = true;
        if (hasCaseMappingLeaf)
            mongoQ.Select.HasClientCaseMappingProjectionLeaf = true;
        if (hasPositionalCtorProjection)
            mongoQ.Select.HasPositionalCtorProjectionShaper = true;
        return true;
    }

    private static bool IsEnumType(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type).IsEnum;

    /// <summary>
    /// True for zero-argument <c>ToLower</c>/<c>ToUpper</c>/<c>ToLowerInvariant</c>/<c>ToUpperInvariant</c> on a
    /// <see langword="string"/>. Their server forms are ASCII-only, so a projected leaf stages only the receiver (see
    /// <see cref="PeelCaseMapping"/>) and the call is re-applied client-side.
    /// </summary>
    internal static bool IsCaseMappingCall(MethodCallExpression call)
        => call is { Object.Type: var objectType, Arguments.Count: 0 }
           && objectType == typeof(string)
           && call.Method.DeclaringType == typeof(string)
           && call.Method.Name is nameof(string.ToLower) or nameof(string.ToUpper)
               or nameof(string.ToLowerInvariant) or nameof(string.ToUpperInvariant);

    /// <summary>
    /// The receiver of a (possibly repeated) case-mapping call, which is what the leaf stages; the call itself stays
    /// in the shaper (see <c>MongoProjectionBindingExpressionVisitor.Visit</c>, and for a positional-ctor projection
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.BindPositionalCtorProjectionMember</c>).
    /// </summary>
    internal static Expression PeelCaseMapping(Expression leafExpression)
    {
        while (leafExpression is MethodCallExpression call && IsCaseMappingCall(call))
            leafExpression = call.Object!;

        return leafExpression;
    }

    /// <summary>
    /// True for <c>Enumerable.AsEnumerable</c>/<c>ToList</c>/<c>ToArray</c> over a <see langword="string"/>
    /// (<c>e.City.AsEnumerable()</c>). MongoDB has no char-sequence representation, so only the string field is pushed
    /// down and the shaper re-applies the call client-side.
    /// </summary>
    /// <remarks>Matched by canonical generic <see cref="MethodInfo"/> definition, not by name.</remarks>
    internal static bool IsStringSequenceMaterializationCall(MethodCallExpression call)
        => call is { Method.IsGenericMethod: true, Arguments: [var source] }
           && source.Type == typeof(string)
           && call.Method.GetGenericMethodDefinition() is var definition
           && (definition == EnumerableMethods.AsEnumerable
               || definition == EnumerableMethods.ToList
               || definition == EnumerableMethods.ToArray);

    /// <summary>
    /// Matches an owned single-reference navigation entity leaf (<c>b.Address</c>), as a bare member access or as
    /// <c>EF.Property(receiver, "Address")</c>.
    /// </summary>
    /// <remarks>
    /// Both spellings matter: after nav-expansion an owned-reference access always arrives as <c>EF.Property</c>; the
    /// bare member spelling is seen only by unit tests that skip preprocessing. Declines collection navigations (see
    /// <see cref="IsNativeArrayProjectionLeaf"/>), non-owned targets (those need <c>$lookup</c>), and leaves whose CLR
    /// type differs from the target entity type.
    /// </remarks>
    private static bool TryGetOwnedReferenceNavigationLeaf(
        MongoQueryExpression mongoQ, ParameterExpression outerParameter, Expression leafExpression,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;
        Expression receiver;
        string name;
        switch (leafExpression)
        {
            case MemberExpression { Expression: not null } me:
                receiver = me.Expression;
                name = me.Member.Name;
                break;
            case MethodCallExpression call when call.Method.IsEFPropertyMethod()
                                                 && call.Arguments.Count == 2
                                                 && call.Arguments[1] is ConstantExpression { Value: string navName }:
                receiver = call.Arguments[0];
                name = navName;
                break;
            default:
                return false;
        }

        if (!IsSelectorParameter(receiver, outerParameter))
        {
            return false;
        }

        if (mongoQ.CollectionExpression.EntityType.FindNavigation(name) is not { } nav
            || nav.IsCollection
            || !nav.IsEmbedded()
            || leafExpression.Type != nav.TargetEntityType.ClrType)
        {
            return false;
        }

        navigation = nav;
        return true;
    }

    /// <summary>
    /// Matches a constructed sub-entity leaf (<c>new Book { Id = e.Id, Title = e.Title }</c>) and translates it to a
    /// <see cref="MongoDocumentConstructionExpression"/> emitting a nested sub-document.
    /// </summary>
    /// <remarks>
    /// Every member must be a plain top-level, default-serialized scalar field; anything else (computed, navigation,
    /// dotted owned field) declines the whole leaf, because a late fallback reads each member by its own undotted root
    /// path. For the same reason it needs no owner-key retention and is exempt from the sibling-readability sweep.
    /// </remarks>
    private static bool TryGetDocumentConstructionLeaf(
        MongoExpressionTranslator translator,
        Expression leafExpression,
        [NotNullWhen(true)] out MongoDocumentConstructionExpression? result)
    {
        result = null;

        if (!leafExpression.TryGetProjectionMembers(out var members))
            return false;

        var translatedMembers = new List<(string, MongoExpression)>();
        var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (memberName, value) in members)
        {
            if ((value is MemberExpression
                    || (value is MethodCallExpression efPropertyCall && efPropertyCall.Method.IsEFPropertyMethod()))
                && translator.TryTranslateField(value, out var field)
                && !field.ElementName.Contains('.')
                && NativeGroupByBinder.HasDefaultKeySerialization(field.Property)
                && seenMembers.Add(memberName))
            {
                translatedMembers.Add((memberName, field));
                continue;
            }

            return false;
        }

        result = new MongoDocumentConstructionExpression(leafExpression, translatedMembers);
        return true;
    }

    /// <summary>
    /// Translates a single projection leaf (field, owned collection/reference, count, computed expression, ...);
    /// returns <see langword="false"/> when it is not natively representable.
    /// </summary>
    private static bool TryTranslateLeaf(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        ParameterExpression outerParameter,
        Expression leafExpression,
        string alias,
        List<LookupExpression> pendingLookups,
        List<MongoCorrelatedReducerLeaf> pendingReducerLeaves,
        List<LookupExpression> pendingBareCountStamps,
        out MongoExpression result,
        out bool isArrayLeaf,
        out bool isOwnedNavEntityLeaf,
        bool allowWholeRootEntityLeaf = false)
        // A non-nullable Length/IndexOf over a possibly-null string would read its null as 0; see
        // MongoAggregationExpressionRenderer.MayBeNullBehindNonNullableType.
        => TryTranslateLeafCore(
               mongoQ, translator, outerParameter, leafExpression, alias, pendingLookups, pendingReducerLeaves,
               pendingBareCountStamps, out result, out isArrayLeaf, out isOwnedNavEntityLeaf, allowWholeRootEntityLeaf)
           && !MongoAggregationExpressionRenderer.ReadsNullAsDefault(
               NativeSlotPopulator.UnwrapBoxingToObjectType(leafExpression), result);

    private static bool TryTranslateLeafCore(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        ParameterExpression outerParameter,
        Expression leafExpression,
        string alias,
        List<LookupExpression> pendingLookups,
        List<MongoCorrelatedReducerLeaf> pendingReducerLeaves,
        List<LookupExpression> pendingBareCountStamps,
        out MongoExpression result,
        out bool isArrayLeaf,
        out bool isOwnedNavEntityLeaf,
        bool allowWholeRootEntityLeaf = false)
    {
        // Set only by the owned array-leaf branch.
        isArrayLeaf = false;
        // Set only by the owned-reference nav-entity branch.
        isOwnedNavEntityLeaf = false;

        // Plain top-level scalar leaf (c.Foo or EF.Property). TryTranslateField decides whether it is a real field; the
        // leaf kinds handled further down decline there, so trying this first is safe.
        if ((leafExpression is MemberExpression
                || (leafExpression is MethodCallExpression efPropertyCall && efPropertyCall.Method.IsEFPropertyMethod()))
            && translator.TryTranslateField(leafExpression, out var field))
        {
            // A non-default-serialized dotted (owned single-ref) leaf must decline: the DOM shaper's field-access
            // resolver is single-hop, can't find the property's serializer, and would silently return the raw stored
            // value.
            if (!NativeGroupByBinder.HasDefaultKeySerialization(field.Property) && field.ElementName.Contains('.'))
            {
                result = null!;
                return false;
            }
            result = field;
            return true;
        }

        // String-to-char-sequence leaf (`e.City.AsEnumerable()`/ToList/ToArray): push down just the string field; the
        // read side re-applies the call (see MongoProjectionBindingExpressionVisitor.Visit).
        if (leafExpression is MethodCallExpression stringSequenceCall
            && IsStringSequenceMaterializationCall(stringSequenceCall)
            && (stringSequenceCall.Arguments[0] is MemberExpression
                || (stringSequenceCall.Arguments[0] is MethodCallExpression stringSeqEfPropertyCall
                    && stringSeqEfPropertyCall.Method.IsEFPropertyMethod()))
            && translator.TryTranslateField(stringSequenceCall.Arguments[0], out var stringSeqField))
        {
            // Same dotted non-default-serialized decline as the plain-field branch.
            if (!NativeGroupByBinder.HasDefaultKeySerialization(stringSeqField.Property) && stringSeqField.ElementName.Contains('.'))
            {
                result = null!;
                return false;
            }

            result = stringSeqField;
            return true;
        }

        // Enum-to-enum cast over a plain field — `(TargetEnum)c.SourceEnum`. MQL has no $toX for enums, but none is
        // needed: the cast is a CLR relabeling, so the leaf is admitted as the bare field. The shaper still reads the
        // whole Convert node through the generic alias-read path (no IProperty/serializer), so the field must be
        // default-serialized.
        if (leafExpression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } castUnary
            && IsEnumType(castUnary.Operand.Type)
            && IsEnumType(castUnary.Type)
            && (castUnary.Operand is MemberExpression
                || (castUnary.Operand is MethodCallExpression castEfPropertyCall && castEfPropertyCall.Method.IsEFPropertyMethod()))
            && translator.TryTranslateField(castUnary.Operand, out var castField)
            && NativeGroupByBinder.HasDefaultKeySerialization(castField.Property))
        {
            result = castField;
            return true;
        }

        // Whole-root-entity leaf — `new { c, Total = ... }`, projected as `$$ROOT`. Wrapped bodies only; a bare `c =>
        // c` keeps the WholeEntity route.
        if (allowWholeRootEntityLeaf
            && IsWholeRootEntityLeaf(mongoQ, leafExpression, outerParameter))
        {
            result = new MongoElementRefExpression(
                MongoElementRefExpression.WholeRootDocumentPath, mongoQ.CollectionExpression.EntityType.ClrType);
            return true;
        }

        // Owned single-reference navigation entity leaf — `new { b.Address, b.Title }` — projected by its document
        // path. Wrapped bodies only; a bare `b => b.Address` declines.
        //
        // The alias must equal the document path: a late fallback (explicit DriverLinq or a TryBuildNativeFactory
        // decline) strips the projection and hands the shaper whole documents, which only have an element named `alias`
        // if the member wasn't renamed (`new { Addr = b.Address }` would silently read nothing).
        if (allowWholeRootEntityLeaf
            && TryGetOwnedReferenceNavigationLeaf(mongoQ, outerParameter, leafExpression, out var ownedNav)
            && ownedNav.TargetEntityType.GetContainingElementName() is { } ownedNavElementName
            && alias == ownedNavElementName)
        {
            result = new MongoElementRefExpression(ownedNavElementName, ownedNav.TargetEntityType.ClrType);
            isOwnedNavEntityLeaf = true;
            return true;
        }

        // Constructed sub-entity leaf — `new { Book = new Book { Id = e.Id, ... } }` — emitted as a nested
        // sub-document. Wrapped bodies only; a construction that is the selector body itself is handled as an ordinary
        // wrapped projection.
        if (allowWholeRootEntityLeaf
            && TryGetDocumentConstructionLeaf(translator, leafExpression, out var construction))
        {
            result = construction;
            return true;
        }

        // Synthetic vector-search score — `EF.Property<double>(e, "__score")` or its Mql.Field spelling. Admitted only
        // when the query emits the $addFields{__score} companion (see TryRecognizeVectorScoreLeaf).
        if (mongoQ.Select.VectorSearch is not null
            && TryRecognizeVectorScoreLeaf(leafExpression, outerParameter, out var scoreType))
        {
            result = new MongoElementRefExpression(MongoVectorSearchScoreStage.ScoreField, scoreType);
            return true;
        }

        // Owned entity-collection leaf — `new { b.Title, b.Posts }`. Nav-expansion wraps it in a
        // MaterializeCollectionNavigationExpression whose Subquery (`EF.Property(b, "Posts").AsQueryable()`) is what
        // gets translated. A primitive collection (`b.Tags`) is a plain member access handled above.
        if (leafExpression is MaterializeCollectionNavigationExpression materializeCollection
            && IsNativeArrayProjectionLeaf(
                materializeCollection.Navigation as INavigation, mongoQ.CollectionExpression.EntityType, alias)
            && translator.TryTranslateOwnedCollectionArray(materializeCollection.Subquery, out var arrayRef))
        {
            result = arrayRef;
            isArrayLeaf = true;
            return true;
        }

        // Reference-collection-nav First/FirstOrDefault reduced to a member — `new { a.Id,
        // a.IdentificationMethods.FirstOrDefault().Method }`. Wrapped bodies only: a bare body's alias is derived from
        // the leaf, and a `_lookup_<Nav>.<Member>` path fits neither alias tier. Today neither tier answers for a
        // MongoElementRefExpression anyway; the gate keeps a future tier widening from admitting a bare body whose
        // late-fallback read is impossible.
        if (allowWholeRootEntityLeaf
            && TryGetCorrelatedReducerLeaf(
                mongoQ, outerParameter, leafExpression, alias, pendingLookups, pendingReducerLeaves,
                out var correlatedReducerRef))
        {
            result = correlatedReducerRef;
            return true;
        }

        if (TryTranslateProjectedCollectionCount(
                mongoQ, outerParameter, leafExpression, pendingLookups, pendingBareCountStamps, out var sizeExpression))
        {
            result = sizeExpression!;
            return true;
        }

        // Projected reference-collection-nav list — `Orders = c.Orders.ToList()`. The read side comes from
        // MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigation in every mode; this arm only
        // keeps Route native and retains the $lookup field under its alias. Wrapped bodies only. Reuses isArrayLeaf
        // because it shares the array leaf's hazards (collection-typed, mixed shaper on fallback, exempt from the
        // sibling sweep; _id retention is inert).
        if (allowWholeRootEntityLeaf
            && TryTranslateProjectedCollectionNavigationList(mongoQ, outerParameter, leafExpression, pendingLookups, out var listNavigation)
            && alias == LookupExpression.GetLookupAlias(listNavigation))
        {
            result = new MongoElementRefExpression(alias, listNavigation.TargetEntityType.ClrType);
            isArrayLeaf = true;
            return true;
        }

        // Arithmetic leaf (+ - * / %), rendered as an aggregation operator document. Gated on a binary arithmetic top
        // node: admitting any TryTranslateValue success would let a falsy (0/false) constant reach $project, which
        // reads it as an exclusion flag and aborts the aggregate.
        if (leafExpression is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo }
            && translator.TryTranslateValue(leafExpression, out var computed))
        {
            result = computed;
            return true;
        }

        // Computed leaves are admitted by resulting node kind, not by "TryTranslateValue succeeded": a bare value in $project
        // is read as an inclusion/exclusion flag (0/false aborts with "Cannot do exclusion on field ... in inclusion
        // projection"), while these kinds all render as documents.
        //
        // Casts over value-converted/non-default-represented fields never get here: TryTranslateValue's
        // AllFieldsDefaultSerialized guard rejects them. The read side's raw-alias Convert bypass
        // (MongoProjectionBindingRemovingExpressionVisitor) depends on that; relaxing the guard breaks it silently
        // (pinned by NativeCastTests.Cast_over_a_value_converted_property_declines_instead_of_reading_the_raw_stored_value).
        //
        // A widening cast (`(long)x.I`) translates to a bare MongoFieldExpression, so it is detected from the original
        // leafExpression; MongoDB operates on the raw numeric value regardless of CLR width, so that is exact.
        //
        // Bare constants/parameters (`Select(x => 8)`) are safe because RenderProject $literal-wraps them, but
        // BsonValue.Create throws for a non-BSON-mappable value (captured anonymous type/POCO), so
        // TryProbeBareValueRenders trial-renders them first.
        if (translator.TryTranslateValue(leafExpression, out var value)
            && (value is MongoSizeExpression or MongoFilteredSizeExpression or MongoConvertExpression
                    or MongoConditionalExpression or MongoDatePartExpression or MongoDateTimeOffsetLocalExpression
                    or MongoElementRefExpression or MongoDateAddExpression or MongoCoalesceExpression
                    or MongoMathExpression or MongoTrimExpression or MongoSubstringExpression or MongoReplaceExpression
                    or MongoStringFirstOrLastExpression
                // Predicate/string-scalar leaves only render in $project via the aggregation dialect, so decline a
                // shape it can't render here (as gate 1c4 does for a bare body) rather than fail at render time.
                || ((value is MongoRegexExpression or MongoStringLengthExpression or MongoStringIndexOfExpression
                        || IsProjectablePredicate(value))
                    && MongoAggregationExpressionRenderer.CanRender(value))
                || (value is MongoConstantExpression or MongoParameterExpression
                    && NativeSlotPopulator.TryProbeBareValueRenders(
                        value, NativeSlotPopulator.UnwrapBoxingToObjectType(leafExpression)))
                || (leafExpression is UnaryExpression { NodeType: ExpressionType.Convert } && value is MongoFieldExpression)))
        {
            result = value;
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Recognizes the synthetic vector-search score leaf: <c>EF.Property&lt;double&gt;(e, "__score")</c> or
    /// <c>Mql.Field(e, "__score", DoubleSerializer.Instance)</c>, rooted on the selector's own parameter.
    /// </summary>
    /// <remarks>
    /// The caller also requires <c>Select.VectorSearch</c>, since only then is <c>$addFields{__score}</c> emitted. The
    /// literal <c>"__score"</c> name keeps general <c>Mql.Field</c> element addressing (serializer and converter
    /// questions) out of the native binder. <c>double</c>/<c>double?</c> only: the shaper reads the value raw by alias
    /// with a default serializer, ignoring <c>Mql.Field</c>'s serializer argument, and <c>$meta:
    /// "vectorSearchScore"</c> is always a BSON double. Anything else declines to driver-LINQ.
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
    /// True when <paramref name="receiver"/> is the selector's own lambda parameter (by reference, never by type),
    /// possibly wrapped in nav-expansion auto-include layers.
    /// </summary>
    /// <remarks>
    /// The <see cref="IncludeExpression"/> peel matters: an entity with an owned navigation gets an auto-include around
    /// the expression the projection reads through, so <c>Mql.Field</c> arrives as <c>Mql.Field(IncludeExpression(e,
    /// ...))</c> while <c>EF.Property</c> arrives with the bare parameter. An include layer changes what is
    /// materialized, never which document is read, so peeling is safe.
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
    /// True when <paramref name="leafExpression"/> is the selector's own root parameter (see <see
    /// cref="IsSelectorParameter"/>) typed as the root entity CLR type — the whole entity, unchanged. Shared by <see
    /// cref="TryPopulateNativeProjection"/>'s ctor-only sub-case 1 and <see cref="TryTranslateLeaf"/>'s <c>$$ROOT</c>
    /// arm; one predicate keeps the two identical, which keeps the <c>$ROOT</c>-as-alias hazard unreachable.
    /// </summary>
    private static bool IsWholeRootEntityLeaf(MongoQueryExpression mongoQ, Expression leafExpression, ParameterExpression outerParameter)
        => IsSelectorParameter(leafExpression, outerParameter)
           && leafExpression.Type == mongoQ.CollectionExpression.EntityType.ClrType;

    /// <summary>
    /// Like <see cref="IsWholeRootEntityLeaf"/>, but also accepts a one-hop <c>ti.Outer</c>/<c>ti.Inner</c> off a
    /// <c>TransparentIdentifier</c> parameter (as produced for a reference-navigation <c>Include</c>). Used only by
    /// <see cref="TryGetSoleWholeRootEntityOperand"/> and <see cref="IsClientOnlyWholeEntityExpression"/>; the
    /// <c>$$ROOT</c> arm and ctor-wrap sub-case 1 must keep using <see cref="IsWholeRootEntityLeaf"/>.
    /// </summary>
    private static bool IsWholeRootEntityLeafOrJoinScopePassthrough(
        MongoQueryExpression mongoQ, Expression leafExpression, ParameterExpression outerParameter)
    {
        if (IsWholeRootEntityLeaf(mongoQ, leafExpression, outerParameter))
        {
            return true;
        }

        var current = leafExpression.RemoveConvert();
        while (current is IncludeExpression include)
        {
            current = include.EntityExpression.RemoveConvert();
        }

        return outerParameter.Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal)
               && current is MemberExpression { Member.Name: "Outer" or "Inner" } member
               && ReferenceEquals(member.Expression, outerParameter)
               && member.Type == mongoQ.CollectionExpression.EntityType.ClrType;
    }

    /// <summary>
    /// True when exactly one operand of <paramref name="methodCall"/> (receiver plus arguments) is the whole entity and
    /// no other operand references <paramref name="outerParameter"/>. The method then runs client-side over the fetched
    /// entity.
    /// </summary>
    /// <remarks>
    /// A second parameter-referencing operand (<c>context.ClientMethod(x, x.CustomerID)</c>) declines: supporting it
    /// would need the shaper fold to recurse through the opaque call's other operands, which is untested.
    /// </remarks>
    private static bool TryGetSoleWholeRootEntityOperand(
        MongoQueryExpression mongoQ, MethodCallExpression methodCall, ParameterExpression outerParameter)
    {
        var sawWholeRootEntityOperand = false;

        if (methodCall.Object != null)
        {
            if (IsWholeRootEntityLeafOrJoinScopePassthrough(mongoQ, methodCall.Object, outerParameter))
            {
                sawWholeRootEntityOperand = true;
            }
            else if (methodCall.Object.ReferencesParameter(outerParameter))
            {
                return false;
            }
        }

        foreach (var argument in methodCall.Arguments)
        {
            if (IsWholeRootEntityLeafOrJoinScopePassthrough(mongoQ, argument, outerParameter))
            {
                if (sawWholeRootEntityOperand)
                {
                    return false;
                }

                sawWholeRootEntityOperand = true;
            }
            else if (argument.ReferencesParameter(outerParameter))
            {
                return false;
            }
        }

        return sawWholeRootEntityOperand;
    }

    /// <summary>
    /// Generalizes <see cref="TryGetSoleWholeRootEntityOperand"/> to a whole client-only body <see
    /// cref="TryTranslateLeaf"/> could not render (e.g. a conditional/concat around a client-method call): true when
    /// the body contains at least one opaque call and references <paramref name="outerParameter"/> only through the
    /// whole entity (or member chains off it). The whole document is then fetched and the entire body evaluated
    /// client-side.
    /// </summary>
    /// <remarks>
    /// The opaque-call requirement matters: a translatable tree with no client call (e.g.
    /// Ternary_Null_Equals_Non_Numeric_First_Part) must keep declining so it still reaches the driver push-down. Only
    /// conditional, binary, unary, member-access and nested-call nodes are walked; anything else declines. Each nested
    /// call gets the same one-whole-entity-operand cap.
    /// </remarks>
    private static bool IsClientOnlyWholeEntityExpression(
        MongoQueryExpression mongoQ, Expression node, ParameterExpression outerParameter)
    {
        var sawOpaqueCall = false;
        return IsClientOnlyWholeEntitySubtree(mongoQ, node, outerParameter, ref sawOpaqueCall) && sawOpaqueCall;
    }

    private static bool IsClientOnlyWholeEntitySubtree(
        MongoQueryExpression mongoQ, Expression node, ParameterExpression outerParameter, ref bool sawOpaqueCall)
    {
        if (!node.ReferencesParameter(outerParameter))
        {
            return true;
        }

        if (IsWholeRootEntityLeafOrJoinScopePassthrough(mongoQ, node, outerParameter))
        {
            return true;
        }

        switch (node)
        {
            case ConditionalExpression conditional:
                return IsClientOnlyWholeEntitySubtree(mongoQ, conditional.Test, outerParameter, ref sawOpaqueCall)
                    && IsClientOnlyWholeEntitySubtree(mongoQ, conditional.IfTrue, outerParameter, ref sawOpaqueCall)
                    && IsClientOnlyWholeEntitySubtree(mongoQ, conditional.IfFalse, outerParameter, ref sawOpaqueCall);

            case BinaryExpression binary:
                return IsClientOnlyWholeEntitySubtree(mongoQ, binary.Left, outerParameter, ref sawOpaqueCall)
                    && IsClientOnlyWholeEntitySubtree(mongoQ, binary.Right, outerParameter, ref sawOpaqueCall);

            case UnaryExpression unary:
                return IsClientOnlyWholeEntitySubtree(mongoQ, unary.Operand, outerParameter, ref sawOpaqueCall);

            case MemberExpression { Expression: not null } member:
                return IsClientOnlyWholeEntitySubtree(mongoQ, member.Expression, outerParameter, ref sawOpaqueCall);

            case MethodCallExpression methodCall when !methodCall.Method.IsEFPropertyMethod()
                                                       && TryGetSoleWholeRootEntityOperand(mongoQ, methodCall, outerParameter):
                sawOpaqueCall = true;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Open generic definition of <c>Mql.Field&lt;TDocument, TField&gt;(TDocument, string,
    /// IBsonSerializer&lt;TField&gt;)</c>, resolved by reflection since the driver has no canonical constant; matched
    /// by definition, never by name.
    /// </summary>
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Mql.Field) && m.GetParameters().Length == 3);

    /// <summary>
    /// The single admissibility rule for an owned entity-collection array projection leaf, shared by the emit side
    /// (<see cref="TryTranslateLeaf"/>) and the shaper side
    /// (<c>MongoProjectionBindingExpressionVisitor.TryBindNativeArrayProjection</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keep this one method: if the emit side accepts a navigation the shaper side rejects, the <c>$project</c>
    /// flattens the array to an alias while the shaper reads the document path, silently yielding an empty collection.
    /// </para>
    /// <para>
    /// Invariant: the alias-addressed read and the navigation's document-path read must coincide, because a late
    /// fallback (e.g. explicit <c>DriverLinq</c>) hands the same alias-addressed shaper a whole un-projected document.
    /// That holds when the array is reachable from the root through single embedded references only (<see
    /// cref="TryGetRootRelativeArrayPath"/>; <c>Home.Notes</c> qualifies, <c>Posts.Comments</c> does not) and the alias
    /// equals that path. Without the alias check, <c>new { b.Title, P = b.Posts }</c> silently returns an empty
    /// collection under <c>DriverLinq</c>.
    /// </para>
    /// <para>
    /// The element type must also have no eager-loaded navigation of its own (i.e. no nested owned navigation): EF's
    /// auto-include for it makes shaper build fail in every <see cref="Infrastructure.MongoQueryMode"/> (a separate
    /// defect). Lazy navigations such as a <c>WithOwner</c> back-reference are fine. See
    /// <c>NativeArrayProjectionTests.Element_with_its_own_navigation_is_declined_and_still_fails_identically_in_every_mode</c>.
    /// </para>
    /// </remarks>
    /// <param name="navigation">
    /// The candidate navigation; <see langword="null"/> (e.g. a skip navigation) declines.
    /// </param>
    /// <param name="rootEntityType">The query root's entity type (<c>CollectionExpression.EntityType</c>).</param>
    /// <param name="alias">The <c>$project</c> alias; both sides derive it from the same member name.</param>
    internal static bool IsNativeArrayProjectionLeaf(INavigation? navigation, IEntityType rootEntityType, string? alias)
        => navigation is not null
           && navigation.IsEmbedded()
           && navigation.IsCollection
           && alias is not null
           // For an OwnsOne hop the path is dotted ("Home.Notes"), which the shaper walks segment by segment
           // (BsonBinding.TryGetValueAtPath).
           && TryGetRootRelativeArrayPath(navigation, rootEntityType, out var arrayPath)
           && alias == arrayPath
           // Eager-loaded navigations on the element crash shaper build (see remarks); non-eager ones are fine, as in
           // IsWholeElementRepresentable's Reference arm.
           && !navigation.TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded);

    /// <summary>
    /// The root-relative document path of an owned collection navigation's stored array — the dotted join of
    /// every containing element name from the query root down to <paramref name="navigation"/>'s own.
    /// <see langword="false"/> when the chain does not reach <paramref name="rootEntityType"/> through single
    /// embedded references only.
    /// </summary>
    /// <remarks>
    /// A collection above the array has no dotted read (the intermediate is an array, not a document), so it declines.
    /// </remarks>
    private static bool TryGetRootRelativeArrayPath(
        INavigation navigation, IEntityType rootEntityType, [NotNullWhen(true)] out string? path)
    {
        var segments = new List<string>();
        var current = navigation;

        // Bounded rather than `while (true)`, guarding against a malformed model.
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
    /// Requiring the member name to equal the navigation's containing element name keeps the renamed-alias decline
    /// (<c>new { P = b.Posts }</c>) intact: only a name that already agreed with the last path segment is replaced. For
    /// a root-declared array leaf the path equals the member name, so no override is registered.
    /// </remarks>
    private static string DeriveWrappedLeafAlias(
        MongoQueryExpression mongoQ, ParameterExpression outerParameter, Expression leafExpression, string memberName)
    {
        if (leafExpression is MaterializeCollectionNavigationExpression materializeCollection
            && materializeCollection.Navigation is INavigation ownedNavigation
            && memberName == ownedNavigation.TargetEntityType.GetContainingElementName()
            && TryGetRootRelativeArrayPath(ownedNavigation, mongoQ.CollectionExpression.EntityType, out var path))
        {
            return path;
        }

        // A projected reference-collection-nav list (`Orders = c.Orders.ToList()`) is read back by
        // TryBindProjectedCollectionNavigation under the lookup's own alias (_lookup_<Nav>), whatever the member is
        // named. The throwaway pendingLookups list is fine: this only picks the alias, and TryTranslateLeaf re-matches
        // to stage the lookup.
        if (TryTranslateProjectedCollectionNavigationList(
                mongoQ, outerParameter, leafExpression, pendingLookups: [], out var refNavigation))
        {
            return LookupExpression.GetLookupAlias(refNavigation);
        }

        return memberName;
    }

    /// <summary>
    /// Null-coalesces a pushed-down bare collection-navigation <c>Count</c> in <c>CapturedExpression</c>
    /// (<c>b.Posts.Count</c> becomes <c>(b.Posts ?? new List&lt;Post&gt;()).Count</c>) for a bare projection committed
    /// under <see cref="ProjectionAliasTier.Synthetic"/>; returns <paramref name="captured"/> unchanged otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Synthetic bare leaf keeps the driver's <c>_v</c> push-down on fallback, but the driver renders a bare
    /// <c>{"$size": "$Posts"}</c>, which is a hard server error on a missing/null array (under both late decline and
    /// explicit <c>DriverLinq</c>). The <c>??</c> spelling renders <c>$ifNull</c> exactly like native; <c>?:</c>
    /// renders <c>$cond</c> and still aborts, because MongoDB evaluates the untaken branch.
    /// </para>
    /// <para>
    /// Applied where <c>MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall</c> assigns
    /// <c>CapturedExpression</c>, not in the commit block: that assignment follows every translated call and would
    /// overwrite a write made from <see cref="TryPopulateNativeProjection"/>.
    /// </para>
    /// <para>
    /// Only the unfiltered owned-collection <c>Count</c> as the body of the bare <c>Select</c> itself is rewritten;
    /// filtered counts, arithmetic and casts don't abort, and reference-collection counts read a <c>$lookup</c> output
    /// that always exists. A primitive-collection bare count (<c>b.Tags.Count</c>) never gets here and still aborts on
    /// a ragged array (see <c>NativeComputedBareProjectionTests</c>). Mutually exclusive with
    /// <c>StripPushedDownSelect</c>, which never fires for a Synthetic bare leaf.
    /// </para>
    /// </remarks>
    internal static Expression? NullCoalesceSyntheticBareCountBody(
        Expression? captured, MongoSelectDefinition select)
    {
        if (captured is null || select.BareProjectionTier != ProjectionAliasTier.Synthetic)
        {
            return captured;
        }

        // Navigates the same two chain shapes as StripPushedDownSelect (Select outermost, or under a no-arg cardinality
        // terminator), deliberately not a tree walk, which would also reach wrapped or subquery counts. Matches
        // `Select` by name to stay in lockstep with StripPushedDownSelect; the arity check and TryRewriteSelect exclude
        // other shapes. Move both to QueryableMethods together.
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
            // Unlike StripPushedDownSelect, the terminator keeps its generic argument: the Select stays and its body
            // type is unchanged.
            return call.Update(call.Object, [rewrittenInner]);
        }

        return captured;
    }

    /// <summary>
    /// Rewrites <paramref name="selectCall"/>'s selector body when it is an unfiltered collection-navigation
    /// <c>Count</c>/<c>LongCount</c> over a navigation rooted at the selector's own parameter.
    /// </summary>
    /// <remarks>
    /// EF lowers <c>Select(b =&gt; b.Posts.Count)</c> to <c>Select(b =&gt;
    /// Queryable.Count(Queryable.AsQueryable(EF.Property&lt;List&lt;Post&gt;&gt;(b, "Posts"))))</c>. Requiring the
    /// navigation to be rooted at the parameter excludes a reference-collection count (an <c>EntityQueryRoot</c>
    /// subquery), which needs no rewrite.
    /// </remarks>
    private static bool TryRewriteSelect(MethodCallExpression selectCall, out Expression rewritten)
    {
        rewritten = selectCall;

        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !TryMatchRewritableBareCountBody(selector, out var navigation, out var empty))
        {
            return false;
        }

        // Safe: the matcher above validated both shapes.
        var countCall = (MethodCallExpression)selector.Body;
        var asQueryableCall = (MethodCallExpression)countCall.Arguments[0];

        var coalesced = Expression.Coalesce(navigation, empty);
        var newBody = countCall.Update(null, [asQueryableCall.Update(null, [coalesced])]);

        var newSelector = Expression.Lambda(selector.Type, newBody, selector.Parameters);
        rewritten = selectCall.Update(
            selectCall.Object,
            [
                selectCall.Arguments[0],
                // Preserve the original quoting so only the body differs.
                selectCall.Arguments[1] is UnaryExpression {NodeType: ExpressionType.Quote}
                    ? Expression.Quote(newSelector)
                    : newSelector
            ]);
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="selector"/>'s body is an unfiltered collection-navigation
    /// <c>Count</c>/<c>LongCount</c> that <see cref="NullCoalesceSyntheticBareCountBody"/> can null-coalesce.
    /// </summary>
    /// <remarks>
    /// The single definition shared by <see cref="TryRewriteSelect"/> (whether to rewrite) and <see
    /// cref="IsFallbackSafeBareSizeLeaf"/> (whether tier 2 may admit the leaf); the two must agree, so don't restate it
    /// elsewhere. It covers only the selector, not the captured-chain shape (see <see
    /// cref="IsFallbackSafeBareSizeLeaf"/>).
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
    /// Builds the empty collection the <c>??</c> substitutes for a null navigation, assignable to <paramref
    /// name="navigationType"/>; <see langword="false"/> when none can be built.
    /// </summary>
    /// <remarks>
    /// A constructible type is constructed as itself. An interface-typed navigation (<c>ICollection&lt;T&gt;</c>,
    /// <c>IEnumerable&lt;T&gt;</c>, ...; see <c>OwnedEntityTests.PersonWithIEnumerableLocations</c>) uses <c>new
    /// List&lt;T&gt;()</c> when assignable; <c>ISet&lt;T&gt;</c>, abstract bases and non-enumerables decline. A decline
    /// also declines tier-2 admission (via <see cref="TryMatchRewritableBareCountBody"/>), so an un-rewritten, aborting
    /// bare <c>$size</c> is never committed.
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
    /// Whether <paramref name="expression"/> is a collection-navigation access on <paramref name="parameter"/>, as
    /// <c>EF.Property&lt;T&gt;(b, "Posts")</c> or <c>b.Posts</c>.
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
    /// True when <paramref name="leaf"/> reads back correctly from a whole, un-projected document, as handed over by a
    /// late <see cref="Infrastructure.MongoQueryMode.DriverLinq"/> fallback or EF's mixed shaper. Only a plain
    /// top-level property-backed field qualifies; its alias needn't match its element name (a projected key, alias
    /// <c>"Id"</c> vs. <c>"_id"</c>), since the mixed read side resolves it through its <see cref="IProperty"/>. Dotted
    /// and computed leaves decline.
    /// </summary>
    /// <remarks>
    /// Also what keeps a <c>$$ROOT</c> leaf away from the late-fallback strip: document-path alias overrides come only
    /// from array and owned-nav-entity leaves, whose presence forces this check over every sibling, and a <c>$$ROOT</c>
    /// element ref is not a <see cref="MongoFieldExpression"/>. So the projection declines before the strip could hand
    /// the native (non-mixed) shaper whole documents it would misread. Pinned by
    /// <c>Ef362ArrayLeafPathTests.A_whole_root_entity_leaf_beside_an_owned_hop_array_leaf_declines_the_whole_projection</c>.
    /// </remarks>
    private static bool IsWholeDocumentReadableLeaf(MongoExpression leaf)
        => leaf is MongoFieldExpression field
           && !field.ElementName.Contains('.');

    /// <summary>
    /// Placeholder alias for a bare body whose leaf is not an owned-collection array; never observable. The leading
    /// space makes it an impossible element name, so it can't satisfy the array branch's alias-agreement check.
    /// </summary>
    private const string BareLeafProvisionalAlias = " bare";

    /// <summary>
    /// Derives a bare selector body's projection alias from its translated leaf, admitting only a leaf that has
    /// a root-relative document path and taking that path as the alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With alias == document path, reading element &lt;alias&gt; and reading the leaf at its path are the same read,
    /// so one shaper works whether or not the <c>$project</c> is actually emitted (a late fallback strips it; see
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback</c>). The same invariant as
    /// <see cref="IsNativeArrayProjectionLeaf"/>, but here the alias is chosen rather than checked.
    /// </para>
    /// <para>
    /// Admits a <see cref="MongoFieldExpression"/> (scalar or primitive collection) or a <see
    /// cref="MongoElementRefExpression"/> (owned array leaf, vector-search <c>__score</c>). Dotted paths decline: the
    /// shaper looks a dotted alias up as a literal key, while <c>$project</c> renders it nested. Computed leaves have
    /// no path and go to <see cref="TryDeriveSyntheticAlias"/>, whose correctness argument is the opposite.
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
    /// Reserved <c>$project</c> element name for a computed bare body — exactly what the driver's LINQ provider names a
    /// bare projection.
    /// </summary>
    /// <remarks>
    /// A Synthetic alias names no document element, so stripping it on fallback would fail (<c>Document element '_v' is
    /// missing but required</c>); instead the driver's un-stripped push-down writes <c>_v</c>, which the shaper already
    /// reads. A stored element named <c>_v</c> can't collide, since the <c>$project</c> replaces the whole document. If
    /// the driver-LINQ fallback ever goes away, this tier needs a different answer.
    /// </remarks>
    internal const string SyntheticBareProjectionAlias = "_v";

    /// <summary>
    /// Derives a computed bare body's alias (<see cref="SyntheticBareProjectionAlias"/>), admitting only node kinds
    /// that render as an aggregation-operator document, or a constant/parameter that <c>RenderProject</c>
    /// $literal-wraps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node-kind gate: an unwrapped bare <c>$project</c> value is read as an inclusion/exclusion flag. Pinned by
    /// <c>NativeComputedBareProjectionTests.Bare_constant_leaf_now_goes_native_via_the_project_literal_wrap</c>.
    /// </para>
    /// <para>
    /// Match on <see cref="MongoBinaryExpression.Operator"/>, not <c>NodeType</c> — every <see cref="MongoExpression"/>
    /// reports <see cref="ExpressionType.Extension"/>.
    /// </para>
    /// <para>
    /// Size nodes are admitted only as the top node (gate 1a), where <see cref="NullCoalesceSyntheticBareCountBody"/>
    /// rewrites the fallback into <c>$ifNull</c>. The rewrite never reaches a nested count, so every other arm requires
    /// <see cref="IsArrayFreeComputedSubtree"/> over the whole subtree (<c>$multiply</c> over <c>$size</c> declines).
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
            // Gate 1a: a size node as the top node whose un-stripped fallback cannot abort (see
            // IsFallbackSafeBareSizeLeaf).
            case MongoSizeExpression or MongoFilteredSizeExpression
                when IsFallbackSafeBareSizeLeaf(leaf, selector, pendingLookups):
                break;

            // Gate 1b: arithmetic/cast/concat. The subtree check matters: `b.Posts.Count * 2` has an arithmetic top
            // node, and the rewrite doesn't reach a nested count.
            case MongoConvertExpression
                or MongoConcatExpression
                or MongoBinaryExpression
                {
                    Operator: MongoBinaryOperator.Add or MongoBinaryOperator.Subtract
                    or MongoBinaryOperator.Multiply or MongoBinaryOperator.Divide
                    or MongoBinaryOperator.IntegerDivide or MongoBinaryOperator.Modulo
                } when IsArrayFreeComputedSubtree(leaf):
                break;

            // Gate 1c: conditional/date-part (`x.Flag ? x.Posts.Count : 0` needs the same subtree check).
            case MongoConditionalExpression or MongoDatePartExpression or MongoDateTimeOffsetLocalExpression
                when IsArrayFreeComputedSubtree(leaf):
                break;

            // Gate 1c2: Math/MathF (`Math.Abs(b.Posts.Count)` needs the same subtree check).
            case MongoMathExpression when IsArrayFreeComputedSubtree(leaf):
                break;

            // Gate 1c3: Trim/TrimStart/TrimEnd, Substring, Replace, and string FirstOrDefault/LastOrDefault; same subtree check.
            case MongoTrimExpression or MongoSubstringExpression or MongoReplaceExpression or MongoStringFirstOrLastExpression
                when IsArrayFreeComputedSubtree(leaf):
                break;

            // Gate 1c4: string predicates, Length/IndexOf, comparisons, and logical and/or/not, rendered as operator
            // documents. CanRender because a predicate node can hold shapes the aggregation dialect declines.
            case MongoRegexExpression or MongoStringLengthExpression or MongoStringIndexOfExpression
                    or MongoBinaryExpression or MongoUnaryExpression
                when (leaf is not (MongoBinaryExpression or MongoUnaryExpression) || IsProjectablePredicate(leaf))
                     && IsArrayFreeComputedSubtree(leaf) && MongoAggregationExpressionRenderer.CanRender(leaf):
                break;

            // Gate 1d: coalesce (`??`, rendered as $ifNull); the subtree check covers the whole right-nested chain.
            case MongoCoalesceExpression when IsArrayFreeComputedSubtree(leaf):
                break;

            // Gate 1e: constant/parameter. No subtree, and RenderProject $literal-wraps it.
            case MongoConstantExpression or MongoParameterExpression:
                break;

            default:
                return false;
        }

        alias = SyntheticBareProjectionAlias;
        return true;
    }

    /// <summary>
    /// Whether a comparison, logical and/or, or logical not can be projected as a value: its aggregation rendering
    /// must answer what .NET does, including for a null or missing field.
    /// </summary>
    /// <remarks>
    /// A lifted relational comparison (<c>x.NullableInt &lt; 5</c>) declines: .NET answers <see langword="false"/>
    /// for null, but <c>$lt</c> orders null/missing below every number and answers <see langword="true"/>. (The
    /// query dialect type-brackets, so the same node is exact in <c>$match</c>.) <c>$eq</c>/<c>$ne</c> against null
    /// are exact via the renderer's <c>$ifNull</c> wrap.
    /// </remarks>
    private static bool IsProjectablePredicate(MongoExpression expression)
        => expression switch
        {
            MongoBinaryExpression
            {
                Operator: MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual
                or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual
            } relational
                => Nullable.GetUnderlyingType(relational.Left.Type) is null
                    && Nullable.GetUnderlyingType(relational.Right.Type) is null,
            MongoBinaryExpression { Operator: MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual } => true,
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } logical
                => IsProjectablePredicateOperand(logical.Left) && IsProjectablePredicateOperand(logical.Right),
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not } not => IsProjectablePredicateOperand(not.Operand),
            _ => false
        };

    // Operands of and/or/not: nested predicates are held to the same rule; other nodes (regex, bare bool field,
    // constants) are left to CanRender.
    private static bool IsProjectablePredicateOperand(MongoExpression operand)
        => operand is not (MongoBinaryExpression or MongoUnaryExpression) || IsProjectablePredicate(operand);

    /// <summary>
    /// Whether a bare size leaf's un-stripped driver fallback cannot abort on a missing or null array — gate 1a's
    /// precondition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unfiltered arm asks the rewrite's own matcher (<see cref="TryMatchRewritableBareCountBody"/>) rather than
    /// restating it; restatements have drifted before. That covers navigation rooting (non-dotted path) and
    /// empty-collection constructibility, e.g. an <c>ISet&lt;Post&gt;</c> navigation declines (pinned by
    /// <c>NativeComputedBareProjectionTests.Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly</c>).
    /// The captured-chain shape can't be checked at bind time, so read the result as "safe as far as the selector can
    /// tell".
    /// </para>
    /// <para>
    /// Reference-collection counts read a <c>$lookup</c> output, which is always an array; filtered counts render
    /// <c>$sum</c>/<c>$map</c>, which tolerates a missing array (still held to non-dotted paths). A count under a
    /// reducing operator (<c>.Distinct()</c>, <c>.Sum()</c>) escapes the rewrite but is coalesced by
    /// <c>MongoEFToLinqTranslatingExpressionVisitor.TryRewriteEmbeddedCollectionNavigationCount</c>. Every decline is
    /// fail-closed.
    /// </para>
    /// </remarks>
    private static bool IsFallbackSafeBareSizeLeaf(
        MongoExpression leaf, LambdaExpression selector, List<LookupExpression> pendingLookups)
        => leaf switch
        {
            // Keyed on the lookup this leaf's own translation registered, so read and write can't drift apart.
            MongoSizeExpression lookupSize
                when pendingLookups.Exists(l => l.As == lookupSize.FieldName) => true,

            // $sum/$map tolerates a missing array; held to the non-dotted rule anyway.
            MongoFilteredSizeExpression filtered => !filtered.ArrayPath.Contains('.'),

            // Everything else must be within the rewrite's reach, as decided by its own matcher.
            MongoSizeExpression => TryMatchRewritableBareCountBody(selector, out _, out _),

            _ => false
        };

    /// <summary>
    /// Whether every node in <paramref name="expression"/> renders without touching an array. An allow-list, so unknown
    /// node kinds — and the size kinds — fail closed.
    /// </summary>
    /// <remarks>
    /// Needed because a top-node gate isn't enough: <c>b.Posts.Count * 2</c> has an arithmetic top node, but its
    /// un-stripped driver fallback renders a bare <c>$size</c> that aborts on a missing/null array, and <see
    /// cref="NullCoalesceSyntheticBareCountBody"/> only rewrites a body that is the count.
    /// </remarks>
    internal static bool IsArrayFreeComputedSubtree(MongoExpression expression)
        => expression switch
        {
            MongoBinaryExpression binary
                => IsArrayFreeComputedSubtree(binary.Left) && IsArrayFreeComputedSubtree(binary.Right),
            MongoConvertExpression convert => IsArrayFreeComputedSubtree(convert.Operand),
            MongoConcatExpression concat => concat.Operands.All(IsArrayFreeComputedSubtree),
            MongoConditionalExpression conditional
                => IsArrayFreeComputedSubtree(conditional.Test)
                    && IsArrayFreeComputedSubtree(conditional.IfTrue)
                    && IsArrayFreeComputedSubtree(conditional.IfFalse),
            MongoCoalesceExpression coalesce
                => IsArrayFreeComputedSubtree(coalesce.Left) && IsArrayFreeComputedSubtree(coalesce.Right),
            MongoDatePartExpression datePart => IsArrayFreeComputedSubtree(datePart.Operand),
            MongoDateTimeOffsetLocalExpression local => IsArrayFreeComputedSubtree(local.Operand),
            MongoMathExpression math => math.Operands.All(IsArrayFreeComputedSubtree),
            MongoTrimExpression trim
                => IsArrayFreeComputedSubtree(trim.Source) && (trim.Chars is null || IsArrayFreeComputedSubtree(trim.Chars)),
            MongoSubstringExpression s => IsArrayFreeComputedSubtree(s.Source) && IsArrayFreeComputedSubtree(s.Start)
                && (s.Length is null || IsArrayFreeComputedSubtree(s.Length)),
            MongoReplaceExpression r => IsArrayFreeComputedSubtree(r.Input) && IsArrayFreeComputedSubtree(r.Find)
                && IsArrayFreeComputedSubtree(r.Replacement),
            MongoStringCompareExpression cmp => IsArrayFreeComputedSubtree(cmp.Left) && IsArrayFreeComputedSubtree(cmp.Right),
            MongoStringFirstOrLastExpression firstOrLast => IsArrayFreeComputedSubtree(firstOrLast.Source),
            MongoRegexExpression r => IsArrayFreeComputedSubtree(r.Field) && IsArrayFreeComputedSubtree(r.Term),
            MongoStringLengthExpression l => IsArrayFreeComputedSubtree(l.Operand),
            MongoStringIndexOfExpression io => IsArrayFreeComputedSubtree(io.Haystack) && IsArrayFreeComputedSubtree(io.Needle)
                && (io.Start is null || IsArrayFreeComputedSubtree(io.Start)),
            MongoUnaryExpression u => IsArrayFreeComputedSubtree(u.Operand),
            // A regex Field can be a Distinct alias ref. Applies to every gate using this check: an element ref is a
            // plain path read (never $size), so it can't abort a fallback on a missing array.
            MongoElementRefExpression => true,
            MongoFieldExpression or MongoConstantExpression or MongoParameterExpression => true,
            // A $type/$isNumber test on its field; never touches an array.
            MongoNumericTypeBracketExpression => true,
            // Includes the size kinds, deliberately excluded by the catch-all.
            _ => false
        };

    /// <summary>
    /// Recognizes a projected collection-navigation <c>Count</c>/<c>LongCount</c> leaf
    /// (<c>select new { ..., OrderCount = c.Orders.Count }</c>) inside a terminal projection.
    /// </summary>
    /// <remarks>
    /// Nav-expansion rewrites <c>c.Orders.Count</c> to <c>Queryable.Count(Queryable.Where(DbSet&lt;Target&gt;(),
    /// predicate))</c>, resolved here against <paramref name="outerParameter"/> because this binder runs before shaper
    /// substitution. The correlation predicate is matched structurally (see <see cref="NativeCorrelationMatcher"/>); an
    /// extra conjunct (<c>c.Orders.Where(o =&gt; o.Amount &gt; 5).Count()</c>) nests the pair deeper, so the match
    /// fails and the projection falls back rather than emitting a wrong count.
    /// </remarks>
    private static bool TryTranslateProjectedCollectionCount(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression leafExpression,
        List<LookupExpression> pendingLookups,
        List<LookupExpression> pendingBareCountStamps,
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

        if (!NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation(
                mongoQ, outerParameter, whereArg, out var navigation))
        {
            return false;
        }

        if (!NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup(
                mongoQ, navigation, pendingLookups, leafExpression.Type, out var lookupToStamp, out var sizeExpression))
        {
            return false;
        }

        // Staged: a later leaf may still decline the whole projection.
        if (lookupToStamp is not null)
            pendingBareCountStamps.Add(lookupToStamp);

        result = sizeExpression;
        return true;
    }

    /// <summary>
    /// Recognizes a projected reference-collection-navigation list leaf — <c>Orders = c.Orders.ToList()</c> (or
    /// <c>ToArray</c>/<c>ToHashSet</c>, optionally with a ThenInclude <c>Select</c>), lowered to
    /// <c>Enumerable.ToList(Queryable.Where(DbSet&lt;Target&gt;(), joinPredicate))</c>. The shaper is built by <see
    /// cref="Visitors.MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigation"/>; this only marks
    /// the member representable and retains the <c>$lookup</c> field through <c>$project</c>.
    /// </summary>
    /// <remarks>
    /// Accepts a strict subset of what the bind side accepts, declining filtered-Include shapes. For a navigation
    /// declared as <c>List&lt;T&gt;</c>, only <c>ToList()</c> reaches this shape; <c>ToArray()</c>/<c>ToHashSet()</c>
    /// lower to <c>MaterializeCollectionNavigationExpression</c> and fall back. An <c>ICollection&lt;T&gt;</c>-declared
    /// navigation has no such gap.
    /// </remarks>
    private static bool TryTranslateProjectedCollectionNavigationList(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression leafExpression,
        List<LookupExpression> pendingLookups,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;

        // Unwrap the materializing terminal operator (ToList / ToArray / ToHashSet).
        if (leafExpression is not MethodCallExpression
            {
                Method: { DeclaringType: var materializeDeclaring } materializeMethod,
                Arguments: [var materializeArg]
            }
            || materializeDeclaring != typeof(Enumerable)
            || materializeMethod.Name is not (nameof(Enumerable.ToList) or nameof(Enumerable.ToArray) or nameof(Enumerable.ToHashSet)))
        {
            return false;
        }

        // Peel the optional Select a ThenInclude adds; the bind side validates it.
        var whereArg = materializeArg;
        if (whereArg is MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring }
            } selectCall
            && selectDeclaring == typeof(Queryable))
        {
            whereArg = selectCall.Arguments[0];
        }

        // The source must be exactly the navigation-join Where; anything layered on top (filtered Include) declines.
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
        {
            return false;
        }

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false,
                out var matchedNavigation))
        {
            return false;
        }

        var lookup = new LookupExpression(matchedNavigation);
        if (!lookup.IsNativeCollectionLookup)
        {
            // TPH-derived target (discriminator $match staged, FallbackOnly), as for the Count leaf.
            return false;
        }

        // Alias collision, as in TryTranslateProjectedCollectionCount: a bare Include lookup for the same nav is
        // reused, but one with a different PipelineKind (filtered Include) declines rather than silently reading the
        // wrong shape.
        var collidingLookup = pendingLookups.FirstOrDefault(l => l.As == lookup.As)
            ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }

        navigation = matchedNavigation;
        return true;
    }

    /// <summary>
    /// Recognizes a reference-collection-nav reducer projected inline
    /// (<c>a.IdentificationMethods.FirstOrDefault().Method</c>), staging its <c>$lookup</c> sub-pipeline and <see
    /// cref="MongoCorrelatedReducerLeaf"/>, and returning a <see cref="MongoElementRefExpression"/> that reads the
    /// reduced member off the unwound result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matches the nav-expanded shape (outermost first), where the navigation member is gone and is resolved from the
    /// FK correlation via <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/>:
    /// </para>
    /// <code>
    /// Queryable.FirstOrDefault(                                   // or .First()
    ///   Queryable.Select(                                         // the reduced member — mandatory
    ///     [Queryable.Where(                                       // only for FirstOrDefault(pred)
    ///       [Queryable.OrderBy|OrderByDescending(                 // only for an explicit sort
    ///         Queryable.Where(EntityQueryRootExpression&lt;Target&gt;, fkCorrelation),
    ///         keySelector)],
    ///       predicate)],
    ///     e =&gt; e.Member))
    /// </code>
    /// <para>
    /// Emits <c>$match</c>, <c>$sort</c>, <c>$limit: 1</c>; filter-then-sort picks the same first row as the tree's
    /// order.
    /// </para>
    /// <para>
    /// There is no driver-LINQ oracle for this family, so each decline surfaces as EF's "could not be translated".
    /// Declines:
    /// </para>
    /// <list type="bullet">
    /// <item>A parameterized predicate: <see cref="LookupExpression.PipelineStages"/> is rendered once with no
    /// placeholder substitution, so a parameter would reach the server as a sentinel. Checked by
    /// <see cref="IsConstantOnlyPredicate"/> and by asserting the <see cref="PlaceholderTable"/> stayed empty.</item>
    /// <item>A TPH-derived target (the lookup constructor would add a discriminator <c>$match</c> and
    /// <see cref="LookupPipelineKind.FallbackOnly"/>, which the initializer would silently overwrite).</item>
    /// <item>Two-hop chains and non-scalar reduced members (they arrive with a <c>Queryable.Join</c>),
    /// owned collections (not an <see cref="EntityQueryRootExpression"/>), and whole-element reductions (no inner
    /// <c>Select</c>).</item>
    /// <item>Anything referencing <paramref name="outerParameter"/>: the sub-pipeline runs in the foreign collection's
    /// scope, and <c>a.Id</c> would silently resolve against the target's own <c>Id</c>.</item>
    /// <item>A second reducer over the same navigation: <see cref="MongoQueryExpression.AddLookup"/> dedupes by
    /// <see cref="LookupExpression.As"/>, silently dropping the second sub-pipeline.</item>
    /// <item><c>First()</c> over a nullable member: the read side can't tell "no row" from "row with null member", so
    /// it could throw "Sequence contains no elements" for a row that exists.</item>
    /// </list>
    /// </remarks>
    private static bool TryGetCorrelatedReducerLeaf(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression leafExpression,
        string alias,
        List<LookupExpression> pendingLookups,
        List<MongoCorrelatedReducerLeaf> pendingReducerLeaves,
        [NotNullWhen(true)] out MongoElementRefExpression? result)
    {
        result = null;

        // Normalize nav-expansion's nullable-widened FirstOrDefault() over a non-nullable value-type member:
        //
        //   Convert(DbSet<M>().Where(fk).Select(m => Convert(m.Rank, int?)).FirstOrDefault(), int)
        //
        // Both Converts are peeled so the gates below run on the normalized tree. Both halves must be present: a
        // user-written `(int)a.Nav.FirstOrDefault()!.NullableRank` has the same outer Convert but no inner one, and
        // admitting it would read 0 where LINQ throws for a matched null. When no row matches, the read side
        // (MongoProjectionBindingRemovingExpressionVisitor.IsDefaultOnEmptyCorrelatedReducerLeaf) maps the absent field
        // to default(T). The leaf keeps the outer, non-nullable type.
        var leafType = leafExpression.Type;
        var reducerNode = leafExpression;
        var nullableWidened = false;
        if (leafExpression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                Type: { IsValueType: true } narrowedType,
                Operand: MethodCallExpression
                {
                    Method: { Name: nameof(Queryable.FirstOrDefault), DeclaringType: var widenedDeclaring }
                } widenedReducer
            }
            && widenedDeclaring == typeof(Queryable)
            // Nullable<Nullable<T>> is impossible, so narrowedType is not itself nullable.
            && Nullable.GetUnderlyingType(widenedReducer.Type) == narrowedType)
        {
            nullableWidened = true;
            reducerNode = widenedReducer;
        }

        // The reducer: First/FirstOrDefault, no-predicate overload only (nav-expansion hoists predicates into a Where).
        if (reducerNode is not MethodCallExpression
            {
                Method: { DeclaringType: var reducerDeclaring } reducerMethod,
                Arguments: [var reducerSource]
            }
            || reducerDeclaring != typeof(Queryable))
        {
            return false;
        }

        bool throwOnEmpty;
        switch (reducerMethod.Name)
        {
            case nameof(Queryable.First):
                throwOnEmpty = true;
                break;
            case nameof(Queryable.FirstOrDefault):
                throwOnEmpty = false;
                break;
            default:
                return false;
        }

        // ── The mandatory inner Select carrying the reduced member. ──────────────────────────────────────────
        if (reducerSource is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring },
                Arguments: [var selectSource, var memberSelectorArg]
            }
            || selectDeclaring != typeof(Queryable))
        {
            return false;
        }

        var memberSelector = memberSelectorArg.UnwrapLambdaFromQuote();
        if (memberSelector is not { Parameters: [var elementParameter] })
        {
            return false;
        }

        // Inner half of the normalization: peel `Convert(m.Member, Nullable<T>)`, only when the outer peel fired.
        var memberBody = memberSelector.Body;
        if (nullableWidened
            && memberBody is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                Operand: var widenedMemberBody
            } widenedMemberConvert
            && Nullable.GetUnderlyingType(widenedMemberConvert.Type) == widenedMemberBody.Type)
        {
            memberBody = widenedMemberBody;
        }
        else if (nullableWidened)
        {
            // Outer Convert without inner: a user narrowing cast over a nullable member (see above). Decline.
            return false;
        }

        // ── The optional Where(predicate) and OrderBy/OrderByDescending layers, in the observed order. ───────
        var source = selectSource;
        LambdaExpression? predicate = null;
        if (source is MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var predWhereDeclaring },
                Arguments: [var predWhereSource, var predicateArg]
            }
            && predWhereDeclaring == typeof(Queryable)
            && predicateArg.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } predicateLambda
            // Skip the FK-correlation Where itself, or a bare `nav.FirstOrDefault().Member` would lose its correlation.
            && predWhereSource is not EntityQueryRootExpression)
        {
            predicate = predicateLambda;
            source = predWhereSource;
        }

        LambdaExpression? sortKeySelector = null;
        var sortAscending = true;
        if (source is MethodCallExpression
            {
                Method:
                {
                    Name: nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending),
                    DeclaringType: var orderDeclaring
                } orderMethod,
                Arguments: [var orderSource, var sortKeyArg]
            }
            && orderDeclaring == typeof(Queryable)
            && sortKeyArg.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } sortKeyLambda)
        {
            sortAscending = orderMethod.Name == nameof(Queryable.OrderBy);
            sortKeySelector = sortKeyLambda;
            source = orderSource;
        }

        // ── What remains must be the FK-correlation Where over the target DbSet. ─────────────────────────────
        if (source is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var fkWhereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var fkPredicateArg]
            }
            || fkWhereDeclaring != typeof(Queryable)
            || fkPredicateArg.UnwrapLambdaFromQuote() is not { Parameters.Count: 1 } fkPredicate)
        {
            return false;
        }

        var targetEntityType = rootExpression.EntityType;
        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                fkPredicate.Body, mongoQ.CollectionExpression.EntityType, outerParameter, targetEntityType,
                requireEmbedded: false, out var navigation))
        {
            return false;
        }

        // TPH-derived target: the LookupExpression constructor would add a discriminator $match (FallbackOnly).
        if (targetEntityType.FindDiscriminatorProperty() is not null
            && targetEntityType != targetEntityType.GetRootType())
        {
            return false;
        }

        // ── Nothing in the sub-pipeline may reach back out to the enclosing document. ─────────────────────────
        if (memberBody.ReferencesParameter(outerParameter)
            || (predicate is not null && predicate.Body.ReferencesParameter(outerParameter))
            || (sortKeySelector is not null && sortKeySelector.Body.ReferencesParameter(outerParameter)))
        {
            return false;
        }

        // ── The reduced member must be a plain, default-serialized, top-level scalar of the target. ──────────
        var elementTranslator = new MongoExpressionTranslator(targetEntityType, elementParameter);
        if (!IsDirectMemberAccessOn(memberBody, elementParameter)
            || !elementTranslator.TryTranslateField(memberBody, out var memberField)
            || memberField.ElementName.Contains('.')
            // Read back by alias with no IProperty, so value converters/non-default representations can't be honored.
            || !NativeGroupByBinder.HasDefaultKeySerialization(memberField.Property))
        {
            return false;
        }

        // First() over a nullable member: the read side's throw-on-empty check can't distinguish "no related row" from
        // "row whose member is null", and would throw "Sequence contains no elements" for a row that exists.
        // FirstOrDefault() is unaffected.
        if (throwOnEmpty && memberField.Property.ClrType.IsNullableType())
        {
            return false;
        }

        var lookup = new LookupExpression(navigation) { PipelineKind = LookupPipelineKind.CorrelatedReducer };

        // Fail closed structurally: the initializer above silently overwrites a constructor-chosen FallbackOnly kind
        // (e.g. the TPH discriminator $match), which would leave an unaccounted stage ahead of ours. The TPH gate
        // covers today's case; this doesn't depend on ordering.
        if (lookup.PipelineStages.Count > 0)
        {
            return false;
        }

        // AddLookup dedupes by As (first wins), which would silently drop this leaf's sub-pipeline. The mirror case is
        // guarded in TryTranslateProjectedCollectionCount.
        if (pendingLookups.Exists(l => l.As == lookup.As)
            || mongoQ.GetPendingLookups().Any(l => l.As == lookup.As))
        {
            return false;
        }

        if (predicate is not null)
        {
            var predicateTranslator = new MongoExpressionTranslator(targetEntityType, predicate.Parameters[0]);
            if (!predicateTranslator.TryTranslate(predicate.Body, out var predicateNode)
                || !IsConstantOnlyPredicate(predicateNode))
            {
                return false;
            }

            var placeholders = new PlaceholderTable();
            var matchBody = new MongoQueryLanguageRenderer().Render(predicateNode, placeholders);
            if (placeholders.Entries.Count > 0 || matchBody is not BsonDocument matchDoc)
            {
                // Defensive: IsConstantOnlyPredicate should already have declined anything parameterized.
                return false;
            }

            lookup.PipelineStages.Add(new BsonDocument("$match", matchDoc));
        }

        if (sortKeySelector is not null)
        {
            if (!IsDirectMemberAccessOn(sortKeySelector.Body, sortKeySelector.Parameters[0])
                || !new MongoExpressionTranslator(targetEntityType, sortKeySelector.Parameters[0])
                    .TryTranslateField(sortKeySelector.Body, out var sortField)
                || sortField.ElementName.Contains('.')
                // $sort uses the stored representation; a converted key (e.g. enum stored as string) may sort
                // differently from the CLR order and silently pick a different "first" row.
                || !NativeGroupByBinder.HasDefaultKeySerialization(sortField.Property))
            {
                return false;
            }

            lookup.PipelineStages.Add(
                new BsonDocument("$sort", new BsonDocument(sortField.ElementName, sortAscending ? 1 : -1)));
        }

        lookup.PipelineStages.Add(new BsonDocument("$limit", 1));

        pendingLookups.Add(lookup);
        pendingReducerLeaves.Add(
            new MongoCorrelatedReducerLeaf(alias, lookup, memberField.ElementName, throwOnEmpty));

        result = new MongoElementRefExpression(
            $"{lookup.As}.{memberField.ElementName}", leafType, valueProperty: memberField.Property);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> is a direct single-hop member read (<c>e.Member</c> or <c>EF.Property(e,
    /// "Member")</c>) off <paramref name="parameter"/>, matched by identity.
    /// </summary>
    private static bool IsDirectMemberAccessOn(Expression expression, ParameterExpression parameter)
        => expression switch
        {
            MemberExpression { Expression: ParameterExpression p } => ReferenceEquals(p, parameter),
            MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [ParameterExpression p, ConstantExpression { Value: string }]
                => ReferenceEquals(p, parameter),
            _ => false
        };

    /// <summary>
    /// Whether a translated predicate renders to fully baked BSON (no <see cref="MongoParameterExpression"/>).
    /// </summary>
    /// <remarks>
    /// An allow-list, so unknown node kinds decline: the result is baked into <see
    /// cref="LookupExpression.PipelineStages"/>, which has no per-execution placeholder substitution.
    /// </remarks>
    private static bool IsConstantOnlyPredicate(MongoExpression expression)
        => expression switch
        {
            MongoBinaryExpression binary
                => IsConstantOnlyPredicate(binary.Left) && IsConstantOnlyPredicate(binary.Right),
            MongoUnaryExpression unary => IsConstantOnlyPredicate(unary.Operand),
            MongoConvertExpression convert => IsConstantOnlyPredicate(convert.Operand),
            MongoFieldExpression or MongoConstantExpression => true,
            // Everything else, including MongoParameterExpression.
            _ => false
        };
}
