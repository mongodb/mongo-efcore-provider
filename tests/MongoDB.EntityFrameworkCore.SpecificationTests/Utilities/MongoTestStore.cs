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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

public class MongoTestStore : TestStore
{
    public TestServer TestServer { get; }

    public static MongoTestStore Create(string name, TestServer testServer)
        => new(name, testServer);

    private MongoTestStore(string name, TestServer testServer)
        : base(name, shared: true)
    {
        TestServer = testServer;
    }

    protected override DbContext CreateDefaultContext()
        => throw new NotSupportedException();

    public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
        => builder.UseMongoDB(TestServer.Client, Name);

#if EF9
    protected override async Task InitializeAsync(Func<DbContext> createContext, Func<DbContext, Task>? seed,
        Func<DbContext, Task>? clean)
    {
        await base.InitializeAsync(createContext, seed, clean).ConfigureAwait(false);

        using var context = createContext();
        var databaseCreator = context.GetService<IDatabaseCreator>();
        if (!await databaseCreator.EnsureCreatedAsync().ConfigureAwait(false)) // Create or update indexes
        {
            // Seed for tests even if we didn't create the database.
            await ((MongoDatabaseCreator)databaseCreator).SeedFromModelAsync().ConfigureAwait(false);
        }
    }
#else
    protected override void Initialize(Func<DbContext> createContext, Action<DbContext>? seed, Action<DbContext>? clean)
    {
        base.Initialize(createContext, seed, clean);

        using var context = createContext();
        var databaseCreator = context.GetService<IDatabaseCreator>();
        if (!databaseCreator.EnsureCreated()) // Create or update indexes
        {
            // Seed for tests even if we didn't create the database.
            ((MongoDatabaseCreator)databaseCreator).SeedFromModel();
        }
    }
#endif

#if !EF9
    public override void Clean(DbContext context)
    {
    }
#endif

    public override Task CleanAsync(DbContext context)
        => Task.CompletedTask;
}
