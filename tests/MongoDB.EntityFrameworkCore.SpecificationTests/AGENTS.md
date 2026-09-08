---
area: Spec conformance & test infrastructure
scope: ["tests/MongoDB.EntityFrameworkCore.SpecificationTests/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs"]
reviewer-agent: spec-conformance-reviewer
adjacent-areas: [all areas — every test mirrors src/ structure]
---

# Spec conformance & test infra — AGENTS.md

Covers the cross-cutting test infrastructure and the EF Core specification-tests project.
`FunctionalTests/CLAUDE.md` re-points here for the shared fixtures (`Utilities/`, `Usings.cs`); per-area test
folders under `FunctionalTests/` belong to the matching `src/` area reviewer.

## Scope

**In:**

- **SpecificationTests** — EF Core's provider-conformance suite. Classes inherit upstream bases (e.g.
  `NorthwindQueryFiltersQueryTestBase<TFixture>`) and override methods to assert the produced MQL via
  `AssertMql(...)`. Fixtures are `*MongoFixture<TModelCustomizer>`; test classes are `*MongoTest`.
- **Functional test infrastructure** (`FunctionalTests/Utilities/`) — `TestServer` (connection bootstrap),
  `TemporaryDatabaseFixture(Base)` (per-test database isolation), `TestDatabaseNamer`, `DatabaseCleaner` (a
  manual `[Fact(Skip)]` opt-in, **not** automatic), `NativeModeAssert` (shared native/parity assertions).
  `TestMqlLoggerFactory` lives in `SpecificationTests/Utilities/`. `ModuleInitialization` (FunctionalTests
  root) registers driver BSON serializers at load via `[ModuleInitializer]`.
- **Parallelism** — `Usings.cs` declares `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  for both projects.

**Out:** the per-area test classes themselves (`Query/`, `Storage/`, …).

## Test infrastructure

- **Connection bootstrap** (`Utilities/TestServer.cs`). The *default* server resolves from `MONGODB_URI`, else
  a `TestContainersTestServer`; the *Atlas* (`IsAtlas`) server resolves from `ATLAS_URI`, else likewise. The
  image is **`mongodb/mongodb-atlas-local`** (Atlas-capable, with Search Index Management), not a plain
  `mongod` — so Atlas tests run for real. Cached with double-checked locking, so each test *process* boots its
  own container on a random port.
  **Recommended: leave both vars unset** — the run is self-contained, Atlas tests run genuinely, and separate
  processes (parallel agents) stay isolated. `ATLAS_URI="Disabled"` skips Atlas tests. A plain `mongod` can't
  run Atlas Search, so Atlas tests against one aren't meaningful.
- **Per-test isolation.** `TemporaryDatabaseFixtureBase.InitializeAsync()` takes a unique name from
  `TestDatabaseNamer.GetUniqueDatabaseName()`. `DisposeAsync` is a no-op — there is **no** automatic teardown;
  stale `Test*` databases are removed only by manually running `DatabaseCleaner.CleanDatabase`. Test methods
  get a collection name from `[CallerMemberName]` via `CreateCollectionName(...)`, with a counter fallback for
  CI where caller names may be unavailable.
- **Fixture sharing.** Heavy fixtures use `[CollectionDefinition("name")]` + `[XUnitCollection("name")]`.
  Encryption and compatibility tests each get their own collection so they serialize cleanly.

| Variable | Effect |
|---|---|
| `MONGODB_URI` / `ATLAS_URI` | Point at an external server instead of a container (see above). |
| `MONGODB_EF_NATIVE_ONLY=1` | Flips every spec context to `MongoQueryMode.NativeOnly` (`MongoTestStore.AddProviderOptions`), so a query that would silently fall back to driver-LINQ throws instead. A full run with this set is a "what actually goes native" report. |
| `CRYPT_SHARED_LIB_PATH` | Required for CSFLE / Queryable Encryption tests; unset ⇒ they skip silently. |
| `EF_TEST_REWRITE_BASELINES=1` | Regenerates `AssertMql` baselines in place (see below). |
| `DRIVER_VERSION` | Overrides the C# driver version (CI forward-compat testing). |

## Specification-tests anchor

- **Upstream package** `Microsoft.EntityFrameworkCore.Specification.Tests`, matched to `Versions.props`.
- **Inheritance** — fixtures inherit `*FixtureBase<TModelCustomizer>`; tests inherit `*TestBase<TFixture>`.
  Each Northwind variant parameterizes a generic fixture (`NorthwindQueryMongoFixture<TModelCustomizer>`) by
  customizer (`NoopModelCustomizer`, `NorthwindQueryFiltersCustomizer`, …).
- **EF-version-conditional fixtures** — upstream renamed `IModelCustomizer` → `ITestModelCustomizer` between
  EF8 and EF9+, and seeding moved `Seed()` → `SeedAsync()`; fixtures `#if EF8` between them.
- **Override the test method and assert MQL; don't re-implement the upstream body.** For a permanently
  unsupported feature use `Skip` with a clear reason, never a silent return.
- **Two comment conventions mark non-passing overrides and mean different things.** `// Fails:` is the durable
  "known gap, with a ticket" marker (~900 uses). `// Failed:` is a *transitional* marker from the EF-117
  Include work, for tests whose behavior changed and weren't yet re-baselined (~30 left), removed as each is
  fixed. Don't conflate them, and don't add new `// Failed:`.

## Regenerating MQL baselines

`AssertMql(...)` baselines are generated, not hand-written:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
```

When an `AssertMql` assertion fails with the var set, `TestMqlLoggerFactory.AssertBaseline` rewrites that
override **in place** from the captured MQL (and writes a `QueryBaseline.txt`). **The test still reports as
failed in this mode** — that is the signal a rewrite happened. Rebuild and re-run *without* the var to confirm
it is genuinely green. Caveats:

- **Data-gated by construction.** `AssertMql(...)` is the last call in an override, after
  `await base.SomeTest(...)`, so a test failing its data/behavior assertion never reaches the rewriter. You can
  therefore re-baseline a passing-data test safely — but it *will* happily record the MQL of a test whose only
  remaining failure is the baseline mismatch, including one asserting a partial pipeline before an expected
  throw.
- **Scope the run** with a tight `--filter`, then diff — a whole-suite rewrite rewrites everything it reaches.
- Truncates at 9 statements (`Output truncated.`), and only works when it can resolve the test's source
  file+line from the stack trace.
- **The rewriter can corrupt files or mis-place output** — always `git diff` and rebuild before trusting it;
  revert and hand-edit if a file looks wrong.

## EF multi-version targeting

Define constants per configuration: `EF8`, `EF9`, `EF10`. Common patterns: `#if EF8` (legacy seeding /
customizer interface), `#if EF8 || EF9` (pre-EF10 type-mapping shapes), `#if !EF8` / `#if !EF8 && !EF9`. Six
configurations build in total; EF8/EF9 target `net8.0`, EF10 targets `net10.0`. The `/test-all` skill builds
and tests all three in parallel.

## Test-area subfolder mirror

The test folders mirror `src/`. When you touch an area, check the matching folder:

| `src/` area | UnitTests | FunctionalTests | SpecificationTests |
|---|---|---|---|
| `Query/` | `Query/` | `Query/` | `Query/` |
| `Storage/` | `Storage/` | `Storage/`, `Update/` | — |
| `Metadata/` | `Metadata/` (+ `Conventions/`, `BsonAttributes/`) | `Metadata/` (+ `Conventions/`) | `Metadata/` |
| `Serializers/` | `Serializers/` | `Mapping/`, `Serialization/` | — |
| `ChangeTracking/` | `ChangeTracking/` | `Mapping/` | — |
| `Extensions/` + `Infrastructure/` | `Extensions/`, `Infrastructure/` | `Design/` | `Extensions/` |
| `Diagnostics/` | — (via fixtures) | — (via `TestMqlLoggerFactory`) | — |
| `ValueGeneration/` | `ValueGeneration/` | `ValueGeneration/` | `Metadata/Conventions/` |

Special concerns under `FunctionalTests/`: `Encryption/` (gated on `CRYPT_SHARED_LIB_PATH`), `Compatibility/`
(stored-data round-trip across provider versions), `Design/` (compiled-model output under
`Design/Generated/EF{8,9,10}/`).

## Common pitfalls

- **Don't enable test parallelization.** Tests share global MongoDB state and rely on serial execution.
- **Don't drop per-test isolation.** A fixed collection name (instead of `[CallerMemberName]`) collides with
  anything else using it; the failure mode is intermittent leaks.
- **MQL assertions are field-order-sensitive.** A new translator branch often needs baselines updated across
  many spec tests.
- **MQL shape does not prove a query went native** — see `Query/AGENTS.md`. Use `NativeOnly`.
- **Unit tests use plain xUnit `Assert.*`** — FluentAssertions is not referenced by the test projects.
- **Encryption tests skip silently** when `CRYPT_SHARED_LIB_PATH` is unset. If you're verifying an encryption
  change, check the variable is exported.
- **EF-version `#if`s in tests are easy to miss.** A test passing on EF10 may be the only config you run
  locally; CI runs all three, and `/test-all` is the local equivalent.
- **Compiled-model generated output** regenerates from the design-time tests. Changing a `Mongo:*` annotation
  updates `Design/Generated/EF{8,9,10}/`; check it regenerates cleanly.

## How to test

Run with `MONGODB_URI` and `ATLAS_URI` unset (Docker required) so each run gets an isolated container.

```bash
# Full functional suite for one EF version
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build

# One spec suite
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
```
