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

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Why a lookup carries a non-empty <see cref="LookupExpression.PipelineStages"/> sub-pipeline; each reason has a
/// different native-eligibility answer. <see cref="FilteredJoinInner"/> filters via
/// <see cref="LookupExpression.InnerPredicate"/>, rendered through the placeholder table; no non-native consumer can
/// honor it, so it must never reach the driver-LINQ bridge (see <see cref="LookupExpression.HasInnerPredicate"/>).
/// </summary>
internal enum LookupPipelineKind
{
    None,
    DiscriminatorNarrowed,
    FilteredInclude,
    NestedInclude,
    CorrelatedReducer,
    FilteredJoinInner
}

/// <summary>
/// Represents a pending $lookup aggregation stage needed to include
/// a cross-collection navigation property.
/// </summary>
internal sealed class LookupExpression
{
    /// <summary>
    /// Create a <see cref="LookupExpression"/> for the given navigation.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> that requires a $lookup.</param>
    /// <param name="forceUnwind">Force $unwind even for collection navigations (used for explicit Join).</param>
    public LookupExpression(INavigation navigation, bool forceUnwind = false)
    {
        Navigation = navigation;
        ForceUnwind = forceUnwind;

        var foreignKey = navigation.ForeignKey;
        var targetEntityType = navigation.TargetEntityType;
        From = targetEntityType.GetCollectionName();

        if (navigation.IsOnDependent)
        {
            LocalField = GetFieldPath(foreignKey.Properties[0]);
            ForeignField = GetFieldPath(foreignKey.PrincipalKey.Properties[0]);
        }
        else
        {
            LocalField = GetFieldPath(foreignKey.PrincipalKey.Properties[0]);
            ForeignField = GetFieldPath(foreignKey.Properties[0]);
        }

        As = GetLookupAlias(navigation);
        TargetEntityType = navigation.TargetEntityType;

        // TPH: sibling subtypes can share the FK value space, so FK equality alone would match sibling-type documents.
        if (targetEntityType.FindDiscriminatorProperty() is { } discriminatorProperty
            && targetEntityType != targetEntityType.GetRootType())
        {
            // Serialized through the discriminator property as the write path does; an abstract type's null value
            // would match a document with no discriminator element, so it is dropped.
            var discriminatorValues = new BsonArray(
                new MongoEFDiscriminator(targetEntityType)
                    .GetDiscriminatorsForTypeAndSubTypes(targetEntityType.ClrType)
                    .Where(v => v is not BsonNull));

            PipelineStages.Add(new BsonDocument("$match",
                new BsonDocument(discriminatorProperty.GetElementName(), new BsonDocument("$in", discriminatorValues))));
            PipelineKind = LookupPipelineKind.DiscriminatorNarrowed;
        }
    }

    /// <summary>
    /// Create a <see cref="LookupExpression"/> for a Join hop with no corresponding model navigation,
    /// built directly from resolved join-key field paths instead of an <see cref="INavigation"/>.
    /// </summary>
    public LookupExpression(
        IEntityType targetEntityType, string collectionName, string localField, string foreignField, string alias,
        bool forceUnwind)
    {
        Navigation = null;
        TargetEntityType = targetEntityType;
        ForceUnwind = forceUnwind;
        From = collectionName;
        LocalField = localField;
        ForeignField = foreignField;
        As = alias;
    }

    /// <summary>
    /// Creates a <see cref="LookupExpression"/> for a navigation-less Join hop keyed on an anonymous type of two or
    /// more properties; <see cref="LocalField"/>/<see cref="ForeignField"/> report the first pair.
    /// </summary>
    public LookupExpression(
        IEntityType targetEntityType, string collectionName, IReadOnlyList<LookupKeyPair> compositeKey, string alias,
        bool forceUnwind)
        : this(targetEntityType, collectionName, compositeKey[0].LocalField, compositeKey[0].ForeignField, alias, forceUnwind)
    {
        System.Diagnostics.Debug.Assert(compositeKey.Count > 1, "A single-pair key uses the localField/foreignField form.");
        CompositeKey = compositeKey;
    }

    /// <summary>
    /// The field a <c>$lookup</c> writes its results to and the shaper reads back from. Centralized so
    /// write and read sites can't drift on the <c>_lookup_</c> format.
    /// </summary>
    /// <param name="navigation">The navigation the lookup supports.</param>
    /// <returns>The <c>_lookup_&lt;NavigationName&gt;</c> field name.</returns>
    public static string GetLookupAlias(IReadOnlyNavigationBase navigation)
        => $"{LookupAliasPrefix}{navigation.Name}";

    /// <summary>The prefix of the synthetic <c>$lookup</c> alias field (see <see cref="GetLookupAlias"/>).</summary>
    public const string LookupAliasPrefix = "_lookup_";

    /// <summary>The navigation this lookup supports, or <see langword="null"/> for a bare key-equality
    /// Join hop with no corresponding model navigation (see EF-377).</summary>
    public INavigation? Navigation { get; }

    /// <summary>The entity type this lookup's <c>$lookup</c> stage produces documents for. Always
    /// available, unlike <see cref="Navigation"/>, so consumers can match a lookup back to an entity
    /// type without assuming a navigation exists.</summary>
    public IEntityType TargetEntityType { get; }

    /// <summary>The target collection name to look up from.</summary>
    public string From { get; }

    /// <summary>The field on the local document to match (the first pair's, for a <see cref="CompositeKey"/>).</summary>
    /// <remarks>
    /// Re-prefixing (a transitive hop, the driver-LeftJoin <c>_outer</c>/<c>_inner</c> shape) throws for a
    /// <see cref="CompositeKey"/> lookup: only the first pair would move, silently joining the rest on the wrong
    /// fields. Today's re-prefix sites only touch navigation-built lookups, so the throw guards future callers.
    /// </remarks>
    public string LocalField
    {
        get => _localField;
        set
        {
            if (CompositeKey != null)
            {
                throw new System.InvalidOperationException(
                    "The local field of a composite-key $lookup can't be re-targeted after construction.");
            }

            _localField = value;
        }
    }

    private string _localField = null!;

    /// <summary>The field on the foreign document to match.</summary>
    public string ForeignField { get; }

    /// <summary>
    /// Every key pair of a navigation-less join on an anonymous key of two or more properties, or
    /// <see langword="null"/> for a single-field lookup.
    /// </summary>
    public IReadOnlyList<LookupKeyPair>? CompositeKey { get; }

    /// <summary>The output array field name in the resulting document.</summary>
    public string As { get; set; }

    /// <summary>
    /// Whether this single-key lookup returns at most one document per outer row: it matches the target's whole <c>_id</c>, which is unique
    /// by construction (a dotted key such as a composite <c>_id.Part</c>, or any other field, can repeat). A navigation declared on an owned
    /// entity is excluded in case its local key sits inside an owned collection, where one outer row holds several keys.
    /// </summary>
    internal bool MatchesAtMostOneDocument
        => CompositeKey is null
           && ForeignField == "_id"
           && !As.Contains('.')
           && Navigation?.DeclaringEntityType.IsOwned() != true;

    /// <summary>
    /// Whether this lookup's <see cref="LocalField"/> reads a field of <paramref name="other"/>'s output (is prefixed by
    /// <c>"&lt;other.As&gt;."</c>), so it must be emitted after <paramref name="other"/>.
    /// </summary>
    internal bool ReadsOutputOf(LookupExpression other)
        => LocalField.StartsWith(other.As + ".", System.StringComparison.Ordinal);

    /// <summary>The full field path for a property, accounting for composite keys stored under <c>_id</c>.</summary>
    /// <remarks>
    /// Shared with <c>JoinLookupImplementsKeySelectors</c>: a plain <c>GetElementName()</c> disagrees for a
    /// component of a multi-property key.
    /// </remarks>
    internal static string GetFieldPath(IReadOnlyProperty property)
    {
        var elementName = property.GetElementName();

        if (property.IsPrimaryKey()
            && property.DeclaringType is IEntityType entityType
            && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return $"_id.{elementName}";
        }

        return elementName;
    }

    /// <summary>
    /// Pipeline stages to apply inside the $lookup for filtered Includes
    /// (e.g., OrderBy, Skip, Take on the included collection).
    /// When non-empty, the pipeline form of $lookup is used instead of localField/foreignField.
    /// </summary>
    public List<BsonDocument> PipelineStages { get; } = [];

    /// <summary>
    /// The filter of an explicit join's inner side (<see cref="LookupPipelineKind.FilteredJoinInner"/>), relative to the
    /// joined document, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Not baked into <see cref="PipelineStages"/>: those have no per-execution placeholder substitution, so a
    /// parameter would be frozen into the cached plan. Rendered by <c>MongoPipelineFactory.RenderLookup</c>.
    /// </remarks>
    public MongoExpression? InnerPredicate { get; internal set; }

    /// <summary>
    /// Whether this lookup carries a join-inner filter only the native renderer can honor. Non-native consumers
    /// (the driver-LINQ bridge) must call this to refuse, never restate it.
    /// </summary>
    public bool HasInnerPredicate => InnerPredicate is not null;

    /// <summary>See <see cref="LookupPipelineKind"/>.</summary>
    /// <remarks>
    /// Write exactly once, at registration (not <see langword="init"/> because the fallback visitor stamps it after
    /// construction); an initializer would overwrite the <see cref="LookupPipelineKind.DiscriminatorNarrowed"/> the
    /// constructor's TPH branch sets.
    /// </remarks>
    public LookupPipelineKind PipelineKind { get; internal set; } = LookupPipelineKind.None;

    /// <summary>Whether this lookup uses a pipeline (filtered Include).</summary>
    public bool HasPipeline => PipelineStages.Count > 0;

    /// <summary>Whether this lookup is a single reference; a navigation-less lookup is always a reference.</summary>
    public bool IsReference => Navigation is not { IsCollection: true } && !IsCorrelatedArraySource;

    /// <summary>
    /// Whether this lookup is the array behind a correlated subquery that is NOT a collection navigation: a keyed,
    /// not-unwound <c>$lookup</c> whose output array the aggregate / quantifier arms read.
    /// </summary>
    /// <remarks>
    /// Never a reference, although <see cref="Navigation"/> is <see langword="null"/>: the lowerer must not unwind it and the
    /// streaming reader must not read it as an entity. Never <see cref="InjectAfterRoot"/>, so the driver-LINQ bridge's
    /// navigation-count rewrite cannot pick it up; the bridge refuses such a query instead.
    /// </remarks>
    public bool IsCorrelatedArraySource { get; init; }

    /// <summary>
    /// Whether a null (or missing) local key must match nothing. <c>$lookup</c> matches a null/missing
    /// <c>localField</c> to null/missing foreign values, so the pipeline form with an explicit non-null condition is used.
    /// </summary>
    public bool ExcludesNullLocalKey { get; init; }

    /// <summary>
    /// The <c>Where</c> lambdas a keyed array lookup applies inside its sub-pipeline (translated into
    /// <see cref="InnerPredicate"/>). Kept so subqueries with the same filters share one array and different
    /// filters or parameters get their own alias.
    /// </summary>
    public IReadOnlyList<System.Linq.Expressions.LambdaExpression> ElementFilters { get; init; } = [];

    /// <summary>
    /// The ordering a keyed array lookup applies inside its sub-pipeline (<c>c.Orders.OrderBy(o =&gt; o.Id).ToList()</c>), as
    /// (element field path, 1 or -1) pairs, primary key first. Rendered after the element filter by
    /// <c>MongoPipelineFactory.RenderLookup</c>.
    /// </summary>
    public IReadOnlyList<(string Field, int Direction)> ElementSort { get; init; } = [];

    /// <summary>
    /// The number of elements a keyed array lookup keeps (<c>FirstOrDefault</c> keeps one), applied after <see cref="ElementSort"/>: a
    /// constant or a query parameter, rendered through the placeholder table so a parameter is read per execution.
    /// </summary>
    public MongoExpression? ElementLimit { get; init; }

    /// <summary>
    /// The number of leading elements a keyed array lookup drops (<c>Skip</c>), applied after <see cref="ElementSort"/>: a constant or a
    /// query parameter.
    /// </summary>
    public MongoExpression? ElementSkip { get; init; }

    /// <summary>
    /// Whether two element limits or skips are the same: equal constants, or the same query parameter (by name, never by value: the value is
    /// per execution, so two parameters are two arrays).
    /// </summary>
    public static bool SameElementCount(MongoExpression? left, MongoExpression? right)
        => (left, right) switch
        {
            (null, null) => true,
            (MongoConstantExpression l, MongoConstantExpression r) => Equals(l.Value, r.Value),
            (MongoParameterExpression l, MongoParameterExpression r) => l.Name == r.Name,
            _ => false
        };

    /// <summary>
    /// The conditions on the outer row a keyed array lookup applies as a gate (<c>c.City == "London"</c>): the group is empty for an outer
    /// row that fails any of them. Rendered by <c>MongoPipelineFactory.RenderLookup</c> from a <c>let</c> of the outer fields.
    /// </summary>
    public IReadOnlyList<LookupOuterCondition> OuterConditions { get; init; } = [];

    /// <summary>Whether the lookup gates its group on the outer row (<see cref="OuterConditions"/>).</summary>
    public bool HasOuterConditions => OuterConditions.Count > 0;

    /// <summary>Whether two sets of outer conditions are the same: the same fields, the same guards, equal constants or the same query parameters.</summary>
    public static bool SameOuterConditions(IReadOnlyList<LookupOuterCondition> left, IReadOnlyList<LookupOuterCondition> right)
        => left.Count == right.Count
           && left.Zip(right).All(pair => pair.First.LocalField == pair.Second.LocalField
                                          && pair.First.Guarded == pair.Second.Guarded
                                          && SameElementCount(pair.First.Value, pair.Second.Value));

    /// <summary>
    /// Whether the sub-pipeline sorts or limits the array. Like <see cref="HasInnerPredicate"/>, only the native renderer honors it, so
    /// a non-native consumer must not read such an array as the whole group.
    /// </summary>
    public bool HasElementStages => ElementSort.Count > 0 || ElementLimit is not null || ElementSkip is not null;

    /// <summary>
    /// The projection aliases of the element leaves (<c>Os = c.Orders.OrderBy(..).ToList()</c>) this array backs; the read side
    /// finds the lookup by the member's alias. Written at the projection binder's commit point, never by a declined leaf.
    /// </summary>
    public List<string> ElementLeafAliases { get; } = [];

    /// <summary>
    /// Whether this lookup is an array that is not the plain, whole group: one a projected element leaf is read from
    /// (<see cref="ElementLeafAliases"/>, whose sub-pipeline may also sort, limit or filter it). The driver-LINQ bridge cannot read
    /// it as a plain array for an aggregate; it renders an element leaf's lookup natively and copies it to the leaf's alias, and refuses a
    /// lookup with element stages but no leaf, so it never returns an empty or unfiltered list. Evaluated after the projection binder's commit.
    /// </summary>
    public bool IsNativeOnlyArray => HasInnerPredicate || HasElementStages || HasOuterConditions || ElementLeafAliases.Count > 0;

    /// <summary>
    /// Whether this lookup is a join whose inner filter (<see cref="InnerPredicate"/>) only the native renderer honors. The driver-LINQ
    /// bridge peels a join to a flat <c>$lookup</c> + <c>$unwind</c> and drops its inner source, so it must refuse rather than return
    /// unfiltered rows. A correlated array's predicate is an element filter the bridge applies itself.
    /// </summary>
    public bool IsJoinWithNativeOnlyInnerFilter => HasInnerPredicate && !IsCorrelatedArraySource;

    /// <summary>
    /// Whether the sub-pipeline sorts, skips or limits an array that no element leaf reads, so nothing could apply those stages
    /// outside the native renderer: the bridge must refuse rather than return an empty or unfiltered list.
    /// </summary>
    public bool HasElementStagesWithoutLeaf => HasElementStages && ElementLeafAliases.Count == 0;

    /// <summary>
    /// Whether the driver-LINQ bridge can read this lookup's array: a plain keyed array behind a correlated subquery
    /// (<see cref="IsCorrelatedArraySource"/>) that filters, sorts and limits nothing. The bridge emits it right after the root, as it
    /// does an <see cref="InjectAfterRoot"/> lookup, so a projection above can read it.
    /// </summary>
    public bool IsBridgeReadableArray => IsCorrelatedArraySource && !IsNativeOnlyArray;

    /// <summary>
    /// Whether the driver-LINQ bridge should leave this lookup out: a keyed array whose element filter only the native renderer applies
    /// and that no element leaf reads. The bridge reads the aggregate from its own plain array and applies the filter as an
    /// <c>Enumerable.Where</c> over it, so emitting this one would only add an unread array.
    /// </summary>
    public bool IsBridgeIgnorable => IsCorrelatedArraySource && (HasInnerPredicate || HasOuterConditions) && ElementLeafAliases.Count == 0;

    /// <summary>The <c>let</c> of the null-excluding form: the local key, null-normalized.</summary>
    internal BsonDocument NullExcludingLet()
        => new("k", new BsonDocument("$ifNull", new BsonArray { $"${LocalField}", BsonNull.Value }));

    internal BsonDocument NullExcludingMatch()
        => new("$and", new BsonArray
        {
            new BsonDocument("$ne", new BsonArray { "$$k", BsonNull.Value }),
            new BsonDocument("$eq", new BsonArray { $"${ForeignField}", "$$k" })
        });

    /// <summary>
    /// Whether the collection-Include binding (flat multi-lookup mode) treats this lookup as the intermediate document
    /// a collection Include declared on <paramref name="declaringType"/> hangs off: an unwound reference hop targeting
    /// that type, matched by type, not alias. Shared with the native collection-Include-over-join gate, which declines
    /// when a join level would match the root's own Include (the binding would nest it under the join's alias while the
    /// reader reads it at root).
    /// </summary>
    internal bool IsCollectionIncludeIntermediateFor(IReadOnlyEntityType declaringType)
        => IsReference && ForceUnwind && TargetEntityType == declaringType;

    /// <summary>A single-level reference Include the native pipeline can emit and the streaming reader can read back.</summary>
    public bool IsStreamableReference
        => IsReference && !HasPipeline && !LocalField.StartsWith(LookupAliasPrefix, System.StringComparison.Ordinal);

    /// <summary>Whether $unwind should be applied after $lookup.</summary>
    public bool ShouldUnwind => IsReference || ForceUnwind;

    public bool ForceUnwind { get; }

    /// <summary>
    /// Set when this collection Include's alias was renamed from <see cref="GetLookupAlias(IReadOnlyNavigationBase)"/>
    /// to avoid colliding with an incompatible ($unwind-ed or <see cref="IsBareCountSizeSource"/>) lookup; it is
    /// still a plain collection Include for <see cref="NativeTranslation.MongoSelectLowerer.AppendLookupStages"/>.
    /// </summary>
    public bool RenamedToAvoidJoinCollision { get; set; }

    /// <summary>
    /// Set when a reference-collection-nav <c>Count</c>'s <see cref="MongoSizeExpression"/> reads this array, so it
    /// must remain the navigation's unfiltered array.
    /// </summary>
    /// <remarks>
    /// Without it, <see cref="MongoQueryExpression.AddLookup"/>'s bare-then-pipelined merge would fold a later
    /// filtered/paged <c>Include</c>'s array into this one, corrupting the count in every
    /// <see cref="Infrastructure.MongoQueryMode"/>. The Include is renamed instead (see <see cref="RenamedToAvoidJoinCollision"/>).
    /// </remarks>
    public bool IsBareCountSizeSource { get; set; }

    /// <summary>
    /// A single-level collection Include the native pipeline can emit as a not-unwound <c>$lookup</c> array at the plain
    /// alias; the driver-LeftJoin and flat-nested shapes stay fallback-only.
    /// </summary>
    public bool IsNativeCollectionLookup
    {
        get
        {
            // A keyed array lookup behind a correlated subquery has no navigation to derive its alias from.
            if (IsCorrelatedArraySource)
            {
                return !HasPipeline && !ForceUnwind;
            }

            if (Navigation is not { IsCollection: true } navigation)
            {
                return false;
            }

            return !HasPipeline && !ForceUnwind && As == GetLookupAlias(navigation);
        }
    }

    /// <summary>
    /// A collection Include reached via <c>ThenInclude</c> off a reference Include: "flat multi-lookup mode" prefixes
    /// <see cref="LocalField"/> and <see cref="As"/> with the reference lookup's alias
    /// (<c>"_lookup_Mid._lookup_Leaves"</c>). Otherwise like <see cref="IsNativeCollectionLookup"/>.
    /// </summary>
    public bool IsTransitiveCollectionLookup
    {
        get
        {
            if (Navigation is not { IsCollection: true } navigation)
            {
                return false;
            }

            var plainAlias = GetLookupAlias(navigation);
            return !HasPipeline && !ForceUnwind && As != plainAlias
                && As.EndsWith("." + plainAlias, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Whether the <c>$unwind</c> following this <c>$lookup</c> uses <c>preserveNullAndEmptyArrays: true</c>
    /// — LEFT-OUTER (principal survives an unmatched join) vs INNER (it is dropped).
    /// <para>
    /// Defaults to <see langword="true"/> so non-join registration sites (Include) get the conservative,
    /// principal-preserving behaviour. The join-translation path overrides it from the actual LINQ operator:
    /// <c>LeftJoin</c>/<c>GroupJoin</c> are left-outer, plain <c>Join</c> is inner — which also covers EF's
    /// lowering of a REQUIRED reference navigation to <c>Queryable.Join</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see langword="init"/>-only: compile-time state on an object reused across executions, so it must be
    /// written exactly once, at registration.
    /// </remarks>
    public bool PreserveNullAndEmptyArrays { get; init; } = true;

    /// <summary>
    /// Whether this $lookup must be injected right after the root collection source (before the user's
    /// downstream pipeline stages) rather than tail-appended. Used for projected collection-navigation
    /// counts (<c>select new { ..., c.Orders.Count }</c>) where a later <c>$match</c>/<c>$project</c>
    /// reads the <c>_lookup_&lt;Nav&gt;</c> array via <c>{ $size: ... }</c> and so must see it already present.
    /// </summary>
    public bool InjectAfterRoot { get; set; }

    /// <summary>
    /// Builds the <c>$lookup</c> stage document: the plain <c>localField</c>/<c>foreignField</c> form, or the pipeline
    /// form (<c>let</c> + <c>pipeline</c>) when <see cref="HasPipeline"/>. Shared by every construction site so they
    /// can't drift on shape.
    /// </summary>
    public BsonDocument ToLookupStageDocument()
    {
        if (CompositeKey != null)
        {
            return CompositeKeyLookupStageDocument(CompositeKey);
        }

        if (ExcludesNullLocalKey)
        {
            return PipelineLookupStage(NullExcludingLet(), NullExcludingMatch());
        }

        if (!HasPipeline)
        {
            return new BsonDocument("$lookup", new BsonDocument
            {
                { "from", From },
                { "localField", LocalField },
                { "foreignField", ForeignField },
                { "as", As }
            });
        }

        return PipelineLookupStage(
            new BsonDocument("localField", $"${LocalField}"),
            new BsonDocument("$eq", new BsonArray { $"${ForeignField}", "$$localField" }));
    }

    private BsonDocument PipelineLookupStage(BsonValue let, BsonValue matchExpression)
    {
        var pipeline = new BsonArray { new BsonDocument("$match", new BsonDocument("$expr", matchExpression)) };
        foreach (var stage in PipelineStages)
        {
            pipeline.Add(stage);
        }

        return new BsonDocument("$lookup", new BsonDocument
        {
            { "from", From },
            { "let", let },
            { "pipeline", pipeline },
            { "as", As }
        });
    }

    // Anonymous-type key equality is member-wise EqualityComparer<T>.Default, so null equals null. Aggregation $eq
    // answers false for null vs missing, so a part that may be null is $ifNull-normalized on both sides.
    private BsonDocument CompositeKeyLookupStageDocument(IReadOnlyList<LookupKeyPair> keys)
    {
        var (let, match) = CompositeKeyLetAndMatch(keys);
        return PipelineLookupStage(let, match);
    }

    /// <summary>
    /// The <c>let</c> (one null-normalized variable per local field) and the <c>$expr</c> match (every pair equal, and a pair that
    /// <see cref="LookupKeyPair.ExcludesNullLocal"/> also non-null on the local side) of a composite-key lookup. Shared by the join
    /// form and by the sub-pipeline form that a filtered, ordered or paged keyed array uses.
    /// </summary>
    internal static (BsonDocument Let, BsonDocument Match) CompositeKeyLetAndMatch(IReadOnlyList<LookupKeyPair> keys)
    {
        var let = new BsonDocument();
        var conditions = new BsonArray();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var variable = $"k{i}";
            let.Add(variable, NullNormalized($"${key.LocalField}", key.MayBeNull));

            // A guarded part (EF's `outerKey != null &&`) matches nothing for a null outer value; an unguarded one equates null with null.
            if (key.ExcludesNullLocal)
                conditions.Add(new BsonDocument("$ne", new BsonArray { $"$${variable}", BsonNull.Value }));

            conditions.Add(new BsonDocument("$eq",
                new BsonArray { NullNormalized($"${key.ForeignField}", key.MayBeNull), $"$${variable}" }));
        }

        return (let, new BsonDocument("$and", conditions));

        static BsonValue NullNormalized(string path, bool mayBeNull)
            => mayBeNull ? new BsonDocument("$ifNull", new BsonArray { path, BsonNull.Value }) : path;
    }

    /// <summary>Builds the <c>$unwind</c> stage document that flattens this lookup's output array.</summary>
    /// <remarks>
    /// <paramref name="preserveNullAndEmptyArrays"/> is a parameter because callers differ: an <c>$unwind</c> nested in
    /// a parent lookup's sub-pipeline must always preserve, or it would drop collection elements.
    /// </remarks>
    public BsonDocument ToUnwindStageDocument(bool preserveNullAndEmptyArrays)
        => UnwindStageDocument(As, preserveNullAndEmptyArrays);

    /// <summary>Builds an <c>$unwind</c> stage for an arbitrary path (the bridge's fixed <c>_inner</c> shape).</summary>
    public static BsonDocument UnwindStageDocument(string path, bool preserveNullAndEmptyArrays)
        => new("$unwind", new BsonDocument
        {
            { "path", "$" + path },
            { "preserveNullAndEmptyArrays", preserveNullAndEmptyArrays }
        });
}

/// <summary>
/// A condition on the outer row that gates a keyed array lookup: the local field equals <paramref name="Value"/> (a constant or a query
/// parameter, never null), and with <paramref name="Guarded"/> the field is also not null.
/// </summary>
internal readonly record struct LookupOuterCondition(string LocalField, MongoExpression Value, bool Guarded);

/// <summary>
/// One local/foreign field pair of a <see cref="LookupExpression.CompositeKey"/>. <see cref="MayBeNull"/> when either
/// side's property is nullable, so the pair is compared null-normalized.
/// </summary>
internal readonly record struct LookupKeyPair(string LocalField, string ForeignField, bool MayBeNull, bool ExcludesNullLocal = false);
