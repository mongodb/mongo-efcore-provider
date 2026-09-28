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
/// Native equivalent of the driver-LINQ bridge's reconstruction in
/// <c>MongoEFToLinqTranslatingExpressionVisitor</c> (works around CSHARP-5296).
/// <see cref="Operand"/> is a <see cref="MongoFieldExpression"/> because the renderer dots
/// <c>.DateTime</c>/<c>.Offset</c> onto its path, which is only valid for a plain field reference.
/// Backs both <c>.DateTime</c> and <c>.LocalDateTime</c> (the client's time zone has no server-side meaning, so
/// the stored offset is used), and the result is millisecond-truncated — both matching the bridge.
/// </remarks>
internal sealed class MongoDateTimeOffsetLocalExpression(MongoFieldExpression operand) : MongoExpression
{
    /// <summary>The <c>DateTimeOffset</c>-typed field to reconstruct the local time from.</summary>
    public MongoFieldExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(DateTime);
}
