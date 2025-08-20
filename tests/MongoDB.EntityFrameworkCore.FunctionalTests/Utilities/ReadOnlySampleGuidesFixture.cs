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

using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

[CollectionDefinition(nameof(ReadOnlySampleGuidesFixture))]
public class ReadOnlySampleGuidesFixtureCollection : ICollectionFixture<ReadOnlySampleGuidesFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

public class ReadOnlySampleGuidesFixture
{
    public ReadOnlySampleGuidesFixture()
    {
        Client = TestServer.Default.Client;
        MongoDatabase = Client.GetDatabase("EFCoreTest-SampleGuides");
        SampleGuides.Populate(MongoDatabase);
    }

    public IMongoDatabase MongoDatabase { get; }
    public IMongoClient Client { get; }
}
