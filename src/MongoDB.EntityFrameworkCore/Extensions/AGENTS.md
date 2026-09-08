---
area: Public API, DI & options
scope: ["src/MongoDB.EntityFrameworkCore/Extensions/**", "src/MongoDB.EntityFrameworkCore/Infrastructure/**", "src/MongoDB.EntityFrameworkCore/Design/**"]
reviewer-agent: public-api-reviewer
adjacent-areas: [Metadata, Storage, Query, Serializers]
---

# Public API, DI & Options — AGENTS.md

Covers the three directories forming the provider's **public configuration surface**
(`Infrastructure/CLAUDE.md` and `Design/CLAUDE.md` re-point here):

- `Extensions/` — fluent + DI entry points and model-building helpers.
- `Infrastructure/` — the options extension, model validator, queryable-encryption schema.
- `Design/` — a one-file design-time hook so `dotnet ef` doesn't crash.

**Out:** annotation storage and conventions (Metadata); the `MongoClientWrapper` holding the live client
(Storage); event IDs and logging (Diagnostics).

## The public surface (the contract)

Keep this list current when adding overloads:

1. **`UseMongoDB(...)`** — every combination of `(connectionString | mongoClient | mongoClientSettings,
   databaseName?)`, plus a direct `MongoOptionsExtension` overload; generic and non-generic variants of each;
   optional trailing `Action<MongoDbContextOptionsBuilder>`.
2. **`AddMongoDB<TContext>(...)`** — the same connection shapes, wiring `AddDbContext<TContext>` in one call.
3. **`AddEntityFrameworkMongoDB()`** — the low-level service registration, called by
   `MongoOptionsExtension.ApplyServices`. Public for users managing their own `EntityFrameworkServicesBuilder`.
4. **Model-building fluent API** — `MongoEntityTypeBuilderExtensions` (`.ToCollection`, `.HasElementName`),
   `MongoPropertyBuilderExtensions` (`.HasElementName`, `.HasBsonRepresentation`, `.HaveDateTimeKind`,
   `.HasBinaryVectorDataType`), `MongoIndexBuilderExtensions` (`.HasCreateIndexOptions`, `.IsVectorIndex`),
   `QueryableEncryptionBuilderExtensions` (`.IsEncrypted` / `.IsEncryptedForEquality` / `.IsEncryptedForRange`).
5. **Runtime database operations** — `MongoDatabaseFacadeExtensions`: `CreateMissingIndexes`,
   `CreateMissingVectorIndexes`, `WaitForVectorIndexes(timeout)`, `BeginTransaction(options)`, and the
   `EnsureCreated(MongoDatabaseCreationOptions)` overload.
6. **Query-time LINQ extension** — `MongoQueryableExtensions.VectorSearch<,>`.

## DI registration

`MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB()` is the single point binding provider services to
EF's `EntityFrameworkServicesBuilder`. Non-exhaustive (defer to the file): `IDatabaseProvider` →
`DatabaseProvider<MongoOptionsExtension>`; `IDatabase` → `MongoDatabaseWrapper`; `IDbContextTransactionManager`
→ `MongoTransactionManager`; `IModelValidator` → `MongoModelValidator`; `IProviderConventionSetBuilder` →
`MongoConventionSetBuilder`; `ITypeMappingSource` → `MongoTypeMappingSource`; `IValueConverterSelector`;
`IValueGeneratorSelector`; the five query factories; singletons `BsonSerializerFactory` and
`MongoShapedQueryCompilingExpressionVisitorDependencies`; scoped `IMongoClientWrapper`,
`IMongoDatabaseCreator`, `IQueryableEncryptionSchemaProvider`, `ITransactionEnlistmentManager`.

## `MongoOptionsExtension`

The `IDbContextOptionsExtension` carrying every configuration field (`ConnectionString`, `ClientSettings`,
`MongoClient`, `DatabaseName`, `CryptProvider`, `CryptProviderPath`, `KeyVaultNamespace`, `KmsProviders`,
`CryptExtraOptions`, `QueryableEncryptionSchemaMode`). Invariants:

- **Immutable** — every `With*` returns a clone. Don't reach in and mutate fields.
- **Connection-source exclusivity** — exactly one of `ConnectionString`/`MongoClient`/`ClientSettings`,
  enforced by `EnsureConnectionNotAlreadyConfigured`. Loosening this is a breaking change; a `With*` for a
  *new* connection source must extend the check.
- **`Info.GetServiceProviderHashCode()` is based on `ConnectionString` + `DatabaseName`** — contexts sharing
  those reuse one internal service provider. Adding state to the hash is fine, but be deliberate.
- **`LogFragment` sanitizes passwords** (`SanitizeConnectionStringForLogging()`). Security-relevant;
  `security-reviewer` flags regressions.
- **`MongoDbContextOptionsBuilder` is intentionally thin** — it exists as a namespace for future
  MongoDB-specific options without polluting EF's global options builder.

## Boundaries with adjacent areas

- **vs Metadata.** The builder methods live here but their *semantics* live in Metadata. Adding an annotation
  is a Metadata change; surfacing it is a change here — two reviewers, two concerns.
- **vs Storage.** This area *configures* the client; `MongoClientWrapper` owns the runtime one, taking the
  resolved options at scoped-service construction.
- **vs Query.** `VectorSearch<,>` is parsed and dispatched by Query's visitors — adding a query-time extension
  here without matching visitor support produces a runtime "not translated" failure.
- **Design.** `MongoDesignTimeServices` calls `AddEntityFrameworkMongoDB()` so tooling sees a complete service
  tree. **Keep that call**, or `dotnet ef` fails with cryptic missing-service errors. The provider does not
  support migrations or scaffolding.

## Common pitfalls

- **Public API is *the* contract.** Any signature, default, visibility or behavior change here is a candidate
  breaking change — and per `BREAKING-CHANGES.md` even minor releases can carry breaks, so they need conscious
  documentation rather than slipping through.
- **`AddMongoDB<TContext>(IMongoClient, ...)` lifecycle.** A user-supplied client is theirs to manage; the
  provider only borrows it. Keep the XML doc on those overloads explicit about this.
- **`MongoModelValidator` runs after conventions** — the right place to reject combinations conventions can't
  catch (e.g. attributes recorded under `Mongo:NotSupportedAttributes`). Validating inside a builder method
  instead leaks validation across the public surface.
- **`VectorSearch(...)` must be at the root of a queryable** (optionally with a pre-`Where`). New overloads
  need matching Query-area changes.
- **Changing the `QueryableEncryptionSchemaMode` default** (auto-generate vs. validate vs. verbatim) is a
  behavior break for existing users.

## How to test

- Unit (options, builders, validators): `…UnitTests/Infrastructure/`, `…UnitTests/Extensions/`.
- Spec (model-builder extensions): `…SpecificationTests/Extensions/`.
- Functional (end-to-end DI, design-time generation): `…FunctionalTests/Design/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Infrastructure|FullyQualifiedName~Extensions|FullyQualifiedName~Design"
```
