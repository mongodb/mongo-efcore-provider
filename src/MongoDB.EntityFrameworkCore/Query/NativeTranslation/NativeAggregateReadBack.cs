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
