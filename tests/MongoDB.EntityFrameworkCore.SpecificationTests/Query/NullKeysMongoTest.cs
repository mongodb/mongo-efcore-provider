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

public class NullKeysMongoTest(NullKeysMongoTest.NullKeysMongoFixture fixture)
    : NullKeysTestBase<NullKeysMongoTest.NullKeysMongoFixture>(fixture)
{
    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override void Include_with_null_FKs_and_nullable_PK()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_with_null_FKs_and_nullable_PK());

    public override void Include_with_non_nullable_FKs_and_nullable_PK()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_with_non_nullable_FKs_and_nullable_PK());

    public override void Include_with_null_fKs_and_non_nullable_PK()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_with_null_fKs_and_non_nullable_PK());

    public override void Include_with_null_fKs_and_nullable_PK()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_with_null_fKs_and_nullable_PK());

    public override void One_to_one_self_ref_Include()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.One_to_one_self_ref_Include());

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    public class NullKeysMongoFixture : NullKeysFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("NullKeysTest");

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
            modelBuilder.Entity<WithStringKey>()
                .HasMany(e => e.Dependents).WithOne(e => e.Principal)
                .HasForeignKey(e => e.Fk);

            // MongoDB unique indexes reject multiple null entries, so the base class's
            // one-to-one self-reference can't be seeded as-is. Mapped as one-to-many to
            // exercise the same Include translation behavior without an extra unique index.
            modelBuilder.Entity<WithStringFk>()
                .HasOne(e => e.Self).WithMany()
                .HasForeignKey(e => e.SelfFk);

            modelBuilder.Entity<WithIntKey>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.HasMany(e => e.Dependents)
                    .WithOne(e => e.Principal)
                    .HasForeignKey(e => e.Fk);
            });

            modelBuilder.Entity<WithNullableIntKey>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.HasMany(e => e.Dependents)
                    .WithOne(e => e.Principal)
                    .HasForeignKey(e => e.Fk);
            });

            modelBuilder.Entity<WithAllNullableIntKey>(b =>
            {
                b.Property(e => e.Id).ValueGeneratedNever();
                b.HasMany(e => e.Dependents)
                    .WithOne(e => e.Principal)
                    .HasForeignKey(e => e.Fk);
            });

            modelBuilder.Entity<WithIntFk>()
                .Property(e => e.Id).ValueGeneratedNever();

            modelBuilder.Entity<WithNullableIntFk>()
                .Property(e => e.Id).ValueGeneratedNever();

            modelBuilder.Entity<WithAllNullableIntFk>()
                .Property(e => e.Id).ValueGeneratedNever();

            modelBuilder.Entity<WithStringKey>().ToCollection("WithStringKey");
            modelBuilder.Entity<WithStringFk>().ToCollection("WithStringFk");
            modelBuilder.Entity<WithIntKey>().ToCollection("WithIntKey");
            modelBuilder.Entity<WithNullableIntFk>().ToCollection("WithNullableIntFk");
            modelBuilder.Entity<WithNullableIntKey>().ToCollection("WithNullableIntKey");
            modelBuilder.Entity<WithIntFk>().ToCollection("WithIntFk");
            modelBuilder.Entity<WithAllNullableIntKey>().ToCollection("WithAllNullableIntKey");
            modelBuilder.Entity<WithAllNullableIntFk>().ToCollection("WithAllNullableIntFk");
        }
    }
}
