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
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public abstract class TestServer : IAsyncLifetime
{
    private static readonly SemaphoreSlim AtlasSempahore = new(1);
    private static TestServer? Default;
    private static readonly SemaphoreSlim DefaultSempahore = new(1);
    private static TestServer? Atlas;

    public static async Task<TestServer> GetOrInitializeTestServerAsync(MongoCondition requiredCondition)
    {
        if (requiredCondition.HasFlag(MongoCondition.IsAtlas))
        {
            // Double-check locking; don't tell Java!
            if (Atlas != null)
            {
                return Atlas;
            }

            await AtlasSempahore.WaitAsync();
            try
            {
                if (Atlas == null)
                {
                    Atlas = await GetOrInitialize("ATLAS_URI");
                }

                if (Atlas == null)
                {
                    Atlas = await GetOrCreateDefault();
                }

                return Atlas;
            }
            finally
            {
                AtlasSempahore.Release();
            }
        }

        return await GetOrCreateDefault();

        static async Task<TestServer> GetOrCreateDefault()
        {
            // Double-check locking; don't tell Java!
            if (Default != null)
            {
                return Default;
            }

            await DefaultSempahore.WaitAsync();
            try
            {
                if (Default == null)
                {
                    Default = await GetOrInitialize("MONGODB_URI");
                }

                return Default!;
            }
            finally
            {
                DefaultSempahore.Release();
            }
        }

        static async Task<TestServer?> GetOrInitialize(string uriName)
        {
            var uri = Environment.GetEnvironmentVariable(uriName);
            if (string.IsNullOrWhiteSpace(uri))
            {
                var containersServer = new TestContainersTestServer();
                await containersServer.InitializeAsync();
                return containersServer;
            }

            if (uri.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var externalServer = new ExternalDatabaseTestServer(uri);
            await externalServer.InitializeAsync();

            var databaseNameCursor = await externalServer.Client.ListDatabaseNamesAsync();
            while (await databaseNameCursor.MoveNextAsync())
            {
                foreach (var databaseName in databaseNameCursor.Current)
                {
                    if (databaseName.StartsWith(TestDatabaseNamer.TestDatabasePrefix))
                    {
                        await externalServer.Client.DropDatabaseAsync(databaseName);
                    }
                }
            }

            return externalServer;
        }
    }

    public abstract string ConnectionString { get; }
    public abstract MongoClient Client { get; }

    private SemanticVersion _serverVersion;
    public SemanticVersion ServerVersion
        => LazyInitializer.EnsureInitialized(ref _serverVersion, () => QueryServerVersion());

    public bool SupportsBitwiseOperators
        => ServerVersion >= new SemanticVersion(6, 3, 0);

    public static bool SupportsAtlas
    {
        get
        {
            var uri = Environment.GetEnvironmentVariable("ATLAS_URI");
            return uri == null || !uri.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool SupportsEncryption
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CRYPT_SHARED_LIB_PATH")) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_BINARIES"));

    public static bool SkipForAtlas(ITestOutputHelper testOutputHelper, string caller)
    {
        if (SupportsAtlas)
        {
            return false;
        }

        testOutputHelper.WriteLine($"'{caller}' skipped because it required Atlas.");
        return true;
    }

    private SemanticVersion QueryServerVersion()
    {
        var database = Client.GetDatabase("__admin");
        var buildInfo = database.RunCommand<BsonDocument>(new BsonDocument("buildinfo", 1), ReadPreference.Primary);
        return SemanticVersion.Parse(buildInfo["version"].AsString);
    }

    public static readonly IMongoClient BrokenClient
        = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress("localhost", 27000),
            ServerSelectionTimeout = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(1)
        });

    public virtual Task InitializeAsync()
        => Task.CompletedTask;

    public virtual Task DisposeAsync()
        => Task.CompletedTask;
}
