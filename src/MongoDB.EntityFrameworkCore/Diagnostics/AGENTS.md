---
area: Diagnostics — events & logging
scope: ["src/MongoDB.EntityFrameworkCore/Diagnostics/**"]
reviewer-agent: diagnostics-reviewer
adjacent-areas: [Storage, Query]
---

# Diagnostics — AGENTS.md

Event IDs, `EventDefinition`s, logger extension methods and `EventData` payloads. Log call sites live in
Storage/Query; the sensitive-data setting is EF's.

## Invariants

- **Event IDs (`MongoEventId`) are contract** — external `DiagnosticSource` subscribers observe them. Append,
  never renumber.
- **Redaction is single-pointed on `ShouldLogSensitiveData()`** (sensitive payloads log as `"?"` otherwise).
  Don't add a second path around it.
- **A new event needs a declared `EventDefinition` field in `MongoLoggingDefinitions`**, or the lazy init throws
  NRE on first fire.
- **`EventData` must snapshot values**, not hold mutable references (e.g. an `IUpdateEntry`).
- Logger extension methods follow: `ShouldLog(...)` → log; `NeedsEventData(...)` → build and dispatch.

## Testing

No `Diagnostics/` test folder. Assert events via `TestMqlLoggerFactory` or a `TestLoggerFactory` filtered by
`MongoEventId.<name>`.
