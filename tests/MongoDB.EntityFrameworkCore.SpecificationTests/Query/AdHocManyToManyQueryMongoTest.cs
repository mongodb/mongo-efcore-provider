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

#if !EF8

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

#if !EF8 && !EF9
public class AdHocManyToManyQueryMongoTest(NonSharedFixture fixture) : AdHocManyToManyQueryTestBase(fixture)
#else
public class AdHocManyToManyQueryMongoTest : AdHocManyToManyQueryTestBase
#endif
{
    public override async Task SelectMany_with_collection_selector_having_subquery()
        // Fails: seed adds entities without explicit int Ids and MongoDB does not auto-generate them
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => base.SelectMany_with_collection_selector_having_subquery());

    public override async Task Many_to_many_load_works_when_join_entity_has_custom_key(bool async)
        // Fails: seed adds entities without explicit int Ids and MongoDB does not auto-generate them
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => base.Many_to_many_load_works_when_join_entity_has_custom_key(async));

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

#endif
