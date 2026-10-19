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

#if !EF8 && !EF9

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Translations;
using Microsoft.EntityFrameworkCore.TestModels.BasicTypesModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query.Translations;

public class MongoBasicTypesQueryFixture : BasicTypesQueryFixtureBase
{
    protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("BasicTypes");

    private ITestStoreFactory? _testStoreFactory;

    protected override ITestStoreFactory TestStoreFactory
        => _testStoreFactory!;

    public TestServer TestServer { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        TestServer = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
        _testStoreFactory = new MongoTestStoreFactory(TestServer);

        await base.InitializeAsync();
    }

    protected override bool UsePooling
        => false;

    public TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ServiceProvider.GetRequiredService<ILoggerFactory>();

    protected override bool ShouldLogCategory(string logCategory)
        => logCategory == DbLoggerCategory.Query.Name;

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);

        modelBuilder.Entity<BasicTypesEntity>().ToCollection("BasicTypesEntities");
        modelBuilder.Entity<NullableBasicTypesEntity>().ToCollection("NullableBasicTypesEntities");
    }

    // Does NOT call base.SeedAsync() — replicates BasicTypesQueryFixtureBase's own seeding (the same
    // BasicTypesData source GetExpectedData() also uses for the in-memory comparison side) but normalizes
    // every DateTime first, for two independent reasons this provider needs and SQL Server/other relational
    // providers don't:
    //   1. An Unspecified-Kind DateTime gets reinterpreted as local time and shifted on insert (the exact bug
    //      NorthwindQueryMongoFixture.AddEntities already works around for Order.OrderDate).
    //   2. BSON's Date type is a fixed millisecond-since-epoch int64 (BSON spec, type 0x09) — there is no
    //      sub-millisecond representation at all, so a seed value with sub-millisecond ticks (BasicTypesData
    //      has at least one) silently truncates on round-trip.
    // GetExpectedData() below is overridden to apply the SAME normalization to its own separately-constructed
    // BasicTypesData, so both sides of every AssertQuery comparison compare the SAME (Utc-kind,
    // millisecond-truncated) value — DateTime equality ignores Kind, so only the truncation actually matters
    // for the comparison, but both are applied together since they're the same "make it survive a Mongo
    // round-trip" operation.
    protected override Task SeedAsync(BasicTypesContext context)
    {
        var data = new BasicTypesData();
        NormalizeDateTimes(data);

        context.AddRange(data.BasicTypesEntities);
        context.AddRange(data.NullableBasicTypesEntities);
        return context.SaveChangesAsync();
    }

    private BasicTypesData? _expectedData;

    public override ISetSource GetExpectedData()
    {
        if (_expectedData is null)
        {
            _expectedData = new BasicTypesData();
            NormalizeDateTimes(_expectedData);
        }

        return _expectedData;
    }

    private static void NormalizeDateTimes(BasicTypesData data)
    {
        foreach (var entity in data.BasicTypesEntities)
        {
            entity.DateTime = AsUtcMillisecond(entity.DateTime);
        }

        foreach (var entity in data.NullableBasicTypesEntities)
        {
            if (entity.DateTime is { } dateTime)
            {
                entity.DateTime = AsUtcMillisecond(dateTime);
            }
        }
    }

    private static DateTime AsUtcMillisecond(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Millisecond, DateTimeKind.Utc);
}

#endif
