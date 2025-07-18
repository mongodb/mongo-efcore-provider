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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonToken = Newtonsoft.Json.JsonToken;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

public class MongoTestStore : TestStore
{
    private readonly string? _dataFilePath;

    public static MongoTestStore Create(string name)
        => new(name, shared: false);

    public static MongoTestStore GetOrCreate(string name, string dataFilePath)
        => new(name, dataFilePath: dataFilePath);

    private MongoTestStore(string name, bool shared = true, string? dataFilePath = null)
        : base(name, shared)
    {
        if (dataFilePath != null)
        {
            _dataFilePath = Path.Combine(
                Path.GetDirectoryName(typeof(MongoTestStore).Assembly.Location)!,
                dataFilePath);
        }
    }

    protected override DbContext CreateDefaultContext()
        => new TestStoreContext(this);

    public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
        => builder.UseMongoDB(TestServer.GetClient(), Name);

    #if EF9
    protected override async Task InitializeAsync(Func<DbContext> createContext, Func<DbContext, Task>? seed,
        Func<DbContext, Task>? clean)
    {
        await base.InitializeAsync(createContext, seed, clean).ConfigureAwait(false);

        using var context = createContext();
        await CleanAsync(context);

        if (_dataFilePath == null)
        {
            InsertDocumentsFromModelData(context);
        }
        else
        {
            InsertDocumentsFromJsonFile(context);
        }
    }
    #else
    protected override void Initialize(Func<DbContext> createContext, Action<DbContext>? seed, Action<DbContext>? clean)
    {
        base.Initialize(createContext, seed, clean);

        using var context = createContext();
        Clean(context);

        if (_dataFilePath == null)
        {
            InsertDocumentsFromModelData(context);
        }
        else
        {
            InsertDocumentsFromJsonFile(context);
        }
    }
    #endif

    private static void InsertDocumentsFromModelData(DbContext context)
    {
        var updateAdapter = context.GetService<IUpdateAdapterFactory>().CreateStandalone();
        var model = context.GetService<IDesignTimeModel>().Model;
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var targetSeed in entityType.GetSeedData())
            {
                var entry = updateAdapter.CreateEntry(targetSeed, updateAdapter.Model.FindEntityType(entityType.Name)!);
                entry.EntityState = EntityState.Added;
            }
        }

        context.GetService<IDatabase>().SaveChanges(updateAdapter.GetEntriesToSave());
    }

    private void InsertDocumentsFromJsonFile(DbContext context)
    {
        var mongoClient = context.GetService<IMongoClientWrapper>();
        var serializer = JsonSerializer.Create();

        using var fs = new FileStream(_dataFilePath!, FileMode.Open, FileAccess.Read);
        using var sr = new StreamReader(fs);
        using var reader = new JsonTextReader(sr);
        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.StartArray)
            {
                NextEntityType:
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.StartObject)
                    {
                        string? entityName = null;
                        string? collectionName = null;
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonToken.PropertyName)
                            {
                                switch (reader.Value)
                                {
                                    case "Name":
                                        reader.Read();
                                        entityName = (string)reader.Value;
                                        break;
                                    case "Collection":
                                        reader.Read();
                                        collectionName = (string)reader.Value;
                                        break;
                                    case "Data":
                                        var documents = new List<BsonDocument>();
                                        while (reader.Read())
                                        {
                                            if (reader.TokenType == JsonToken.StartObject)
                                            {
                                                var document = BsonDocument.Parse(serializer.Deserialize<JObject>(reader)!.ToString());
                                                document["$type"] = entityName;
                                                documents.Add(document);

                                            }
                                            else if (reader.TokenType == JsonToken.EndObject)
                                            {
                                                mongoClient.GetCollection<BsonDocument>(collectionName!).InsertMany(documents);
                                                goto NextEntityType;
                                            }
                                        }

                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    #if !EF9
    public override void Clean(DbContext context)
        => TestServer.GetClient().DropDatabase(Name);
    #endif

    public override async Task CleanAsync(DbContext context)
        => await TestServer.GetClient().DropDatabaseAsync(Name).ConfigureAwait(false);

    private class TestStoreContext(MongoTestStore testStore) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseMongoDB(TestServer.GetClient(), testStore.Name);
    }
}
