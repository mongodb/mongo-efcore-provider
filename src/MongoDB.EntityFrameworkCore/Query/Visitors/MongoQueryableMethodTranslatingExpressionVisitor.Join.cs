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

// Join, GroupJoin and LeftJoin translation, lookup building and inner-shaper rebinding.
internal sealed partial class MongoQueryableMethodTranslatingExpressionVisitor
{
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
}
