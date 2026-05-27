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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query.Translations;

public class BasicTypesQueryMongoFixture : BasicTypesQueryFixtureBase
{
    private BasicTypesData? _expectedData;

    public override ISetSource GetExpectedData()
        => _expectedData ??= CreateExpectedData();

    private static BasicTypesData CreateExpectedData()
    {
        var data = new BasicTypesData();
        foreach (var entity in data.BasicTypesEntities)
        {
            entity.DateTime = ForMongo(entity.DateTime);
        }

        foreach (var entity in data.NullableBasicTypesEntities)
        {
            if (entity.DateTime.HasValue)
            {
                entity.DateTime = ForMongo(entity.DateTime.Value);
            }
        }

        return data;
    }

    protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("BasicTypesTest");

    private ITestStoreFactory? _testStoreFactory;

    protected override ITestStoreFactory TestStoreFactory
        => _testStoreFactory!;

    public override async Task InitializeAsync()
    {
        var server = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
        _testStoreFactory = new MongoTestStoreFactory(server);

        await base.InitializeAsync();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);

        modelBuilder.Entity<BasicTypesEntity>().ToCollection("BasicTypesEntities");
        modelBuilder.Entity<NullableBasicTypesEntity>().ToCollection("NullableBasicTypesEntities");
    }

    protected override Task SeedAsync(BasicTypesContext context)
    {
        var data = new BasicTypesData();
        foreach (var entity in data.BasicTypesEntities)
        {
            entity.DateTime = ForMongo(entity.DateTime);
        }

        foreach (var entity in data.NullableBasicTypesEntities)
        {
            if (entity.DateTime.HasValue)
            {
                entity.DateTime = ForMongo(entity.DateTime.Value);
            }
        }

        context.AddRange(data.BasicTypesEntities);
        context.AddRange(data.NullableBasicTypesEntities);
        return context.SaveChangesAsync();
    }

    // MongoDB BSON DateTime has millisecond precision and is always stored as UTC. Stamp/truncate the
    // seed values to match so round-tripped values stay equal to the expected in-memory data.
    private static DateTime ForMongo(DateTime value)
    {
        var truncated = value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMillisecond));
        return DateTime.SpecifyKind(truncated, DateTimeKind.Utc);
    }
}

#endif
