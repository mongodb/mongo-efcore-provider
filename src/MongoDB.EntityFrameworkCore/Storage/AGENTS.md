---
area: Storage, update pipeline & transactions
scope: ["src/MongoDB.EntityFrameworkCore/Storage/**"]
reviewer-agent: storage-reviewer
adjacent-areas: [Query, Metadata, Serializers, Infrastructure]
---

# Storage — AGENTS.md

## Scope

The runtime execution layer: wraps `IMongoClient`/`IMongoDatabase`/`IMongoCollection<BsonDocument>`, turns
EF's `IUpdateEntry` change set into MongoDB writes, manages transactions and sessions, creates
databases/collections/indexes/vector indexes, and supplies type mappings and MongoDB-specific
`ValueConverter`s.

**Out:** BSON serialization (Serializers), entity/property metadata (Metadata), query translation (Query), and
client *construction* from a connection string/settings (Infrastructure builds the client; Storage wraps it).

## SaveChanges flow

```
DbContext.SaveChanges()
  → MongoDatabaseWrapper.SaveChanges(IList<IUpdateEntry>)
      │
      ├─ promote owned-entity entries to their document-root parents (GetAllChangedRootEntries)
      ├─ MongoUpdate.CreateAll(rootEntries)
      │      Added    → InsertOneModel<BsonDocument>
      │      Modified → UpdateOneModel<BsonDocument>  (filter = _id + concurrency tokens; update = $set)
      │      Deleted  → DeleteOneModel<BsonDocument>  (filter = _id + concurrency tokens)
      │      RowVersion is incremented in place via SetStoreGeneratedValues
      │
      ├─ AddEntriesPromotedDuringSave (re-sync EF's tracker for promoted roots)
      │
      ├─ decide transaction mode (AutoTransactionBehavior):
      │      Always (default) → implicit transaction
      │      WhenNeeded       → transaction iff operationCount > 1
      │      Never            → none; concurrency checks lose their guarantee
      │
      ├─ MongoUpdateBatch.CreateBatches(updates)  — group by collection name
      ├─ per batch: IMongoCollection<BsonDocument>.BulkWrite(session, models)
      │      AssertWritesApplied() → DbUpdateConcurrencyException if matched/inserted/deleted counts disagree
      │
      └─ commit / rollback the implicit transaction
```

## Key entry points

| Type | Responsibility |
|---|---|
| `IMongoClientWrapper` / `MongoClientWrapper` | Lazy-initializes client/database; `GetCollection<T>`, `StartSession`, `Execute<T>(MongoExecutableQuery)`. Also where Queryable Encryption auto-schema is injected at client construction. |
| `MongoDatabaseWrapper` | EF's `IDatabase`; orchestrates SaveChanges. **Not** an interface to implement — users go through `DbContext.SaveChanges()`. |
| `MongoTransactionManager` / `MongoTransaction` / `MongoTransactionEnlistmentManager` | Transaction control. `MongoTransaction` wraps `IClientSessionHandle` with a strict `Active → Committed \| RolledBack \| Failed → Disposed` state machine. Ambient `System.Transactions` are explicitly rejected. |
| `MongoDatabaseCreator` | `EnsureCreated`/`EnsureDeleted`, collection + index + vector-index creation, seeding (seeds use a standalone `IUpdateAdapter` through the same update pipeline). |
| `MongoUpdate` + `MongoUpdateBatch` | `IUpdateEntry` → `WriteModel<BsonDocument>`, and batch grouping by collection. |
| `MongoTypeMapping` + `MongoTypeMappingSource` | EF type mapping with MongoDB awareness (`ObjectId`, `Decimal128`, `BinaryVector*`, collections, dictionaries). |
| `ValueConversion/*` | `ObjectId ↔ string` and `Decimal128 ↔ decimal` converters plus the selector. |
| `BsonTypeHelper`, `BsonBinding`, `RowVersion` | Small helpers: BsonType ↔ string for index specs, document-field accessors for shapers, row-version detect/increment. |

## Boundaries with adjacent areas

- **vs Infrastructure.** `MongoOptionsExtension` carries connection config and *constructs* the client;
  `MongoClientWrapper` is the runtime wrapper over the resolved client.
- **vs Metadata.** Storage reads metadata heavily (`GetCollectionName`, `GetElementName`, `IsRowVersion`, owned
  shape, PK) but never writes annotations.
- **vs Serializers.** `MongoUpdate.WriteProperty` gets serialization info from `BsonSerializerFactory` and
  writes through the resulting `IBsonSerializer`. **A `BsonWriter`/`BsonReader` in this area is almost
  certainly a layering mistake.**
- **vs Query.** `MongoClientWrapper.Execute(MongoExecutableQuery)` is the seam: Query produces, Storage
  executes. The wrapper doesn't translate; the visitors don't execute.
- **vs Query — bulk `ExecuteUpdate`/`ExecuteDelete` crosses as *behavior*, not data.**
  `MongoBulkOperationExecutor` runs server-side bulk writes from a `MongoBulkPlan` carrying
  `Func<QueryContext, …>` translation delegates it invokes at runtime (bulk translation is
  parameter-value-dependent). So a translation failure surfaces from a Storage frame —
  `TranslateBulkOrThrow` maps it to EF's canonical "could not be translated". The executor resolves its
  services from `QueryContext`; it does not translate.
- **vs the driver.** Storage may call `IMongoClient`, `IMongoDatabase`, `IMongoCollection<BsonDocument>`,
  `IClientSessionHandle` and `IndexKeysDefinitionBuilder` directly. No other area should.

## Common pitfalls

- **Two transaction-orchestration paths exist — keep them in sync.** `SaveChanges` starts its implicit
  transaction via `MongoTransaction.Start` (honoring `AutoTransactionBehavior.WhenNeeded`, count-based); the
  bulk two-phase path goes through `database.BeginTransaction()` (so phase-1 reads see
  `Database.CurrentTransaction`) and always begins, mapping standalone-deployment failures via
  `IsTransactionsUnsupported`. Any transaction-policy change must land in **both**.
- **Owned-entity root promotion** (`GetAllChangedRootEntries`): owned entries are replaced by their document
  root, and an Unchanged root with a modified owned child is promoted to Modified. Don't filter the entry list
  before promotion.
- **Concurrency-filter values are *original* values** (`entry.GetOriginalValue(property)`). Using current
  values silently corrupts conflict detection.
- **RowVersion ordering inside `MongoUpdate.ConvertModified` is contract:** `CreateWhereFilter` (captures the
  pre-increment token) → `SetStoreGeneratedValues` (increments in memory) → `WriteEntity` (serializes the
  post-increment value into `$set`). Reordering breaks the invariant that WHERE matches pre-increment while
  `$set` writes post-increment. Note the in-memory value is already advanced if serialization then fails.
- **`AutoTransactionBehavior.Never` disables optimistic concurrency.** Tokens still go into the filter, but
  without a transaction the increment isn't atomic with the read of the original.
- **Cross-collection atomicity comes from the transaction, not the bulk.** Each collection gets its own
  `BulkWrite`; don't reason about `BulkWrite` as the unit of atomicity.
- **Implicit transactions roll back on Dispose.** If `SaveChanges` throws before the commit,
  `MongoTransaction.Dispose` aborts — don't wrap the commit in a swallowing `catch`.
- **`EnsureCreated` is idempotent by design** — it re-applies seeds and re-creates indexes, tolerating
  duplicate-key errors while seeding. Tighten that contract carefully.
- **Multi-EF type-mapping shifts.** `MongoTypeMappingSource` has `#if EF8 || EF9` branches for dictionary
  comparers (EF10 reworked the signatures); changes must compile on all three.

## How to test

- Unit (helpers, converters): `…UnitTests/Storage/`.
- Functional (transactions, EnsureCreated, index creation): `…FunctionalTests/Storage/`.
- Update path (SaveChanges states, concurrency, owned entities): `…FunctionalTests/Update/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Storage|FullyQualifiedName~Update"
```
