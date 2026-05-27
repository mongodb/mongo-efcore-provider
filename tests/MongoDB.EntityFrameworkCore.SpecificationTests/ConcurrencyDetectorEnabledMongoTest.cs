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
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class ConcurrencyDetectorEnabledMongoTest(ConcurrencyDetectorEnabledMongoTest.ConcurrencyDetectorMongoFixture fixture)
    : ConcurrencyDetectorEnabledTestBase<ConcurrencyDetectorEnabledMongoTest.ConcurrencyDetectorMongoFixture>(fixture)
{
    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override Task Find(bool async)
        => base.Find(async);

    public override Task Count(bool async)
        => base.Count(async);

    public override Task First(bool async)
        => base.First(async);

    public override Task Last(bool async)
        => base.Last(async);

    public override Task Single(bool async)
        => base.Single(async);

    public override Task Any(bool async)
        => base.Any(async);

    public override Task ToList(bool async)
        => base.ToList(async);

    public override Task SaveChanges(bool async)
        => base.SaveChanges(async);

    public class ConcurrencyDetectorMongoFixture : ConcurrencyDetectorFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("ConcurrencyDetectorEnabled");

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
            modelBuilder.Entity<Product>().ToCollection("Products");
        }
    }
}
