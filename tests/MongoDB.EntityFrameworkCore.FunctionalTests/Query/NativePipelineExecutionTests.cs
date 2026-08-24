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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativePipelineExecutionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    [Fact]
    public void NativePipeline_match_stage_returns_correct_BsonDocuments()
    {
        // Arrange: seed a collection with a few entities
        var collection = database.CreateCollection<Customer>();
        collection.InsertMany([
            new Customer { Id = ObjectId.GenerateNewId(), Name = "Alice", Score = 10 },
            new Customer { Id = ObjectId.GenerateNewId(), Name = "Bob",   Score = 20 },
            new Customer { Id = ObjectId.GenerateNewId(), Name = "Carol", Score = 30 },
        ]);

        using var db = SingleEntityDbContext.Create(collection);

        // Obtain IMongoClientWrapper from the context's service provider
        var clientWrapper = db.GetService<IMongoClientWrapper>();
        var collectionNamespace = collection.CollectionNamespace;

        // Build a placeholder Provider/Query from collection.AsQueryable() — the native path does
        // not use them, but MongoExecutableQuery's constructor requires non-null values.
        var bsonCollection = database.MongoDatabase.GetCollection<BsonDocument>(collectionNamespace.CollectionName);
        var queryable = bsonCollection.AsQueryable();
        var provider = (IMongoQueryProvider)queryable.Provider;
        Expression placeholderQuery = queryable.Expression;

        var nativePipeline = new[] { BsonDocument.Parse("{ $match: { Score: { $gt: 15 } } }") };

        var executableQuery = new MongoExecutableQuery(
            placeholderQuery,
            ResultCardinality.Enumerable,
            provider,
            collectionNamespace,
            new ReadOnlyDictionary<string, object>(new Dictionary<string, object>()))
        {
            NativePipeline = nativePipeline,
            Streaming = false,
        };

        // Act
        var results = clientWrapper.Execute<BsonDocument>(executableQuery, out _).ToList();

        // Assert: only Bob (20) and Carol (30) have Score > 15
        Assert.Equal(2, results.Count);
        Assert.All(results, doc => Assert.True(doc["Score"].AsInt32 > 15));
        Assert.Contains(results, doc => doc["Name"].AsString == "Bob");
        Assert.Contains(results, doc => doc["Name"].AsString == "Carol");
    }
}
