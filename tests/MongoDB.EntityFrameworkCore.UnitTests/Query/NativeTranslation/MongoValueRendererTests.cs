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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoValueRendererTests
{
    [Fact]
    public void RenderValue_property_less_constant_creates_bson_value()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoConstantExpression(42, forSerialization: null);

        var result = MongoValueRenderer.RenderValue(node, placeholders);

        Assert.Equal(BsonValue.Create(42), result);
    }

    [Fact]
    public void RenderValue_property_less_parameter_creates_placeholder()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoParameterExpression("p0", forSerialization: null);

        var result = MongoValueRenderer.RenderValue(node, placeholders);

        // A sentinel placeholder value was produced AND recorded in the table under the parameter name.
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
        Assert.Single(placeholders.Entries);
        Assert.Equal("p0", placeholders.Entries[0].Name);
    }

    [Fact]
    public void RenderValue_unsupported_node_throws_native_not_supported()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoFieldExpression(property: null!, elementName: "x");

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => MongoValueRenderer.RenderValue(node, placeholders));
    }

    [Fact]
    public void SentinelKey_is_not_dollar_prefixed()
    {
        // Final-review finding I-1: MongoQueryLanguageRenderer.RenderUnary's !(x == value) arm distinguishes
        // an already-built operator document (e.g. { $gt: 5 }) from a bare value it must still wrap in
        // { $eq: … } by checking whether the value is a BsonDocument whose first element name starts with
        // '$'. The one document-valued value that check actually sees is THIS sentinel (a parameterized
        // equality's rendered value) — so the wrap is correct only because the sentinel key is not
        // '$'-prefixed. If it ever became one, RenderUnary would misclassify the sentinel as an operator
        // document and skip the $eq wrap, emitting the illegal { field: { $not: <bareValue> } } form for
        // every parameterized equality negation.
        Assert.False(PlaceholderTable.SentinelKey.StartsWith('$'));
    }
}
