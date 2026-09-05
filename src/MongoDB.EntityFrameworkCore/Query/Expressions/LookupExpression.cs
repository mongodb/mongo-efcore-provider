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
/// Distinguishes WHY this lookup carries a non-empty <see cref="LookupExpression.PipelineStages"/> sub-pipeline, since
/// each reason has a different native-eligibility answer. <see cref="None"/>: no pipeline stages.
/// <see cref="FallbackOnly"/>: TPH discriminator narrowing or a filtered Include (OrderBy/Skip/Take on the
/// Include target, EF-440) — both remain fallback/mixed-visitor-only (see
/// <c>MongoSelectLowerer.AppendLookupStages</c>'s exhaustive pipeline-kind dispatch).
/// <see cref="NestedInclude"/>: a collection-then-collection/reference <c>ThenInclude</c> (EF-450) — its
/// nested <c>$lookup</c>(s), staged by <c>MongoProjectionBindingExpressionVisitor</c>'s
/// <c>ExtractNestedIncludePipeline</c>/<c>ExtractThenIncludesFromSubquery</c>/<c>AddReferenceLookupStages</c>,
/// render via the same <c>let</c>+<c>pipeline</c> shape (<see cref="LookupExpression.ToLookupStageDocument"/>)
/// the driver-LINQ fallback bridge already used. <see cref="CorrelatedReducer"/>: a reference-collection-nav
/// First/FirstOrDefault projection leaf (EF-449) — rendered via the DIFFERENT localField/foreignField+pipeline
/// shape (<c>MongoPipelineFactory.RenderLookup</c>'s own dispatch), since that shape has a real equality FK to
/// combine with the pipeline, unlike <see cref="NestedInclude"/>'s own correlation.
/// </summary>
internal enum LookupPipelineKind
{
    None,
    FallbackOnly,
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
    /// <summary>
    /// Create a <see cref="LookupExpression"/> for the given navigation.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> that requires a <c>$lookup</c>.</param>
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
            // e.g., Order.Customer where FK (CustomerId) is on Order
            LocalField = GetFieldPath(foreignKey.Properties[0]);
            ForeignField = GetFieldPath(foreignKey.PrincipalKey.Properties[0]);
        }
        else
        {
            // e.g., Customer.Orders where FK (CustomerId) is on Order
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

    /// <summary>The field on the local document to match.</summary>
    public string LocalField { get; set; }

    /// <summary>The field on the foreign document to match.</summary>
    public string ForeignField { get; }

    /// <summary>The output array field name in the resulting document.</summary>
    public string As { get; set; }

    /// <summary>
    /// Get the full MongoDB field path for a property, accounting for composite keys
    /// stored under the _id document.
    /// </summary>
    private static string GetFieldPath(IReadOnlyProperty property)
    {
        var elementName = property.GetElementName();

        // For properties that are part of a composite primary key, they are stored nested
        // under _id (e.g., { _id: { OrderID: 10248, ProductID: 11 } }).
        // The element name alone won't match — we need the full path _id.OrderID.
        if (property.IsPrimaryKey()
            && property.DeclaringType is IEntityType entityType
            && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return $"_id.{elementName}";
        }

        return elementName;
    }

    /// <summary>
    /// Pipeline stages to apply inside the <c>$lookup</c> for filtered Includes
    /// (e.g., OrderBy, Skip, Take on the included collection).
    /// When non-empty, the pipeline form of <c>$lookup</c> is used instead of localField/foreignField.
    /// </summary>
    public List<BsonDocument> PipelineStages { get; } = [];

    /// <summary>See <see cref="LookupPipelineKind"/>.</summary>
    /// <remarks>
    /// Compile-time state on an object reused across executions, so the same write-once discipline
    /// <see cref="PreserveNullAndEmptyArrays"/> enforces with <see langword="init"/> is EXPECTED of callers
    /// here, even though it cannot be enforced by the language: this property must be settable after
    /// construction (the fallback visitor stamps it once the lookup's disposition is known), so it stays a
    /// plain <c>internal set</c>. Write it exactly once, at registration; never re-stamp a kind an earlier
    /// registration already chose — in particular, an object-initializer assignment runs AFTER the
    /// constructor and would silently overwrite the <see cref="LookupPipelineKind.FallbackOnly"/> the
    /// TPH-discriminator branch of the constructor sets, leaving the discriminator <c>$match</c> it prepended
    /// to <see cref="PipelineStages"/> unaccounted for.
    /// </remarks>
    public LookupPipelineKind PipelineKind { get; internal set; } = LookupPipelineKind.None;

    /// <summary>Whether this lookup uses a pipeline (filtered Include).</summary>
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

    /// <summary>Whether $unwind is forced regardless of navigation type.</summary>
    public bool ForceUnwind { get; }

    /// <summary>
    /// A single-level collection Include the native pipeline can emit as a <c>$lookup</c> array (no
    /// <c>$unwind</c>), readable by the DOM collection materializer from a root-level
    /// <c>_lookup_&lt;Nav&gt;</c> field: a collection nav, no filtered-Include pipeline stages, not
    /// force-unwound, and <see cref="As"/> equal to the plain alias (excludes the driver-LeftJoin and
    /// flat-nested shapes, which remain fallback-only).
    /// </summary>
    /// <remarks>
    /// A navigation-LESS lookup (an EF-377 <c>Join</c> hop with no model navigation) is never a collection
    /// Include, so it is excluded here rather than dereferenced — <see cref="Navigation"/> is nullable.
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
    /// Builds the <c>$lookup</c> stage document for this lookup: the plain <c>localField</c>/<c>foreignField</c>
    /// form, or — when <see cref="HasPipeline"/> — the pipeline form (<c>let</c> + <c>pipeline</c>, with the FK
    /// equality as the pipeline's own leading <c>$match</c>, followed by <see cref="PipelineStages"/>).
    /// </summary>
    /// <remarks>
    /// Shared by every <c>$lookup</c> construction site so they can't drift on shape: the driver-LINQ fallback
    /// bridge (<c>MongoEFToLinqTranslatingExpressionVisitor.EmitLookupStages</c>), a nested ThenInclude
    /// sub-lookup embedded in a parent's own <see cref="PipelineStages"/>
    /// (<c>MongoProjectionBindingExpressionVisitor.BuildLookupDocument</c>), and the native pipeline
    /// (<c>MongoPipelineFactory.RenderLookup</c>).
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
}
