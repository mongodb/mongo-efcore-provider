using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class PrimitiveCollectionTests(TemporaryDatabaseFixture tempDatabase) : IClassFixture<TemporaryDatabaseFixture>
{
    class MissingPrimitiveList
    {
        public ObjectId _id { get; set; }
    }

    class NonNullablePrimitiveList
    {
        public ObjectId _id { get; set; }
        public List<int> items { get; set; }
    }

    class NullablePrimitiveList
    {
        public ObjectId _id { get; set; }
        public List<int>? items { get; set; }
    }

    class NonNullablePrimitiveArray
    {
        public ObjectId _id { get; set; }
        public int[] items { get; set; }
    }

    class NullablePrimitiveArray
    {
        public ObjectId _id { get; set; }
        public int[]? items { get; set; }
    }

    [Fact]
    public void Non_nullable_primitive_list_is_empty_when_bson_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<NonNullablePrimitiveList>();
        collection.WriteTestDocs([
            new NonNullablePrimitiveList
            {
                items = []
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.Empty(actual.items);
    }

    [Fact]
    public void Non_nullable_primitive_list_throws_when_bson_missing()
    {
        var collection = tempDatabase.CreateTemporaryCollection<MissingPrimitiveList>();
        collection.WriteTestDocs([new MissingPrimitiveList()]);
        var db = SingleEntityDbContext.Create<MissingPrimitiveList, NonNullablePrimitiveArray>(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.First());
        Assert.Contains("null", ex.Message);
        Assert.Contains("non-nullable", ex.Message);
        Assert.Contains(nameof(NonNullablePrimitiveList.items), ex.Message);
    }

    [Fact]
    public void Non_nullable_primitive_list_throws_when_bson_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<NonNullablePrimitiveList>();
        collection.WriteTestDocs([
            new NonNullablePrimitiveList
            {
                items = null
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.First());
        Assert.Contains("null", ex.Message);
        Assert.Contains("non-nullable", ex.Message);
        Assert.Contains(nameof(NonNullablePrimitiveList.items), ex.Message);
    }

    [Fact]
    public void Nullable_primitive_list_is_null_when_bson_missing()
    {
        var collection = tempDatabase.CreateTemporaryCollection<MissingPrimitiveList>();
        collection.WriteTestDocs([new MissingPrimitiveList()]);
        var db = SingleEntityDbContext.Create<MissingPrimitiveList, NullablePrimitiveArray>(collection);

        var actual = db.Entitites.First();
        Assert.Null(actual.items);
    }

    [Fact]
    public void Nullable_primitive_list_is_empty_when_bson_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<NullablePrimitiveList>();
        collection.WriteTestDocs([
            new NullablePrimitiveList
            {
                items = []
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.NotNull(actual.items);
        Assert.Empty(actual.items);
    }

    [Fact]
    public void Nullable_primitive_list_is_null_when_bson_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<NullablePrimitiveList>();
        collection.WriteTestDocs([
            new NullablePrimitiveList
            {
                items = null
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.Null(actual.items);
    }
}
