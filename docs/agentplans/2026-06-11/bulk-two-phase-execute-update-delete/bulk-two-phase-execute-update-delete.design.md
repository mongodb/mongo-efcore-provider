# Two-phase `ExecuteUpdate` / `ExecuteDelete` for ordered / paged / distinct sources — Design

**Date:** 2026-06-11
**Status:** Approved (design); pending implementation plan
**JIRA:** EF-107 (follow-on to the initial bulk implementation)

## Summary

Extend the provider's server-side bulk `ExecuteDelete` / `ExecuteUpdate` so the source query may
combine `Where` with `OrderBy` / `ThenBy` / `Skip` / `Take` / `Distinct`. MongoDB's
`deleteMany` / `updateMany` take only a filter predicate — no `sort`, no `limit` — so these shapes
cannot be expressed as a single command and are rejected today. We add a **two-phase `_id`
emulation**: phase 1 runs the source query as an ordinary read projecting only the primary key
(`_id`); phase 2 issues `deleteMany` / `updateMany` filtered by `{ _id: { $in: <ids> } }`. This is
implemented entirely in the provider — no server or driver change required.

The existing `Where`-only path is **unchanged**: it remains a single atomic `deleteMany` /
`updateMany` with no transaction. The two-phase path is used only for the shapes that would
otherwise fail, and it wraps the two operations in a transaction for race-safety — so the
transaction cost is incurred only where the alternative is a hard failure.

## Background

The initial EF-107 work (`2026-06-09-execute-update-delete-design.md`) compiles a bulk operation
into one server command and rejects any source operator other than `Queryable.Where`
(`ValidateBulkSource` in `MongoQueryableMethodTranslatingExpressionVisitor`). The follow-on
analysis (`docs/failing-spec-tests.md`, `EF-X016`) identified a **provider-only alternative** for
the sort / limit / distinct family: query the target `_id`s, then act on them by `$in`. Its
trade-off is the loss of single-statement atomicity, recovered by running both phases inside a
transaction.

Key architectural fact that makes this clean (verified in
`MongoQueryableMethodTranslatingExpressionVisitor` and
`MongoShapedQueryCompilingExpressionVisitor`):

- The provider does **not** translate `OrderBy` / `Skip` / `Take` / `Distinct` into pipeline stages
  itself. `VisitMethodCall` captures the whole LINQ chain as `MongoQueryExpression.CapturedExpression`
  and hands it to the **driver's LINQ v3 provider**, which generates the pipeline. These operators
  are *not* in the switch that routes to the `null`-returning `Translate*` overrides, so they pass
  through and the read path already supports them.
- `Join` / `GroupBy` / `SelectMany` / `Intersect` / `Except` are deliberately routed to `base`
  precisely so they hit the `null` overrides and throw a clean "not supported" error. The read path
  **cannot** translate them.

So the set of shapes the two-phase path can support is exactly "what the read path can already
project." That boundary is self-enforcing: an operator the read path can't translate cannot reach
phase 1.

`TranslateQuery<TSource>` (in `MongoShapedQueryCompilingExpressionVisitor`) is the existing reusable
hook — it builds the driver `IQueryable<TSource>` from `collection.AsQueryable(session)`, wraps it
in the EF entity serializer, and translates the captured chain through
`MongoEFToLinqTranslatingExpressionVisitor`. Phase 1 reuses it with a key projection, so all
operator translation stays in one place (the driver).

### Out of scope

Read-side translation of `Join` / `GroupBy` / `SelectMany` / set operations is a separate, larger
query-engine feature (valuable well beyond bulk, with its own feasibility risk against the driver's
LINQ v3 capabilities). Once it exists, bulk inherits it for free through the same two-phase
machinery. It is **not** part of this work. Multiple-collection updates remain rejected — a
relational table-sharing concept with no document-model meaning.

## Decisions

| Decision | Choice |
|---|---|
| EF version support | EF9 + EF10 only, under the existing `#if !EF8` gate. EF8 still rejects bulk entirely. |
| Shapes added | `Where` combined with `OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending` / `Skip` / `Take` / `Distinct`. |
| Shapes still rejected | `Join` / `GroupBy` / `SelectMany` / `Intersect` / `Except` / `Select` / any other operator → canonical non-query translation error (unchanged). |
| `Where`-only path | Unchanged — single atomic `deleteMany` / `updateMany`, no transaction. |
| Phase-1 execution | Reuse `TranslateQuery<TSource>` with a primary-key (`_id`) projection; the driver translates `OrderBy`/`Skip`/`Take`/`Distinct`. |
| Phase-2 execution | `deleteMany` / `updateMany` filtered by `{ _id: { $in: <ids> } }`; update reuses existing `BuildUpdate` (`$set` / pipeline). |
| Transaction (ambient present) | Both phases run on the ambient `CurrentTransaction.Session`; provider does **not** commit (user owns it). |
| Transaction (none present) | Auto-begin on a fresh session, commit on success, abort on exception. |
| `AutoTransactionBehavior.Never` | Throw an actionable error telling the user to open a transaction for this shape, instead of auto-starting. |
| No transaction support (standalone) | Throw an actionable error naming the requirement. |
| Phase 1 placement | Runs **inside** the transaction scope (ambient or auto-started), so phase 1 and phase 2 share one snapshot. |
| Empty phase 1 | Skip phase 2; the auto-started transaction commits as a no-op (nothing written); return 0. |
| Return value | Phase-2 `DeletedCount` / `MatchedCount`, same `checked((int))` overflow contract as the single-command path. |
| Concurrency tokens | Not checked (standard EF behavior for these APIs; unchanged). |
| Diagnostics | Reuse existing `Executing/ExecutedBulkDelete` / `…Update` event IDs; add a two-phase / target-count detail. No new event IDs. |
| Public surface | None. `MongoNonQueryExpression` stays `internal`. |

## Components & data flow

```
ExecuteDelete / ExecuteUpdate  (EF core hook, EF9/EF10)
   │
   ▼  MongoQueryableMethodTranslatingExpressionVisitor
   │      TranslateExecuteDelete / TranslateExecuteUpdate
   │      └─ ClassifyBulkSource(chain):
   │            Where-only            → Strategy.SingleCommand
   │            + OrderBy/Skip/Take/Distinct → Strategy.TwoPhase
   │            anything else         → canonical non-query error
   │      → MongoNonQueryExpression { Kind, Setters, Strategy, SourceQuery }
   │
   ▼  MongoShapedQueryCompilingExpressionVisitor   (Execute/ExecuteAsync<TSource>)
          SingleCommand → existing path: BuildFilter → deleteMany/updateMany (no txn)
          TwoPhase:
            ├─ acquire session (see Transaction lifecycle)
            ├─ phase 1: TranslateQuery<TSource>(source chain + Select(_id)) → enumerate ids
            ├─ if ids empty → return 0 (no txn, no phase 2)
            ├─ phase 2: deleteMany/updateMany({ _id: { $in: ids } }) on session
            └─ commit (if auto-started) / abort on exception
```

### Translate-time: `ClassifyBulkSource`

`ValidateBulkSource` becomes `ClassifyBulkSource`, returning the strategy. It walks the captured
chain (after `UnwrapBulkOperator`) and:

- accepts `Queryable.Where` (as today) — contributes to the predicate;
- accepts `Queryable.OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending` / `Skip` /
  `Take` / `Distinct` — marks the operation `TwoPhase`;
- rejects anything else with the canonical `NonQueryTranslationFailedWithDetails` error and the same
  "Only … can scope a bulk operation" detail message, extended to name the now-supported operators.

Matching is by canonical `MethodInfo` on `typeof(Queryable)` (consistent with the existing
reference-equality discipline), so a same-named operator from another type still rejects.

### Marker node: `MongoNonQueryExpression`

Add `enum Strategy { SingleCommand, TwoPhase }` and a `Strategy Strategy { get; }` property set in
both constructors (Delete and Update). No other change — `SourceQuery.CapturedExpression` already
carries the full chain phase 1 needs.

### Execute-time: phase 1 (key projection)

For `TwoPhase`, reuse `TranslateQuery<TSource>` against `SourceQuery`, with the translate callback
appending a projection to the entity's primary-key property, and enumerate (sync/async per
`IsAsync`) on the acquired session to collect the `_id` values.

- MongoDB maps every entity's primary key to a single `_id` field — scalar for a single-property
  key, or a composite sub-document for composite keys. Both shapes are supported: `$in` works
  uniformly with either value type, so no special-casing is needed.
- The **implemented** phase 1 reads whole documents via the normal read path and retains only the
  `_id` field on the client side; it does **not** issue a server-side `{ _id: 1 }` projection.
  Adding a server-side `{ _id: 1 }` projection to reduce wire transfer is a possible future
  optimization but is out of scope for the current implementation.
- If a key projection cannot be formed (owned/keyless edge cases that cannot be a bulk root), fall
  back to the canonical non-query error.

### Execute-time: phase 2 + transaction lifecycle

Phase 2 builds `Builders<BsonDocument>.Filter.In("_id", ids)` and runs the existing
`deleteMany` / `updateMany`. For update, `BuildUpdate` is reused unchanged — setters (`$set` /
pipeline, constant / self-referencing) are orthogonal to the source shape.

Session acquisition:

1. **Ambient `CurrentTransaction` (a `MongoTransaction`) present** → use its `Session` for both
   phases; do not commit or abort (the user owns the transaction).
2. **No ambient transaction, `AutoTransactionBehavior != Never`** → start a client session, call
   `StartTransaction`, run both phases, `CommitTransaction` on success, `AbortTransaction` on
   exception, and dispose the session.
3. **No ambient transaction, `AutoTransactionBehavior == Never`** → throw an `InvalidOperationException`
   instructing the user to open an explicit transaction around this bulk shape.
4. **Deployment without transaction support** → throw an actionable `InvalidOperationException`
   naming the requirement (a transaction-capable deployment). Where feasible this is detected before
   issuing writes; otherwise the driver's transaction-unsupported error is wrapped with the same
   guidance.

Phase 1 runs **inside** the transaction scope so it and phase 2 observe one snapshot — a non-empty
phase 1 evaluated outside a transaction and then deleted inside one would reopen the race the
transaction is meant to close. An empty phase 1 therefore still sits inside the (auto-started)
transaction; it simply skips phase 2 and commits a no-op, returning 0.

The architecture note already in the file (bulk executors call the driver collection directly and
read the ambient session, a deliberate narrow exception to "Query never touches the driver") is
extended to cover phase 1's read and the auto-started transaction.

### Count semantics

Return the phase-2 result count (`DeletedCount` / `MatchedCount`) under the existing
`checked((int))` overflow contract. Inside the transaction, phases 1 and 2 observe a consistent
snapshot, so the count reflects the targeted set. (Without the transaction, a concurrent delete
between phases could shrink the count — which is the race the transaction closes.) `Take(0)` /
empty phase 1 returns 0 (committing a no-op transaction when one was auto-started).

### Diagnostics

The two-phase executor logs through the existing `ExecutingBulkDelete` / `ExecutedBulkDelete` /
`ExecutingBulkUpdate` / `ExecutedBulkUpdate` events, with a detail indicating two-phase execution
and the resolved target count, so the extra read round-trip is observable. No new event IDs — the
registry stays stable.

## Testing

- **Functional** (`FunctionalTests/Query/ExecuteDeleteTests`, `ExecuteUpdateTests`): add
  `OrderBy + Take`, `Skip + Take`, `Distinct`, and ordered-update cases; assert affected counts and
  post-state. Assert the auto-started transaction commits on success and rolls back on a forced
  failure (no partial effect). Add a `Where`-only regression asserting **no** transaction is started
  (single-command path preserved).
- **Transaction policy**: a test for `AutoTransactionBehavior.Never` (clear throw, no write issued)
  and a standalone-deployment error test (skipped/guarded when the test server is a replica set, per
  `TestServer` conditions).
- **Specification suite**: re-examine the `NorthwindBulkUpdates` cases tagged `EF-X016` — the
  `OrderBy`/`Skip`/`Take`/`Distinct`-scoped ones should now pass via two-phase. Move those from
  run-and-assert-failure to `base` (green) and update `docs/failing-spec-tests.md` (`EF-X016` shrinks
  to the join/group/set-op shapes only).
- **Unit** (`UnitTests/Query/Expressions/MongoNonQueryExpressionTests`): cover the new `Strategy`
  classification.
- **Multi-EF**: `/test-all` across EF8 (still rejects bulk), EF9, EF10.

## Risks & mitigations

- **Phase-1 / phase-2 divergence without a transaction** — closed by running both phases in one
  transaction; the `Never` and standalone cases throw rather than silently racing.
- **Phase-1 projection cost** — phase 1 transfers only `_id`s, not full documents; large target sets
  produce a large `$in`, which is the inherent cost of the emulation and is documented.
- **Behavior drift from the read path** — avoided by reusing `TranslateQuery` / the driver for all
  operator translation rather than hand-building the phase-1 pipeline.
- **EF version skew** — entirely under `#if !EF8`; no new public surface, so no breaking-change
  exposure.
