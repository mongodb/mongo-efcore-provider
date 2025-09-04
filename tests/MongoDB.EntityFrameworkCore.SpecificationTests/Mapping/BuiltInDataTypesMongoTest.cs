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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Mapping;

public class TestServer(string connectionString)
{
    public static TestServer Default { get; } = new(GetDefaultConnectionString());
    public static TestServer Atlas { get; } = new(Environment.GetEnvironmentVariable("ATLAS_SEARCH") ?? GetDefaultConnectionString());

    private static string GetDefaultConnectionString()
        => Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";

    public string ConnectionString { get; } = connectionString;

    public MongoClient Client { get; } = new(connectionString);

    private SemanticVersion _serverVersion;
    private SemanticVersion ServerVersion
        => LazyInitializer.EnsureInitialized(ref _serverVersion, () => QueryServerVersion());

    public bool SupportsBitwiseOperators
        => ServerVersion >= new SemanticVersion(6, 3, 0);

    private SemanticVersion QueryServerVersion()
    {
        var database = Client.GetDatabase("__admin");
        var buildInfo = database.RunCommand<BsonDocument>(new BsonDocument("buildinfo", 1), ReadPreference.Primary);
        return SemanticVersion.Parse(buildInfo["version"].AsString);
    }

    private const string TestDatabasePrefix = "EFCoreTest-";
    private readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private int _dbCount;
    public string GetUniqueDatabaseName(string staticName)
        => $"{TestDatabasePrefix}{staticName}-{TimeStamp}-{Interlocked.Increment(ref _dbCount)}";

    public static readonly IMongoClient BrokenClient
        = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress("localhost", 27000),
            ServerSelectionTimeout = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(1)
        });
}

public class CSTests
{
    // [ConditionalFact]
    // public void Default_client()
    // {
    //     NewMethod(TestServer.Default.Client);
    // }

    [ConditionalFact]
    public void Atlas_client()
    {
        // NewMethod(TestServer.Atlas.Client);

        var atlasSearchUri = Environment.GetEnvironmentVariable("ATLAS_SEARCH");
        Ensure.IsNotNullOrEmpty(atlasSearchUri, nameof(atlasSearchUri));

        var mongoClientSettings = MongoClientSettings.FromConnectionString(atlasSearchUri);
        //mongoClientSettings.ClusterSource = DisposingClusterSource.Instance;

        var mongoClient = new MongoClient(atlasSearchUri);

        NewMethod(mongoClient);

    }

    private static void NewMethod(MongoClient client)
    {
        var database = client.GetDatabase("TestDb_" + Guid.NewGuid());
        database.CreateCollection("CN");

        var collection = database.GetCollection<Book>("CN");

        var books = new List<Book>();
        for (var i = 0; i < 1; i++)
        {
            books.Add(new Book { Title = "X", Floats = [0.33f, -0.52f] });


            collection.InsertMany(books);

            collection.SearchIndexes.CreateOne(new CreateSearchIndexModel(
                "X",
                SearchIndexType.VectorSearch,
                BsonDocument.Parse(
                    "{ fields: [ { type: 'vector', path: 'Floats', numDimensions: 2, similarity: 'cosine' }, { type: 'filter', path: 'Title' } ] }")));
        }
    }

    public class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; }
        public float[] Floats { get; set; }
    }
}

public class BuiltInDataTypesMongoTest(
    BuiltInDataTypesMongoTest.BuiltInDataTypesMongoFixture fixture,
    ITestOutputHelper testOutputHelper)
    : BuiltInDataTypesTestBase<BuiltInDataTypesMongoTest.BuiltInDataTypesMongoFixture>(fixture)
{

    [ConditionalFact]
    public void Default_client()
    {
        NewMethod(TestServer.Default.Client);
    }

    [ConditionalFact]
    public void Atlas_client()
    {
        NewMethod(TestServer.Atlas.Client);
    }

    private static void NewMethod(MongoClient client)
    {
        var database = client.GetDatabase("TestDb_" + Guid.NewGuid());
        database.CreateCollection("CN");

        var collection = database.GetCollection<Book>("CN");

        var books = new List<Book>();
        for (var i = 0; i < 1; i++)
        {
            books.Add(new Book { Title = "X", Floats = [0.33f, -0.52f] });


            collection.InsertMany(books);

            collection.SearchIndexes.CreateOne(new CreateSearchIndexModel(
                "X",
                SearchIndexType.VectorSearch,
                BsonDocument.Parse(
                    "{ fields: [ { type: 'vector', path: 'Floats', numDimensions: 2, similarity: 'cosine' }, { type: 'filter', path: 'Title' } ] }")));
        }
    }

    public class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; }
        public float[] Floats { get; set; }
    }

    // Fails: Enum casting issue EF-215
    public override async Task Can_filter_projection_with_captured_enum_variable(bool async)
        => Assert.Contains(
            "Unexpected target type: Microsoft.EntityFrameworkCore.BuiltInDataTypesTestBase`1+EmailTemplateTypeDto[",
            (await Assert.ThrowsAsync<Exception>(() => base.Can_filter_projection_with_captured_enum_variable(async))).Message);

    // Fails: Enum casting issue EF-215
    public override async Task Can_filter_projection_with_inline_enum_variable(bool async)
        => Assert.Contains(
            "Unexpected target type: Microsoft.EntityFrameworkCore.BuiltInDataTypesTestBase`1+EmailTemplateTypeDto[",
            (await Assert.ThrowsAsync<Exception>(() => base.Can_filter_projection_with_inline_enum_variable(async))).Message);

    #if EF9
    // Fails: Include issue EF-117
    public override async Task Can_insert_and_read_back_with_string_key()
        => Assert.Contains(
            "Including navigation 'Navigation' is not supported as the navigation is not embedded in same resource.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Can_insert_and_read_back_with_string_key()))
            .Message);

    // Fails: Cross-document navigation access issue EF-216
    public override Task Can_read_back_bool_mapped_as_int_through_navigation()
        => AssertTranslationFailed(() => base.Can_read_back_bool_mapped_as_int_through_navigation());

    // Fails: Cross-document navigation access issue EF-216
    public override Task Can_read_back_mapped_enum_from_collection_first_or_default()
        => AssertTranslationFailed(() => base.Can_read_back_mapped_enum_from_collection_first_or_default());

    // Fails: Call ToString on DateTimeOffset EF-217
    public override async Task Object_to_string_conversion()
        => Assert.Contains(
            "Unsupported conversion from object to string in $convert with no onError value.",
            (await Assert.ThrowsAsync<MongoCommandException>(() => base.Object_to_string_conversion())).Message);

    // Fails: Projecting DateTimeOffset members EF-218
    public override async Task Optional_datetime_reading_null_from_database()
        => Assert.Contains(
            "Serializer for System.DateTimeOffset does not represent members as fields.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.Optional_datetime_reading_null_from_database()))
            .Message);
    #else
    // Fails: Include issue EF-117
    public override void Can_insert_and_read_back_with_string_key()
        => Assert.Contains(
            "Including navigation 'Navigation' is not supported as the navigation is not embedded in same resource.",
            Assert.Throws<InvalidOperationException>(() => base.Can_insert_and_read_back_with_string_key()).Message);

    // Fails: Cross-document navigation access issue EF-216
    public override void Can_read_back_bool_mapped_as_int_through_navigation()
        => AssertTranslationFailed(() => base.Can_read_back_bool_mapped_as_int_through_navigation());

    // Fails: Cross-document navigation access issue EF-216
    public override void Can_read_back_mapped_enum_from_collection_first_or_default()
        => AssertTranslationFailed(() => base.Can_read_back_mapped_enum_from_collection_first_or_default());

    // Fails: Call ToString on DateTimeOffset EF-217
    public override void Object_to_string_conversion()
        => Assert.Contains(
            "Unsupported conversion from object to string in $convert with no onError value.",
            Assert.Throws<MongoCommandException>(() => base.Object_to_string_conversion()).Message);

    // Fails: Projecting DateTimeOffset members EF-218
    public override void Optional_datetime_reading_null_from_database()
        => Assert.Contains(
            "Serializer for System.DateTimeOffset does not represent members as fields.",
            Assert.Throws<NotSupportedException>(() => base.Optional_datetime_reading_null_from_database()).Message);
    #endif

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    private static async Task AssertTranslationFailed(Func<Task> query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);

    public class BuiltInDataTypesMongoFixture : BuiltInDataTypesFixtureBase
    {
        public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
            => base.AddOptions(builder.ConfigureWarnings(w => w.Ignore(MongoEventId.ColumnAttributeWithTypeUsed)));

        protected override ITestStoreFactory TestStoreFactory
            => MongoTestStoreFactory.Instance;

        public override bool StrictEquality
            => true;

        public override int IntegerPrecision
            => 53;

        public override bool SupportsAnsi
            => false;

        public override bool SupportsUnicodeToAnsiConversion
            => false;

        public override bool SupportsLargeStringComparisons
            => true;

        public override bool SupportsBinaryKeys
            => false;

        public override bool SupportsDecimalComparisons
            => true;

        public override DateTime DefaultDateTime
            => new();

        public override bool PreservesDateTimeKind
            => false;
    }
}
