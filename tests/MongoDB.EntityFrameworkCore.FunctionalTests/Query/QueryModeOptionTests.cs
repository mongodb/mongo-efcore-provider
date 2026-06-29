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
}
