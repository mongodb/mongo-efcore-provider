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
/// <param name="Name">The key part's name, or null for a scalar key.</param>
/// <param name="FieldRef">The keyed value.</param>
/// <param name="ThrowsOnNull">
/// A projected Distinct's key part carried from its projection's <see cref="MongoProjection.ThrowsOnNull"/>: the value
/// is a null-propagated scalar behind a non-nullable type. Operators resolving the flattened alias (through
/// <c>MongoExpressionTranslator.DistinctAliasScope</c>, which sees only this grouping) mark their element reference
/// with it.
/// </param>
/// <param name="MarksMissing">
/// A named key part over a value that may be MISSING (<c>NonNullableValueRead.DefaultOnMalformedMissing</c>: a projected
/// Distinct's <c>x.Rank > 1 ? -1 : x.Rank</c>). The <c>$group</c> <c>_id</c> also carries
/// <c>&lt;Name&gt;</c><see cref="MissingMarkerSuffix"/>: <c>{ $eq: [ { $type: value }, "missing" ] }</c>, since an
/// <c>_id</c> whose only sub-key is MISSING answers <c>{ Name: null }</c>, one group with an explicit null. With the
/// marker a missing and a null value form two groups, as driver-LINQ's grouping on the projected document did, and the
/// flatten restores MISSING from it.
/// </param>
internal sealed record MongoGroupingKeyPart(
    string? Name, MongoExpression FieldRef, bool ThrowsOnNull = false, bool MarksMissing = false)
{
    /// <summary>Suffix of the <c>_id</c> sub-key holding a <see cref="MarksMissing"/> part's missing marker.</summary>
    internal const string MissingMarkerSuffix = "__isMissing";
}

/// <summary>One <c>$group</c> accumulator. <paramref name="Operand"/> is null for count (<c>$sum: 1</c>).</summary>
internal sealed record MongoGroupAccumulator(string OutputField, string Operator, MongoExpression? Operand);
