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

internal static class TestServer
{
    private const string MongoServer = "localhost";

    public const string TestDatabasePrefix = "EFCoreTest-";

    private static readonly MongoClientSettings __mongoClientSettings = new() {Server = MongoServerAddress.Parse(MongoServer)};
    private static readonly MongoClient __mongoClient = new(__mongoClientSettings);
    private static readonly string __timeStamp = DateTime.Now.ToString("s").Replace(':', '-');

    private static int __count;

    public static IMongoClient GetClient()
        => __mongoClient;

    public static IMongoDatabase GetDatabase(string name)
        => __mongoClient.GetDatabase(name);

    public static TemporaryDatabase CreateTemporaryDatabase()
        => new (GetDatabase($"{TestDatabasePrefix}-{__timeStamp}@{Interlocked.Increment(ref __count)}"));
}
