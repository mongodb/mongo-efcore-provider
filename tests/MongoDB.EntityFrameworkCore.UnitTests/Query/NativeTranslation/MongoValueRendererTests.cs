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

        // A sentinel was produced and recorded in the table under the parameter name.
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
        Assert.Single(placeholders.Entries);
        Assert.Equal("p0", placeholders.Entries[0].Name);
    }

    [Fact]
    public void RenderValue_property_less_array_element_parameter_creates_array_element_placeholder()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoParameterExpression("args_0", forSerialization: null, arrayElementIndex: 2);

        var result = MongoValueRenderer.RenderValue(node, placeholders);

        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
        Assert.Single(placeholders.Entries);
        var entry = placeholders.Entries[0];
        Assert.Equal("args_0", entry.Name);
        Assert.Null(entry.Serializer);
        Assert.False(entry.IsArray);
        Assert.Equal(2, entry.ArrayElementIndex);
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
        // RenderUnary's !(x == value) arm treats a document whose first key starts with '$' as an operator
        // document and skips the { $eq: … } wrap. A '$'-prefixed sentinel would be misclassified, emitting the
        // illegal { field: { $not: <bareValue> } } for every parameterized equality negation.
        Assert.False(PlaceholderTable.SentinelKey.StartsWith('$'));
    }
}
