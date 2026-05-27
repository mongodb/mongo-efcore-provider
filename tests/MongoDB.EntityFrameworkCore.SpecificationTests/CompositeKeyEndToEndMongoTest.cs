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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class CompositeKeyEndToEndMongoTest(CompositeKeyEndToEndMongoTest.CompositeKeyEndToEndMongoFixture fixture)
    : CompositeKeyEndToEndTestBase<CompositeKeyEndToEndMongoTest.CompositeKeyEndToEndMongoFixture>(fixture)
{
    public override async Task Can_use_generated_values_in_composite_key_end_to_end()
    {
        // Fails: MongoDB does not auto-generate int or Guid primary key values.
        // EF8 throws NotSupportedException eagerly; EF9+ leaves the int at 0 so the test's Assert.True fails.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => base.Can_use_generated_values_in_composite_key_end_to_end());
        Assert.True(
            ex is NotSupportedException || ex.GetType().FullName == "Xunit.Sdk.TrueException",
            $"Unexpected exception type: {ex.GetType()}");
    }

    public class CompositeKeyEndToEndMongoFixture : CompositeKeyEndToEndFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("CompositeKeyEndToEnd");

        private ITestStoreFactory? _testStoreFactory;

        protected override ITestStoreFactory TestStoreFactory
            => _testStoreFactory!;

        public override async Task InitializeAsync()
        {
            var server = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
            _testStoreFactory = new MongoTestStoreFactory(server);

            await base.InitializeAsync();
        }
    }
}
