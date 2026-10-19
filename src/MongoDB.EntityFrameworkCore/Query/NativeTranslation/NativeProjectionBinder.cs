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
        // Any leaf is row-independent and evaluated client-side instead of projected (IsRowIndependentLeaf); committed
        // to HasClientEvaluatedProjectionLeaf.
        var hasClientEvaluatedLeaf = false;
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
        // Members (BareProjectionMemberKey for a bare body) whose leaf is a ternary over client-only constructions
        // (TryCollectClientConditionalBranches): only the test is staged, under the member's alias. Committed via
        // AddClientConditionalMember.
        var clientConditionalMembers = new List<string>();
        // The row reads inside those branches, keyed by binding-member name (to catch two different reads bound under
        // one member), and every member name the read side looks up while walking the branches; staged and checked
        // after the switch.
        var clientConditionalReads =
            new Dictionary<string, ClientConditionalRead>(StringComparer.OrdinalIgnoreCase);
        var clientConditionalMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The synthetic aliases those reads were staged under (ClientConditionalReadAliasPrefix); committed via
        // AddClientConditionalRead.
        var clientConditionalReadAliases = new List<string>();

        switch (selector.Body)
        {
            // Wrapped body: anonymous type/DTO via NewExpression-with-Members or MemberInit. A construction that fails
            // TryGetProjectionMembers falls through to the bare-body case.
            //
            // A MemberInit whose constructor arguments are all row-independent (`new Dto(param) { A = x.A }`) is
            // admitted too: only its bindings are members, and the arguments stay on the shaper, which evaluates them.
            case NewExpression or MemberInitExpression
                when selector.Body.TryGetProjectionMembers(
                    out var wrappedMembers, rowIndependentConstructorArgumentsOver: selector.Parameters[0]):
                hasClientEvaluatedLeaf |= selector.Body is MemberInitExpression { NewExpression.Arguments.Count: > 0 };
                foreach (var (memberName, member) in wrappedMembers)
                {
                    var memberValue = PeelCaseMapping(member);
                    hasCaseMappingLeaf |= !ReferenceEquals(memberValue, member);
                    var alias = DeriveWrappedLeafAlias(mongoQ, selector.Parameters[0], memberValue, memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], memberValue, alias, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var leaf, out var isArrayLeaf, out var isOwnedNavEntityLeaf, out var throwsOnNull, allowWholeRootEntityLeaf: true))
                    {
                        // Last resort, only after the leaf arms (including the $literal path, which keeps a renderable
                        // constant's MQL) declined: a row-independent leaf is evaluated by the shaper and never
                        // projected.
                        if (IsRowIndependentLeaf(member, selector.Parameters[0]))
                        {
                            hasClientEvaluatedLeaf = true;
                            continue;
                        }

                        // A ternary over client-only constructions (`X = c.City == "Seattle" ? new P { ... } : new P
                        // { ... }`): stage only the test, as this member's bool leaf, and let the shaper evaluate the
                        // conditional. Reached only after TryTranslateLeaf declined the whole ternary, which it does
                        // for construction branches its class map would misread
                        // (HasMisreadDocumentConstructionBranch).
                        if (memberValue is ConditionalExpression wrappedClientConditional
                            && TryCollectClientConditionalBranches(
                                translator, selector.Parameters[0], wrappedClientConditional, memberName,
                                clientConditionalReads, clientConditionalMemberNames)
                            && TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], wrappedClientConditional.Test,
                                alias, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var testLeaf,
                                out _, out _, out var testThrowsOnNull)
                            && TryStageLeaf(alias, testLeaf, wrappedClientConditional.Test, testThrowsOnNull))
                        {
                            clientConditionalMembers.Add(memberName);
                            hasClientEvaluatedLeaf = true;
                            continue;
                        }

                        return false;
                    }

                    if (!TryStageLeaf(alias, leaf, memberValue, throwsOnNull, isArrayLeaf, isOwnedNavEntityLeaf))
                        return false;
                    // The nav-entity leaf always registers a DocumentPath override, even though its alias equals the
                    // member name: the late-fallback strip it triggers is what supplies the retained _id.
                    if (alias != memberName || isOwnedNavEntityLeaf)
                        namedAliasOverrides.Add((memberName, alias));
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
            //
            // A whole-entity argument (`new KeyValuePair<Customer, string>(c, c.City)`) is never a scalar positional
            // leaf: the `$$ROOT` read of it would be a driver class-map deserialization of the raw document, which
            // throws FormatException ("Element '_id' does not match ...") or yields an untracked instance. Such a body
            // falls through to the default arm's whole-entity client construction (IsScalarPositionalConstruction).
            case NewExpression { Members: null, Arguments: { Count: > 1 } } when
                selector.Body.TryGetProjectionMembers(out var positionalMembers, allowPositionalConstructorArguments: true)
                && IsScalarPositionalConstruction(mongoQ, selector.Body, positionalMembers, selector.Parameters[0]):
                foreach (var (memberName, member) in positionalMembers)
                {
                    var memberValue = PeelCaseMapping(member);
                    hasCaseMappingLeaf |= !ReferenceEquals(memberValue, member);
                    var alias = DeriveWrappedLeafAlias(mongoQ, selector.Parameters[0], memberValue, memberName);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], memberValue, alias, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var leaf, out var isArrayLeaf, out var isOwnedNavEntityLeaf, out var throwsOnNull, allowWholeRootEntityLeaf: true))
                        return false;
                    if (!TryStageLeaf(alias, leaf, memberValue, throwsOnNull, isArrayLeaf, isOwnedNavEntityLeaf))
                        return false;
                    if (alias != memberName || isOwnedNavEntityLeaf)
                        namedAliasOverrides.Add((memberName, alias));
                }

                hasPositionalCtorProjection = true;
                break;

            // Positional container of scalar elements — `new[] { x.I, x.NullableI }`, `new object[] { x.I, x.S }`
            // (TryGetProjectionMembers' allowContainerElements; not `new List<object> { ... }`, see there). Each
            // element is one `_ctorArg<N>` leaf, bound by index through the positional-ctor shaper
            // (HasPositionalCtorProjectionShaper).
            // A boxing Convert-to-object is peeled (UnwrapContainerElementBoxing) so the element is translated and read
            // with its operand's own type; the read side re-boxes it, keeping each element's runtime type.
            //
            // Disjoint from the neighbouring shapes: a container whose elements are all row-independent is
            // IsRowIndependentLeaf's (default arm), and one with a whole-entity element, or one the whole-entity
            // client construction claims, is NativeClientWholeEntityShape's (IsScalarPositionalConstruction). Elements
            // are bare scalar leaves only (allowWholeRootEntityLeaf: false): no `$$ROOT`, owned-nav entity, nested
            // construction or collection-navigation leaf is read by a synthetic alias.
            case NewArrayExpression when
                !IsRowIndependentLeaf(selector.Body, selector.Parameters[0])
                && selector.Body.TryGetProjectionMembers(out var containerElements, allowContainerElements: true)
                && IsScalarPositionalConstruction(mongoQ, selector.Body, containerElements, selector.Parameters[0]):
                foreach (var (elementName, element) in containerElements)
                {
                    var elementOperand = UnwrapContainerElementBoxing(element);
                    var elementValue = PeelCaseMapping(elementOperand);
                    hasCaseMappingLeaf |= !ReferenceEquals(elementValue, elementOperand);
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], elementValue, elementName, pendingLookups, pendingReducerLeaves, pendingBareCountStamps, out var leaf, out var isArrayLeaf, out _, out var throwsOnNull))
                        return false;
                    if (!TryStageLeaf(elementName, leaf, elementValue, throwsOnNull, isArrayLeaf))
                        return false;
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
                    // A row-independent body the $literal path can't render (`x => new { }`, or a captured value EF
                    // folded the whole body into, as in `x => new { f = closure }`): the shaper evaluates it and
                    // nothing is projected for it but the sentinel below. Tried only after the bare arms declined.
                    if (IsRowIndependentLeaf(selector.Body, selector.Parameters[0]))
                    {
                        hasClientEvaluatedLeaf = true;
                        break;
                    }

                    // A ternary over client-only constructions (`c.City == "Seattle" ? new P { ... } : new P { ... }`):
                    // the test is staged as the bare bool leaf and the shaper evaluates the conditional, as in the
                    // wrapped arm. Disjoint from the whole-entity client construction below: these branches read no
                    // whole entity.
                    if (selector.Body is ConditionalExpression bareClientConditional
                        && TryCollectClientConditionalBranches(
                            translator, selector.Parameters[0], bareClientConditional, MongoSelectDefinition.BareProjectionMemberKey,
                            clientConditionalReads, clientConditionalMemberNames)
                        && TryBindAsBareProjection(
                            bareClientConditional.Test, BareLeafProvisionalAlias, allowWholeRootEntityLeafForThis: false))
                    {
                        clientConditionalMembers.Add(MongoSelectDefinition.BareProjectionMemberKey);
                        hasClientEvaluatedLeaf = true;
                        break;
                    }

                    // Untranslatable (e.g. embeds a client method, or constructs over the whole entity:
                    // `new object[] { x }`, `new Wrapper(x) { City = x.City }`). If every entity reference is the whole
                    // entity, fetch whole documents and evaluate the body client-side, as in the client-method arm above.
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
                    out var bareThrowsOnNull, allowWholeRootEntityLeafForThis))
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

            // Nothing is staged before the single bare leaf, so the alias is always fresh.
            // isOwnedNavEntityLeaf is discarded above: TryTranslateLeaf only produces it on the wrapped arm's
            // alias-equals-member-name path, never for a bare or ctor-wrap leaf.
            if (!TryStageLeaf(derivedAlias, bareLeaf, bareLikeExpr, bareThrowsOnNull, bareIsArrayLeaf))
            {
                return false;
            }

            bareProjectionAlias = derivedAlias;
            return true;
        }

        // Claims the alias (false on a case-insensitive collision) and stages one leaf, keeping leafIsArray and
        // leafIsOwnedNavEntity index-aligned with projections and folding the leaf into the has*Leaf flags.
        bool TryStageLeaf(
            string alias, MongoExpression leaf, Expression value, bool throwsOnNull,
            bool isArrayLeaf = false, bool isOwnedNavEntityLeaf = false)
        {
            if (!seenAliases.Add(alias))
            {
                return false;
            }

            projections.Add(new MongoProjection(alias, leaf, value, throwsOnNull));
            leafIsArray.Add(isArrayLeaf);
            hasArrayLeaf |= isArrayLeaf;
            leafIsOwnedNavEntity.Add(isOwnedNavEntityLeaf);
            hasOwnedNavEntityLeaf |= isOwnedNavEntityLeaf;
            hasStringSequenceLeaf |= value is MethodCallExpression stringSequenceCall
                                     && IsStringSequenceMaterializationCall(stringSequenceCall);
            return true;
        }

        // The row reads of client-evaluated ternaries. The read side binds each under the member path it walks
        // (`X.Name`) and takes its alias from the staged read itself (MongoSelectDefinition
        // .TryGetClientConditionalReadAlias), registered explicitly for that path. A name the walk enters must still be
        // no other leaf's alias (a nested construction is looked up by name, and a duplicate alias would be renamed by
        // AddToProjection's de-dup) and no member with an alias override. Kept away from the
        // array/owned-nav leaves too: their whole-document fallback and _id retention aren't reasoned for it.
        if (clientConditionalMembers.Count > 0)
        {
            if (hasArrayLeaf || hasOwnedNavEntityLeaf)
                return false;

            foreach (var memberName in clientConditionalMemberNames)
            {
                if (seenAliases.Contains(memberName)
                    || namedAliasOverrides.Exists(o => string.Equals(o.MemberName, memberName, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            // One staged projection per DISTINCT read: the read side looks each read's alias up structurally
            // (TryGetClientConditionalReadAlias), so two members reading the same field (`Id = x.Name, Name = x.Name`)
            // must resolve to one alias, which ApplyProjection then shares between them.
            var stagedReads = new List<Expression>();
            foreach (var read in clientConditionalReads.Values)
            {
                if (stagedReads.Exists(r => ExpressionEqualityComparer.Instance.Equals(r, read.Value)))
                    continue;

                stagedReads.Add(read.Value);
                var readAlias = ClientConditionalReadAliasPrefix + clientConditionalReadAliases.Count;
                if (!TryStageLeaf(readAlias, read.Field, read.Value, throwsOnNull: false))
                    return false;

                clientConditionalReadAliases.Add(readAlias);
            }
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

        // Every leaf is client-evaluated: stage a constant so Route stays Projection (one row per document) instead of
        // collapsing to WholeEntity. A constant rather than _id, so a Distinct over it dedups to the one row the
        // (execution-wide) client value calls for. See ClientEvaluatedSentinelAlias.
        if (hasClientEvaluatedLeaf && projections.Count == 0)
        {
            projections.Add(new MongoProjection(ClientEvaluatedSentinelAlias, new MongoConstantExpression(true, null)));
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
        if (hasClientEvaluatedLeaf)
            mongoQ.Select.HasClientEvaluatedProjectionLeaf = true;
        foreach (var clientConditionalMember in clientConditionalMembers)
            mongoQ.Select.AddClientConditionalMember(clientConditionalMember);
        foreach (var clientConditionalReadAlias in clientConditionalReadAliases)
            mongoQ.Select.AddClientConditionalRead(clientConditionalReadAlias);
        return true;
    }

    /// <summary>A row read inside a client-evaluated ternary's branch, staged under the binding member's name.</summary>
    /// <param name="Path">The member path the read side binds it under, to tell two reads sharing a last name apart.</param>
    /// <param name="Value">The read, as written (<c>x.Name</c>).</param>
    /// <param name="Field">Its translation, a plain top-level field.</param>
    private readonly record struct ClientConditionalRead(string Path, Expression Value, MongoFieldExpression Field);

    /// <summary>
    /// The shape rule for a ternary the shaper evaluates client-side over a projected test: each branch is a
    /// row-independent tree (<see cref="IsRowIndependentLeaf"/>) or a construction (<see cref="MemberInitExpression"/>
    /// over a row-independent <c>new</c>, or an anonymous <see cref="NewExpression"/>) whose members are such trees,
    /// such constructions, or a plain top-level field read (<c>Name = x.Name</c>). Collects the reads (keyed by the
    /// binding member's name) and every member name the read side will look up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read anywhere else (a constructor argument, an array element, under a <c>Convert</c>) declines: the read
    /// side binds a read under the member it is visited at, so it needs a member of its own. Two reads under one
    /// name must be the same read at the same path (<c>c ? new P { Name = x.A } : new P { Name = x.B }</c> would
    /// bind both to one member and read one as the other), so that declines too. The caller checks the collected
    /// names against the projection's other aliases.
    /// </para>
    /// <para>
    /// The test itself is the caller's: it is staged as an ordinary bool leaf of the member, so both readers bind it
    /// exactly as they bind <c>new { X = test }</c>. The read side admits the ternary only through
    /// <c>MongoSelectDefinition.IsClientConditionalLeaf</c>, which matches that staged test.
    /// </para>
    /// </remarks>
    private static bool TryCollectClientConditionalBranches(
        MongoExpressionTranslator translator,
        ParameterExpression selectorParameter,
        ConditionalExpression conditional,
        string path,
        Dictionary<string, ClientConditionalRead> reads,
        HashSet<string> memberNames)
    {
        return TryCollectBranch(conditional.IfTrue, path)
               && TryCollectBranch(conditional.IfFalse, path);

        bool TryCollectBranch(Expression node, string nodePath)
        {
            if (IsRowIndependentLeaf(node, selectorParameter))
            {
                CollectRowIndependentMemberNames(node);
                return true;
            }

            switch (node)
            {
                case MemberInitExpression memberInit when IsRowIndependentLeaf(memberInit.NewExpression, selectorParameter):
                    foreach (var binding in memberInit.Bindings)
                    {
                        if (binding is not MemberAssignment assignment
                            || !TryCollectMember(assignment.Member.Name, assignment.Expression, nodePath))
                            return false;
                    }

                    return true;

                case NewExpression { Members: { } newMembers } anonymous when newMembers.Count == anonymous.Arguments.Count:
                    for (var i = 0; i < newMembers.Count; i++)
                    {
                        if (!TryCollectMember(newMembers[i].Name, anonymous.Arguments[i], nodePath))
                            return false;
                    }

                    return true;

                default:
                    return false;
            }
        }

        bool TryCollectMember(string memberName, Expression value, string ownerPath)
        {
            AddMemberNameUnlessScalar(memberName, value);
            var memberPath = ownerPath + "." + memberName;
            if (!IsPlainTopLevelFieldRead(translator, value, out var field))
                return TryCollectBranch(value, memberPath);

            memberNames.Add(memberName);

            if (reads.TryGetValue(memberName, out var existing))
                return string.Equals(existing.Path, memberPath, StringComparison.Ordinal)
                       && ExpressionEqualityComparer.Instance.Equals(existing.Value, value);

            reads[memberName] = new ClientConditionalRead(memberPath, value, field);
            return true;
        }

        // The read side looks a member up by name only when its value is a row read (bound by name) or a construction
        // (checked against the staged sub-document constructions by name); a row-independent scalar value (constant,
        // parameter, member of a constant, under Converts) is evaluated in place without a lookup.
        void AddMemberNameUnlessScalar(string memberName, Expression value)
        {
            if (value.RemoveConvert() is not (ConstantExpression or ParameterExpression or MemberExpression { Expression: ConstantExpression })
#if !EF8 && !EF9
                && value.RemoveConvert() is not QueryParameterExpression
#endif
               )
                memberNames.Add(memberName);
        }

        // The read side still walks a row-independent construction's members (entering each), so their names count
        // (see AddMemberNameUnlessScalar).
        void CollectRowIndependentMemberNames(Expression node)
        {
            switch (node)
            {
                case MemberInitExpression memberInit:
                    CollectRowIndependentMemberNames(memberInit.NewExpression);
                    foreach (var binding in memberInit.Bindings)
                    {
                        if (binding is MemberAssignment assignment)
                        {
                            AddMemberNameUnlessScalar(binding.Member.Name, assignment.Expression);
                            CollectRowIndependentMemberNames(assignment.Expression);
                        }
                    }

                    break;
                case NewExpression newExpression:
                    for (var i = 0; i < newExpression.Arguments.Count; i++)
                    {
                        if (newExpression.Members is { } members && i < members.Count)
                            AddMemberNameUnlessScalar(members[i].Name, newExpression.Arguments[i]);
                        CollectRowIndependentMemberNames(newExpression.Arguments[i]);
                    }

                    break;
                case NewArrayExpression newArray:
                    foreach (var element in newArray.Expressions)
                        CollectRowIndependentMemberNames(element);
                    break;
                case ListInitExpression listInit:
                    CollectRowIndependentMemberNames(listInit.NewExpression);
                    foreach (var initializer in listInit.Initializers)
                    foreach (var argument in initializer.Arguments)
                        CollectRowIndependentMemberNames(argument);
                    break;
                case UnaryExpression unary:
                    CollectRowIndependentMemberNames(unary.Operand);
                    break;
            }
        }
    }

    /// <summary>
    /// A plain top-level, default-serialized scalar field read (<c>x.Name</c> or <c>EF.Property</c>): the member rule
    /// of a constructed sub-entity leaf (<see cref="TryGetDocumentConstructionLeaf"/>) and of a row read inside a
    /// client-evaluated ternary's branch (<see cref="TryCollectClientConditionalBranches"/>). Such a read resolves off a
    /// whole document by its property too, so a late fallback's mixed reader reads it correctly.
    /// </summary>
    private static bool IsPlainTopLevelFieldRead(
        MongoExpressionTranslator translator, Expression value, [NotNullWhen(true)] out MongoFieldExpression? field)
    {
        return TryTranslatePlainField(translator, value, out field)
               && !field.ElementName.Contains('.')
               && NativeGroupByBinder.HasDefaultKeySerialization(field.Property);
    }

    /// <summary>
    /// Translates a plain member read (<c>x.Name</c> or <c>EF.Property(x, "Name")</c>) to its field;
    /// <see cref="MongoExpressionTranslator.TryTranslateField"/> decides whether it is a real field. Any other node
    /// kind declines without being offered to the translator.
    /// </summary>
    private static bool TryTranslatePlainField(
        MongoExpressionTranslator translator, Expression expression, [NotNullWhen(true)] out MongoFieldExpression? field)
    {
        field = null;
        return (expression is MemberExpression
                || (expression is MethodCallExpression efPropertyCall && efPropertyCall.Method.IsEFPropertyMethod()))
               && translator.TryTranslateField(expression, out field);
    }

    /// <summary>
    /// Whether a translated value's result is (or may be, through a nested ternary or coalesce) a
    /// <see cref="MongoDocumentConstructionExpression"/> that an alias read of the whole value would misread.
    /// </summary>
    /// <remarks>
    /// Such a value is read back whole through the constructed type's serializer (a driver class map), while the
    /// native sub-document is keyed by member names. When the class map names an element differently (by convention
    /// <c>Id</c> maps to <c>_id</c>), the read throws <c>FormatException</c>
    /// (<c>BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames</c> asks the serializer the reader uses). A
    /// ternary over such constructions is then not projected whole; <see cref="TryCollectClientConditionalBranches"/>
    /// evaluates it client-side instead. One whose class map agrees (<c>new MyStruct { X = ..., Y = ... }</c>) stays a
    /// server-side <c>$cond</c>.
    /// </remarks>
    internal static bool HasMisreadDocumentConstructionBranch(MongoExpression value)
        => value switch
        {
            MongoDocumentConstructionExpression construction
                => !Serializers.BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(
                       construction.Type, construction.Members.Select(m => m.MemberName))
                   || construction.Members.Any(m => HasMisreadDocumentConstructionBranch(m.Value)),
            MongoConditionalExpression conditional
                => HasMisreadDocumentConstructionBranch(conditional.IfTrue)
                   || HasMisreadDocumentConstructionBranch(conditional.IfFalse),
            MongoCoalesceExpression coalesce
                => HasMisreadDocumentConstructionBranch(coalesce.Left) || HasMisreadDocumentConstructionBranch(coalesce.Right),
            _ => false
        };

    /// <summary>
    /// Whether a translated projection leaf that the read side reads back whole by its alias (a ternary or coalesce)
    /// would be misread: <see cref="HasMisreadDocumentConstructionBranch"/> over a ternary/coalesce value. The single
    /// gate every binder staging such a leaf calls (this binder, <c>NativeJoinScopeProjectionBinder</c>'s computed and
    /// null-check-ternary leaves, the bare join-scope value leaf, <c>NativeGroupByBinder</c>'s computed members). A
    /// bare <see cref="MongoDocumentConstructionExpression"/> leaf is not one: it is read member by member.
    /// </summary>
    internal static bool IsMisreadWholeValueLeaf(MongoExpression value)
        => value is MongoConditionalExpression or MongoCoalesceExpression
           && HasMisreadDocumentConstructionBranch(value);

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
    /// True for a numeric computed leaf the server evaluates whole: <c>-x</c> (<see cref="ExpressionType.Negate"/> or
    /// <see cref="ExpressionType.NegateChecked"/> over a numeric operand), or a widening numeric cast over such a
    /// negation or over an arithmetic (<c>+ - * / %</c>) <see cref="BinaryExpression"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the emit side (<c>TryTranslateLeafCore</c>) and the read side
    /// (<c>MongoProjectionBindingExpressionVisitor</c>), so one can't admit a shape the other reads differently. A
    /// narrowing cast is excluded: it translates to <c>$toX</c>, whose semantics differ from C# truncation.
    /// </remarks>
    internal static bool IsNumericComputedLeafShape(Expression leaf)
        => leaf switch
        {
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate
                => MongoExpressionTranslator.IsNumericType(negate.Type)
                   && MongoExpressionTranslator.IsNumericType(negate.Operand.Type),
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                => MongoExpressionTranslator.IsWideningNumericConvert(
                       convert.Operand.Type.UnwrapNullableType(), convert.Type.UnwrapNullableType())
                   && (convert.Operand is UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked }
                           && IsNumericComputedLeafShape(convert.Operand)
                       || IsArithmeticLeafShape(convert.Operand) && MongoExpressionTranslator.IsNumericType(convert.Operand.Type)),
            _ => false
        };

    /// <summary>
    /// True for a binary arithmetic node (<c>+ - * / %</c>): the top node of an arithmetic computed leaf.
    /// </summary>
    /// <remarks>
    /// Shared by the emit side (<c>TryTranslateLeafCore</c>, <c>NativeSelectManyBinder</c>) and the read side
    /// (<c>MongoProjectionBindingExpressionVisitor</c>), so both admit the same operator set.
    /// </remarks>
    internal static bool IsArithmeticLeafShape(Expression expression)
        => expression is BinaryExpression
        {
            NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
            or ExpressionType.Divide or ExpressionType.Modulo
        };

    /// <summary>
    /// True for a projection leaf whose value is the same for every row of one execution, so the shaper can evaluate it
    /// client-side and nothing is projected for it: a tree of <see cref="ConstantExpression"/>, query parameters,
    /// <see cref="NewExpression"/>, <see cref="MemberInitExpression"/> (assignment bindings only),
    /// <see cref="ExpressionType.NewArrayInit"/>, <see cref="ListInitExpression"/>, <c>Convert</c>, and instance
    /// member access on a constant (<c>new { A = new DateTime() }</c>, <c>new { }</c>, a captured anonymous-type or
    /// <c>List&lt;T&gt;</c> value, <c>new Dto(param)</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any other node (the selector parameter or any other lambda parameter, a method call, a query root, a subquery,
    /// any other extension node such as a structural-type shaper) is row-dependent or unknown, so the answer is
    /// <see langword="false"/>. A whole-entity operand is therefore never row-independent, which keeps this disjoint
    /// from the whole-entity client constructions (<c>IsClientOnlyWholeEntityExpression</c>).
    /// </para>
    /// <para>
    /// The emit side admits such a leaf only after <c>TryTranslateLeaf</c> declined it (the <c>$literal</c> path
    /// keeps the MQL of a renderable constant or parameter). The read side
    /// (<c>MongoProjectionBindingExpressionVisitor.Visit</c>) calls this same predicate, over the rebased selector,
    /// where <paramref name="selectorParameter"/> is <see langword="null"/>; the parameter has been replaced by an
    /// extension node there, which this rejects anyway.
    /// </para>
    /// </remarks>
    internal static bool IsRowIndependentLeaf(Expression leaf, ParameterExpression? selectorParameter)
        => leaf switch
        {
            ConstantExpression => true,
#if EF8 || EF9
            // EF8/EF9 funcletize a captured value into a ParameterExpression named with the query-parameter prefix.
            ParameterExpression parameter
                => !ReferenceEquals(parameter, selectorParameter)
                   && parameter.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                   == true,
#else
            QueryParameterExpression => true,
#endif
            NewExpression newExpression => newExpression.Arguments.All(a => IsRowIndependentLeaf(a, selectorParameter)),
            MemberInitExpression memberInit
                => IsRowIndependentLeaf(memberInit.NewExpression, selectorParameter)
                   && memberInit.Bindings.All(
                       b => b is MemberAssignment assignment && IsRowIndependentLeaf(assignment.Expression, selectorParameter)),
            NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray
                => newArray.Expressions.All(e => IsRowIndependentLeaf(e, selectorParameter)),
            ListInitExpression listInit
                => IsRowIndependentLeaf(listInit.NewExpression, selectorParameter)
                   && listInit.Initializers.All(i => i.Arguments.All(a => IsRowIndependentLeaf(a, selectorParameter))),
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                => IsRowIndependentLeaf(convert.Operand, selectorParameter),
            MemberExpression { Expression: ConstantExpression } => true,
            _ => false
        };

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
        if (!leafExpression.TryGetMemberOrEFProperty(out var receiver, out var name)
            || !IsSelectorParameter(receiver, outerParameter))
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
            if (IsPlainTopLevelFieldRead(translator, value, out var field)
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
        out bool throwsOnNull,
        bool allowWholeRootEntityLeaf = false)
    {
        if (!TryTranslateLeafCore(
                mongoQ, translator, outerParameter, leafExpression, alias, pendingLookups, pendingReducerLeaves,
                pendingBareCountStamps, out result, out isArrayLeaf, out isOwnedNavEntityLeaf, allowWholeRootEntityLeaf))
        {
            throwsOnNull = false;
            return false;
        }

        // A non-nullable Length/IndexOf over a possibly-null string (MongoAggregationExpressionRenderer
        // .MayBeNullBehindNonNullableType) is rendered null-safely, so the server answers null where EF throws. Flag the
        // leaf (MongoProjection.ThrowsOnNull) so the read side reads it as T? and throws EF's "Nullable object must have
        // a value." instead of reading the null as 0; decline where an operator ($max/$min, Sign) may absorb the null
        // into a non-null answer. This call is the flag's only source.
        switch (MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(
                    NativeSlotPopulator.UnwrapBoxingToObjectType(leafExpression), result))
        {
            case NonNullableValueRead.Decline:
                throwsOnNull = false;
                return false;
            case NonNullableValueRead.ThrowOnNull:
                throwsOnNull = true;
                return true;
            default:
                throwsOnNull = false;
                return true;
        }
    }

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

        // Plain top-level scalar leaf (c.Foo or EF.Property), or a string-to-char-sequence leaf over one
        // (`e.City.AsEnumerable()`/ToList/ToArray), which pushes down just the string field; the read side re-applies
        // the call (see MongoProjectionBindingExpressionVisitor.Visit). TryTranslateField decides whether it is a real
        // field; the leaf kinds handled further down decline there, so trying this first is safe.
        var plainFieldCandidate = leafExpression is MethodCallExpression stringSequenceCall
                                  && IsStringSequenceMaterializationCall(stringSequenceCall)
            ? stringSequenceCall.Arguments[0]
            : leafExpression;
        if (TryTranslatePlainField(translator, plainFieldCandidate, out var field))
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

        // Enum-to-enum cast over a plain field — `(TargetEnum)c.SourceEnum`. MQL has no $toX for enums, but none is
        // needed: the cast is a CLR relabeling, so the leaf is admitted as the bare field. The shaper still reads the
        // whole Convert node through the generic alias-read path (no IProperty/serializer), so the field must be
        // default-serialized.
        if (leafExpression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } castUnary
            && IsEnumType(castUnary.Operand.Type)
            && IsEnumType(castUnary.Type)
            && TryTranslatePlainField(translator, castUnary.Operand, out var castField)
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
        if (IsArithmeticLeafShape(leafExpression)
            && translator.TryTranslateValue(leafExpression, out var computed))
        {
            result = computed;
            return true;
        }

        // Negate (`-x.A`) or a widening cast over Negate/arithmetic (`(long)-x.A`, `-(long)x.A`, `(long)(x.A + x.B)`).
        // `-x` translates to {$subtract: [0, x]} and TryTranslateValue drops a widening Convert, so each arrives as an
        // operator document (never a bare falsy value). The read side (MongoProjectionBindingExpressionVisitor) reads
        // the server value whole for exactly these shapes via the same predicate; re-applying Negate client-side over
        // the negated alias would answer +x.
        if (IsNumericComputedLeafShape(leafExpression)
            && translator.TryTranslateValue(leafExpression, out var numeric)
            && numeric is MongoBinaryExpression or MongoUnaryExpression)
        {
            result = numeric;
            return true;
        }

        // `x.When.TimeOfDay`: only ever a whole projection leaf (see MongoDatePart.TimeOfDay), read back through
        // BsonSerializerFactory.TimeOfDayMillisecondsSerializer.
        if (translator.TryTranslateTimeOfDayLeaf(leafExpression, out var timeOfDay))
        {
            result = timeOfDay;
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
            // A ternary over constructions its type's class map reads differently; see
            // HasMisreadDocumentConstructionBranch.
            && !IsMisreadWholeValueLeaf(value)
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
        => ReferenceEquals(PeelIncludes(receiver), outerParameter);

    /// <summary>
    /// Peels nav-expansion's <see cref="IncludeExpression"/> layers off <paramref name="expression"/>, removing a
    /// <c>Convert</c> around each level (unlike <see cref="ExpressionExtensionMethods.UnwrapIncludes"/>).
    /// </summary>
    private static Expression PeelIncludes(Expression expression)
    {
        var current = expression.RemoveConvert();

        while (current is IncludeExpression include)
        {
            current = include.EntityExpression.RemoveConvert();
        }

        return current;
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

        var current = PeelIncludes(leafExpression);

        return outerParameter.Type.IsTransparentIdentifierType()
               && current is MemberExpression { Member.Name: "Outer" or "Inner" } member
               && ReferenceEquals(member.Expression, outerParameter)
               && member.Type == mongoQ.CollectionExpression.EntityType.ClrType;
    }

    /// <summary>
    /// True when exactly one operand of <paramref name="methodCall"/> (receiver plus arguments) is the whole entity and
    /// no other operand references <paramref name="outerParameter"/>. The method then runs client-side over the fetched
    /// entity. Delegates to the shape rule shared with the read side (<see cref="NativeClientWholeEntityShape"/>).
    /// </summary>
    private static bool TryGetSoleWholeRootEntityOperand(
        MongoQueryExpression mongoQ, MethodCallExpression methodCall, ParameterExpression outerParameter)
        => NativeClientWholeEntityShape.HasSoleWholeEntityOperand(
            methodCall, node => ClassifyClientWholeEntityOperand(mongoQ, node, outerParameter));

    /// <summary>
    /// Generalizes <see cref="TryGetSoleWholeRootEntityOperand"/> to a whole client-only body <see
    /// cref="TryTranslateLeaf"/> could not render: a conditional/concat around a client-method call, or a client
    /// construction with a whole-entity operand (<c>new object[] { x }</c>, <c>new List&lt;object&gt; { x }</c>,
    /// <c>new Wrapper(x) { City = x.City }</c>). The whole document is then fetched and the entire body evaluated
    /// client-side over the materialized entity.
    /// </summary>
    /// <remarks>
    /// The shape rule, including the at-least-one-client-only-operand requirement, is <see
    /// cref="NativeClientWholeEntityShape.IsClientOnlyTree"/>; the read side
    /// (<c>MongoShapedQueryCompilingExpressionVisitor.IsCtorWrappedEntityShaper</c>) calls the same method.
    /// </remarks>
    private static bool IsClientOnlyWholeEntityExpression(
        MongoQueryExpression mongoQ, Expression node, ParameterExpression outerParameter)
        => NativeClientWholeEntityShape.IsClientOnlyTree(
            node, n => ClassifyClientWholeEntityOperand(mongoQ, n, outerParameter));

    /// <summary>
    /// True when a positional construction (a multi-argument member-less <c>new</c>, or a positional container) may take
    /// the scalar positional path, which reads every argument/element by its synthetic <c>_ctorArg&lt;N&gt;</c> alias.
    /// False when an argument is the whole entity (through a boxing/upcast <c>Convert</c>, as
    /// <see cref="NativeClientWholeEntityShape"/> sees a construction operand), or when the whole-entity client
    /// construction claims the body (<see cref="IsClientOnlyWholeEntityExpression"/>, e.g. an opaque client call on the
    /// entity as an element): those fetch whole documents and construct client-side instead.
    /// </summary>
    /// <remarks>
    /// The whole-entity test is the client-construction classifier itself (<see cref="ClassifyClientWholeEntityOperand"/>),
    /// so the two paths cannot disagree about what a whole-entity operand is. Without it the positional arm admitted the
    /// entity as a <c>$$ROOT</c> leaf, whose read throws FormatException for a key not named <c>Id</c>.
    /// </remarks>
    private static bool IsScalarPositionalConstruction(
        MongoQueryExpression mongoQ, Expression body, IReadOnlyList<(string MemberName, Expression Value)> members,
        ParameterExpression outerParameter)
        => !members.Any(m => ClassifyClientWholeEntityOperand(mongoQ, m.Value.RemoveConvert(), outerParameter)
                             == ClientWholeEntityOperand.WholeEntity)
           && !IsClientOnlyWholeEntityExpression(mongoQ, body, outerParameter);

    /// <summary>
    /// Strips the boxing <c>Convert</c>-to-<see cref="object"/> layers a container adds around an element
    /// (<c>new object[] { x.I }</c>), so the element is translated and read as its operand (with the operand's own type
    /// and serializer) and re-boxed afterwards, keeping its runtime type. Shared by the emit sides
    /// (<see cref="TryPopulateNativeProjection"/>'s container arm, <see cref="NativeJoinScopeProjectionBinder"/>) and the
    /// read sides (<c>MongoQueryableMethodTranslatingExpressionVisitor.BuildPositionalCtorProjectionShaper</c> and
    /// <c>BuildSelectManyResultShaper</c>), so they peel exactly the same layers.
    /// </summary>
    internal static Expression UnwrapContainerElementBoxing(Expression element)
    {
        while (element is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
               && convert.Type == typeof(object))
        {
            element = convert.Operand;
        }

        return element;
    }

    // The emit-side classifier: over the selector the entity is the selector parameter (or a one-hop join-scope
    // passthrough to it), and a subtree not referencing the parameter reads nothing of the row.
    private static ClientWholeEntityOperand ClassifyClientWholeEntityOperand(
        MongoQueryExpression mongoQ, Expression node, ParameterExpression outerParameter)
        => !node.ReferencesParameter(outerParameter)
            ? ClientWholeEntityOperand.EntityFree
            : IsWholeRootEntityLeafOrJoinScopePassthrough(mongoQ, node, outerParameter)
                ? ClientWholeEntityOperand.WholeEntity
                : ClientWholeEntityOperand.Walk;

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

        var elementType = navigationType.TryGetEnumerableElementType();
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
    /// <c>$project</c> element name of the constant sentinel staged when every leaf of a projection is client-evaluated
    /// (<see cref="IsRowIndependentLeaf"/>), so <c>Route</c> stays <c>Projection</c> (one row per document) instead of
    /// collapsing to a whole-entity fetch. Nothing reads it back. A constant, not <c>_id</c>, so a <c>Distinct</c> over
    /// the projection dedups to one row (the client value is the same for every row of one execution).
    /// </summary>
    internal const string ClientEvaluatedSentinelAlias = "_c";

    /// <summary>
    /// <c>$project</c> element-name prefix (<c>_cr0</c>, <c>_cr1</c>, ...) of a client-evaluated ternary's branch row
    /// reads. Synthetic, so the read side must take it from the staged read
    /// (<c>MongoSelectDefinition.TryGetClientConditionalReadAlias</c>) and can't derive it from the member path.
    /// </summary>
    internal const string ClientConditionalReadAliasPrefix = "_cr";

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

        if (!NativeCorrelationMatcher.TryMatchCorrelatedRootWhere(
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
        if (!NativeCorrelationMatcher.TryMatchCorrelatedRootWhere(mongoQ, outerParameter, whereArg, out var matchedNavigation))
        {
            return false;
        }

        // As for the Count leaf: a TPH-derived target declines, a bare Include lookup for the same nav is reused, and
        // one with a different PipelineKind (filtered Include) declines rather than silently reading the wrong shape.
        if (!NativeCorrelationMatcher.TryStageNativeCollectionLookup(
                mongoQ, new LookupExpression(matchedNavigation), pendingLookups, out _))
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
        if (!NativeCorrelationMatcher.TryMatchCorrelatedRootWhere(mongoQ, outerParameter, source, out var navigation))
        {
            return false;
        }

        // The matcher resolves only a navigation targeting the Where's root entity type.
        var targetEntityType = navigation.TargetEntityType;

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
        if (NativeCorrelationMatcher.FindStagedOrPendingLookup(mongoQ, pendingLookups, lookup.As) is not null)
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
        => expression.TryGetMemberOrEFProperty(out var receiver, out _) && ReferenceEquals(receiver, parameter);

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
