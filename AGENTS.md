# AGENTS.md — MongoDB EF Core Provider

The MongoDB database provider for [Entity Framework Core](https://github.com/dotnet/efcore). Bridges EF Core's
change tracker, LINQ pipeline, and model-building API onto MongoDB documents via the official
[MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver).

## Tech stack & layout

- One project, `src/MongoDB.EntityFrameworkCore/`, packaged as `MongoDB.EntityFrameworkCore`.
- **Multi-EF-version targeting via build *configurations*, not target frameworks:** `Debug|Release EF8`,
  `EF9`, `EF10`. EF8/EF9 build `net8.0`; EF10 builds `net10.0`. The active version is selected by the
  `EF8`/`EF9`/`EF10` define constant — see the version-conditional `<PropertyGroup>`s in the `.csproj`.
- EF and driver versions are pinned in `Versions.props`. `DRIVER_VERSION` overrides the driver (CI
  forward-compat testing).
- `<Nullable>enable</Nullable>` on `src/`. `<NoWarn>EF1001</NoWarn>` — the provider intentionally consumes EF
  Core's internal APIs.
- xUnit; **plain `Assert.*`** (FluentAssertions is not referenced). Tests run **serially** —
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.

| Project | Purpose |
|---|---|
| `src/MongoDB.EntityFrameworkCore/` | The provider. |
| `tests/…UnitTests/` | Fast, no database. |
| `tests/…FunctionalTests/` | Integration against a real MongoDB — includes encryption, transactions, vector search, design-time, compatibility. |
| `tests/…SpecificationTests/` | EF Core's provider-conformance suite. |

## Editing

- **Preserve file BOMs.**
- `src/` is nullable-enabled — annotate new types accordingly.
- Conditional code uses the `EF8`/`EF9`/`EF10` symbols. Common guards: `#if EF8 || EF9` (legacy behavior),
  `#if !EF8` (EF9+). See `Storage/MongoTypeMappingSource.cs` and `Query/QueryingEnumerable.cs`.

## Commands

```bash
# Build / test one EF version (replace EF10 with EF8 or EF9)
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test  MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
dotnet test  MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"
```

For all three versions in parallel, invoke the `/test-all` skill.

## Testing

**Recommended: run with both `MONGODB_URI` and `ATLAS_URI` unset.** `TestServer` then has TestContainers boot a
`mongodb/mongodb-atlas-local` container, which (a) runs the Atlas-gated tests (vector search) **for real**
against Atlas Search, and (b) gives each `dotnet test` process its **own** container and uniquely-named
databases — so parallel runs and parallel agents don't collide. Cost: Docker required, plus a one-time ~2 GB
image pull.

Connection resolution (`FunctionalTests/Utilities/TestServer.cs`): the default server comes from `MONGODB_URI`
(else a container); the Atlas (`IsAtlas`) server from `ATLAS_URI` (else a container — so Atlas tests run
whenever `ATLAS_URI` isn't `"Disabled"`, regardless of `MONGODB_URI`). Point either var at an external server
to use it instead; note a plain `mongod`/replica set can't run Atlas Search.

Each test gets a unique database via `TestDatabaseNamer.GetUniqueDatabaseName()`. `[ModuleInitializer]` in
`FunctionalTests/ModuleInitialization.cs` registers BSON serializers at load.

| Feature area | Required env vars |
|---|---|
| CSFLE / Queryable Encryption | `CRYPT_SHARED_LIB_PATH` (unset ⇒ those tests skip silently) |
| MongoDB connection | `MONGODB_URI` or `ATLAS_URI` (otherwise Docker auto-spins) |
| Driver-version override | `DRIVER_VERSION` |

See `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` for the rest (baseline regeneration,
`MONGODB_EF_NATIVE_ONLY`, fixture patterns).

## Versioning & breaking changes

The major version tracks the EF Core major it supports, so **this project does not follow strict semver** —
breaking changes can land in minor releases. `BREAKING-CHANGES.md` is the running log.

Breaks are measured **against the latest released version of the assembly** (the most recent published NuGet
package), **not** against `main`. A public API added and then changed within the current unreleased cycle never
shipped, so it isn't a break.

Releases are tagged `v<major>.<minor>.<patch>` (optionally `-preview.N`); `v8.*`/`v9.*`/`v10.*` ship in
parallel. Find the baseline with the GitHub release list, **not** local `git tag` (clone tags are frequently
stale):

```bash
gh release list --limit 1 --json tagName,isLatest        # absolute latest
gh release list --limit 100 --json tagName               # highest non-preview v<major>.* = that line's baseline
git show <tag>:<path>                                    # diff the file at that tag against the working tree
```

**Is a break:** public API signature/default/visibility changes; annotation-key changes (the `Mongo:` prefix —
they affect stored compiled models and design-time output); behavior changes affecting persisted document shape
(element name, BSON representation, discriminator field, Guid representation); `IMongoClientWrapper` /
`IMongoDatabaseCreator` / `IMongoTransactionManager` interface changes (users are warned not to implement
these, but they're observable public surface); default-value changes for `AutoTransactionBehavior`, conventions,
or `BsonRepresentation` handling.

**Is not a break:**

- Anything `internal`, regardless of `InternalsVisibleTo` (which exists only for the test assemblies).
- The exception type thrown for an **unsupported** feature (a not-yet-implemented operator, an unsupported
  mapping, a guard rejecting something the provider doesn't support). Only the exception type of a *supported*
  operation is contract.
- **Which internal path a supported LINQ query takes** (native MQL translator vs. driver-LINQ) and **the exact
  MQL emitted**. In particular, the native translator being the **default** is not a break: results are
  unchanged, unsupported shapes fall back automatically, and `UseQueryMode(MongoQueryMode.DriverLinq)` restores
  the previous path.

## Async conventions

Follows EF Core's pattern, not the driver's: there is **no enforced sync/async pairing**. Async surfaces exist
where EF Core defines them (`*Async`) and where the underlying driver call is async. Library code uses
`ConfigureAwait(false)` consistently. `CancellationToken` flows through to driver calls without substitution;
new async methods must take one and pass it on.

## Commit & PR conventions

- The first commit message and the PR title start with a JIRA number: `EF-1234: Description`.
- The branch name usually matches: `EF-1234`.

## Functional areas

Each area has its own `AGENTS.md` (auto-loaded when working in that subtree) and a read-only reviewer
sub-agent. See `docs/agents-architecture.md` for the layout and how to add one.

| Area | Location | Reviewer |
|---|---|---|
| Query / LINQ translation | `src/…/Query/AGENTS.md` | `query-reviewer` |
| Storage, update pipeline & transactions | `src/…/Storage/AGENTS.md` | `storage-reviewer` |
| Metadata, attributes & conventions | `src/…/Metadata/AGENTS.md` | `metadata-reviewer` |
| Serialization & change tracking | `src/…/Serializers/AGENTS.md` | `serialization-reviewer` |
| Public API, DI & options | `src/…/Extensions/AGENTS.md` | `public-api-reviewer` |
| Diagnostics: events & logging | `src/…/Diagnostics/AGENTS.md` | `diagnostics-reviewer` |
| Value generation | `src/…/ValueGeneration/AGENTS.md` | `value-generation-reviewer` |
| Spec conformance & test infra | `tests/…SpecificationTests/AGENTS.md` | `spec-conformance-reviewer` |

**Feature reviewers** span several directories and are keyed by file globs rather than an `AGENTS.md`:
`vector-search-reviewer` (`VectorIndex*`, `BinaryVector*`, `VectorSearch*`) and `encryption-reviewer`
(`CryptProvider.cs`, `QueryableEncryption*`).

**Cross-cutting reviewers** apply one lens across the whole diff and run on **every** invocation of
`/review-ef-core-provider`: `api-stability-reviewer` (public API / breaking changes),
`ef-conformance-reviewer` (EF Core integration, multi-version compat, service registration),
`security-reviewer` (credential redaction, sensitive-data logging, KMS, TLS). With a PR number,
`pr-summary-reviewer` also runs ("what does this PR do, and is it a good change?").

## External references

- [EF Core source](https://github.com/dotnet/efcore) — authoritative for EF Core APIs, conventions, and the
  specification suite. [EF Core docs](https://learn.microsoft.com/en-us/ef/core/) for concepts.
- [MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver) — every BSON serializer, `IMongoClient`
  call and LINQ-v3 hook lives here. The boundary is `BsonSerializerFactory` + `IMongoClientWrapper`.
- [MongoDB EF provider docs](https://www.mongodb.com/docs/entity-framework/) · JIRA:
  <https://jira.mongodb.org/projects/EF/>
