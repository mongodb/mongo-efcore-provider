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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class IncludeOneToOneMongoTest(IncludeOneToOneMongoTest.OneToOneQueryMongoFixture fixture)
    : IncludeOneToOneTestBase<IncludeOneToOneMongoTest.OneToOneQueryMongoFixture>(fixture)
{
    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override void Include_address()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_address());

    public override void Include_address_EF_Property()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_address_EF_Property());

    public override void Include_address_no_tracking()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_address_no_tracking());

    public override void Include_address_no_tracking_EF_Property()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_address_no_tracking_EF_Property());

    public override void Include_address_shadow()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_address_shadow());

    public override void Include_address_when_person_already_tracked()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() =>
        {
            using var context = CreateContext();
            context.Set<Person>().Single(p => p.Name == "John Snow");
            context.Set<Person>().Include(p => p.Address).ToList();
        });

    public override void Include_person()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_person());

    public override void Include_person_EF_Property()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_person_EF_Property());

    public override void Include_person_no_tracking()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_person_no_tracking());

    public override void Include_person_no_tracking_EF_Property()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_person_no_tracking_EF_Property());

    public override void Include_person_shadow()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_person_shadow());

    public override void Include_person_when_address_already_tracked()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() =>
        {
            using var context = CreateContext();
            context.Set<Address>().Single(a => a.City == "Meereen");
            context.Set<Address>().Include(a => a.Resident).ToList();
        });

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    public class OneToOneQueryMongoFixture : OneToOneQueryFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("OneToOneQueryTest");

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

            // MongoDB does not auto-generate int primary keys, so keys are assigned explicitly in seed.
            modelBuilder.Entity<Person>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.ToCollection("People");
            });
            modelBuilder.Entity<Address>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.ToCollection("Addresses");
            });
            modelBuilder.Entity<Person2>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.ToCollection("People2");
            });
            modelBuilder.Entity<Address2>().ToCollection("Addresses2");
        }

#if EF8
        protected override void Seed(PoolableDbContext context)
        {
            AddEntities(context);
            context.SaveChanges();
        }
#endif

        protected override Task SeedAsync(PoolableDbContext context)
        {
            AddEntities(context);
            return context.SaveChangesAsync();
        }

        private static void AddEntities(PoolableDbContext context)
        {
            var address1 = new Address { Id = 1, Street = "3 Dragons Way", City = "Meereen" };
            var address2 = new Address { Id = 2, Street = "42 Castle Black", City = "The Wall" };
            var address3 = new Address { Id = 3, Street = "House of Black and White", City = "Braavos" };

            context.Set<Person>().AddRange(
                new Person { Id = 1, Name = "Daenerys Targaryen", Address = address1 },
                new Person { Id = 2, Name = "John Snow", Address = address2 },
                new Person { Id = 3, Name = "Arya Stark", Address = address3 },
                new Person { Id = 4, Name = "Harry Strickland" });

            context.Set<Address>().AddRange(address1, address2, address3);

            var address21 = new Address2 { Id = "1", Street = "3 Dragons Way", City = "Meereen" };
            var address22 = new Address2 { Id = "2", Street = "42 Castle Black", City = "The Wall" };
            var address23 = new Address2 { Id = "3", Street = "House of Black and White", City = "Braavos" };

            context.Set<Person2>().AddRange(
                new Person2 { Id = 1, Name = "Daenerys Targaryen", Address = address21 },
                new Person2 { Id = 2, Name = "John Snow", Address = address22 },
                new Person2 { Id = 3, Name = "Arya Stark", Address = address23 });

            context.Set<Address2>().AddRange(address21, address22, address23);
        }
    }
}
