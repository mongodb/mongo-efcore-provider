---
area: Storage, update pipeline & transactions
scope: ["src/MongoDB.EntityFrameworkCore/Storage/**"]
reviewer-agent: storage-reviewer
adjacent-areas: [Query, Metadata, Serializers, Infrastructure]
---

# Storage — AGENTS.md

## Scope

The runtime execution layer. Wraps `IMongoClient` / `IMongoDatabase` / `IMongoCollection<BsonDocument>`, translates EF's `IUpdateEntry` change set into MongoDB write operations, manages transactions and sessions, creates databases / collections / indexes / vector indexes, and supplies type mappings and MongoDB-specific `ValueConverter`s.

In: client wrapper, database wrapper, transaction manager + transaction wrapper, update / bulk-write pipeline, database creator (incl. vector-index creation and model seeding), type-mapping source, MongoDB-specific value converters (ObjectId ↔ string, Decimal128 ↔ decimal), row-version handling, BSON-binding helpers used during shaping.

Out: BSON serialization (Serializers area), entity / property metadata (Metadata area), query translation (Query area), client construction from a connection string / settings (Infrastructure area — `MongoOptionsExtension` builds the client; Storage wraps and consumes it).

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
      │      Always (default) → wrap in implicit transaction
      │      WhenNeeded       → wrap iff operationCount > 1
      │      Never            → no transaction; concurrency checks lose their guarantee
      │
      ├─ MongoUpdateBatch.CreateBatches(updates)  — group by collection name
      ├─ for each batch: IMongoCollection<BsonDocument>.BulkWrite(session, models)
      │      AssertWritesApplied() → throw DbUpdateConcurrencyException if matched/inserted/deleted counts disagree
      │
      └─ commit / rollback the implicit transaction
```

## Key entry points

- `IMongoClientWrapper` / `MongoClientWrapper` — wraps `IMongoClient`/`IMongoDatabase`; lazy-initializes them, supplies `GetCollection<T>(...)`, `StartSession(...)`, and `Execute<T>(MongoExecutableQuery)`. Also where Queryable Encryption auto-schema gets injected at client construction.
- `MongoDatabaseWrapper` — EF's `IDatabase` implementation; orchestrates the SaveChanges pipeline. **Not** an interface to implement — users go through EF's `DbContext.SaveChanges()`.
- `IMongoTransactionManager` + `MongoTransactionManager` + `MongoTransaction` + `MongoTransactionEnlistmentManager` — transaction control. `MongoTransaction` wraps `IClientSessionHandle` with a strict `Active → Committed | RolledBack | Failed → Disposed` state machine. Ambient `System.Transactions` are explicitly rejected.
- `IMongoDatabaseCreator` / `MongoDatabaseCreator` — `EnsureCreated` / `EnsureDeleted`, collection creation, index and vector-index creation, seed-data application (seeds use a standalone `IUpdateAdapter` and the same update pipeline).
- `MongoUpdate` + `MongoUpdateBatch` — the EF-`IUpdateEntry` → `WriteModel<BsonDocument>` converter and its batch grouping (by collection name).
- `MongoTypeMapping` + `MongoTypeMappingSource` — EF type-mapping infrastructure with MongoDB awareness (`ObjectId`, `Decimal128`, `BinaryVectorFloat32`/`Int8`/`PackedBit`, collections, dictionaries).
- `ValueConversion/MongoValueConverterSelector` and the seven converter files under `ValueConversion/` — bidirectional converters for `ObjectId ↔ string` and `Decimal128 ↔ decimal`.
- `BsonTypeHelper`, `BsonBinding`, `RowVersion` — small helpers (BsonType ↔ string for index specs; document-field accessor expressions for shapers; row-version detection and increment).

## Boundaries with adjacent areas

- **vs Infrastructure.** `MongoOptionsExtension` (Infrastructure) carries the user's connection config and *constructs* the `MongoClient` (or accepts one). `MongoClientWrapper` (Storage) is the runtime wrapper; it depends on the resolved client.
- **vs Metadata.** Storage reads metadata extensively (`GetCollectionName`, `GetElementName`, `IsRowVersion`, owned-entity shape, primary key) — but never writes annotations. New metadata for the update path goes in Metadata; Storage just consumes it.
- **vs Serializers.** `MongoUpdate.WriteProperty` looks up serialization info via `BsonSerializerFactory.GetPropertySerializationInfo(property)` and writes BSON through the resulting `IBsonSerializer`. Storage never serializes BSON directly; if you find a `BsonWriter` / `BsonReader` here, that's almost certainly a layering mistake.
- **vs Query.** `MongoClientWrapper.Execute(MongoExecutableQuery)` is the seam — Query area produces the `MongoExecutableQuery`, Storage executes it via the driver. The wrapper doesn't translate; the visitors don't execute.
- **vs the driver.** Storage is allowed to call `IMongoClient`, `IMongoDatabase`, `IMongoCollection<BsonDocument>`, `IClientSessionHandle`, and `IndexKeysDefinitionBuilder` directly. No other area should.

## Common pitfalls

- **Owned-entity root promotion** (`MongoDatabaseWrapper.GetAllChangedRootEntries`). Owned entries are replaced by their document root, and an Unchanged root with a modified owned child is promoted to Modified. Don't filter the entry list before promotion.
- **Cross-collection consistency is per-collection.** Each collection gets its own `BulkWrite`. Even inside an implicit transaction, you're trusting the transaction — not the bulk — for cross-collection atomicity. Don't reason about `BulkWrite` as if it were the unit of atomicity.
- **`AutoTransactionBehavior.Never` disables optimistic concurrency.** Concurrency tokens still go into the WHERE clause, but without a transaction the row-version increment isn't guaranteed atomic with the read of the original.
- **Concurrency-filter values are *original* values** (`entry.GetOriginalValue(property)`), not current. Switching to current values silently corrupts conflict detection.
- **RowVersion increments before serialization.** If serialization fails, the in-memory value is already advanced. Callers should expect that on retry-after-failure they may need to re-read the entity.
- **Implicit transactions roll back on Dispose.** If `SaveChanges` throws before the explicit commit, `MongoTransaction.Dispose` aborts. Don't add `try { Commit() } catch` that swallows — let it propagate.
- **Idempotency of `EnsureCreated`.** It re-applies seeds and re-creates indexes; duplicate-key errors during seeding are tolerated by design. Tighten that contract carefully.
- **Multi-EF type-mapping shifts.** `MongoTypeMappingSource` has `#if EF8 || EF9` branches for dictionary comparers (EF10 reworked the comparer signatures). Changes need to compile under all three.

## How to test

- Unit tests (helpers, value converters): `tests/MongoDB.EntityFrameworkCore.UnitTests/Storage/`.
- Functional tests (transactions, EnsureCreated, index creation): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Storage/`.
- Update-path tests (SaveChanges with various entity states, concurrency, owned entities): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Update/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Storage|FullyQualifiedName~Update"
```
