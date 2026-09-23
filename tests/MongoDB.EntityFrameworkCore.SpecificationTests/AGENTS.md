---
area: Spec conformance & test infrastructure
scope: ["tests/MongoDB.EntityFrameworkCore.SpecificationTests/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs"]
reviewer-agent: spec-conformance-reviewer
adjacent-areas: [all areas — every test mirrors src/ structure]
---

# Spec conformance & test infra — AGENTS.md

Cross-cutting test infrastructure and the EF Core specification-tests project. `FunctionalTests/CLAUDE.md`
re-points here for the shared fixtures; per-area test folders under `FunctionalTests/` belong to the matching
`src/` area reviewer.

## Scope

**In:**

- **SpecificationTests** — EF Core's provider-conformance suite. Classes inherit upstream bases and override
  methods to assert MQL via `AssertMql(...)`. Fixtures are `*MongoFixture<TModelCustomizer>`; tests are
  `*MongoTest`.
- **Functional test infrastructure** (`FunctionalTests/Utilities/`) — `TestServer` (connection bootstrap),
  `TemporaryDatabaseFixture(Base)` (per-test DB isolation), `TestDatabaseNamer`, `DatabaseCleaner` (manual
  `[Fact(Skip)]` opt-in, not automatic), `NativeModeAssert`. `TestMqlLoggerFactory` lives in
  `SpecificationTests/Utilities/`. `ModuleInitialization` registers driver BSON serializers at load.
- **Parallelism** — `Usings.cs` declares `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  for both projects.

**Out:** per-area test classes themselves (`Query/`, `Storage/`, …).

## Test infrastructure

- **Connection bootstrap** (`Utilities/TestServer.cs`). Default server: `MONGODB_URI`, else a
  `TestContainersTestServer`. Atlas (`IsAtlas`) server: `ATLAS_URI`, else likewise. Image is
  `mongodb/mongodb-atlas-local` (Atlas-capable, Search Index Management), so Atlas tests run for real. Cached
  with double-checked locking — each test *process* boots its own container on a random port.
  **Recommended: leave both vars unset** so the run is self-contained and separate processes stay isolated.
  `ATLAS_URI="Disabled"` skips Atlas tests. A plain `mongod` can't run Atlas Search.
- **Per-test isolation.** `TemporaryDatabaseFixtureBase.InitializeAsync()` gets a unique name from
  `TestDatabaseNamer.GetUniqueDatabaseName()`. `DisposeAsync` is a no-op — no automatic teardown; stale
  `Test*` databases are removed only by manually running `DatabaseCleaner.CleanDatabase`. Test methods get a
  collection name via `[CallerMemberName]`, with a counter fallback for CI.
- **Fixture sharing** — heavy fixtures use `[CollectionDefinition("name")]` + `[XUnitCollection("name")]`;
  encryption and compatibility tests each get their own collection to serialize cleanly.

| Variable | Effect |
|---|---|
| `MONGODB_URI` / `ATLAS_URI` | Point at an external server instead of a container. |
| `MONGODB_EF_NATIVE_ONLY=1` | Flips every spec context to `MongoQueryMode.NativeOnly`, so a would-be fallback throws instead. A full run is a "what actually goes native" report. |
| `CRYPT_SHARED_LIB_PATH` | Required for CSFLE/Queryable Encryption tests; unset ⇒ skip silently. |
| `EF_TEST_REWRITE_BASELINES=1` | Regenerates `AssertMql` baselines in place (see below). |
| `DRIVER_VERSION` | Overrides the C# driver version (CI forward-compat testing). |

## Specification-tests anchor

- **Upstream package** `Microsoft.EntityFrameworkCore.Specification.Tests`, matched to `Versions.props`.
- **Inheritance** — fixtures inherit `*FixtureBase<TModelCustomizer>`; tests inherit `*TestBase<TFixture>`.
- **EF-version-conditional fixtures** — upstream renamed `IModelCustomizer` → `ITestModelCustomizer` between
  EF8 and EF9+, and `Seed()` → `SeedAsync()`; fixtures `#if EF8` between them.
- **Override the test method and assert MQL; don't re-implement the upstream body.** Permanently unsupported
  → `Skip` with a clear reason, never a silent return.
- **Two comment conventions, different meanings.** `// Fails:` = durable known gap with a ticket. `// Failed:`
  = transitional marker (EF-117 Include work) for tests not yet re-baselined — removed as fixed. Don't add new
  `// Failed:`.

## Regenerating MQL baselines

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
```

When an `AssertMql` assertion fails with the var set, `TestMqlLoggerFactory.AssertBaseline` rewrites that
override in place from captured MQL. **The test still reports failed** — that's the signal a rewrite happened;
rebuild and rerun without the var to confirm green. Caveats:

- **Data-gated by construction** — `AssertMql(...)` is the last call, after `await base.SomeTest(...)`, so a
  data/behavior failure never reaches the rewriter. But it will happily record MQL for a test whose only
  remaining failure is the baseline mismatch, including a partial pipeline before an expected throw.
- **Scope the run** with a tight `--filter`, then diff — a whole-suite rewrite touches everything it reaches.
- Truncates at 9 statements; only works when it can resolve the test's source file+line from the stack trace.
- **Can corrupt files or mis-place output** — always `git diff` and rebuild before trusting it.

## EF multi-version targeting

Define constants: `EF8`, `EF9`, `EF10`. Common patterns: `#if EF8` (legacy seeding/customizer interface),
`#if EF8 || EF9`, `#if !EF8` / `#if !EF8 && !EF9`. EF8/EF9 target `net8.0`, EF10 targets `net10.0`. `/test-all`
builds and tests all three in parallel.

## Test-area subfolder mirror

Test folders mirror `src/`. When you touch an area, check the matching folder:

| `src/` area | UnitTests | FunctionalTests | SpecificationTests |
|---|---|---|---|
| `Query/` | `Query/` | `Query/` | `Query/` |
| `Storage/` | `Storage/` | `Storage/`, `Update/` | — |
| `Metadata/` | `Metadata/` (+`Conventions/`, `BsonAttributes/`) | `Metadata/` (+`Conventions/`) | `Metadata/` |
| `Serializers/` | `Serializers/` | `Mapping/`, `Serialization/` | — |
| `ChangeTracking/` | `ChangeTracking/` | `Mapping/` | — |
| `Extensions/` + `Infrastructure/` | `Extensions/`, `Infrastructure/` | `Design/` | `Extensions/` |
| `Diagnostics/` | — (via fixtures) | — (via `TestMqlLoggerFactory`) | — |
| `ValueGeneration/` | `ValueGeneration/` | `ValueGeneration/` | `Metadata/Conventions/` |

Special concerns under `FunctionalTests/`: `Encryption/` (gated on `CRYPT_SHARED_LIB_PATH`), `Compatibility/`
(stored-data round-trip across provider versions), `Design/` (compiled-model output under
`Design/Generated/EF{8,9,10}/`).

## Common pitfalls

- **Don't enable test parallelization** — tests share global MongoDB state.
- **Don't drop per-test isolation** — a fixed collection name collides with anything else using it
  (intermittent leaks).
- **MQL assertions are field-order-sensitive** — a new translator branch often needs baselines updated across
  many spec tests.
- **MQL shape does not prove a query went native** — see `Query/AGENTS.md`. Use `NativeOnly`.
- **Unit tests use plain xUnit `Assert.*`** — no FluentAssertions.
- **Encryption tests skip silently** when `CRYPT_SHARED_LIB_PATH` is unset.
- **EF-version `#if`s in tests are easy to miss locally** — CI runs all three; `/test-all` is the local
  equivalent.
- **Compiled-model generated output** regenerates from design-time tests; check `Design/Generated/EF{8,9,10}/`
  after a `Mongo:*` annotation change.

## How to test

Run with `MONGODB_URI` and `ATLAS_URI` unset (Docker required) for an isolated container per run.

```bash
# Full functional suite for one EF version
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build

# One spec suite
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
```
