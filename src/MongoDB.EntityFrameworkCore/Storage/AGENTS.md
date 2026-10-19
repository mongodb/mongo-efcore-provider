---
area: Storage, update pipeline & transactions
scope: ["src/MongoDB.EntityFrameworkCore/Storage/**"]
reviewer-agent: storage-reviewer
adjacent-areas: [Query, Metadata, Serializers, Infrastructure]
---

# Storage — AGENTS.md

Runtime execution layer: wraps `IMongoClient`/`IMongoDatabase`/collections (`MongoClientWrapper`), turns EF's
`IUpdateEntry` set into writes (`MongoDatabaseWrapper.SaveChanges` → `MongoUpdate.CreateAll` →
`MongoUpdateBatch.CreateBatches` → per-collection `BulkWrite` → `AssertWritesApplied` throws
`DbUpdateConcurrencyException` on count mismatch), manages transactions (`MongoTransaction*`), creates
databases/collections/indexes (`MongoDatabaseCreator`), and supplies type mappings and converters.
Only Storage may call the driver client/collection/session types directly.

## Invariants

- **Two transaction paths must stay in sync.** `SaveChanges` starts its implicit transaction via
  `MongoTransaction.Start` (honoring `AutoTransactionBehavior.WhenNeeded`); the bulk two-phase path goes through
  `database.BeginTransaction()` and always begins. A policy change must land in both.
- **Owned-entity root promotion** (`GetAllChangedRootEntries`): an Unchanged root with a modified owned child is
  promoted to Modified; don't filter the entry list before promotion.
- **Concurrency-filter values are *original* values** (`GetOriginalValue`); current values corrupt conflict
  detection.
- **RowVersion ordering is contract**: `CreateWhereFilter` (pre-increment) → `SetStoreGeneratedValues` (increments
  in memory) → `WriteEntity` (post-increment into `$set`). The in-memory value is already advanced if
  serialization then fails.
- **`AutoTransactionBehavior.Never` removes optimistic concurrency's atomicity**, not the tokens.
- **Cross-collection atomicity comes from the transaction**, not `BulkWrite` (one per collection).
- **Implicit transactions roll back on Dispose**; don't wrap the commit in a swallowing `catch`. Ambient
  `System.Transactions` are rejected; `MongoTransaction` has a strict `Active → Committed | RolledBack | Failed →
  Disposed` state machine.
- **`EnsureCreated` is idempotent by design** (re-applies seeds, re-creates indexes, tolerates duplicate-key errors
  while seeding). Seeding goes through the normal update pipeline.
- **Query → Storage seam is `MongoClientWrapper.Execute(MongoExecutableQuery)`**; the wrapper doesn't translate.
  Bulk `ExecuteUpdate`/`ExecuteDelete` crosses as behavior: `MongoBulkOperationExecutor` runs a `MongoBulkPlan` whose
  translation delegates are invoked per execution (translation is parameter-value-dependent);
  `TranslateBulkOrThrow` maps failures to EF's canonical "could not be translated".
- **Serializers come from `BsonSerializerFactory`** (`MongoUpdate.WriteProperty`); a `BsonWriter`/`BsonReader` here is
  almost certainly a layering mistake. Storage reads metadata, never writes annotations.
- **Queryable Encryption auto-schema is injected at client construction** in `MongoClientWrapper`;
  `MongoOptionsExtension` (Infrastructure) builds the client.
- **`MongoTypeMappingSource` has `#if EF8 || EF9` branches** (EF10 reworked dictionary-comparer signatures); must
  compile on all three.
- `MongoDatabaseWrapper` and `IMongoClientWrapper` are not meant to be implemented by users.

## Testing

`tests/…UnitTests/Storage/`; `…FunctionalTests/Storage/` (transactions, EnsureCreated, indexes) and `…/Update/`
(SaveChanges states, concurrency, owned entities); filter `FullyQualifiedName~Storage|FullyQualifiedName~Update`.
