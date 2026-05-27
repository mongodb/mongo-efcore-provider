---
area: Diagnostics — events & logging
scope: ["src/MongoDB.EntityFrameworkCore/Diagnostics/**"]
reviewer-agent: diagnostics-reviewer
adjacent-areas: [Storage, Query]
---

# Diagnostics — AGENTS.md

## Scope

EF Core's events / logging integration. Defines the provider's event IDs (`MongoEventId`), the lazy-initialized `EventDefinition`s under `MongoLoggingDefinitions`, the logger extension methods that emit events, and the `EventData` payloads dispatched to `DiagnosticSource` subscribers. Touchpoints are query execution, bulk writes (insert/update/delete), and transaction lifecycle.

In: every file under `src/MongoDB.EntityFrameworkCore/Diagnostics/`.

Out: the actual log call sites (those live in Storage and Query and call into the extension methods here); the configuration of sensitive-data logging (set on `DbContextOptionsBuilder.EnableSensitiveDataLogging()`, read via the logger's `ShouldLogSensitiveData()`).

## Key entry points

- `MongoEventId` — the event ID registry. Values are *versioned and immutable*; **new event IDs are appended**, never inserted, never renumbered. Categories are grouped by `DbLoggerCategory.*` (Database.Command, Database.Transaction, Update, Model.Validation, Query, Database).
- `MongoLoggingDefinitions` — extends EF's `LoggingDefinitions`; lazily allocates `EventDefinition` instances via `NonCapturingLazyInitializer` (the EF Core convention for diagnostics).
- `MongoLoggerExtensions`, `MongoLoggerTransactionExtensions`, `MongoLoggerUpdateExtensions` — the extension methods that:
  1. Check `ShouldLog(...)` → emit to `ILogger`.
  2. Check `NeedsEventData(...)` → allocate an `EventData` and dispatch to `DiagnosticSource` subscribers.
  Sensitive payloads (e.g. MQL with bound parameter values) require `ShouldLogSensitiveData()` to be true; otherwise the message logs `"?"` for the sensitive part.
- `MongoQueryEventData`, `MongoBulkWriteEventData`, `MongoTransactionStartingEventData`, etc. — the strongly-typed event payloads.

## Boundaries with adjacent areas

- **vs Storage.** `MongoDatabaseWrapper`, `MongoClientWrapper`, and `MongoTransaction` are the *callers* — they call the extension methods here. The actual log emission and event-data construction live here.
- **vs Query.** `MongoClientWrapper.Execute(...)` logs the executed MQL via `MongoLoggerExtensions.ExecutedMqlQuery(...)`; the redaction logic for that path lives here.

## Common pitfalls

- **Event-ID stability.** `MongoEventId` members are part of the contract observed by external `DiagnosticSource` subscribers and `EnableSensitiveDataLogging`-aware tooling. Reordering, renumbering, or removing one is a breaking change. Add new members at the end.
- **Redaction is single-pointed.** MQL queries are only printed in full when `ShouldLogSensitiveData()` returns true. Don't bypass that gate (e.g. by formatting MQL into a non-`ExecutedMqlQuery` log path) — `security-reviewer` will flag it, and the sensitive flag is the *only* user-visible control.
- **Event-definition fields are nullable and lazily initialized.** Forgetting to declare a new event's `EventDefinition` field in `MongoLoggingDefinitions` means the lazy-init lambda will throw NRE the first time the event fires.
- **`EventData` constructors take a snapshot.** Do not capture mutable references (e.g. an `IUpdateEntry` whose state may change); copy the values you need into the event-data type's properties.

## How to test

There is no dedicated `Diagnostics/` test folder — diagnostics are validated indirectly through the test infrastructure (`TestMqlLoggerFactory` in `tests/.../SpecificationTests/Utilities/` and `tests/.../FunctionalTests/Utilities/`) and through features that assert on events (e.g. `QueryableEncryptionTests` checks that `EncryptedNullablePropertyEncountered` is raised). To add a focused diagnostics test, hook a `TestLoggerFactory` into a `DbContextOptionsBuilder` and capture filtered events by `MongoEventId.<name>`.
