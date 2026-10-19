---
area: Public API, DI & options
scope: ["src/MongoDB.EntityFrameworkCore/Extensions/**", "src/MongoDB.EntityFrameworkCore/Infrastructure/**", "src/MongoDB.EntityFrameworkCore/Design/**"]
reviewer-agent: public-api-reviewer
adjacent-areas: [Metadata, Storage, Query, Serializers]
---

# Public API, DI & Options — AGENTS.md

The provider's public configuration surface: `Extensions/` (fluent + DI entry points, model-building helpers),
`Infrastructure/` (options extension, model validator, queryable-encryption schema), `Design/` (design-time hook).

## Contract

Signature/default/visibility/behavior changes here are candidate breaking changes (see root `AGENTS.md`). Keep
overload sets complete when adding one:

- `UseMongoDB(...)` — every `(connectionString | mongoClient | mongoClientSettings, databaseName?)` combination
  plus a `MongoOptionsExtension` overload; generic/non-generic; optional trailing
  `Action<MongoDbContextOptionsBuilder>`. `AddMongoDB<TContext>(...)` mirrors the connection shapes.
- `AddEntityFrameworkMongoDB()` (`MongoServiceCollectionExtensions`) is the single binding point to EF's
  `EntityFrameworkServicesBuilder`.
- Model builders (`MongoEntityTypeBuilderExtensions`, `MongoPropertyBuilderExtensions`,
  `MongoIndexBuilderExtensions`, `QueryableEncryptionBuilderExtensions`) and `MongoDatabaseFacadeExtensions`.
- `MongoQueryableExtensions.VectorSearch<,>` must be at the queryable root (optionally after a `Where`); a new
  query-time extension or overload needs matching Query visitor support.

## Invariants

- **`MongoOptionsExtension` is immutable** (every `With*` clones) and **exactly one** of `ConnectionString`/
  `MongoClient`/`ClientSettings` may be set (`EnsureConnectionNotAlreadyConfigured`); extend that check for any new
  connection source.
- **`GetServiceProviderHashCode()` is based on `ConnectionString` + `DatabaseName`**; contexts sharing them share an
  internal service provider.
- **`LogFragment` must never include the connection string** (credentials).
- **A user-supplied `IMongoClient` is theirs**; the provider only borrows it.
- **`MongoModelValidator` runs after conventions**; validate option combinations there, not in a builder method.
- **`MongoDesignTimeServices` must keep calling `AddEntityFrameworkMongoDB()`** or `dotnet ef` fails with cryptic
  missing-service errors. No migrations/scaffolding support.
- Changing `QueryableEncryptionSchemaMode`'s default is a behavior break.
- Annotation semantics live in Metadata; builder methods here only surface them.

## Testing

`tests/…UnitTests/Infrastructure/`, `…UnitTests/Extensions/`, `…SpecificationTests/Extensions/`,
`…FunctionalTests/Design/`; filter `FullyQualifiedName~Infrastructure|FullyQualifiedName~Extensions|FullyQualifiedName~Design`.
