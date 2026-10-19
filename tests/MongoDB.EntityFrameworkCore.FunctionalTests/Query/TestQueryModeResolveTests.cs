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
public class TestQueryModeResolveTests
{
    [Fact]
    public void Unset_resolves_to_Native()
    {
        Assert.Equal(MongoQueryMode.Native, TestQueryMode.Resolve(null, null));
        Assert.Equal(MongoQueryMode.Native, TestQueryMode.Resolve("", "  "));
        Assert.Equal(MongoQueryMode.Native, TestQueryMode.Resolve("0", null));
    }

    [Theory]
    [InlineData("NativeOnly")]
    [InlineData("nativeonly")]
    [InlineData("NATIVEONLY")]
    [InlineData(" nativeOnly ")]
    public void NativeOnly_is_case_insensitive(string value)
        => Assert.Equal(MongoQueryMode.NativeOnly, TestQueryMode.Resolve(null, value));

    [Theory]
    [InlineData("DriverLinq", MongoQueryMode.DriverLinq)]
    [InlineData("driverlinq", MongoQueryMode.DriverLinq)]
    [InlineData("Native", MongoQueryMode.Native)]
    public void Named_modes_resolve(string value, MongoQueryMode expected)
        => Assert.Equal(expected, TestQueryMode.Resolve(null, value));

    [Fact]
    public void Alias_alone_resolves_to_NativeOnly()
        => Assert.Equal(MongoQueryMode.NativeOnly, TestQueryMode.Resolve("1", null));

    [Fact]
    public void Alias_with_NativeOnly_query_mode_resolves_to_NativeOnly()
        => Assert.Equal(MongoQueryMode.NativeOnly, TestQueryMode.Resolve("1", "nativeonly"));

    [Theory]
    [InlineData("DriverLinq")]
    [InlineData("Native")]
    public void Alias_with_conflicting_query_mode_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryMode.Resolve("1", value));
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
    public void Unrecognised_query_mode_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryMode.Resolve(null, value));
        Assert.Contains("MONGODB_EF_QUERY_MODE", ex.Message);
    }

    [Fact]
    public void Garbage_query_mode_throws_even_with_alias()
        => Assert.Throws<InvalidOperationException>(() => TestQueryMode.Resolve("1", "garbage"));

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    public void Unrecognised_alias_value_throws(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestQueryMode.Resolve(value, null));
        Assert.Contains("MONGODB_EF_NATIVE_ONLY", ex.Message);
    }
}
