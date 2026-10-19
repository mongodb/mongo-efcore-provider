---
area: Diagnostics — events & logging
scope: ["src/MongoDB.EntityFrameworkCore/Diagnostics/**"]
reviewer-agent: diagnostics-reviewer
adjacent-areas: [Storage, Query]
---

# Diagnostics — AGENTS.md

## Scope

EF Core's events/logging integration: event IDs, `EventDefinition`s, logger extension methods, and the
`EventData` payloads dispatched to `DiagnosticSource`. Touchpoints: query execution, bulk writes, transaction
lifecycle.

**Out:** log call sites (Storage/Query call into here); the sensitive-data setting itself
(`EnableSensitiveDataLogging()` / `ShouldLogSensitiveData()`).

## Key entry points

- `MongoEventId` — event-ID registry by `DbLoggerCategory.*`. **Versioned and immutable**: append, never
  renumber.
- `MongoLoggingDefinitions` — extends EF's `LoggingDefinitions`; lazily allocates `EventDefinition`s via
  `NonCapturingLazyInitializer`.
- `MongoLoggerExtensions`/`…TransactionExtensions`/`…UpdateExtensions` — each method: `ShouldLog(...)` → log,
  `NeedsEventData(...)` → build `EventData` and dispatch. Sensitive payloads require
  `ShouldLogSensitiveData()`, else log as `"?"`.
- `MongoQueryEventData`, `MongoBulkWriteEventData`, `MongoTransactionStartingEventData`, … — typed payloads.

## Common pitfalls

- **Event-ID stability is contract** — external `DiagnosticSource` subscribers observe these values.
- **Redaction is single-pointed** on `ShouldLogSensitiveData()`; don't add a second path around it
  (`security-reviewer` flags this).
- **New events need a declared `EventDefinition` field** in `MongoLoggingDefinitions`, or the lazy-init throws
  NRE on first fire.
- **`EventData` must snapshot values**, not mutable references (e.g. an `IUpdateEntry`).

## How to test

No `Diagnostics/` test folder — covered via `TestMqlLoggerFactory` and feature tests asserting events (e.g.
`QueryableEncryptionTests`). For a focused check, hook a `TestLoggerFactory` into `DbContextOptionsBuilder` and
filter by `MongoEventId.<name>`.
