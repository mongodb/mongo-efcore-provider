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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public class BsonTypeHelperTests
{
    [Theory]
    [InlineData(BsonType.Array, "array")]
    [InlineData(BsonType.Binary, "binData")]
    [InlineData(BsonType.Boolean, "bool")]
    [InlineData(BsonType.DateTime, "date")]
    [InlineData(BsonType.Decimal128, "decimal")]
    [InlineData(BsonType.Document, "object")]
    [InlineData(BsonType.Double, "double")]
    [InlineData(BsonType.Int32, "int")]
    [InlineData(BsonType.Int64, "long")]
    [InlineData(BsonType.JavaScript, "javascript")]
    [InlineData(BsonType.JavaScriptWithScope, "javascriptWithScope")]
    [InlineData(BsonType.MaxKey, "maxKey")]
    [InlineData(BsonType.MinKey, "minKey")]
    [InlineData(BsonType.Null, "null")]
    [InlineData(BsonType.ObjectId, "objectId")]
    [InlineData(BsonType.RegularExpression, "regex")]
    [InlineData(BsonType.String, "string")]
    [InlineData(BsonType.Symbol, "symbol")]
    [InlineData(BsonType.Timestamp, "timestamp")]
    [InlineData(BsonType.Undefined, "undefined")]
    public void BsonTypeToString_returns_expected_string(BsonType type, string expected)
    {
        Assert.Equal(expected, BsonTypeHelper.BsonTypeToString(type));
    }

    [Fact]
    public void BsonTypeToString_throws_for_unexpected_type()
    {
        Assert.Throws<ArgumentException>(() => BsonTypeHelper.BsonTypeToString((BsonType)999));
    }
}
