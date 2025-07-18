using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

internal static class TestServer
{
    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    private static readonly MongoClient MongoClient = new(ConnectionString);

    public static IMongoClient GetClient()
        => MongoClient;

    public static readonly IMongoClient BrokenClient
        = new MongoClient(new MongoClientSettings { Server = new MongoServerAddress("localhost", 27000), ServerSelectionTimeout = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(1)});
}
