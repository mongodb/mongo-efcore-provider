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
        }
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

    /// <summary>Whether this lookup uses a pipeline (filtered Include).</summary>
    public bool HasPipeline => PipelineStages.Count > 0;

    /// <summary>Whether this lookup is for a single reference (not a collection).</summary>
    public bool IsReference => !Navigation.IsCollection;

    /// <summary>Whether $unwind should be applied after $lookup.</summary>
    public bool ShouldUnwind => IsReference || ForceUnwind;

    /// <summary>Whether $unwind is forced regardless of navigation type.</summary>
    public bool ForceUnwind { get; }

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
}
