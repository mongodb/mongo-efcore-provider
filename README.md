# MongoDB Entity Framework Core Provider

[![MongoDB.EntityFrameworkCore](https://img.shields.io/nuget/v/MongoDB.EntityFrameworkCore.svg)](https://www.nuget.org/packages/MongoDB.EntityFrameworkCore/)

The MongoDB EF Core Provider requires Entity Framework Core 8 or 9 on .NET 8 or later and a MongoDB database server 5.0 or later, preferably in a transaction-enabled configuration.

## Getting Started

### Basic Setup
First, create a DbContext with the desired entities and configuration:

```csharp
internal class PlanetDbContext : DbContext
{
    public DbSet<Planet> Planets { get; init; }

    public static PlanetDbContext Create(IMongoDatabase database) =>
        new(new DbContextOptionsBuilder<PlanetDbContext>()
            .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName)
            .Options);

    public PlanetDbContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Planet>().ToCollection("planets");
    }
}
```

### Connection Options

#### Option 1: Direct Database Connection
```csharp
var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI");
var mongoClient = new MongoClient(mongoConnectionString);
var db = PlanetDbContext.Create(mongoClient.GetDatabase("planets"));
db.Database.EnsureCreated();
var planet = db.Planets.FirstOrDefault(x => x.Name == "Earth");
```

#### Option 2: Using Dependency Injection
```csharp
var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDbUri")!;
builder.Services.AddDbContext<PlanetDbContext>(options =>
{
    options.UseMongoDB(mongoConnectionString, DatabaseName);
});
```

If you need some more configuration, you can create the `MongoClient` and inject it into the DbContext:

```csharp
var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDbUri")!;
var mongoUrl = new MongoUrl(mongoConnectionString);
var mongoClient = new MongoClient(mongoUrl);
builder.Services.AddSingleton<IMongoClient>(mongoClient);
builder.Services.AddDbContext<PlanetDbContext>((provider, options) =>
{
    var client = provider.GetRequiredService<IMongoClient>();
    options.UseMongoDB(client, DatabaseName);
});
```

Later on, simply inject `PlanetDbContext` where it's needed and continue as you would normally.

## Supported Features

Entity Framework Core and MongoDB have a wide variety of features. This provider supports a subset of the functionality available in both, specifically:

- Querying with `Where`, `Find`, `First`, `Single`, `OrderBy`, `ThenBy`, `Skip`, `Take` etc.
- Vector search with the `VectorSearch` extension method on DbSet and fluent vector index configuration
- Top-level aggregate `Any`, `Count`, `LongCount`, `Sum`, `Min`, `Max`, `Average`, `All`
- Mapping properties to BSON elements using `[Column]` or `[BsonElement]` attributes or `HasElementName("name")` method
- Mapping entities to collections via `[Table("name")]`,  `ToCollection("name")` or by convention from the DbSet property name
- Single or composite keys of standard types including string, `Guid` and `ObjectId` etc.
- Properties with typical CLR types (`int`, `string`, `Guid`, `decimal`, `DateOnly` etc.) & MongoDB types (`ObjectId`, `Decimal128`)
- Properties that are arrays, lists, dictionaries (string keys) of simple CLR types including binary `byte[]`
- Owned entities (aka value types, sub-documents, embedded documents) both directly and in collection properties
- `BsonIgnore`, `BsonId`, `BsonDateTimeOptions`, `BsonElement`, `BsonRepresentation` and `BsonRequired` support
- Storage type configuration through EF ValueConverters or BSON representation attributes and fluent APIs
- Query and update logging of MQL (sensitive logging must be enabled)
- `EnsureCreated` & `EnsureDeleted` to ensure collections and the database created at app start-up
- Optimistic concurrency support through `IsConcurrencyToken`/`ConcurrencyCheckAttribute` & `IsRowVersion`/`TimestampAttribute`
- AutoTransactional `SaveChanges` & `SaveChangesAsync` - all changes committed or rolled-back together
- `CamelCaseElementNameConvention` for helping map Pascal-cased C# properties to camel-cased BSON elements
- Type discriminators including `OfType<T>` and `Where(e => e is T)`
- Support for EF shadow properties and EF.Proxy for navigation traversal
- [Client Side Field Level Encryption](https://www.mongodb.com/docs/manual/core/csfle/quick-start/) and [Queryable Encryption](https://www.mongodb.com/docs/manual/core/queryable-encryption/) compatibility

## Limitations

A number of Entity Framework Core features are not currently supported but planned for future release. If you require use of these facilities
in the mean-time consider using the existing [MongoDB C# Driver's](https://github.com/mongodb/mongo-csharp-driver) LINQ provider which may support them.

### Planned for future releases

- Select projections with client-side operations
- GroupBy operations
- Includes/joins
- Geospatial
- Atlas search
- ExecuteUpdate & ExecuteDelete bulk operations (EF 9 only)

### Not supported, out-of-scope features

- Keyless entity types
- Migrations
- Database-first & model-first
- Document (table) splitting
- Temporal tables
- Timeseries
- GridFS

## Breaking changes

This project's version-numbers are aligned with Entity Framework Core and as-such we can not use the semver convention of constraining breaking changes solely to major version numbers. Please keep an eye on our [Breaking Changes](/BREAKING-CHANGES.md) document before upgrading to a new version of this provider.

## Documentation

- [MongoDB](https://www.mongodb.com/docs)
- [EF Core Provider Guide](https://www.mongodb.com/docs/entity-framework/current/)
- [EF Core Provider API Docs](https://mongodb.github.io/mongo-efcore-provider/8.2.0/api/index.html)

## Questions/Bug Reports

- [Forums](https://www.mongodb.com/community/forums/)
- [Jira](https://jira.mongodb.org/projects/EF/)

If you've identified a security vulnerability in a driver or any other MongoDB project, please report it according to the [instructions here](https://www.mongodb.com/docs/manual/tutorial/create-a-vulnerability-report).

## Contributing

Please see our [guidelines](CONTRIBUTING.md) for contributing to the driver.

Thank you to [everyone](https://github.com/mongodb/mongo-efcore-provider/graphs/contributors) who has contributed to this project.
