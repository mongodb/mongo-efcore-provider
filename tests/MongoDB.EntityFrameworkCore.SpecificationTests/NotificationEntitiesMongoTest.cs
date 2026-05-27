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
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class NotificationEntitiesMongoTest(NotificationEntitiesMongoTest.NotificationEntitiesMongoFixture fixture)
    : NotificationEntitiesTestBase<NotificationEntitiesMongoTest.NotificationEntitiesMongoFixture>(fixture)
{
    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override void Include_brings_entities_referenced_from_already_tracked_notification_entities_as_Unchanged()
        // Fails: Cross-document Include is not supported EF-117
        => AssertTranslationFailed(() => base.Include_brings_entities_referenced_from_already_tracked_notification_entities_as_Unchanged());

    public override void Include_brings_collections_referenced_from_already_tracked_notification_entities_as_Unchanged()
        // Fails: Cross-document Include for collection navigations is not supported EF-117
        => Assert.Contains(
            "Including navigation",
            Assert.Throws<InvalidOperationException>(
                () => base.Include_brings_collections_referenced_from_already_tracked_notification_entities_as_Unchanged()).Message);

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    public class NotificationEntitiesMongoFixture : NotificationEntitiesFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("NotificationEntities");

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

            modelBuilder.Entity<Blog>().ToCollection("Blogs");
            modelBuilder.Entity<Post>().ToCollection("Posts");
        }
    }
}
