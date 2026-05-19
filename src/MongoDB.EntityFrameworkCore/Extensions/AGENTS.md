---
area: Public API, DI & options
scope: ["src/MongoDB.EntityFrameworkCore/Extensions/**", "src/MongoDB.EntityFrameworkCore/Infrastructure/**", "src/MongoDB.EntityFrameworkCore/Design/**"]
reviewer-agent: public-api-reviewer
adjacent-areas: [Metadata, Storage, Query, Serializers]
---

# Public API, DI & Options — AGENTS.md

This `AGENTS.md` covers the three directories that together form the provider's **public configuration surface**: `Extensions/` (fluent + DI entry points and model-building helpers), `Infrastructure/` (options-extension, model validator, queryable-encryption schema), and `Design/` (a one-file design-time hook so `dotnet ef` doesn't crash). `src/MongoDB.EntityFrameworkCore/Infrastructure/CLAUDE.md` and `src/MongoDB.EntityFrameworkCore/Design/CLAUDE.md` re-point here.

## Scope

In:

- `UseMongoDB(...)` overloads on `DbContextOptionsBuilder` — connection-string, `IMongoClient`, `MongoClientSettings`, and `MongoOptionsExtension` shapes.
- `AddMongoDB<TContext>(...)` overloads on `IServiceCollection` and the provider's own `AddEntityFrameworkMongoDB()` service-registration entry point.
- `MongoOptionsExtension` (the `IDbContextOptionsExtension` carrying every configuration field) and `MongoDbContextOptionsBuilder` (currently a thin namespace for nested fluent calls).
- Model-building extensions surfacing Metadata annotations: `MongoEntityTypeBuilderExtensions` (`.ToCollection`, `.HasElementName`), `MongoPropertyBuilderExtensions` (`.HasElementName`, `.HasBsonRepresentation`, `.HaveDateTimeKind`, `.HasBinaryVectorDataType`), `MongoIndexBuilderExtensions` (`.HasCreateIndexOptions`, `.IsVectorIndex`), `QueryableEncryptionBuilderExtensions` (`.IsEncrypted` / `.IsEncryptedForEquality` / `.IsEncryptedForRange`).
- Query-time helpers exposed as extensions: `MongoQueryableExtensions.VectorSearch(...)` and `MongoDatabaseFacadeExtensions` (`CreateMissingIndexes`, `CreateMissingVectorIndexes`, `WaitForVectorIndexes`, `BeginTransaction`).
- `Infrastructure/MongoModelValidator` and the queryable-encryption schema providers / builders.
- `Design/MongoDesignTimeServices` — the `[assembly: DesignTimeProviderServices(...)]` entry point that lets `dotnet ef` resolve provider services for compiled-model generation. The provider does **not** support migrations or scaffolding; this exists so the tooling doesn't fall over.

Out: actual annotation storage and conventions (Metadata); the `MongoClientWrapper` that holds the live client (Storage); event IDs and logging (Diagnostics).

## Public configuration surface (the contract)

The big public-API "buckets" — keep this list current when adding overloads:

1. **`UseMongoDB(...)`** — every combination of `(connectionString | mongoClient | mongoClientSettings, databaseName?)`, plus a `MongoOptionsExtension` direct-injection overload. Generic (`<TContext>`) and non-generic variants of each. Optional trailing `Action<MongoDbContextOptionsBuilder>` for nested options.
2. **`AddMongoDB<TContext>(...)`** — same connection shapes; wires up an `AddDbContext<TContext>` registration in one call.
3. **`AddEntityFrameworkMongoDB()`** — the *low-level* service registration; called internally by `MongoOptionsExtension.ApplyServices`. Public for users who already manage their own `EntityFrameworkServicesBuilder`.
4. **Model-building fluent API** (`Mongo*BuilderExtensions` and `QueryableEncryptionBuilderExtensions`) — surface Metadata annotations through `EntityTypeBuilder` / `PropertyBuilder` / `IndexBuilder`.
5. **Runtime database operations** (`MongoDatabaseFacadeExtensions`) — `database.CreateMissingIndexes()`, `.CreateMissingVectorIndexes()`, `.WaitForVectorIndexes(timeout)`, `.BeginTransaction(transactionOptions)`, and the `EnsureCreated(MongoDatabaseCreationOptions)` overload.
6. **Query-time LINQ extension** (`MongoQueryableExtensions.VectorSearch<,>`).

## DI registration shape

`MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB()` is the single point where provider services are bound to EF Core's `EntityFrameworkServicesBuilder`. Key registrations (non-exhaustive — defer to the file):

- `IDatabaseProvider` → `DatabaseProvider<MongoOptionsExtension>`
- `IDatabase` → `MongoDatabaseWrapper`
- `IDbContextTransactionManager` → `MongoTransactionManager`
- `IModelValidator` → `MongoModelValidator`
- `IProviderConventionSetBuilder` → `MongoConventionSetBuilder`
- `ITypeMappingSource` → `MongoTypeMappingSource`
- `IValueConverterSelector` → `MongoValueConverterSelector`
- `IValueGeneratorSelector` → `MongoValueGeneratorSelector`
- Query factories: `IQueryCompilationContextFactory`, `IQueryTranslationPreprocessorFactory`, `IQueryTranslationPostprocessorFactory`, `IQueryableMethodTranslatingExpressionVisitorFactory`, `IShapedQueryCompilingExpressionVisitorFactory`
- Singletons: `BsonSerializerFactory`, `MongoShapedQueryCompilingExpressionVisitorDependencies`
- Scoped: `IMongoClientWrapper`, `IMongoDatabaseCreator`, `IQueryableEncryptionSchemaProvider`, `ITransactionEnlistmentManager`

## `MongoOptionsExtension`

The `IDbContextOptionsExtension` that carries every configuration field. Properties (non-exhaustive): `ConnectionString`, `ClientSettings`, `MongoClient`, `DatabaseName`, `CryptProvider`, `CryptProviderPath`, `KeyVaultNamespace`, `KmsProviders`, `CryptExtraOptions`, `QueryableEncryptionSchemaMode`.

Invariants worth knowing:

- **Immutable.** Every `With*` returns a clone. Don't reach in and mutate fields.
- **Connection-source exclusivity.** Exactly one of `ConnectionString` / `MongoClient` / `ClientSettings` may be set; the `With*` methods enforce this. Loosening this is a breaking change.
- **`Info.GetServiceProviderHashCode()` is based on `ConnectionString` + `DatabaseName`.** Contexts sharing those reuse the same internal service provider. Adding more state to the hash is fine but be deliberate.
- **`LogFragment` sanitizes passwords** — `SanitizeConnectionStringForLogging()`. This is a security-relevant guardrail; the `security-reviewer` will flag any regression.
- **`MongoDbContextOptionsBuilder` is intentionally thin** — it currently delegates everything to the underlying `DbContextOptionsBuilder`. The class exists as a namespace for future MongoDB-specific options without polluting the global EF options builder.

## Boundaries with adjacent areas

- **vs Metadata.** The `Mongo*BuilderExtensions` live here but their *semantics* (what `Mongo:CollectionName` means; how `BsonRepresentationConfiguration` is interpreted) live in Metadata. Adding an annotation is a Metadata change; surfacing it as a builder method is a change here. Two reviewers, two PR concerns.
- **vs Storage.** `MongoOptionsExtension` owns the *configuration* of the client; `MongoClientWrapper` (Storage) owns the *runtime* client. The wrapper takes the extension's resolved options at scoped-service construction time.
- **vs Query.** `MongoQueryableExtensions.VectorSearch<,>` is parsed and dispatched by the Query area's visitors — adding a query-time extension here without the corresponding visitor support produces a runtime "not translated" failure.
- **Design.** `MongoDesignTimeServices` calls `AddEntityFrameworkMongoDB()` so the tooling sees a complete service tree. It does not implement scaffolding (`dotnet ef dbcontext scaffold`).

## Common pitfalls

- **Public API is *the* contract.** Any signature, default-value, visibility, or behavior change to a method here is a candidate breaking change — and per `BREAKING-CHANGES.md`, even minor version bumps can carry breaks, so they need conscious documentation rather than slip-through.
- **`AddMongoDB<TContext>(IMongoClient, ...)` lifecycle warning.** When a user passes an existing `IMongoClient`, the client's lifecycle is theirs to manage; the provider only borrows it. Keep the XML doc on those overloads explicit about this.
- **Connection-source mutual exclusion.** `EnsureConnectionNotAlreadyConfigured` enforces it. New `With*` methods for *new* connection sources must extend this check.
- **`MongoModelValidator` runs after conventions.** Adding a validation here is the right place to reject combinations conventions can't catch (e.g. unsupported BSON attributes recorded via `Mongo:NotSupportedAttributes`). Adding it inside a builder method instead leaks validation across the public surface.
- **Vector-search call shape.** `VectorSearch(...)` must be at the root of a queryable (optionally with a pre-`Where` filter). Adding new overloads needs Query-area visitor changes to match.
- **`Design/MongoDesignTimeServices` must call `AddEntityFrameworkMongoDB()`**. If you split design-time services, keep that call in place or `dotnet ef` will fail with cryptic missing-service errors.
- **Queryable Encryption schema mode (`QueryableEncryptionSchemaMode`)** decides whether the provider auto-generates schemas, validates them, or takes the user's verbatim. Changing the default is a behavior break for existing users.

## How to test

- Unit tests (options, builders, validators): `tests/MongoDB.EntityFrameworkCore.UnitTests/Infrastructure/`, `tests/MongoDB.EntityFrameworkCore.UnitTests/Extensions/`.
- Spec tests (model-builder extensions): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Extensions/`.
- Functional tests (end-to-end DI, design-time generation): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Design/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Infrastructure|FullyQualifiedName~Extensions|FullyQualifiedName~Design"
```
