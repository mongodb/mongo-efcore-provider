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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests;

public class EntityWithValue<T>
{
    public ObjectId _id { get; set; }
    public T value { get; set; }
}

public class EntityWithId<T>
{
    public T _id { get; set; }
}

public static class Exerciser
{
    public static void TestConvertedValueRoundTrip<TEntity, TStorage>(
        TemporaryDatabaseFixture database,
        TEntity expectedValue,
        Func<TEntity, TStorage> converter,
        Action<ModelBuilder>? modelConfig = default,
        Func<TEntity, TEntity>? roundForComparison = default,
        [CallerMemberName] string? caller = default)
    {
        roundForComparison ??= e => e;
        var collectionName = caller + expectedValue;
        var collection = database.CreateCollection<EntityWithValue<TEntity>>(collectionName);

        var expected = new EntityWithValue<TEntity>
        {
            _id = ObjectId.GenerateNewId(), value = expectedValue
        };

        modelConfig ??= _ => { };

        {
            // Test creation via EF
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            db.Entities.Add(expected);
            Assert.Equal(1, db.SaveChanges());
        }

        {
            // Test retrieval via EF
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            var found = db.Entities.First();
            Assert.Equal(roundForComparison(expectedValue), roundForComparison(found.value));
            Assert.Equal(expected._id, found._id);
        }

        {
            // Test MongoDB C# driver retrieval
            var mongoCollection = database.GetCollection<EntityWithValue<TStorage>>(collectionName);
            var found = mongoCollection.AsQueryable().First();
            Assert.Equal(converter(expectedValue), found.value);
            Assert.Equal(expected._id, found._id);
        }

        {
            // Test query via EF
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            var found = db.Entities.First(e => e.value.Equals(expectedValue));
            Assert.Equal(roundForComparison(expectedValue), roundForComparison(found.value));
            Assert.Equal(expected._id, found._id);
        }

        {
            // Create native one in MongoDB C# Driver & read via EF
            var mongoCollectionName = collectionName + "_";
            var mongoCollection = database.CreateCollection<EntityWithValue<TStorage>>(mongoCollectionName);
            var expectedMongo = new EntityWithValue<TStorage>
            {
                _id = ObjectId.GenerateNewId(), value = converter(expectedValue)
            };
            mongoCollection.InsertOne(expectedMongo);

            var efCollection = database.GetCollection<EntityWithValue<TEntity>>(mongoCollectionName);
            using var db = SingleEntityDbContext.Create(efCollection, modelConfig);
            var found = db.Entities.First();
            Assert.Equal(roundForComparison(expectedValue), roundForComparison(found.value));
        }
    }

    public static void TestConvertedIdRoundTrip<TEntity, TStorage>(
        TemporaryDatabaseFixture database,
        TEntity expectedId,
        Func<TEntity, TStorage> converter,
        Action<ModelBuilder>? modelConfig = default,
        [CallerMemberName] string? caller = default)
    {
        var collectionName = caller + expectedId;
        var collection = database.CreateCollection<EntityWithId<TEntity>>(collectionName);

        var expected = new EntityWithId<TEntity>
        {
            _id = expectedId
        };

        modelConfig ??= _ => { };

        // Test creation via EF
        {
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            db.Entities.Add(expected);

            Assert.Equal(1, db.SaveChanges());
        }

        // Test retrieval via EF
        {
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            var found = db.Entities.First();
            Assert.Equal(expectedId, found._id);
        }

        {
            // Test MongoDB C# driver retrieval
            var mongoCollection = database.GetCollection<EntityWithId<TStorage>>(collectionName);
            var found = mongoCollection.AsQueryable().First();
            Assert.Equal(converter(expectedId), found._id);
        }

        {
            // Test query via EF
            using var db = SingleEntityDbContext.Create(collection, modelConfig);
            var found = db.Entities.First(e => e._id.Equals(expectedId));
            Assert.Equal(expected._id, found._id);
        }

        {
            // Create native one in MongoDB C# Driver & read via EF
            var mongoCollectionName = collectionName + "_";
            var mongoCollection = database.CreateCollection<EntityWithId<TStorage>>(mongoCollectionName);
            var expectedMongo = new EntityWithId<TStorage>
            {
                _id = converter(expectedId)
            };
            mongoCollection.InsertOne(expectedMongo);

            var efCollection = database.GetCollection<EntityWithId<TEntity>>(mongoCollectionName);
            using var db = SingleEntityDbContext.Create(efCollection, modelConfig);
            var found = db.Entities.First();
            Assert.Equal(expectedId, found._id);
        }
    }
}
