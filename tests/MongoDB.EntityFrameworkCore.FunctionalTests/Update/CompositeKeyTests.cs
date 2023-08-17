using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

public class CompositeKeyTests : IDisposable
{
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();
    public void Dispose() => _tempDatabase.Dispose();

    [Fact]
    public void AddSingleKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SingleKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var entity = new SingleKeyEntity { Id = 1, Data = "AddMe"};
        dbContext.Entitites.Add(entity);
        dbContext.SaveChanges();

        var document = documentCollection.AsQueryable().Single();
        Assert.NotNull(document);
        Assert.Equal(BsonDocument.Parse("{ _id : 1, Data : 'AddMe' }"), document);
    }

    [Fact]
    public void UpdateSingleKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SingleKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var entity = new SingleKeyEntity { Id = 1, Data = "Update Me" };
        dbContext.Entitites.Add(entity);
        dbContext.SaveChanges();

        entity.Data = "Updated";
        dbContext.SaveChanges();

        var document = documentCollection.AsQueryable().Single();
        Assert.NotNull(document);
        Assert.Equal(BsonDocument.Parse("{ _id : 1, Data : 'Updated' }"), document);
    }

    [Fact]
    public void DeleteSingleKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SingleKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var entity = new SingleKeyEntity { Id = 1, Data = "Remove Me" };
        dbContext.Entitites.Add(entity);
        dbContext.SaveChanges();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        var documents = documentCollection.AsQueryable().ToList();
        Assert.Empty(documents);
    }

    [Fact]
    public void AddCompositeKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<CompositeKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var expected = new CompositeKeyEntity { EntityId = 1, SubId = "one", Data = "AddMe"};
        dbContext.Entitites.Add(expected);
        dbContext.SaveChanges();

        var document = documentCollection.AsQueryable().Single();
        Assert.NotNull(document);
        // TODO: note we are expecting to have key components in both document: _id and document itself
        Assert.Equal(BsonDocument.Parse("{ _id : { EntityId : 1, SubId : 'one' }, EntityId : 1, SubId : 'one', Data : 'AddMe' }"), document);
    }

    [Fact]
    public void UpdateCompositeKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<CompositeKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var entity = new CompositeKeyEntity { EntityId = 1, SubId = "subId", Data = "Update Me" };
        dbContext.Entitites.Add(entity);
        dbContext.SaveChanges();

        entity.Data = "Updated";
        dbContext.SaveChanges();

        var document = documentCollection.AsQueryable().Single();
        Assert.NotNull(document);
        // TODO: note we are expecting to have key components in both document: _id and document itself
        Assert.Equal(BsonDocument.Parse("{ _id : { EntityId : 1, SubId : 'subId' }, EntityId : 1, SubId : 'subId', Data : 'Updated' }"), document);
    }

    [Fact]
    public void DeleteCompositeKeyEntity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<CompositeKeyEntity>();
        var documentCollection =
            _tempDatabase.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var dbContext = SingleEntityDbContext.Create(collection);

        var entity = new CompositeKeyEntity { EntityId = 1, SubId = "subId", Data = "Update Me" };
        dbContext.Entitites.Add(entity);
        dbContext.SaveChanges();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        var documents = documentCollection.AsQueryable().ToList();
        Assert.Empty(documents);
    }

    [PrimaryKey(nameof(Id))]
    class SingleKeyEntity
    {
        public int Id { get; set; }

        public string Data { get; set; }
    }

    [PrimaryKey(nameof(EntityId), nameof(SubId))]
    class CompositeKeyEntity
    {
        public int EntityId { get; set; }

        public string SubId { get; set; }

        public string Data { get; set; }
    }
}
