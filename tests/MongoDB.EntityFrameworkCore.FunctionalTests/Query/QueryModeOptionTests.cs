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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class QueryModeOptionTests
{
    [Fact]
    public void QueryMode_defaults_to_Native()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        new MongoDbContextOptionsBuilder(options); // no UseQueryMode call
        var ext = options.Options.FindExtension<MongoOptionsExtension>();
        Assert.Null(ext); // no extension added by just constructing the builder
    }

    [Fact]
    public void QueryMode_on_fresh_extension_defaults_to_Native()
    {
        var ext = new MongoOptionsExtension();
        Assert.Equal(MongoQueryMode.Native, ext.QueryMode);
    }

    [Fact]
    public void UseQueryMode_round_trips_through_the_extension()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        var optionsExtension = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(optionsExtension);
        new MongoDbContextOptionsBuilder(options).UseQueryMode(MongoQueryMode.DriverLinq);
        Assert.Equal(MongoQueryMode.DriverLinq, options.Options.FindExtension<MongoOptionsExtension>()!.QueryMode);
    }

    [Fact]
    public void UseQueryMode_NativeOnly_round_trips_through_the_extension()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        var optionsExtension = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(optionsExtension);
        new MongoDbContextOptionsBuilder(options).UseQueryMode(MongoQueryMode.NativeOnly);
        Assert.Equal(MongoQueryMode.NativeOnly, options.Options.FindExtension<MongoOptionsExtension>()!.QueryMode);
    }

    [Fact]
    public void WithQueryMode_does_not_mutate_original()
    {
        var original = new MongoOptionsExtension();
        var clone = original.WithQueryMode(MongoQueryMode.DriverLinq);

        Assert.Equal(MongoQueryMode.Native, original.QueryMode);
        Assert.Equal(MongoQueryMode.DriverLinq, clone.QueryMode);
    }

    [Fact]
    public void WithQueryMode_rejects_undefined_enum()
    {
        var extension = new MongoOptionsExtension();

        Assert.Throws<ArgumentOutOfRangeException>(() => extension.WithQueryMode((MongoQueryMode)99));
    }

    [Fact]
    public void QueryMode_survives_a_subsequent_With_clone()
    {
        var extension = new MongoOptionsExtension().WithQueryMode(MongoQueryMode.DriverLinq);

        var clone = extension.WithDatabaseName("SomeDatabase");

        Assert.Equal(MongoQueryMode.DriverLinq, clone.QueryMode);
    }

    [Fact]
    public void Differing_query_mode_yields_distinct_service_provider_hash_and_not_same_provider()
    {
        var native = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        var driverLinq = native.WithQueryMode(MongoQueryMode.DriverLinq);

        Assert.NotEqual(native.Info.GetServiceProviderHashCode(), driverLinq.Info.GetServiceProviderHashCode());
        Assert.False(native.Info.ShouldUseSameServiceProvider(driverLinq.Info));
    }

    [Fact]
    public void LogFragment_omits_query_mode_when_native_and_includes_when_not()
    {
        var native = new MongoOptionsExtension();
        var driverLinq = native.WithQueryMode(MongoQueryMode.DriverLinq);

        Assert.DoesNotContain("QueryMode", native.Info.LogFragment);
        Assert.Contains("QueryMode=DriverLinq", driverLinq.Info.LogFragment);
    }
}
