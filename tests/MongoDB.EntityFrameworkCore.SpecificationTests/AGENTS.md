---
area: Spec conformance & test infrastructure
scope: ["tests/MongoDB.EntityFrameworkCore.SpecificationTests/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/**", "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs"]
reviewer-agent: spec-conformance-reviewer
adjacent-areas: [all areas — every test mirrors src/ structure]
---

# Spec conformance & test infra — AGENTS.md

EF Core's provider-conformance suite plus shared functional-test infrastructure (`FunctionalTests/Utilities/`:
`TestServer`, `TemporaryDatabaseFixture(Base)`, `TestDatabaseNamer`, `DatabaseCleaner`, `NativeModeAssert`;
`TestMqlLoggerFactory` is in `SpecificationTests/Utilities/`). Per-area test folders belong to the matching `src/`
area. Connection and encryption env vars: see root `AGENTS.md`.

## Conventions

- **Specification tests** inherit upstream `*TestBase<TFixture>`/`*FixtureBase<TModelCustomizer>` (package
  `Microsoft.EntityFrameworkCore.Specification.Tests`, matched to `Versions.props`); fixtures are
  `*MongoFixture<T>`, tests `*MongoTest`. Override the test method and assert MQL with `AssertMql(...)`; don't
  re-implement the upstream body. Permanently unsupported → `Skip` with a reason, never a silent return.
- **Known-failing spec tests** carry `// Fails:` (durable gap with a ticket). `// Failed:` is a legacy transitional
  marker; don't add new ones.
- **EF-version `#if`s**: upstream renamed `IModelCustomizer` → `ITestModelCustomizer` and `Seed()` → `SeedAsync()`
  between EF8 and EF9+. CI runs all three versions; use `/test-all` locally.
- **Isolation**: `TemporaryDatabaseFixtureBase.InitializeAsync()` takes a unique name from
  `TestDatabaseNamer.GetUniqueDatabaseName()`; collection names come from `[CallerMemberName]`. Never use fixed
  names. `DisposeAsync` is a no-op; stale `Test*` databases are removed only by manually running
  `DatabaseCleaner.CleanDatabase`.
- **Never enable test parallelization** (`Usings.cs` disables it; tests share global MongoDB state). Heavy fixtures
  use `[CollectionDefinition]` + `[XUnitCollection]`; encryption and compatibility tests get their own collections.
- The container server is cached per test process (random port). `ATLAS_URI="Disabled"` skips Atlas tests.
- `MONGODB_EF_NATIVE_ONLY=1` flips every spec context to `MongoQueryMode.NativeOnly`, so any fallback throws.
  MQL shape alone does not prove a query went native.
- MQL assertions are field-order-sensitive; a new translator branch often needs baselines updated across many tests.
- After a `Mongo:*` annotation change, regenerate design-time compiled-model output under
  `FunctionalTests/Design/Generated/EF{8,9,10}/`. `Encryption/` is gated on `CRYPT_SHARED_LIB_PATH`; `Compatibility/`
  round-trips stored data across provider versions.

## Regenerating MQL baselines

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
```

When an `AssertMql` fails with the var set, `TestMqlLoggerFactory.AssertBaseline` rewrites that override in place
from captured MQL. **The test still reports failed** (the signal a rewrite happened); rebuild and rerun without the
var to confirm green.

- `AssertMql` runs after `await base.Test(...)`, so data/behavior failures never reach the rewriter — but it will
  record MQL for a test whose only remaining failure is the baseline, including a partial pipeline before an
  expected throw.
- Always scope with a tight `--filter`, then `git diff` and rebuild before trusting it: it can corrupt files or
  mis-place output, truncates at 9 statements, and needs to resolve source file+line from the stack trace.

## Test folder mirror

Test folders mirror `src/` (`UnitTests/`, `FunctionalTests/`, `SpecificationTests/` each have `Query/`, `Storage/`,
`Metadata/`, `Serializers/`, ... as applicable). Exceptions: `Storage/` functional tests also use `Update/`;
`Serializers/` and `ChangeTracking/` functional tests use `Mapping/`/`Serialization/`; `Extensions/` +
`Infrastructure/` functional tests use `Design/`; `ValueGeneration/` convention coverage is in
`SpecificationTests/Metadata/Conventions/`.

## Running

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
```
