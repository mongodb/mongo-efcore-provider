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
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.TestModels.ComplexNavigationsModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class ComplexNavigationsQueryMongoFixture : ComplexNavigationsQueryFixtureBase
{
    protected override string StoreName
        => TestDatabaseNamer.GetUniqueDatabaseName("ComplexNavigations");

    private ITestStoreFactory? _testStoreFactory;

    protected override ITestStoreFactory TestStoreFactory
        => _testStoreFactory!;

    public TestServer TestServer { get; private set; }

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

        // MongoDB requires every entity's primary key to be mapped to the "_id" element. The
        // complex-navigation entities use string keys (Name/DefaultText/Text) that are not auto-mapped.
        modelBuilder.Entity<ComplexNavigationField>().Property(e => e.Name).HasElementName("_id");
        modelBuilder.Entity<ComplexNavigationString>().Property(e => e.DefaultText).HasElementName("_id");
        modelBuilder.Entity<ComplexNavigationGlobalization>().Property(e => e.Text).HasElementName("_id");
        modelBuilder.Entity<ComplexNavigationLanguage>().Property(e => e.Name).HasElementName("_id");

        // The seed data uses Unspecified-kind DateTimes; MongoDB stores DateTime as UTC, so on write the
        // driver shifts the value by the local UTC offset and breaks data assertions. Pin the kind to UTC
        // before serialization so the components round-trip unchanged (the Northwind fixture does the
        // equivalent by normalizing its seed data to UTC).
        var toUtc = new ValueConverter<DateTime, DateTime>(
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var toUtcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(toUtc);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(toUtcNullable);
                }
            }
        }

        // The one-to-one relationships produce unique indexes on nullable foreign keys. MongoDB rejects a
        // unique index when more than one document is null/missing for that key (and partial filters cannot
        // express "not null"), which breaks EnsureCreated for the seeded data. Index uniqueness is not needed
        // for query correctness in these tests, so relax the auto-created FK indexes to non-unique.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var index in entityType.GetDeclaredIndexes().Where(i => i.IsUnique))
            {
                index.IsUnique = false;
            }
        }
    }
}
