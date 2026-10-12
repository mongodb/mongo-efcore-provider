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
/// A native <c>{ $size: … }</c> aggregation expression over an array field, identified by its dotted document
/// path.
/// </summary>
/// <remarks>
/// Used for a projected collection-navigation <c>Count</c> over a <c>$lookup</c> alias (always an array, so the
/// plain form), and for an owned collection's count (<c>b.Posts.Count</c>), which may be missing or BSON null —
/// <c>$size</c> on either aborts the aggregate, so <see cref="NullSafe"/> wraps the path in <c>$ifNull</c>.
/// Not built on <see cref="MongoFieldExpression"/>, which requires a backing <c>IProperty</c>.
/// </remarks>
internal sealed class MongoSizeExpression : MongoExpression
{
    public MongoSizeExpression(string fieldName, Type type, bool nullSafe = false)
    {
        FieldName = fieldName;
        Type = type;
        NullSafe = nullSafe;
    }

    public string FieldName { get; }

    /// <summary>
    /// Whether the path is wrapped in <c>$ifNull</c> so a missing or null array counts as empty instead of
    /// aborting the aggregate.
    /// </summary>
    public bool NullSafe { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
