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
/// A raw reference to a document element by its (possibly dotted) path, with no associated
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>. Used by the native <c>$group</c>
/// flattening <c>$project</c> to read back the grouped output — the group <c>_id</c> (scalar key), a
/// composite sub-key (<c>_id.&lt;Name&gt;</c>), or an accumulator output field — into a top-level result
/// alias. Renders in the aggregation-expression dialect as <c>"$" + Path</c>.
/// </summary>
internal sealed class MongoElementRefExpression(string path, Type clrType) : MongoExpression
{
    /// <summary>The (possibly dotted) element path, e.g. <c>_id</c>, <c>_id.Country</c>, or <c>Total</c>.</summary>
    public string Path { get; } = path;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;
}
