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

using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

// Unit-level: exercises the pure Resolve overload only; no environment variables or statics are touched.
public class TestQueryProviderResolveTests
{
    [Fact]
    public void Unset_resolves_to_Native()
    {
        Assert.Equal(MongoQueryProvider.Native, TestQueryProvider.Resolve(null, null));
        Assert.Equal(MongoQueryProvider.Native, TestQueryProvider.Resolve("", "  "));
        Assert.Equal(MongoQueryProvider.Native, TestQueryProvider.Resolve("0", null));
    }

    [Theory]
    [InlineData("NativeOnly")]
    [InlineData("nativeonly")]
    [InlineData("NATIVEONLY")]
    [InlineData(" nativeOnly ")]
    public void NativeOnly_is_case_insensitive(string value)
        => Assert.Equal(MongoQueryProvider.NativeOnly, TestQueryProvider.Resolve(null, value));

    [Theory]
    [InlineData("DriverLinq", MongoQueryProvider.DriverLinq)]
    [InlineData("driverlinq", MongoQueryProvider.DriverLinq)]
    [InlineData("Native", MongoQueryProvider.Native)]
    public void Named_modes_resolve(string value, MongoQueryProvider expected)
        => Assert.Equal(expected, TestQueryProvider.Resolve(null, value));

    [Fact]
    public void Alias_alone_resolves_to_NativeOnly()
        => Assert.Equal(MongoQueryProvider.NativeOnly, TestQueryProvider.Resolve("1", null));

    [Fact]
    public void Alias_with_NativeOnly_query_provider_resolves_to_NativeOnly()
        => Assert.Equal(MongoQueryProvider.NativeOnly, TestQueryProvider.Resolve("1", "nativeonly"));

    [Theory]
    [InlineData("DriverLinq")]
    [InlineData("Native")]
    public void Alias_with_conflicting_query_provider_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryProvider.Resolve("1", value));
        Assert.Contains("MONGODB_EF_NATIVE_ONLY", ex.Message);
        Assert.Contains("conflicts", ex.Message);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("Natve")]
    [InlineData("7")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("Native, DriverLinq")]
    public void Unrecognised_query_provider_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryProvider.Resolve(null, value));
        Assert.Contains("MONGODB_EF_QUERY_PROVIDER", ex.Message);
    }

    [Fact]
    public void Garbage_query_provider_throws_even_with_alias()
        => Assert.Throws<InvalidOperationException>(() => TestQueryProvider.Resolve("1", "garbage"));

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    public void Unrecognised_alias_value_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryProvider.Resolve(value, null));
        Assert.Contains("MONGODB_EF_NATIVE_ONLY", ex.Message);
    }
}
