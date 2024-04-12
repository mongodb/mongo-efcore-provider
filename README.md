# MongoDB Entity Framework Core Provider Preview

This project is currently in preview and not recommended for production use yet.

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

And then to get going with the DbContext

```csharp
var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_URI");
var mongoClient = new MongoClient(mongoConnectionString);
var db = PlanetDbContext.Create(mongoClient.GetDatabase("planets"));
```

## Limitations & Roadmap

This preview has a number of limitations at this time. Please consider the following support matrix.

### Supported in Preview 1

- Entity Framework Core 7 & .NET 7 or later
- Querying with Where, Find, First, Single, OrderBy, ThenBy, Skip, Take
- Top-level aggregates of Any, Count, LongCount
- Mapping properties to BSON Element Names using `[Column]` attribute or `HasElementName("name")` method
- Mapping entities to collections using `[Table("name")]` attribute or `ToCollection("name")` method
- Composite keys
- Properties with typical CLR types (int, string, Guid, decimal), Mongo types (ObjectId, Decimal128) and "value" objects
- Properties containing arrays and lists of simple CLR types as well as "value" objects

### Roadmap for next releases

- Entity Framework Core 8 & .NET 8 or later
- Select projections
- Sum, Average, Min, Max etc.
- Value converters
- Type discriminators
- Logging
- Transactions
- Mapping configuration options

### Not supported but considering for future releases

- ExecuteUpdate & ExecuteDelete
- Properties with dictionary type
- Binary/byte array properties
- Keyless entity types
- Additional CLR types (DateOnly, TimeOnly etc).
- EF shadow properties
- GroupBy operations
- Relationships between entities
- Includes/joins
- Foreign keys and navigation traversal
  
### Not supported out-of-scope features

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
- [Documentation](https://www.mongodb.com/docs/entity-framework/current/)

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
