---
area: Diagnostics — events & logging
scope: ["src/MongoDB.EntityFrameworkCore/Diagnostics/**"]
reviewer-agent: diagnostics-reviewer
adjacent-areas: [Storage, Query]
---

# Diagnostics — AGENTS.md

## Scope

EF Core's events/logging integration: the provider's event IDs, their `EventDefinition`s, the logger extension
methods that emit them, and the `EventData` payloads dispatched to `DiagnosticSource`. Touchpoints are query
execution, bulk writes, and transaction lifecycle.

**Out:** the log *call sites* (they live in Storage and Query and call into here), and the sensitive-data
setting itself (`DbContextOptionsBuilder.EnableSensitiveDataLogging()`, read via `ShouldLogSensitiveData()`).

## Key entry points

- `MongoEventId` — the event-ID registry, grouped by `DbLoggerCategory.*`. Values are **versioned and
  immutable**: append new IDs, never insert or renumber.
- `MongoLoggingDefinitions` — extends EF's `LoggingDefinitions`; lazily allocates `EventDefinition`s via
  `NonCapturingLazyInitializer` (EF's own diagnostics convention).
- `MongoLoggerExtensions`, `MongoLoggerTransactionExtensions`, `MongoLoggerUpdateExtensions` — each method
  checks `ShouldLog(...)` → emits to `ILogger`, then `NeedsEventData(...)` → allocates an `EventData` and
  dispatches. Sensitive payloads (MQL with bound parameter values) require `ShouldLogSensitiveData()`;
  otherwise the sensitive part logs as `"?"`.
- `MongoQueryEventData`, `MongoBulkWriteEventData`, `MongoTransactionStartingEventData`, … — the typed payloads.

## Boundaries with adjacent areas

- **vs Storage.** `MongoDatabaseWrapper`/`MongoClientWrapper`/`MongoTransaction` are the callers; emission and
  event-data construction live here.
- **vs Query.** `MongoClientWrapper.Execute(...)` logs executed MQL via `ExecutedMqlQuery(...)`; the redaction
  for that path lives here.

## Common pitfalls

- **Event-ID stability is contract.** External `DiagnosticSource` subscribers observe these. Reordering,
  renumbering or removing a member is a breaking change.
- **Redaction is single-pointed.** MQL is only printed in full when `ShouldLogSensitiveData()` is true. Don't
  bypass the gate by formatting MQL into some other log path — that flag is the *only* user-visible control,
  and `security-reviewer` flags regressions.
- **Event-definition fields are lazily initialized.** Forgetting to declare a new event's `EventDefinition`
  field in `MongoLoggingDefinitions` makes the lazy-init lambda throw NRE the first time the event fires.
- **`EventData` must snapshot.** Don't capture mutable references (an `IUpdateEntry` whose state may change) —
  copy the values you need into the payload's properties.

## How to test

There is no `Diagnostics/` test folder — diagnostics are covered indirectly via `TestMqlLoggerFactory` (in
SpecificationTests and FunctionalTests `Utilities/`) and by features asserting on events (e.g.
`QueryableEncryptionTests` checks `EncryptedNullablePropertyEncountered` is raised). For a focused test, hook a
`TestLoggerFactory` into a `DbContextOptionsBuilder` and filter captured events by `MongoEventId.<name>`.
