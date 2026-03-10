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

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Storage.ValueConversion;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage.ValueConversion;

public class MongoValueConverterSelectorTests
{
    private static MongoValueConverterSelector CreateSelector()
        => new(new ValueConverterSelectorDependencies());

    [Fact]
    public void Select_ObjectId_to_string_returns_converter()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(ObjectId), typeof(string)).ToList();

        Assert.Contains(converters, c => c.Create() is ObjectIdToStringConverter);
    }

    [Fact]
    public void Select_ObjectId_with_null_provider_returns_string_converter()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(ObjectId)).ToList();

        Assert.Contains(converters, c => c.Create() is ObjectIdToStringConverter);
    }

    [Fact]
    public void Select_string_to_ObjectId_returns_converter()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(string), typeof(ObjectId)).ToList();

        Assert.Contains(converters, c => c.Create() is StringToObjectIdConverter);
    }

    [Fact]
    public void Select_Decimal128_to_decimal_returns_converter()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(Decimal128), typeof(decimal)).ToList();

        Assert.Contains(converters, c => c.Create() is Decimal128ToDecimalConverter);
    }

    [Fact]
    public void Select_decimal_to_Decimal128_returns_converter()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(decimal), typeof(Decimal128)).ToList();

        Assert.Contains(converters, c => c.Create() is DecimalToDecimal128Converter);
    }

    [Fact]
    public void Select_includes_base_converters()
    {
        var selector = CreateSelector();

        var converters = selector.Select(typeof(int), typeof(string)).ToList();

        Assert.NotEmpty(converters);
    }

    [Fact]
    public void Select_returns_consistent_results_across_calls()
    {
        var selector = CreateSelector();

        var first = selector.Select(typeof(ObjectId), typeof(string)).ToList();
        var second = selector.Select(typeof(ObjectId), typeof(string)).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(c => c.ProviderClrType), second.Select(c => c.ProviderClrType));
        Assert.Equal(first.Select(c => c.ModelClrType), second.Select(c => c.ModelClrType));
    }
}
