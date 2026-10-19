---
area: Storage, update pipeline & transactions
scope: ["src/MongoDB.EntityFrameworkCore/Storage/**"]
reviewer-agent: storage-reviewer
adjacent-areas: [Query, Metadata, Serializers, Infrastructure]
---

# Storage — AGENTS.md

## Scope

The runtime execution layer: wraps `IMongoClient`/`IMongoDatabase`/`IMongoCollection<BsonDocument>`, turns
EF's `IUpdateEntry` change set into writes, manages transactions/sessions, creates
databases/collections/indexes/vector indexes, supplies type mappings and MongoDB-specific `ValueConverter`s.

**Out:** BSON serialization (Serializers), entity/property metadata (Metadata), query translation (Query),
client *construction* from a connection string (Infrastructure builds it; Storage wraps it).

## SaveChanges flow

```
DbContext.SaveChanges()
  → MongoDatabaseWrapper.SaveChanges(IList<IUpdateEntry>)
      ├─ promote owned-entity entries to their document-root parents (GetAllChangedRootEntries)
      ├─ MongoUpdate.CreateAll(rootEntries)
      │      Added    → InsertOneModel<BsonDocument>
      │      Modified → UpdateOneModel<BsonDocument>  (filter = _id + concurrency tokens; update = $set)
      │      Deleted  → DeleteOneModel<BsonDocument>  (filter = _id + concurrency tokens)
      │      RowVersion incremented in place via SetStoreGeneratedValues
      ├─ AddEntriesPromotedDuringSave (re-sync EF's tracker for promoted roots)
      ├─ decide transaction mode (AutoTransactionBehavior): Always (default, implicit) / WhenNeeded
      │      (iff operationCount > 1) / Never (concurrency checks lose their guarantee)
      ├─ MongoUpdateBatch.CreateBatches(updates)  — group by collection name
      ├─ per batch: IMongoCollection<BsonDocument>.BulkWrite(session, models)
      │      AssertWritesApplied() → DbUpdateConcurrencyException on count mismatch
      └─ commit / rollback the implicit transaction
```

## Key entry points

| Type | Responsibility |
|---|---|
| `IMongoClientWrapper`/`MongoClientWrapper` | Lazy client/database init; `GetCollection<T>`, `StartSession`, `Execute<T>(MongoExecutableQuery)`. Queryable Encryption auto-schema is injected here at client construction. |
| `MongoDatabaseWrapper` | EF's `IDatabase`; orchestrates SaveChanges. Not meant to be implemented by users. |
| `MongoTransactionManager`/`MongoTransaction`/`MongoTransactionEnlistmentManager` | Transaction control. `MongoTransaction` wraps `IClientSessionHandle` with a strict `Active → Committed \| RolledBack \| Failed → Disposed` state machine. Ambient `System.Transactions` are rejected. |
| `MongoDatabaseCreator` | `EnsureCreated`/`EnsureDeleted`, collection/index/vector-index creation, seeding (via a standalone `IUpdateAdapter` through the same update pipeline). |
| `MongoUpdate`+`MongoUpdateBatch` | `IUpdateEntry` → `WriteModel<BsonDocument>`; batch grouping by collection. |
| `MongoTypeMapping`+`MongoTypeMappingSource` | EF type mapping with MongoDB awareness. |
| `ValueConversion/*` | `ObjectId ↔ string` and `Decimal128 ↔ decimal` converters + selector. |
| `BsonTypeHelper`, `BsonBinding`, `RowVersion` | BsonType↔string for index specs, document-field accessors for shapers, row-version detect/increment. |

## Boundaries with adjacent areas

- **vs Infrastructure.** `MongoOptionsExtension` constructs the client; `MongoClientWrapper` wraps the
  resolved one at runtime.
- **vs Metadata.** Reads metadata heavily, never writes annotations.
- **vs Serializers.** `MongoUpdate.WriteProperty` gets serializers from `BsonSerializerFactory`. A
  `BsonWriter`/`BsonReader` in this area is almost certainly a layering mistake.
- **vs Query.** `MongoClientWrapper.Execute(MongoExecutableQuery)` is the seam — Query produces, Storage
  executes; the wrapper doesn't translate.
- **vs Query — bulk `ExecuteUpdate`/`ExecuteDelete` crosses as behavior, not data.**
  `MongoBulkOperationExecutor` runs bulk writes from a `MongoBulkPlan` carrying translation delegates it
  invokes at runtime (translation is parameter-value-dependent); `TranslateBulkOrThrow` maps failures to EF's
  canonical "could not be translated" from a Storage frame.
- **vs the driver.** Storage may call `IMongoClient`/`IMongoDatabase`/`IMongoCollection<BsonDocument>`/
  `IClientSessionHandle`/`IndexKeysDefinitionBuilder` directly; no other area should.

## Common pitfalls

- **Two transaction paths must stay in sync.** `SaveChanges` starts its implicit transaction via
  `MongoTransaction.Start` (honoring `AutoTransactionBehavior.WhenNeeded`); the bulk two-phase path goes
  through `database.BeginTransaction()` and always begins. A transaction-policy change must land in both.
- **Owned-entity root promotion** (`GetAllChangedRootEntries`) — an Unchanged root with a modified owned child
  is promoted to Modified; don't filter the entry list before promotion.
- **Concurrency-filter values are *original* values** (`GetOriginalValue`) — current values corrupt conflict
  detection.
- **RowVersion ordering is contract**: `CreateWhereFilter` (pre-increment) → `SetStoreGeneratedValues`
  (increments in memory) → `WriteEntity` (writes post-increment into `$set`). The in-memory value is already
  advanced if serialization fails after.
- **`AutoTransactionBehavior.Never` disables optimistic concurrency's atomicity**, not the tokens themselves.
- **Cross-collection atomicity comes from the transaction**, not `BulkWrite` (each collection gets its own).
- **Implicit transactions roll back on Dispose** — don't wrap the commit in a swallowing `catch`.
- **`EnsureCreated` is idempotent by design** — re-applies seeds, re-creates indexes, tolerates duplicate-key
  errors while seeding.
- **Multi-EF type-mapping shifts** — `MongoTypeMappingSource` has `#if EF8 || EF9` branches (EF10 reworked
  dictionary-comparer signatures); changes must compile on all three.

## How to test

- Unit: `…UnitTests/Storage/`.
- Functional: `…FunctionalTests/Storage/` (transactions, EnsureCreated, index creation), `…FunctionalTests/Update/` (SaveChanges states, concurrency, owned entities).

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Storage|FullyQualifiedName~Update"
```
