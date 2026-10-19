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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// How a terminal <c>Min</c>/<c>Max</c> reads its server value back, and which operands the server can reduce faithfully.
/// </summary>
internal static class NativeAggregateReadBack
{
    /// <summary>
    /// The property whose stored value a terminal aggregate's <paramref name="operand"/> holds unchanged (a bare field, or a
    /// reference to a bare-field leaf of <paramref name="select"/>'s projection); <see langword="null"/> for anything computed.
    /// </summary>
    internal static IReadOnlyProperty? FindOperandProperty(MongoSelectDefinition select, MongoExpression operand)
    {
        switch (operand)
        {
            case MongoFieldExpression field:
                return field.Property;

            case MongoElementRefExpression { ValueProperty: { } valueProperty }:
                return valueProperty;

            case MongoElementRefExpression elementRef:
                foreach (var projection in select.Projection)
                {
                    if (projection.Alias == elementRef.Path)
                        return projection.Expression is MongoFieldExpression leaf ? leaf.Property : null;
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>The element a <see cref="ReducesWrappedValue"/> aggregate wraps each operand value in.</summary>
    internal const string WrappedValueField = "_v";

    /// <summary>
    /// Whether a terminal <c>Min</c>/<c>Max</c> with a non-nullable value-type result reduces <c>{_v: operand}</c> documents, as
    /// driver-LINQ does. A bare <c>$min</c>/<c>$max</c> skips MISSING/null operands, so over a malformed document it would answer
    /// another row's value (or <c>0</c>); document comparison orders <c>{}</c> below <c>{_v: null}</c> below any value, so a MISSING
    /// winner reads <c>default</c> and a null winner throws (<see cref="ReadWrappedValue"/>). A nullable result keeps the bare
    /// reduction, since LINQ's nullable <c>Min</c>/<c>Max</c> skip nulls.
    /// </summary>
    internal static bool ReducesWrappedValue(MongoCardinality cardinality)
        => cardinality is { Aggregate: MongoAggregateOperator.Min or MongoAggregateOperator.Max }
           && cardinality.ResultType.IsValueType
           && Nullable.GetUnderlyingType(cardinality.ResultType) is null;

    /// <summary>
    /// The operand a terminal aggregate's <c>$group</c> accumulator reduces. For a <see cref="ReducesWrappedValue"/> aggregate whose
    /// operand may be MISSING where driver-LINQ renders null (a widening the translator dropped and the driver renders as
    /// <c>$toLong</c>; see <see cref="MongoAggregationExpressionRenderer.MayAnswerUnfaithfulMissing"/>), MISSING is mapped to null
    /// so the reduction throws like the driver's instead of reading <c>default</c>.
    /// </summary>
    internal static MongoExpression ReductionOperand(MongoSelectDefinition select, MongoCardinality cardinality)
    {
        var operand = cardinality.Selector!;
        if (!ReducesWrappedValue(cardinality))
            return operand;

        var leaf = operand;
        if (operand is MongoElementRefExpression elementRef)
        {
            foreach (var projection in select.Projection)
            {
                if (projection.Alias == elementRef.Path)
                    leaf = projection.Expression;
            }
        }

        return MongoAggregationExpressionRenderer.MayAnswerUnfaithfulMissing(cardinality.ResultType, leaf)
            ? new MongoCoalesceExpression(operand, new MongoConstantExpression(null, forSerialization: null))
            : operand;
    }

    /// <summary>
    /// The value a <see cref="ReducesWrappedValue"/> aggregate's winner holds: <see langword="null"/> when MISSING (read as
    /// <c>default</c>); for an explicit null, throws EF's "Nullable object must have a value." (driver-LINQ threw
    /// <see cref="FormatException"/>), or <see langword="null"/> for a type whose driver deserializer reads null as default
    /// (<see cref="MongoAggregationExpressionRenderer.DriverReadsNullAsDefault"/>: <c>bool</c>).
    /// </summary>
    internal static BsonValue? ReadWrappedValue(BsonValue wrapped, Type resultType)
    {
        if (wrapped is not BsonDocument document || !document.TryGetValue(WrappedValueField, out var value))
            return null;

        if (!value.IsBsonNull)
            return value;

        return MongoAggregationExpressionRenderer.DriverReadsNullAsDefault(resultType)
            ? null
            : throw new InvalidOperationException("Nullable object must have a value.");
    }

    /// <summary>
    /// Whether the server's <c>$min</c>/<c>$max</c> over <paramref name="property"/> orders like the CLR value. A default
    /// <c>TimeSpan</c> is stored as an invariant string the server orders lexicographically; a numeric representation is fine.
    /// </summary>
    internal static bool HasFaithfulServerOrdering(IReadOnlyProperty property)
    {
        if (property.ClrType.UnwrapNullableType() != typeof(TimeSpan))
            return true;

        return property.GetBsonRepresentation() is { BsonType: not BsonType.String };
    }
}
