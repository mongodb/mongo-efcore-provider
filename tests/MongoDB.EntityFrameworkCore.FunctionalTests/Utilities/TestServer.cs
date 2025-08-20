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

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class TestServer(string connectionString)
{
    public static TestServer Default { get; } = new(GetDefaultConnectionString());
    public static TestServer Atlas { get; } = new(Environment.GetEnvironmentVariable("ATLAS_SEARCH") ?? GetDefaultConnectionString());

    private static string GetDefaultConnectionString()
        => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";

    public string ConnectionString { get; } = connectionString;

    public MongoClient Client { get; } = new(connectionString);

    private SemanticVersion _serverVersion;
    private SemanticVersion ServerVersion
        => LazyInitializer.EnsureInitialized(ref _serverVersion, () => QueryServerVersion());

    public bool SupportsBitwiseOperators
        => ServerVersion >= new SemanticVersion(6, 3, 0);

    private SemanticVersion QueryServerVersion()
    {
        var database = Client.GetDatabase("__admin");
        var buildInfo = database.RunCommand<BsonDocument>(new BsonDocument("buildinfo", 1), ReadPreference.Primary);
        return SemanticVersion.Parse(buildInfo["version"].AsString);
    }

    private const string TestDatabasePrefix = "EFCoreTest-";
    private readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private int _dbCount;
    public string GetUniqueDatabaseName(string staticName)
        => $"{TestDatabasePrefix}{staticName}-{TimeStamp}-{Interlocked.Increment(ref _dbCount)}";

    public static readonly IMongoClient BrokenClient
        = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress("localhost", 27000),
            ServerSelectionTimeout = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(1)
        });
}

// internal static class TestServer
// {
//     public static readonly string ConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
//     private static readonly MongoClient MongoClient = new(ConnectionString);
//
//     public static IMongoClient GetClient()
//         => MongoClient;
//
//     public static readonly IMongoClient BrokenClient
//         = new MongoClient(new MongoClientSettings { Server = new MongoServerAddress("localhost", 27000), ServerSelectionTimeout = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(1)});
// }
