---
area: Spec conformance & test infrastructure
scope: ["tests/MongoDB.EntityFrameworkCore.SpecificationTests/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs"]
reviewer-agent: spec-conformance-reviewer
adjacent-areas: [all areas — every test mirrors src/ structure]
---

# Spec conformance & test infra — AGENTS.md

This `AGENTS.md` covers the cross-cutting test infrastructure and the EF Core specification-tests project. `tests/MongoDB.EntityFrameworkCore.FunctionalTests/CLAUDE.md` re-points here for the shared fixtures (`Utilities/`, `Usings.cs`); per-area test folders under `FunctionalTests/` are owned by the corresponding `src/` area reviewer.

## Scope

In:

- **SpecificationTests project.** EF Core's standard provider-conformance suite. Test classes inherit upstream bases from `Microsoft.EntityFrameworkCore.Specification.Tests` (e.g. `NorthwindQueryFiltersQueryTestBase<TFixture>`) and override methods to assert the produced MQL via `AssertMql(...)`. The fixture pattern is `*MongoFixture<TModelCustomizer>` and the test class pattern is `*MongoTest`.
- **Functional test infrastructure** (`tests/.../FunctionalTests/Utilities/`) — `TestServer` (connection bootstrap from `ATLAS_URI` / `MONGODB_URI` / TestContainers fallback), `TemporaryDatabaseFixtureBase` / `TemporaryDatabaseFixture` (per-test database isolation), `TestDatabaseNamer` (unique names via `Interlocked.Increment`), `DatabaseCleaner` (a manual `[Fact(Skip = "...")]` opt-in cleaner — not automatic). `TestMqlLoggerFactory` lives in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/` (MQL capture by filtering `MongoEventId.ExecutedMqlQuery`). `ModuleInitialization` lives at the FunctionalTests project root (`tests/.../FunctionalTests/ModuleInitialization.cs`) and registers driver BSON serializers at module load via `[ModuleInitializer]`.
- **Parallelism / collection semantics** — `Usings.cs` declares `[assembly: CollectionBehavior(DisableTestParallelization = true)]` for FunctionalTests and SpecificationTests. Without this, shared MongoDB state and fixture/collection setup would race.

Out: per-area test classes themselves (`Query/`, `Storage/`, etc.) — those are owned by their respective `src/` area reviewers.

## Test infrastructure

- **Connection bootstrap** (`tests/.../FunctionalTests/Utilities/TestServer.cs`). The *default* server resolves from `MONGODB_URI` (else a `TestContainersTestServer`); the *Atlas* (`IsAtlas`) server resolves from `ATLAS_URI` (else a `TestContainersTestServer`). `TestContainersTestServer` boots **`mongodb/mongodb-atlas-local`** (Atlas-capable, with the Search Index Management service), not a plain `mongod` — so Atlas tests run for real against a container. `ATLAS_URI="Disabled"` skips Atlas tests; an external `ATLAS_URI`/`MONGODB_URI` uses that server instead. Cached once via double-checked locking, so each test *process* boots its own container(s) on a random host port. **Recommended: leave both `MONGODB_URI` and `ATLAS_URI` unset** — the whole run is then self-contained on atlas-local, Atlas tests run genuinely, and separate processes (parallel agents) are isolated.
- **Per-test isolation.** `TemporaryDatabaseFixtureBase.InitializeAsync()` requests a unique database name from `TestDatabaseNamer.GetUniqueDatabaseName()` (which appends an `Interlocked.Increment` counter to a timestamped prefix). `DisposeAsync` returns `Task.CompletedTask` — there is no automatic teardown; stale `Test*` databases are cleaned up only by manually running the `[Fact(Skip = "Manually run to clean up the database")]` `DatabaseCleaner.CleanDatabase` test. Test methods get a *collection name* derived from `[CallerMemberName]` (`CreateCollectionName(...)`) with a sequential-counter fallback for CI where caller names may be unavailable.
- **Fixture sharing.** Heavy fixtures are declared with `[CollectionDefinition("name")]` and consumed via `[XUnitCollection("name")]`. Encryption tests live in their own collection (`[XUnitCollection("Encryption")]`) so they serialize cleanly with the crypto state. Compatibility tests likewise.
- **MQL assertion pattern.** Specification tests override `await base.SomeTest()` and follow with `AssertMql("{ $match: ... }", "{ $project: ... }")` (see e.g. `NorthwindQueryFiltersQueryMongoTest`). The pipeline-string format is whitespace-insensitive but field-order-sensitive.

## Specification-tests anchor

- **Upstream package.** `Microsoft.EntityFrameworkCore.Specification.Tests` (matched to the EF version in `Versions.props`).
- **Inheritance pattern.** Fixtures inherit `*FixtureBase<TModelCustomizer>` from upstream; tests inherit `*TestBase<TFixture>`. Each Northwind variant uses a generic fixture (`NorthwindQueryMongoFixture<TModelCustomizer>`) parameterized by `TModelCustomizer` (`NoopModelCustomizer`, `NorthwindQueryFiltersCustomizer`, etc.).
- **EF-version-conditional fixture interfaces.** Upstream renamed `IModelCustomizer` → `ITestModelCustomizer` between EF8 and EF9+; fixtures `#if EF8` between the two. Seeding moved from `Seed()` to `SeedAsync()` in EF9+ — same `#if`.
- **What to override, what not to.** Override the test method and assert MQL; do *not* re-implement the upstream test body. If a test is permanently unsupported (e.g. Join), use `Skip` with a clear reason, not a silent return.

## EF multi-version targeting

- `.csproj` define constants per configuration: `EF8`, `EF9`, `EF10`. Common patterns: `#if EF8` (legacy seeding / customizer interface), `#if EF8 || EF9` (pre-EF10 type-mapping shapes — see `Storage/MongoTypeMappingSource.cs`), `#if !EF8` or `#if !EF8 && !EF9` (EF9+ / EF10+ only).
- The build produces six configurations: `{Debug,Release} {EF8,EF9,EF10}`. Target frameworks differ — EF8/EF9 build `net8.0`, EF10 builds `net10.0`.
- The `/test-all` skill (`.claude/skills/test-all/`) spawns three parallel sub-agents, one per EF version, to build and test in parallel.

## Test-area subfolder mirror

The test subfolder structure mirrors `src/`. When you touch an area, check the matching test subfolder:

| `src/` area | UnitTests folder | FunctionalTests folder | SpecificationTests folder |
|---|---|---|---|
| `Query/` | `Query/` | `Query/` | `Query/` |
| `Storage/` | `Storage/` | `Storage/`, `Update/` | — |
| `Metadata/` | `Metadata/` (incl. `Conventions/`, `BsonAttributes/`) | `Metadata/` (incl. `Conventions/`) | `Metadata/` |
| `Serializers/` | `Serializers/` | `Mapping/`, `Serialization/` | — |
| `ChangeTracking/` | `ChangeTracking/` | `Mapping/` (dictionary tracking) | — |
| `Extensions/` + `Infrastructure/` | `Extensions/`, `Infrastructure/` | `Design/` | `Extensions/` |
| `Diagnostics/` | (none — exercised via fixtures) | (via `TestMqlLoggerFactory`) | — |
| `ValueGeneration/` | `ValueGeneration/` | `ValueGeneration/` | `Metadata/Conventions/` (the generation convention) |

Special test concerns under `FunctionalTests/`: `Encryption/` (CSFLE / QE end-to-end, gated on `CRYPT_SHARED_LIB_PATH`), `Compatibility/` (stored-data round-trip across provider versions), `Design/` (`dotnet ef`-style compiled-model output under `Design/Generated/EF{8,9,10}/`).

## Common pitfalls

- **Don't enable test parallelization.** Tests share global MongoDB state and rely on serial execution for stable fixture/collection setup; parallel runs cross-pollute.
- **Don't drop test isolation.** A test that uses a fixed collection name (instead of `[CallerMemberName]`) collides with anything else using that name across the suite — the failure mode is intermittent leaks.
- **Encryption tests skip silently** when `CRYPT_SHARED_LIB_PATH` is unset (`SupportsEncryption` returns false). If you're verifying an encryption change, check that the variable is exported in your shell.
- **MQL assertions are field-order-sensitive.** When you add a new translator branch, expected-MQL strings often need updates across many specification tests.
- **EF-version `#if`s in tests are easy to miss.** A test passing on EF10 may be the only configuration anyone runs locally; CI runs all three, but `/test-all` is the local equivalent.
- **Compiled-model generated output** (`FunctionalTests/Design/Generated/EF{8,9,10}/`) is regenerated by the design-time tests. If you change a `Mongo:*` annotation, expect these to update; check that the version-specific output regenerates cleanly.

## How to test

**Run with both `MONGODB_URI` and `ATLAS_URI` unset** (Docker required): each run gets its own isolated
`mongodb/mongodb-atlas-local` container, so Atlas-gated tests run for real and parallel runs/agents don't
collide. Don't set `MONGODB_URI` to a plain local `mongod`/replica set unless you intend to — Atlas tests
won't be meaningful there.

```bash
# Run the full functional suite for one EF version (both env vars unset → isolated atlas-local container)
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build

# Run only the Northwind Where suite
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
```

For full multi-EF coverage, invoke the `/test-all` skill.

## Regenerating MQL baselines

`AssertMql(...)` baselines are generated, not hand-written. To (re)generate them, run the test(s)
with the `EF_TEST_REWRITE_BASELINES` environment variable set to `1` (or `TRUE`):

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj   -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
```

When an `AssertMql` assertion fails with the rewrite var set, `TestMqlLoggerFactory.AssertBaseline`
rewrites that override's `AssertMql(...)` **in place** from the actually-captured MQL (and also writes
a `QueryBaseline.txt` artifact). The test still reports as failed in this mode — that's the signal a
rewrite happened. Then **rebuild and re-run without the var** to confirm the test is genuinely green.

Important properties / caveats:

- **It is data-gated by construction.** `AssertMql(...)` is the *last* call in an override, after
  `await base.SomeTest(...)`. A test that fails its data/behavior assertion (or throws) never reaches
  `AssertBaseline`, so it is never rewritten. This means you can re-baseline a *passing-data* test safely
  without blessing a wrong result — but it also means the rewrite will happily record the MQL of a test
  whose only remaining failure is the baseline mismatch, including tests asserting a *partial* pipeline
  emitted before an expected throw.
- **Scope the run.** A whole-suite rewrite run will rewrite every test that reaches `AssertBaseline`. Use
  a tight `--filter` so you only touch the intended tests, then diff the result.
- **It truncates at 9 statements** (`Output truncated.`), and it can only rewrite when it can resolve the
  test's source file+line from the stack trace.
- **The auto-rewriter can corrupt some files / mis-place output** — always `git diff` the result and
  rebuild before trusting it; revert and hand-edit if a file looks wrong.

> Transitional note (EF-117 Include work): overrides tagged with a `// Failed:` comment mark tests whose
> behavior changed during the in-progress Include work and were not yet re-baselined; they are removed as
> each test is fixed and its baseline refreshed. `// Fails:` (no "d") is the separate, durable
> known-gap-with-ticket convention documented in `docs/failing-spec-tests.md`.

