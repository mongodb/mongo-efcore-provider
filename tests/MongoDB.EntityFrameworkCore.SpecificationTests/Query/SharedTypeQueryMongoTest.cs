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
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

#if !EF8 && !EF9
public class SharedTypeQueryMongoTest(NonSharedFixture fixture) : SharedTypeQueryTestBase(fixture)
#else
public class SharedTypeQueryMongoTest : SharedTypeQueryTestBase
#endif
{
    public override async Task Can_use_shared_type_entity_type_in_query_filter(bool async)
        // Fails: seed inserts shared-type entity without an Id; MongoDB rejects multiple rows with default _id 0 EF-27
        => await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(
            () => base.Can_use_shared_type_entity_type_in_query_filter(async));

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
