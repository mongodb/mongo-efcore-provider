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
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Shared value-serialization helpers used by both the compile-time renderer
/// (<see cref="MongoQueryLanguageRenderer"/>, baking inline constants) and the per-execution
/// parameter binder (<see cref="MongoPipelineFactory"/>). Keeping a single implementation here
/// guarantees a captured constant and a runtime parameter of the same value serialize identically.
/// </summary>
internal static class BsonValueSerializer
{
    /// <summary>
    /// Coerces a CLR value to <paramref name="target"/> so the property/value serializer (which casts hard to
    /// its exact type) accepts it. Handles <c>Nullable&lt;T&gt;</c> by unwrapping to the underlying type, then
    /// applies enum and numeric promotion. Returns the value unchanged if no safe coercion applies.
    /// Ported verbatim from the spike's <c>MongoPredicateTranslator.CoerceToPropertyType</c>.
    /// </summary>
    public static object? Coerce(Type target, object? value)
    {
        if (value is null)
            return null;

        var resolved = Nullable.GetUnderlyingType(target) ?? target;
        var valueType = value.GetType();
        if (valueType == resolved)
            return value;

        if (resolved.IsEnum && Enum.GetUnderlyingType(resolved) is var enumBase &&
            (valueType == enumBase || value is IConvertible))
            return Enum.ToObject(resolved, value);

        if (value is IConvertible && (resolved.IsPrimitive || resolved == typeof(decimal)))
            return Convert.ChangeType(value, resolved, CultureInfo.InvariantCulture);

        return value;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> through <paramref name="serializer"/> using a
    /// <see cref="BsonDocumentWriter"/> with a <c>"v"</c> wrapper element, returning the wrapped value.
    /// This is the single serialize-block shared by the compile-time and run-time native paths so the two
    /// emit identical BSON. The caller is responsible for any coercion (via <see cref="Coerce"/>) beforehand.
    /// </summary>
    public static BsonValue SerializeThroughWriter(IBsonSerializer serializer, object? value)
    {
        var doc = new BsonDocument();
        using (var writer = new BsonDocumentWriter(doc))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            serializer.Serialize(BsonSerializationContext.CreateRoot(writer), value);
            writer.WriteEndDocument();
        }

        return doc["v"];
    }
}
