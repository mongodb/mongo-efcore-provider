using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DependencyInjection.Example.API;

public class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public required string Id { get; set; }

    [BsonElement("title")]
    public string? Title { get; set; }

    [BsonElement("brand")]
    public string? Brand { get; set; }

    [BsonElement("tags")]
    public List<string>? Tags { get; set; }

}


