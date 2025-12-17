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

using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Storage;

internal static class BsonTypeHelper
{
    internal static BsonType GetBsonType(IReadOnlyProperty property)
    {
        var representation = property.GetBsonRepresentation();
        return representation?.BsonType ?? BsonSerializerFactory.GetBsonType(property.ClrType);
    }

    internal static string BsonTypeToString(BsonType type)
        => type switch
        {
            BsonType.Array => "array",
            BsonType.Binary => "binData",
            BsonType.Boolean => "bool",
            BsonType.DateTime => "date",
            BsonType.Decimal128 => "decimal",
            BsonType.Document => "object",
            BsonType.Double => "double",
            BsonType.Int32 => "int",
            BsonType.Int64 => "long",
            BsonType.JavaScript => "javascript",
            BsonType.JavaScriptWithScope => "javascriptWithScope",
            BsonType.MaxKey => "maxKey",
            BsonType.MinKey => "minKey",
            BsonType.Null => "null",
            BsonType.ObjectId => "objectId",
            BsonType.RegularExpression => "regex",
            BsonType.String => "string",
            BsonType.Symbol => "symbol",
            BsonType.Timestamp => "timestamp",
            BsonType.Undefined => "undefined",
            _ => throw new ArgumentException($"Unexpected BSON type: {type}.", nameof(type))
        };
}
