---
area: Public API, DI & options
scope: ["src/MongoDB.EntityFrameworkCore/Extensions/**", "src/MongoDB.EntityFrameworkCore/Infrastructure/**", "src/MongoDB.EntityFrameworkCore/Design/**"]
reviewer-agent: public-api-reviewer
adjacent-areas: [Metadata, Storage, Query, Serializers]
---

# Public API, DI & Options — AGENTS.md

Covers the provider's **public configuration surface** across three dirs (`Infrastructure/CLAUDE.md` and
`Design/CLAUDE.md` re-point here):

- `Extensions/` — fluent + DI entry points and model-building helpers.
- `Infrastructure/` — the options extension, model validator, queryable-encryption schema.
- `Design/` — a one-file design-time hook so `dotnet ef` doesn't crash.

**Out:** annotation storage/conventions (Metadata); the live `MongoClientWrapper` (Storage); event IDs/logging
(Diagnostics).

## The public surface (the contract)

Keep current when adding overloads:

1. **`UseMongoDB(...)`** — every `(connectionString | mongoClient | mongoClientSettings, databaseName?)`
   combination, plus a direct `MongoOptionsExtension` overload; generic/non-generic; optional trailing
   `Action<MongoDbContextOptionsBuilder>`.
2. **`AddMongoDB<TContext>(...)`** — same connection shapes via `AddDbContext<TContext>`.
3. **`AddEntityFrameworkMongoDB()`** — low-level service registration for users managing their own
   `EntityFrameworkServicesBuilder`.
4. **Model-building fluent API** — `MongoEntityTypeBuilderExtensions`, `MongoPropertyBuilderExtensions`,
   `MongoIndexBuilderExtensions`, `QueryableEncryptionBuilderExtensions`.
5. **Runtime database operations** — `MongoDatabaseFacadeExtensions`: `CreateMissingIndexes`,
   `CreateMissingVectorIndexes`, `WaitForVectorIndexes(timeout)`, `BeginTransaction(options)`,
   `EnsureCreated(MongoDatabaseCreationOptions)`.
6. **Query-time LINQ extension** — `MongoQueryableExtensions.VectorSearch<,>`.

## DI registration

`MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB()` is the single binding point to EF's
`EntityFrameworkServicesBuilder` (see the file for the full service list: `IDatabase`, `IDbContextTransactionManager`,
`IModelValidator`, `IProviderConventionSetBuilder`, `ITypeMappingSource`, query factories, `BsonSerializerFactory`,
`IMongoClientWrapper`, etc.).

## `MongoOptionsExtension`

The `IDbContextOptionsExtension` carrying every config field. Invariants:

- **Immutable** — every `With*` returns a clone.
- **Connection-source exclusivity** — exactly one of `ConnectionString`/`MongoClient`/`ClientSettings`
  (`EnsureConnectionNotAlreadyConfigured`). A new connection-source `With*` must extend this check.
- **`Info.GetServiceProviderHashCode()`** is based on `ConnectionString` + `DatabaseName` — contexts sharing
  those reuse one internal service provider.
- **`LogFragment` never includes the connection string** — it carries credentials; security-relevant.

## Boundaries with adjacent areas

- **vs Metadata.** Builder methods live here; annotation semantics live in Metadata — reviewed separately.
- **vs Storage.** This configures the client; `MongoClientWrapper` owns the runtime one.
- **vs Query.** `VectorSearch<,>` is parsed by Query's visitors — a new query-time extension here needs
  matching visitor support.
- **vs Design.** `MongoDesignTimeServices` calls `AddEntityFrameworkMongoDB()`; keep that call or `dotnet ef`
  fails with cryptic missing-service errors. No migrations/scaffolding support.

## Common pitfalls

- **Public API is the contract** — signature/default/visibility/behavior changes here are candidate breaking
  changes even in minor releases (`BREAKING-CHANGES.md`).
- **`AddMongoDB<TContext>(IMongoClient, ...)`** — a user-supplied client is theirs to manage; the provider only
  borrows it.
- **`MongoModelValidator` runs after conventions** — validate combinations there, not inside a builder method.
- **`VectorSearch(...)` must be at queryable root** (optionally after a `Where`); new overloads need matching
  Query changes.
- **Changing `QueryableEncryptionSchemaMode`'s default** is a behavior break.

## How to test

- Unit: `…UnitTests/Infrastructure/`, `…UnitTests/Extensions/`.
- Spec: `…SpecificationTests/Extensions/`.
- Functional: `…FunctionalTests/Design/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Infrastructure|FullyQualifiedName~Extensions|FullyQualifiedName~Design"
```
