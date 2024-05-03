# MongoDB Entity Framework Core Provider 

The MongoDB EF Core Provider requires Entity Framework Core 8 on .NET 8 or later.

## Getting Started

Setup a DbContext with your desired entities and configuration

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

To get going with the DbContext:

```csharp
var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI");
var mongoClient = new MongoClient(mongoConnectionString);
var db = PlanetDbContext.Create(mongoClient.GetDatabase("planets"));
```

## Supported Features

Entity Framework Core and MongoDB have a wide variety of features. This provider supports a subset of the functionality available in both, specifically:

- Querying with Where, Find, First, Single, OrderBy, ThenBy, Skip, Take etc.
- Top-level aggregates of Any, Count, LongCount
- Mapping properties to BSON elements using `[Column]` or `[BsonElement]` attributes or `HasElementName("name")` method
- Mapping entities to collections using `[Table("name")]` attribute or `ToCollection("name")` method
- Single or composite keys of standard types including string, Guid and ObjectId
- Properties with typical CLR types (int, string, Guid, decimal), Mongo types (ObjectId, Decimal128)
- Properties containing arrays and lists of simple CLR types
- Owned entities (aka value types, sub-documents, embedded documents) both directly and within collections
- BsonIgnore, BsonId, BsonDateTimeOptions, BsonElement and BsonRequired support
- Value converters using `HasConversion`
- Query and update logging including MQL (sensitive mode only)
- Some mapping configuration options for DateTime

## Limitations

A number of Entity Framework Core features are not currently supported but planned for future release. If you require use of these facilities
in the mean-time consider using the existing [MongoDB C# Driver's](https://github.com/mongodb/mongo-csharp-driver) LINQ provider which supports them.

### Planned for next release

- Select projections must only contain server-translatable expressions
- Sum, Average, Min, Max etc. are not supported at top level
- Transactions
- Type discriminators
- Additional mapping configuration options
- EnsureCreated & EnsureDeleted operations

### Not supported but considering for future releases

- ExecuteUpdate & ExecuteDelete
- Properties of Dictionary type
- Binary/byte array properties
- Additional CLR types (DateOnly, TimeOnly etc).
- EF shadow properties
- GroupBy operations
- Relationships between entities
- Includes/joins
- Foreign keys and navigation traversal
  
### Not supported & out-of-scope features

- Keyless entity types
- Schema migrations
- Database-first & model-first
- Alternate keys
- Document (table) splitting
- Temporal tables
- Spacial data
- Timeseries
- Atlas search

## Documentation

- [MongoDB](https://www.mongodb.com/docs)
- [EF Provider Guide](https://www.mongodb.com/docs/entity-framework/current/)
- [EF Provider API Docs](https://mongodb.github.io/mongo-efcore-provider/8.0.0/api/index.html)

## Questions/Bug Reports

- [Forums](https://www.mongodb.com/community/forums/)
- [Jira](https://jira.mongodb.org/projects/EF/)

If you’ve identified a security vulnerability in a driver or any other MongoDB project, please report it according to the [instructions here](https://www.mongodb.com/docs/manual/tutorial/create-a-vulnerability-report).

## Contributing

Please see our [guidelines](CONTRIBUTING.md) for contributing to the driver.

### Maintainers:
* Damien Guard              damien.guard@mongodb.com
* Oleksandr Poliakov        oleksandr.poliakov@mongodb.com
* Robert Stam               robert@mongodb.com
