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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

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
    }

    /// <summary>
    /// The synthetic field name that a cross-collection <c>$lookup</c> writes its joined documents to
    /// (the lookup's <see cref="As"/>) and that the shaper reads them back from. Centralized so every
    /// write site (the lookup stage) and read site (projection binding) derive the identical alias from
    /// the navigation, rather than re-spelling the <c>_lookup_</c> format independently and risking a
    /// write/read mismatch.
    /// </summary>
    /// <param name="navigation">The navigation the lookup supports.</param>
    /// <returns>The <c>_lookup_&lt;NavigationName&gt;</c> field name.</returns>
    public static string GetLookupAlias(IReadOnlyNavigationBase navigation)
        => $"_lookup_{navigation.Name}";

    /// <summary>The navigation this lookup supports.</summary>
    public INavigation Navigation { get; }

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
    /// Pipeline stages to apply inside the $lookup for filtered Includes
    /// (e.g., OrderBy, Skip, Take on the included collection).
    /// When non-empty, the pipeline form of $lookup is used instead of localField/foreignField.
    /// </summary>
    public List<BsonDocument> PipelineStages { get; } = [];

    /// <summary>
    /// User filtered-Include / dependent query-filter predicates applied to this cross-collection target
    /// (e.g. <c>.Include(c =&gt; c.Orders.Where(o =&gt; o.Total &gt; 100))</c>), in execution order. Captured as raw
    /// element-parameter lambdas during pipeline extraction and rendered to a sub-pipeline <c>$match</c> by the
    /// driver (using the EF entity serializer) when the $lookup is emitted — the provider never hand-builds the
    /// predicate BSON. The <c>$match</c> stages are emitted before any paging (<c>$sort</c>/<c>$skip</c>/<c>$limit</c>).
    /// </summary>
    public List<LambdaExpression> FilterPredicates { get; } = [];

    /// <summary>
    /// Best-effort filter predicates (a source-side query filter reached after descending through a
    /// ThenInclude's Select/Join). Rendered into the sub-pipeline <c>$match</c> if the driver can express
    /// them AND the lookup already uses the pipeline form, otherwise DROPPED — these are frequently redundant
    /// query filters and may reference a navigation that has no <c>$lookup</c> sub-pipeline representation.
    /// They intentionally do NOT force the pipeline form (so a lookup that would otherwise use
    /// localField/foreignField keeps that shape, dropping the predicate as before). Distinct from
    /// <see cref="FilterPredicates"/>, which are explicit user filtered-Include predicates that fail loudly if
    /// unrenderable. EF-X021.
    /// </summary>
    public List<LambdaExpression> BestEffortFilterPredicates { get; } = [];

    /// <summary>Whether this lookup uses a pipeline (filtered Include) rather than localField/foreignField.</summary>
    public bool HasPipeline => PipelineStages.Count > 0 || FilterPredicates.Count > 0;

    /// <summary>
    /// Filter-predicate `$match` stages for NESTED ThenInclude targets that are deferred to
    /// <see cref="Visitors.MongoEFToLinqTranslatingExpressionVisitor.EmitLookupStages"/> for rendering. A nested
    /// `$lookup` is hand-assembled during projection binding (where the EF→driver-LINQ visitor / serializer are
    /// unavailable), so its user-filter predicate cannot be rendered there. Each entry records the nested
    /// pipeline (<see cref="BsonArray"/>, mutated in place before serialization), the index at which to insert
    /// the rendered `$match` (after the FK-correlation `$match`, before paging), the predicate lambdas, and the
    /// nested target entity type whose serializer renders them. Collected on the ROOT lookup (bubbled up through
    /// nesting). EF-X021.
    /// </summary>
    public List<(BsonArray Pipeline, int Index, IReadOnlyList<LambdaExpression> Predicates, IEntityType TargetEntityType)>
        PendingNestedFilterRenders { get; } = [];

    /// <summary>Whether this lookup is for a single reference (not a collection).</summary>
    public bool IsReference => !Navigation.IsCollection;

    /// <summary>Whether $unwind should be applied after $lookup.</summary>
    public bool ShouldUnwind => IsReference || ForceUnwind;

    /// <summary>Whether $unwind is forced regardless of navigation type.</summary>
    public bool ForceUnwind { get; }

    /// <summary>
    /// Whether this $lookup must be injected right after the root collection source (before the user's
    /// downstream pipeline stages) rather than tail-appended. Used for projected collection-navigation
    /// counts (<c>select new { ..., c.Orders.Count }</c>) where a later <c>$match</c>/<c>$project</c>
    /// reads the <c>_lookup_&lt;Nav&gt;</c> array via <c>{ $size: ... }</c> and so must see it already present.
    /// </summary>
    public bool InjectAfterRoot { get; set; }

    /// <summary>
    /// Whether the parent sub-document of this lookup's dotted <see cref="As"/> path (an OPTIONAL
    /// cross-collection reference that was left-joined with <c>preserveNullAndEmptyArrays</c>) must be
    /// re-nulled when its key is absent. Writing a nested <c>$lookup</c> result into a dotted path
    /// (<c>_inner._lookup_&lt;Nav&gt;</c>) makes MongoDB synthesise the parent even when the reference had no
    /// match, producing a present-but-keyless document that defeats the left-join null guard. Only set for
    /// optional reference parents — required references always match (no synthesis) and the root is never
    /// absent. See <see cref="Visitors.MongoEFToLinqTranslatingExpressionVisitor"/>. EF-X024.
    /// </summary>
    public bool NormalizeParent { get; set; }
}
