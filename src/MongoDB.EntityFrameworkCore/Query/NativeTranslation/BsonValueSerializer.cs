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
/// Value serialization shared by the compile-time renderer (<see cref="MongoQueryLanguageRenderer"/>, inline
/// constants) and the per-execution parameter binder (<see cref="MongoPipelineFactory"/>), so a constant and a
/// parameter of the same value serialize identically.
/// </summary>
internal static class BsonValueSerializer
{
    /// <summary>
    /// Field name wrapping a bare scalar in a <see cref="BsonDocument"/>; also used by the scalar-aggregate output
    /// stages (<c>$count</c>/<c>$group</c>) and read back in
    /// <see cref="Visitors.MongoShapedQueryCompilingExpressionVisitor"/>.
    /// </summary>
    internal const string ScalarField = "v";

    /// <summary>
    /// Coerces a CLR value to <paramref name="target"/> (unwrapping <c>Nullable&lt;T&gt;</c>; enum and numeric
    /// promotion) because serializers cast hard to their exact type. Returns the value unchanged otherwise.
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
    /// Serializes <paramref name="value"/> through <paramref name="serializer"/> inside a <c>"v"</c> wrapper and
    /// returns the wrapped value. Callers must <see cref="Coerce"/> first.
    /// </summary>
    public static BsonValue SerializeThroughWriter(IBsonSerializer serializer, object? value)
    {
        var doc = new BsonDocument();
        using (var writer = new BsonDocumentWriter(doc))
        {
            writer.WriteStartDocument();
            writer.WriteName(ScalarField);
            serializer.Serialize(BsonSerializationContext.CreateRoot(writer), value);
            writer.WriteEndDocument();
        }

        return doc[ScalarField];
    }
}
