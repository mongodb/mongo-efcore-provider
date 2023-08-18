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

using System.ComponentModel.DataAnnotations.Schema;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions;

public sealed class MongoPrimaryKeyDiscoveryConventionTests : IDisposable
{
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();

    public void Dispose() => _tempDatabase.Dispose();

    class UnderscoreIdNamedProperty
    {
        public ObjectId _id { get; set; }

        public string name { get; set; }
    }

    class IdNamedProperty
    {
        public ObjectId Id { get; set; }

        public string name { get; set; }
    }

    class ColumnAttributedIdProperty
    {
        [Column("_id")]
        public ObjectId MyPrimaryKey { get; set; }

        public string name { get; set; }
    }

    class Product
    {
        public string ProductId { get; set; }
        public string name { get; set; }
    }

    class StoredProduct
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_underscore_id_named_property()
    {

        var collection = _tempDatabase.CreateTemporaryCollection<UnderscoreIdNamedProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(new UnderscoreIdNamedProperty {_id = id, name = name});
            dbContext.SaveChanges();
        }

        { // Find with CSharpDriver
            var actual = collection.Database.GetCollection<UnderscoreIdNamedProperty>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, directFound.name);
        }

        { // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection);
            var found = dbContext.Entitites.Single(f => f._id == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_Id_named_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<IdNamedProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(new IdNamedProperty {Id = id, name = name});
            dbContext.SaveChanges();
        }

        { // Find with CSharpDriver
            var actual = collection.Database.GetCollection<IdNamedProperty>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f.Id == id).Single();
            Assert.Equal(name, directFound.name);
        }

        { // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection);
            var found = dbContext.Entitites.Single(f => f.Id == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_ColumnAttributed_named_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<ColumnAttributedIdProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            var entity = new ColumnAttributedIdProperty {MyPrimaryKey = id, name = name};
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        { // Find with CSharpDriver
            var actual = collection.Database.GetCollection<UnderscoreIdNamedProperty>(collection.CollectionNamespace.CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, found.name);
        }

        { // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection);
            var found = dbContext.Entitites.Single(f => f.MyPrimaryKey == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_EntityId_named_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Product>();

        var id = Guid.NewGuid().ToString();
        var name = Guid.NewGuid().ToString();

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            var entity = new Product {ProductId = id, name = name};
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        { // Find with CSharpDriver
            var actual = collection.Database.GetCollection<StoredProduct>(collection.CollectionNamespace.CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, found.name);
        }

        { // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection);
            var found = dbContext.Entitites.Single(f => f.ProductId == id);
            Assert.Equal(name, found.name);
        }
    }
}
