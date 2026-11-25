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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class QueryProviderOptionTests
{
    [Fact]
    public void QueryProvider_defaults_to_Native()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        new MongoDbContextOptionsBuilder(options); // no UseQueryProvider call
        var ext = options.Options.FindExtension<MongoOptionsExtension>();
        Assert.Null(ext); // no extension added by just constructing the builder
    }

    [Fact]
    public void QueryProvider_on_fresh_extension_defaults_to_the_process_default()
    {
        // Native unless MONGODB_EF_QUERY_PROVIDER / MONGODB_EF_NATIVE_ONLY overrides the process-wide default.
        var ext = new MongoOptionsExtension();
        Assert.Equal(TestQueryProvider.Current, ext.QueryProvider);
    }

    [Fact]
    public void UseQueryProvider_round_trips_through_the_extension()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        var optionsExtension = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(optionsExtension);
        new MongoDbContextOptionsBuilder(options).UseQueryProvider(MongoQueryProvider.DriverLinq);
        Assert.Equal(MongoQueryProvider.DriverLinq, options.Options.FindExtension<MongoOptionsExtension>()!.QueryProvider);
    }

    [Fact]
    public void UseQueryProvider_NativeOnly_round_trips_through_the_extension()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        var optionsExtension = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(optionsExtension);
        new MongoDbContextOptionsBuilder(options).UseQueryProvider(MongoQueryProvider.NativeOnly);
        Assert.Equal(MongoQueryProvider.NativeOnly, options.Options.FindExtension<MongoOptionsExtension>()!.QueryProvider);
    }

    [Fact]
    public void WithQueryProvider_does_not_mutate_original()
    {
        var original = new MongoOptionsExtension().WithQueryProvider(MongoQueryProvider.Native);
        var clone = original.WithQueryProvider(MongoQueryProvider.DriverLinq);

        Assert.Equal(MongoQueryProvider.Native, original.QueryProvider);
        Assert.Equal(MongoQueryProvider.DriverLinq, clone.QueryProvider);
    }

    [Fact]
    public void WithQueryProvider_rejects_undefined_enum()
    {
        var extension = new MongoOptionsExtension();

        Assert.Throws<ArgumentOutOfRangeException>(() => extension.WithQueryProvider((MongoQueryProvider)99));
    }

    [Fact]
    public void QueryProvider_survives_a_subsequent_With_clone()
    {
        var extension = new MongoOptionsExtension().WithQueryProvider(MongoQueryProvider.DriverLinq);

        var clone = extension.WithDatabaseName("SomeDatabase");

        Assert.Equal(MongoQueryProvider.DriverLinq, clone.QueryProvider);
    }

    [Fact]
    public void Differing_query_provider_yields_distinct_service_provider_hash_and_not_same_provider()
    {
        var native = new MongoOptionsExtension().WithConnectionString("mongodb://localhost")
            .WithQueryProvider(MongoQueryProvider.Native);
        var driverLinq = native.WithQueryProvider(MongoQueryProvider.DriverLinq);

        Assert.NotEqual(native.Info.GetServiceProviderHashCode(), driverLinq.Info.GetServiceProviderHashCode());
        Assert.False(native.Info.ShouldUseSameServiceProvider(driverLinq.Info));
    }

    [Fact]
    public void LogFragment_omits_query_provider_when_native_and_includes_when_not()
    {
        var native = new MongoOptionsExtension().WithQueryProvider(MongoQueryProvider.Native);
        var driverLinq = native.WithQueryProvider(MongoQueryProvider.DriverLinq);

        Assert.DoesNotContain("QueryProvider", native.Info.LogFragment);
        Assert.Contains("QueryProvider=DriverLinq", driverLinq.Info.LogFragment);
    }
}
