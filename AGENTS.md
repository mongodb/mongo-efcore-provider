# AGENTS.md — MongoDB EF Core Provider

## Overview
The MongoDB database provider for [Entity Framework Core](https://github.com/dotnet/efcore). Bridges EF Core's change tracker, LINQ pipeline, and model-building API onto MongoDB documents via the official [MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver).

## Tech Stack
- Single project (`src/MongoDB.EntityFrameworkCore/`) packaged as `MongoDB.EntityFrameworkCore` on NuGet.
- Multi-EF-version targeting via build *configurations* (not target frameworks): `Debug|Release EF8`, `Debug|Release EF9`, `Debug|Release EF10`. EF8/EF9 build for `net8.0`; EF10 builds for `net10.0`. The active EF version is selected by the `EF8` / `EF9` / `EF10` define constant — see the version-conditional `<PropertyGroup>`s in `src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj`.
- EF and driver versions are pinned in `Versions.props`. The C# driver version can be overridden by setting the `DRIVER_VERSION` env var (CI uses this to test forward-compat).
- `<Nullable>enable</Nullable>` on `src/`. `<NoWarn>EF1001</NoWarn>` — the provider intentionally consumes EF Core's internal APIs.
- xUnit + FluentAssertions for tests. Tests run **serially** — see `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs` (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`).

## Project Structure
- `src/MongoDB.EntityFrameworkCore/` — the provider.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/` — fast unit tests; no database connection required.
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/` — integration tests that hit a real MongoDB; cover end-to-end behavior including encryption, transactions, vector search, design-time, and compatibility.
- `tests/MongoDB.EntityFrameworkCore.SpecificationTests/` — EF Core's standard provider-conformance suite (inherits test bases from `Microsoft.EntityFrameworkCore.Specification.Tests`).

## Editing
- Preserve file BOMs.
- All `src/` code obeys `<Nullable>enable</Nullable>` — annotate new types accordingly.
- Conditional code across EF versions uses the `EF8` / `EF9` / `EF10` symbols. Common guards: `#if EF8 || EF9` (legacy-EF behavior) and `#if !EF8` (EF9+ features) — see `Storage/MongoTypeMappingSource.cs` and `Query/QueryingEnumerable.cs` for representative examples.

## Versioning conventions

This project does **not** follow strict semantic versioning — the major version tracks the EF Core major version it supports, so breaking changes can land in minor releases. See `BREAKING-CHANGES.md` for the running log. What counts as a break:

- Public API signature, default, or visibility changes.
- Annotation key changes (under the `Mongo:` prefix — see `Metadata/MongoAnnotationNames.cs`) — these affect stored compiled models and design-time output.
- Behavior changes affecting persisted document shape (element name, BSON representation, discriminator field, Guid representation, etc.).
- `IMongoClientWrapper` interface changes (users are warned not to implement this themselves; it exists for DI).
- Default-value changes for `AutoTransactionBehavior`, conventions, or `BsonRepresentation` handling.

## Async conventions
The provider follows EF Core's pattern, not the C# driver's. There is **no enforced sync/async pairing** of public methods — async surfaces exist where EF Core defines them (`*Async` variants) and where the underlying driver call is async. Library code uses `ConfigureAwait(false)` consistently. `CancellationToken` flows from EF Core through to driver calls without substitution; new async methods must take a `CancellationToken` and pass it on.

## Commands
- Build a single EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (replace `EF10` with `EF8` or `EF9`).
- Run all tests for one EF version: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`.
- Run one test class: `dotnet test ... --filter "FullyQualifiedName~ClassName"`.
- Build + test all three EF versions in parallel: invoke the `/test-all` skill (under `.claude/skills/test-all/`).

## Testing
- Functional and SpecificationTests need MongoDB. `tests/.../FunctionalTests/Utilities/TestServer.cs` checks env vars in priority order: `ATLAS_URI` (set to `"Disabled"` to skip Atlas tests), then `MONGODB_URI`. If neither is set, a `TestContainersTestServer` boots a local MongoDB via Docker.
- Tests run **serially** (parallelization disabled at the assembly level) — required because fixtures share global MongoDB state and each test relies on a uniquely-named database / collection that would race under parallel execution.
- Each test gets a unique database name via `TestDatabaseNamer.GetUniqueDatabaseName()`.
- `[ModuleInitializer]` in `tests/.../FunctionalTests/ModuleInitialization.cs` registers BSON serializers at load time.

| Feature area | Required environment variables |
|---|---|
| CSFLE / Queryable Encryption | `CRYPT_SHARED_LIB_PATH` |
| MongoDB connection (otherwise Docker auto-spins) | `MONGODB_URI` or `ATLAS_URI` |
| Driver-version override for CI compat testing | `DRIVER_VERSION` |

If `CRYPT_SHARED_LIB_PATH` is unset, the encryption test collection's `SupportsEncryption` returns false and the relevant tests are skipped.

## Commit and PR conventions
- The first commit message and the PR title start with a JIRA number: `EF-1234: Description`.
- The branch name usually matches the JIRA number: `EF-1234`.

## Functional areas

Each area has its own `AGENTS.md` (auto-loaded by Claude Code when working in that subtree) and a corresponding read-only reviewer sub-agent in `.claude/agents/`. See `docs/agents-architecture.md` for the layout and how to add a new area.

| Area | Location | Reviewer |
|---|---|---|
| Query / LINQ translation | `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | `query-reviewer` |
| Storage, update pipeline & transactions | `src/MongoDB.EntityFrameworkCore/Storage/AGENTS.md` | `storage-reviewer` |
| Metadata, attributes & conventions | `src/MongoDB.EntityFrameworkCore/Metadata/AGENTS.md` | `metadata-reviewer` |
| Serialization & change tracking | `src/MongoDB.EntityFrameworkCore/Serializers/AGENTS.md` (+ `src/MongoDB.EntityFrameworkCore/ChangeTracking/CLAUDE.md` cross-ref) | `serialization-reviewer` |
| Public API, DI & options | `src/MongoDB.EntityFrameworkCore/Extensions/AGENTS.md` (+ `src/MongoDB.EntityFrameworkCore/Infrastructure/CLAUDE.md` and `src/MongoDB.EntityFrameworkCore/Design/CLAUDE.md` cross-refs) | `public-api-reviewer` |
| Diagnostics: events & logging | `src/MongoDB.EntityFrameworkCore/Diagnostics/AGENTS.md` | `diagnostics-reviewer` |
| Value generation | `src/MongoDB.EntityFrameworkCore/ValueGeneration/AGENTS.md` | `value-generation-reviewer` |
| Spec conformance & test infra | `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` (+ `tests/MongoDB.EntityFrameworkCore.FunctionalTests/CLAUDE.md` cross-ref) | `spec-conformance-reviewer` |

## Feature reviewers

These reviewers cover cohesive features that span more than one directory. They have no dedicated `AGENTS.md` — they're keyed by file-pattern globs in their `description` and in the `/review-ef-core-provider` mapping table.

| Feature | Reviewer | Spans |
|---|---|---|
| Atlas Vector Search | `vector-search-reviewer` | `VectorIndex*`, `BinaryVector*`, `VectorSearch*` across Query, Metadata, Storage, Extensions |
| Client-Side Field Level Encryption / Queryable Encryption | `encryption-reviewer` | `CryptProvider.cs`, `QueryableEncryption*` across Infrastructure, Extensions, Metadata, Storage |

## Cross-cutting reviewers

These reviewers have no per-area `AGENTS.md`; they apply a single lens across the whole diff and run on **every** invocation of `/review-ef-core-provider`.

| Concern | Reviewer |
|---|---|
| Public API / breaking changes (annotation keys, signatures, behavior) | `api-stability-reviewer` |
| EF Core integration correctness — multi-version compat (EF8/EF9/EF10), service registration, conventions hygiene | `ef-conformance-reviewer` |
| Security — credential redaction, sensitive-data logging, KMS plumbing, TLS surfaces | `security-reviewer` |

## PR-summary reviewer (external PR mode only)

When `/review-ef-core-provider` is invoked with a PR number, one additional reviewer runs:

| Concern | Reviewer |
|---|---|
| Holistic "what does this PR do, and is it a good change?" — reads the PR body and the full diff | `pr-summary-reviewer` |

## External references

- [EF Core source](https://github.com/dotnet/efcore) — authoritative for EF Core APIs, conventions, and the test specification suite.
- [EF Core docs](https://learn.microsoft.com/en-us/ef/core/) — authoritative for EF Core concepts (model building, change tracking, query pipeline, etc.).
- [MongoDB C# Driver](https://github.com/mongodb/mongo-csharp-driver) — every BSON serializer, IMongoClient call, and LINQ-v3 hook the provider uses lives here. The provider has its own `AGENTS.md` partitioning over there; the boundary is `BsonSerializerFactory` + `IMongoClientWrapper`.
- [MongoDB EF Provider docs](https://www.mongodb.com/docs/entity-framework/).
- JIRA: <https://jira.mongodb.org/projects/EF/>.
