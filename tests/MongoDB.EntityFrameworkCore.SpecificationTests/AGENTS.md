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
- **Functional test infrastructure** (`tests/.../FunctionalTests/Utilities/`) — `TestServer` (connection bootstrap from `ATLAS_URI` / `MONGODB_URI` / TestContainers fallback), `TemporaryDatabaseFixtureBase` / `TemporaryDatabaseFixture` (per-test database isolation), `TestDatabaseNamer` (unique names; prefix-based cleanup), `TestMqlLoggerFactory` (MQL capture by filtering `MongoEventId.ExecutedMqlQuery`), `ModuleInitialization` (registers driver BSON serializers at module load via `[ModuleInitializer]`).
- **Parallelism / collection semantics** — `Usings.cs` declares `[assembly: CollectionBehavior(DisableTestParallelization = true)]` for FunctionalTests and SpecificationTests. Without this, fixture cleanup-by-prefix and shared MongoDB state would race.

Out: per-area test classes themselves (`Query/`, `Storage/`, etc.) — those are owned by their respective `src/` area reviewers.

## Test infrastructure

- **Connection bootstrap** (`tests/.../FunctionalTests/Utilities/TestServer.cs`). Priority: `ATLAS_URI` (`"Disabled"` skips Atlas tests) → `MONGODB_URI` → `TestContainersTestServer` (Docker-backed local Mongo). The cached server is initialized once via double-checked locking.
- **Per-test isolation.** `TemporaryDatabaseFixtureBase.InitializeAsync()` requests a unique database name from `TestDatabaseNamer`; cleanup drops everything with the test-database prefix. Test methods get a *collection name* derived from `[CallerMemberName]` (`CreateCollectionName(...)`) with a sequential-counter fallback for CI where caller names may be unavailable.
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

- **Don't enable test parallelization.** Fixtures rely on prefix-based cleanup; parallel runs cross-pollute databases.
- **Don't drop test isolation.** A test that uses a fixed collection name (instead of `[CallerMemberName]`) collides with anything else using that name across the suite — the failure mode is intermittent leaks.
- **Encryption tests skip silently** when `CRYPT_SHARED_LIB_PATH` is unset (`SupportsEncryption` returns false). If you're verifying an encryption change, check that the variable is exported in your shell.
- **MQL assertions are field-order-sensitive.** When you add a new translator branch, expected-MQL strings often need updates across many specification tests.
- **EF-version `#if`s in tests are easy to miss.** A test passing on EF10 may be the only configuration anyone runs locally; CI runs all three, but `/test-all` is the local equivalent.
- **Compiled-model generated output** (`FunctionalTests/Design/Generated/EF{8,9,10}/`) is regenerated by the design-time tests. If you change a `Mongo:*` annotation, expect these to update; check that the version-specific output regenerates cleanly.

## How to test

```bash
# Run the full functional suite for one EF version
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build

# Run only the Northwind Where suite
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
```

For full multi-EF coverage, invoke the `/test-all` skill.
