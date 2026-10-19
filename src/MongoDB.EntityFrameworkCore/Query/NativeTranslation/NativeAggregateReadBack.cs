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
/// <remarks>
/// <see cref="FindOperandProperty"/> names the property whose stored value a <c>Min</c>/<c>Max</c> returns unchanged, so
/// the shaper reads the "v" field through that property's serializer (an enum, <c>DateOnly</c> or <c>char</c> is not the
/// generic mapped BSON value). <see cref="HasFaithfulServerOrdering"/> is the one predicate the binder's gate and any
/// other caller share for operands whose server ordering differs from the CLR one.
/// </remarks>
internal static class NativeAggregateReadBack
{
    /// <summary>
    /// The property whose stored value <paramref name="operand"/> (a terminal aggregate's operand) holds unchanged: a bare
    /// field, or an element reference to a bare-field leaf of <paramref name="select"/>'s own projection
    /// (<c>Select(e =&gt; e.P).Max()</c>). <see langword="null"/> for anything computed.
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
    /// Whether a terminal <c>Min</c>/<c>Max</c> with a non-nullable value-type result reduces <c>{_v: operand}</c>
    /// documents rather than the bare operand, as driver-LINQ does. A bare <c>$min</c>/<c>$max</c> skips a MISSING or null
    /// operand, so over a malformed document (a required element omitted or explicitly null) it answers another row's
    /// value, or null when no row has one, which read back as <c>0</c>. Document comparison orders <c>{}</c> (MISSING) below
    /// <c>{_v: null}</c> below any value, so the reduction keeps driver-LINQ's answer: a MISSING winner reads
    /// <c>default</c>, a null winner throws (<see cref="ReadWrappedValue"/>). The lowerer and the reader both call this.
    /// A nullable result keeps the bare reduction: LINQ's nullable <c>Min</c>/<c>Max</c> skip nulls, which driver-LINQ's
    /// wrapped reduction does not (a MISSING or null row wins its <c>Min</c>).
    /// </summary>
    internal static bool ReducesWrappedValue(MongoCardinality cardinality)
        => cardinality is { Aggregate: MongoAggregateOperator.Min or MongoAggregateOperator.Max }
           && cardinality.ResultType.IsValueType
           && Nullable.GetUnderlyingType(cardinality.ResultType) is null;

    /// <summary>
    /// The operand a terminal aggregate's <c>$group</c> accumulator reduces. For a <see cref="ReducesWrappedValue"/>
    /// aggregate whose operand (or the projection leaf it references: <c>Select(x =&gt; (long)x.Rank).Max()</c>) may be
    /// MISSING where driver-LINQ's rendering is null (a widening the translator dropped and the driver renders as
    /// <c>$toLong</c>; see <see cref="MongoAggregationExpressionRenderer.MayAnswerUnfaithfulMissing"/>), MISSING is mapped
    /// to null (<c>$ifNull: [operand, null]</c>) so the reduction, like the driver's, throws instead of reading
    /// <c>default</c>. Otherwise the selector itself.
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
    /// The value a <see cref="ReducesWrappedValue"/> aggregate's <paramref name="wrapped"/> winner holds:
    /// <see langword="null"/> when MISSING (<c>{}</c>, read as <c>default</c>); for an explicit null, throws EF's
    /// "Nullable object must have a value." (driver-LINQ's deserializer threw <see cref="FormatException"/>), or
    /// <see langword="null"/> (<c>default</c>) for a type whose driver deserializer reads null as default
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
    /// Whether the server's <c>$min</c>/<c>$max</c> over <paramref name="property"/>'s stored value orders like the CLR
    /// value. A default <c>TimeSpan</c> is stored as an invariant-format string ("00:01:00"), which the server orders
    /// lexicographically, so it does not; a numeric representation does.
    /// </summary>
    internal static bool HasFaithfulServerOrdering(IReadOnlyProperty property)
    {
        if (property.ClrType.UnwrapNullableType() != typeof(TimeSpan))
            return true;

        return property.GetBsonRepresentation() is { BsonType: not BsonType.String };
    }
}
