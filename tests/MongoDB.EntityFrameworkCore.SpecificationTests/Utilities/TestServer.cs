using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

internal static class TestServer
{
    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    private static readonly MongoClient MongoClient = new(ConnectionString);

    public static IMongoClient GetClient()
        => MongoClient;

    private static SemanticVersion _serverVersion;
    private static SemanticVersion ServerVersion
        => LazyInitializer.EnsureInitialized(ref _serverVersion, () => QueryServerVersion());

    public static bool SupportsBitwiseOperators
        => ServerVersion >= new SemanticVersion(6, 3, 0);

    private static SemanticVersion QueryServerVersion()
    {
        var database = MongoClient.GetDatabase("__admin");
        var buildInfo = database.RunCommand<BsonDocument>(new BsonDocument("buildinfo", 1), ReadPreference.Primary);
        return SemanticVersion.Parse(buildInfo["version"].AsString);
    }

    private const string TestDatabasePrefix = "EFCoreTest-";
    private static readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private static int DbCount;
    public static string GetUniqueDatabaseName(string staticName)
        => $"{TestDatabasePrefix}{staticName}-{TimeStamp}-{Interlocked.Increment(ref DbCount)}";

}
