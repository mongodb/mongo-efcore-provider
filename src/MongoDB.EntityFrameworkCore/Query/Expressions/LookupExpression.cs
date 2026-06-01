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
    /// <param name="parentAlias">
    /// Optional running dotted alias path of an ancestor lookup (e.g. <c>_lookup_Customer</c>) when this
    /// lookup is a nested child of a reference-rooted <c>ThenInclude</c> chain. When non-null, both
    /// <see cref="LocalField"/> and <see cref="As"/> are prefixed with this path so the join reads from
    /// — and writes back into — the unwound parent object rather than the document root.
    /// </param>
    public LookupExpression(INavigation navigation, bool forceUnwind = false, string? parentAlias = null)
    {
        Navigation = navigation;
        ForceUnwind = forceUnwind;

        var foreignKey = navigation.ForeignKey;
        var targetEntityType = navigation.TargetEntityType;
        From = targetEntityType.GetCollectionName();

        string localField;
        if (navigation.IsOnDependent)
        {
            // e.g., Order.Customer where FK (CustomerId) is on Order
            localField = GetFieldPath(foreignKey.Properties[0]);
            ForeignField = GetFieldPath(foreignKey.PrincipalKey.Properties[0]);
        }
        else
        {
            // e.g., Customer.Orders where FK (CustomerId) is on Order
            localField = GetFieldPath(foreignKey.PrincipalKey.Properties[0]);
            ForeignField = GetFieldPath(foreignKey.Properties[0]);
        }

        var alias = GetAlias(navigation);
        if (parentAlias is not null)
        {
            LocalField = $"{parentAlias}.{localField}";
            As = $"{parentAlias}.{alias}";
        }
        else
        {
            LocalField = localField;
            As = alias;
        }
    }

    /// <summary>
    /// The single source of truth for the <c>$lookup</c> output field name for a navigation.
    /// Both the producer (this expression's <see cref="As"/>) and the shaper-side consumer
    /// (<c>EntityProjectionExpression.BindNavigation</c>) must use this so the field written by
    /// the <c>$lookup</c> stage and the field the shaper reads always agree.
    /// </summary>
    public static string GetAlias(INavigation navigation)
        => $"_lookup_{navigation.Name}";

    /// <summary>The navigation this lookup supports.</summary>
    public INavigation Navigation { get; }

    /// <summary>The target collection name to look up from.</summary>
    public string From { get; }

    /// <summary>The field on the local document to match.</summary>
    public string LocalField { get; }

    /// <summary>The field on the foreign document to match.</summary>
    public string ForeignField { get; }

    /// <summary>The output array field name in the resulting document.</summary>
    public string As { get; }

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
}
