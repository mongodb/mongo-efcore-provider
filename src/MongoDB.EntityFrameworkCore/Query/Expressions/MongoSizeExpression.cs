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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a native <c>{ $size: "$&lt;field&gt;" }</c> aggregation expression over a named array
/// field. Currently used only for a projected collection-navigation <c>Count</c>
/// (<c>select new { ..., OrderCount = c.Orders.Count }</c>), where <see cref="FieldName"/> is the
/// synthetic <c>_lookup_&lt;Nav&gt;</c> array field written by the matching
/// <see cref="LookupExpression"/> (see <see cref="LookupExpression.GetLookupAlias"/>).
/// </summary>
/// <remarks>
/// This does not wrap a <see cref="MongoFieldExpression"/> because that node requires a backing
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>, and the <c>_lookup_&lt;Nav&gt;</c>
/// array is a synthetic pipeline field with no such property — it is written by a <c>$lookup</c> stage,
/// not mapped from the model. Holding the raw field name directly avoids inventing a second synthetic
/// field-reference node for a single, narrow use.
/// </remarks>
internal sealed class MongoSizeExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoSizeExpression"/> over the named array field.
    /// </summary>
    /// <param name="fieldName">The synthetic array field name (e.g. <c>_lookup_Orders</c>).</param>
    /// <param name="type">The CLR type of the resulting count (typically <see cref="int"/> or <see cref="long"/>).</param>
    public MongoSizeExpression(string fieldName, Type type)
    {
        FieldName = fieldName;
        Type = type;
    }

    /// <summary>The synthetic array field name this <c>$size</c> is computed over.</summary>
    public string FieldName { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
