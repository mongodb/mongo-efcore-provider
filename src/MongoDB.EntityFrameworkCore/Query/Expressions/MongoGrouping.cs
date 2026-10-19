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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Dialect-neutral IR for a native <c>$group</c>: the grouping key and the accumulators produced over each group.
/// </summary>
internal sealed class MongoGrouping(
    IReadOnlyList<MongoGroupingKeyPart> key,
    IReadOnlyList<MongoGroupAccumulator> accumulators)
{
    /// <summary>Grouping key parts. A single part with <see cref="MongoGroupingKeyPart.Name"/> null is a scalar key
    /// rendered directly as <c>_id</c>; named parts render as a composite <c>_id</c> sub-document.</summary>
    public IReadOnlyList<MongoGroupingKeyPart> Key { get; } = key;

    /// <summary>Accumulators, one per aggregate output field.</summary>
    public IReadOnlyList<MongoGroupAccumulator> Accumulators { get; } = accumulators;

    public bool IsCompositeKey => Key.Count != 1 || Key[0].Name != null;
}

/// <summary>One part of a grouping key. <paramref name="Name"/> is null for a scalar (single-part) key.</summary>
internal sealed record MongoGroupingKeyPart(string? Name, MongoExpression FieldRef);

/// <summary>One <c>$group</c> accumulator. <paramref name="Operand"/> is null for count (<c>$sum: 1</c>).</summary>
internal sealed record MongoGroupAccumulator(string OutputField, string Operator, MongoExpression? Operand);
