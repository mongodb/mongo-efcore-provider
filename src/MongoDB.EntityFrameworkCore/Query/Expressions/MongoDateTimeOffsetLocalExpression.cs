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
/// Reconstructs the "local" <see cref="DateTime"/> from a stored <c>DateTimeOffset</c> field — this provider
/// stores <c>DateTimeOffset</c> as a subdocument with <c>DateTime</c> (UTC) and <c>Offset</c> (minutes)
/// fields (<c>Storage/Serializers.BsonSerializerFactory</c> — the driver's own <c>DateTimeOffsetSerializer</c>,
/// <c>BsonType.Document</c>), so the local value is the sum of the two, rendered via <c>$dateAdd</c>.
/// </summary>
/// <remarks>
/// This is the same reconstruction <c>MongoEFToLinqTranslatingExpressionVisitor</c> already performs for the
/// driver-LINQ bridge (EF-218, working around CSHARP-5296), re-expressed as a native aggregation-expression
/// node.
/// <para>
/// <see cref="Operand"/> is deliberately typed as <see cref="MongoFieldExpression"/>, not the general
/// <see cref="MongoExpression"/> — the renderer dots <c>.DateTime</c>/<c>.Offset</c> onto its element path,
/// which is only valid MQL when the operand is a plain field reference; there is no way to sub-field-access an
/// arbitrary COMPUTED document value without <c>$getField</c>, which this node does not attempt.
/// </para>
/// <para>
/// Two facts this node's own <see cref="Type"/>/rendering deliberately do NOT distinguish, carried here so a
/// maintainer doesn't need to archaeology into the driver bridge to learn them: (1) this node backs BOTH
/// <c>.DateTime</c> AND <c>.LocalDateTime</c> — the two are treated identically because true
/// <c>.LocalDateTime</c> semantics need the *executing machine's* time zone, which has no meaning server-side;
/// both use the value's own stored <c>Offset</c> instead. (2) the reconstructed <c>DateTime</c> is
/// millisecond-truncated relative to full tick precision (the stored <c>DateTime</c> sub-field itself loses
/// sub-millisecond ticks vs. client-side evaluation via <c>.Ticks</c>). Neither is a bug; both mirror the
/// pre-existing driver-LINQ bridge behavior (<c>MongoEFToLinqTranslatingExpressionVisitor</c>) exactly.
/// </para>
/// </remarks>
internal sealed class MongoDateTimeOffsetLocalExpression(MongoFieldExpression operand) : MongoExpression
{
    /// <summary>The <c>DateTimeOffset</c>-typed field to reconstruct the local time from.</summary>
    public MongoFieldExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(DateTime);
}
