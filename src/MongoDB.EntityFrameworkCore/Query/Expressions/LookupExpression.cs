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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Why a lookup carries a non-empty <see cref="LookupExpression.PipelineStages"/> sub-pipeline; each reason has a
/// different native-eligibility answer. <see cref="FallbackOnly"/>: TPH discriminator narrowing, fallback-only
/// (see <c>MongoSelectLowerer.AppendLookupStages</c>). <see cref="FilteredInclude"/> and
/// <see cref="NestedInclude"/> (filtered Include; collection-then-collection/reference <c>ThenInclude</c>): the
/// <c>let</c>+<c>pipeline</c> shape of <see cref="LookupExpression.ToLookupStageDocument"/>, native-eligible.
/// <see cref="CorrelatedReducer"/>: reference-collection-nav First/FirstOrDefault projection leaf, rendered via
/// the localField/foreignField+pipeline shape (<c>MongoPipelineFactory.RenderLookup</c>) since it has a real
/// equality FK.
/// </summary>
internal enum LookupPipelineKind
{
    None,
    FallbackOnly,
    FilteredInclude,
    NestedInclude,
    CorrelatedReducer
}

/// <summary>
/// Represents the pending data needed to build a <c>$lookup</c> aggregation stage for including
/// a cross-collection navigation property. This is a data holder, not a pipeline stage itself —
/// the lowerer/pipeline factory render it into the actual <c>$lookup</c>/<c>$unwind</c> stage documents.
/// </summary>
internal sealed class LookupExpression
{
    /// <summary>Creates a <see cref="LookupExpression"/> for the given navigation.</summary>
    /// <param name="navigation">The navigation that requires a <c>$lookup</c>.</param>
    /// <param name="forceUnwind">Force <c>$unwind</c> even for collection navigations (explicit Join).</param>
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

        // TPH: sibling subtypes can share the same FK value space, so FK equality alone would also
        // match sibling-type documents; narrow by discriminator to just this type and its derived types.
        if (targetEntityType.FindDiscriminatorProperty() is { } discriminatorProperty
            && targetEntityType != targetEntityType.GetRootType())
        {
            var discriminatorValues = new BsonArray(
                targetEntityType.GetDerivedTypes().Prepend(targetEntityType)
                    .Select(d => BsonValue.Create(d.GetDiscriminatorValue())));

            PipelineStages.Add(new BsonDocument("$match",
                new BsonDocument(discriminatorProperty.GetElementName(), new BsonDocument("$in", discriminatorValues))));
            PipelineKind = LookupPipelineKind.FallbackOnly;
        }
    }

    /// <summary>Creates a <see cref="LookupExpression"/> for a Join hop with no model navigation, from resolved key field paths.</summary>
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
    /// The <c>_lookup_&lt;NavigationName&gt;</c> field a <c>$lookup</c> writes to and the shaper reads back from;
    /// centralized so write and read sites can't drift.
    /// </summary>
    /// <param name="navigation">The navigation the lookup supports.</param>
    /// <returns>The alias field name.</returns>
    public static string GetLookupAlias(IReadOnlyNavigationBase navigation)
        => $"{LookupAliasPrefix}{navigation.Name}";

    /// <summary>The prefix of the synthetic <c>$lookup</c> alias field (see <see cref="GetLookupAlias"/>).</summary>
    public const string LookupAliasPrefix = "_lookup_";

    /// <summary>The navigation this lookup supports, or <see langword="null"/> for a bare key-equality
    /// Join hop with no corresponding model navigation (see EF-377).</summary>
    public INavigation? Navigation { get; }

    /// <summary>The entity type this lookup produces documents for. Unlike <see cref="Navigation"/>, always available.</summary>
    public IEntityType TargetEntityType { get; }

    /// <summary>The target collection name to look up from.</summary>
    public string From { get; }

    /// <summary>The field on the local document to match.</summary>
    public string LocalField { get; set; }

    /// <summary>The field on the foreign document to match.</summary>
    public string ForeignField { get; }

    /// <summary>The output array field name in the resulting document.</summary>
    public string As { get; set; }

    /// <summary>The full field path for a property, accounting for composite keys stored under <c>_id</c>.</summary>
    /// <remarks>
    /// <c>internal</c> so <c>JoinLookupImplementsKeySelectors</c> compares against the same composite-key-aware
    /// path <see cref="ForeignField"/>/<see cref="LocalField"/> were built from; a plain <c>GetElementName()</c>
    /// disagrees for a component of a multi-property key.
    /// </remarks>
    internal static string GetFieldPath(IReadOnlyProperty property)
    {
        var elementName = property.GetElementName();

        // Composite-key components are stored nested under _id (e.g. _id.OrderID).
        if (property.IsPrimaryKey()
            && property.DeclaringType is IEntityType entityType
            && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return $"_id.{elementName}";
        }

        return elementName;
    }

    /// <summary>
    /// Stages applied inside the <c>$lookup</c> for filtered Includes (OrderBy, Skip, Take). When non-empty the
    /// pipeline form of <c>$lookup</c> is used instead of localField/foreignField.
    /// </summary>
    public List<BsonDocument> PipelineStages { get; } = [];

    /// <summary>See <see cref="LookupPipelineKind"/>.</summary>
    /// <remarks>
    /// Write exactly once, at registration (not <see langword="init"/> because the fallback visitor stamps it after
    /// construction). An object-initializer assignment would overwrite the <see cref="LookupPipelineKind.FallbackOnly"/>
    /// the constructor's TPH branch sets, leaving its discriminator <c>$match</c> unaccounted for.
    /// </remarks>
    public LookupPipelineKind PipelineKind { get; internal set; } = LookupPipelineKind.None;

    /// <summary>Whether this lookup uses the pipeline form (filtered Include).</summary>
    public bool HasPipeline => PipelineStages.Count > 0;

    /// <summary>Whether this lookup is for a single reference (not a collection). A navigation-less
    /// lookup (<see cref="Navigation"/> is <see langword="null"/>) is always treated as a reference.</summary>
    public bool IsReference => Navigation is not { IsCollection: true };

    /// <summary>
    /// A single-level reference Include the native pipeline can emit and the streaming reader can read back:
    /// a reference nav, no filtered-Include pipeline stages, not a transitive <c>_lookup_</c> local field.
    /// </summary>
    public bool IsStreamableReference
        => IsReference && !HasPipeline && !LocalField.StartsWith(LookupAliasPrefix, System.StringComparison.Ordinal);

    /// <summary>Whether <c>$unwind</c> should be applied after <c>$lookup</c>.</summary>
    public bool ShouldUnwind => IsReference || ForceUnwind;

    /// <summary>Whether <c>$unwind</c> is forced regardless of navigation type.</summary>
    public bool ForceUnwind { get; }

    /// <summary>
    /// Set when this collection Include's alias was renamed from <see cref="GetLookupAlias(IReadOnlyNavigationBase)"/>
    /// to avoid colliding with an incompatible ($unwind-ed or <see cref="IsBareCountSizeSource"/>) lookup, so
    /// <see cref="NativeTranslation.MongoSelectLowerer.AppendLookupStages"/> treats it as a plain collection Include
    /// under a different field name.
    /// </summary>
    public bool RenamedToAvoidJoinCollision { get; set; }

    /// <summary>
    /// Set when a reference-collection-nav <c>Count</c>/<c>LongCount</c>'s <see cref="MongoSizeExpression"/> reads
    /// this array (via <see cref="NativeTranslation.NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup"/>),
    /// so it must remain the navigation's unfiltered array.
    /// </summary>
    /// <remarks>
    /// Registered at <c>Where</c>/projection time, before a later filtered/paged <c>Include</c> of the same navigation
    /// may register at the same alias. Without this flag, <see cref="MongoQueryExpression.AddLookup"/>'s
    /// bare-then-pipelined merge would fold the Include's paged array into it, corrupting the count in every
    /// <see cref="Infrastructure.MongoQueryMode"/>. Instead the Include is renamed (see <see cref="RenamedToAvoidJoinCollision"/>).
    /// </remarks>
    public bool IsBareCountSizeSource { get; set; }

    /// <summary>
    /// A single-level collection Include the native pipeline can emit as a <c>$lookup</c> array (no
    /// <c>$unwind</c>), readable by the DOM collection materializer from a root-level
    /// <c>_lookup_&lt;Nav&gt;</c> field: a collection nav, no filtered-Include pipeline stages, not
    /// force-unwound, and <see cref="As"/> equal to the plain alias (excludes the driver-LeftJoin and
    /// flat-nested shapes, which remain fallback-only).
    /// </summary>
    /// <remarks>
    /// A navigation-less lookup (a <c>Join</c> hop) is never a collection Include; <see cref="Navigation"/> is nullable.
    /// </remarks>
    public bool IsNativeCollectionLookup
    {
        get
        {
            if (Navigation is not { IsCollection: true } navigation)
            {
                return false;
            }

            return !HasPipeline && !ForceUnwind && As == GetLookupAlias(navigation);
        }
    }

    /// <summary>
    /// A collection Include reached via <c>ThenInclude</c> off a reference Include (e.g.
    /// <c>Orders.Include(o =&gt; o.Customer.Orders)</c>): "flat multi-lookup mode" prefixes <see cref="LocalField"/>
    /// and <see cref="As"/> with the reference lookup's alias (<c>"_lookup_Mid._lookup_Leaves"</c>) so the shaper
    /// reads the array under the unwound intermediate document. Otherwise like
    /// <see cref="IsNativeCollectionLookup"/>.
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
    /// Whether the following <c>$unwind</c> uses <c>preserveNullAndEmptyArrays: true</c> (left-outer). Defaults to
    /// <see langword="true"/> for Include; the join path sets it from the LINQ operator.
    /// </summary>
    /// <remarks>
    /// <see langword="init"/>-only: compile-time state on an object reused across executions.
    /// </remarks>
    public bool PreserveNullAndEmptyArrays { get; init; } = true;

    /// <summary>
    /// Whether this <c>$lookup</c> must be injected right after the root source rather than tail-appended, e.g.
    /// projected collection counts where a later stage reads the array via <c>$size</c>.
    /// </summary>
    public bool InjectAfterRoot { get; set; }

    /// <summary>
    /// Builds the <c>$lookup</c> stage document for this lookup: the plain <c>localField</c>/<c>foreignField</c>
    /// form, or — when <see cref="HasPipeline"/> — the pipeline form (<c>let</c> + <c>pipeline</c>, with the FK
    /// equality as the pipeline's own leading <c>$match</c>, followed by <see cref="PipelineStages"/>).
    /// </summary>
    /// <remarks>
    /// Shared by every <c>$lookup</c> construction site (fallback bridge, nested ThenInclude sub-lookups, native
    /// pipeline) so they can't drift on shape.
    /// </remarks>
    public BsonDocument ToLookupStageDocument()
    {
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

        var pipeline = new BsonArray
        {
            new BsonDocument("$match",
                new BsonDocument("$expr",
                    new BsonDocument("$eq", new BsonArray { $"${ForeignField}", "$$localField" })))
        };
        foreach (var stage in PipelineStages)
        {
            pipeline.Add(stage);
        }

        return new BsonDocument("$lookup", new BsonDocument
        {
            { "from", From },
            { "let", new BsonDocument("localField", $"${LocalField}") },
            { "pipeline", pipeline },
            { "as", As }
        });
    }

    /// <summary>Builds the <c>$unwind</c> stage document that flattens this lookup's output array.</summary>
    /// <remarks>
    /// <paramref name="preserveNullAndEmptyArrays"/> is a parameter, not read from
    /// <see cref="PreserveNullAndEmptyArrays"/>, because callers legitimately differ: the flat-lookup path follows
    /// the LINQ operator, while an <c>$unwind</c> nested inside a parent collection lookup's sub-pipeline is always
    /// preserving (otherwise it would drop collection elements, changing the Include's result set).
    /// </remarks>
    public BsonDocument ToUnwindStageDocument(bool preserveNullAndEmptyArrays)
        => UnwindStageDocument(As, preserveNullAndEmptyArrays);

    /// <summary>
    /// Builds an <c>$unwind</c> stage document for an arbitrary array path — for the driver-LINQ bridge's
    /// fixed <c>_inner</c> shape, which has no <see cref="LookupExpression"/> behind it.
    /// </summary>
    public static BsonDocument UnwindStageDocument(string path, bool preserveNullAndEmptyArrays)
        => new("$unwind", new BsonDocument
        {
            { "path", "$" + path },
            { "preserveNullAndEmptyArrays", preserveNullAndEmptyArrays }
        });
}
