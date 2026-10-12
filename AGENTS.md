# AGENTS.md — MongoDB EF Core Provider

The MongoDB database provider for [Entity Framework Core](https://github.com/dotnet/efcore), built on the official
[MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver).

## Tech stack & layout

- **Multi-EF-version targeting via build *configurations*, not target frameworks:** `Debug|Release EF8`, `EF9`,
  `EF10` (EF8/EF9 build `net8.0`, EF10 `net10.0`), selected by the `EF8`/`EF9`/`EF10` define constant (see the
  `.csproj`). EF and driver versions are pinned in `Versions.props`; `DRIVER_VERSION` overrides the driver.
- `src/` is `<Nullable>enable</Nullable>`. `EF1001` is suppressed: the provider intentionally uses EF internals.
- xUnit with **plain `Assert.*`** (no FluentAssertions). Tests run **serially**
  (`DisableTestParallelization = true`).
- `tests/…UnitTests` (no database), `…FunctionalTests` (real MongoDB), `…SpecificationTests` (EF Core's conformance suite).

## Editing

- **Preserve file BOMs.** Annotate new `src/` types for nullability.
- Version-conditional code uses `#if EF8 || EF9` (legacy) and `#if !EF8` (EF9+); see
  `Storage/MongoTypeMappingSource.cs`, `Query/QueryingEnumerable.cs`.

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
`mongodb/mongodb-atlas-local` container per `dotnet test` process, so Atlas-gated tests (vector search) really run
and parallel runs/agents don't collide. Needs Docker and a one-time ~2 GB image pull.

Connection resolution (`FunctionalTests/Utilities/TestServer.cs`): default server from `MONGODB_URI` (else a
container); Atlas server (`IsAtlas`) from `ATLAS_URI` (else a container; Atlas tests run unless `ATLAS_URI` is
`"Disabled"`). A plain `mongod`/replica set can't run Atlas Search.

Each test gets a unique database (`TestDatabaseNamer.GetUniqueDatabaseName()`).

| Feature area | Required env vars |
|---|---|
| CSFLE / Queryable Encryption | `CRYPT_SHARED_LIB_PATH` (unset ⇒ those tests skip silently) |
| MongoDB connection | `MONGODB_URI` or `ATLAS_URI` (otherwise Docker auto-spins) |
| Driver-version override | `DRIVER_VERSION` |

See `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` for baseline regeneration,
`MONGODB_EF_QUERY_MODE` (query mode for all test suites; `MONGODB_EF_NATIVE_ONLY=1` is an alias for `NativeOnly`)
and fixture patterns.

## Versioning & breaking changes

The major version tracks the EF Core major, so **this project does not follow strict semver**: breaking changes
can land in minor releases. `BREAKING-CHANGES.md` is the running log.

Breaks are measured **against the latest released version of the assembly** (latest published NuGet package),
**not** `main`; an API added and changed within the unreleased cycle is not a break. Judge released behavior from
the tag, never inferred from the branch.

Releases are tagged `v<major>.<minor>.<patch>` (optionally `-preview.N`); `v8.*`/`v9.*`/`v10.*` ship in parallel.
Find the baseline via the GitHub release list, **not** local `git tag` (frequently stale):

```bash
gh release list --limit 1 --json tagName,isLatest        # absolute latest
gh release list --limit 100 --json tagName               # highest non-preview v<major>.* = that line's baseline
git show <tag>:<path>                                    # diff the file at that tag against the working tree
```

**Is a break:** public API signature/default/visibility changes; annotation-key changes (`Mongo:` prefix —
they affect stored compiled models and design-time output); behavior changes affecting persisted document
shape (element name, BSON representation, discriminator field, Guid representation);
`IMongoClientWrapper`/`IMongoDatabaseCreator`/`IMongoTransactionManager` interface changes (observable public
surface, even though users are warned not to implement these); default-value changes for
`AutoTransactionBehavior`, conventions, or `BsonRepresentation` handling.

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

Follows EF Core's pattern, not the driver's: **no enforced sync/async pairing**. Async surfaces exist where EF Core
defines them (`*Async`) and where the underlying driver call is async. Library code uses `ConfigureAwait(false)`.
New async methods must take a `CancellationToken` and pass it to driver calls unsubstituted.

## Commit & PR conventions

- The first commit message and the PR title start with a JIRA number: `EF-1234: Description`.
- The branch name usually matches: `EF-1234`.

## Reviewers

Each area folder has an `AGENTS.md` (auto-loaded in that subtree) and a read-only reviewer sub-agent in
`.claude/agents`; see `docs/agents-architecture.md`.

**Feature reviewers** span several directories and are keyed by file globs rather than an `AGENTS.md`:
`vector-search-reviewer` (`VectorIndex*`, `BinaryVector*`, `VectorSearch*`) and `encryption-reviewer`
(`CryptProvider.cs`, `QueryableEncryption*`).

**Cross-cutting reviewers** apply one lens across the whole diff and run on **every** invocation of
`/review-ef-core-provider`: `api-stability-reviewer` (public API / breaking changes),
`ef-conformance-reviewer` (EF Core integration, multi-version compat, service registration),
`security-reviewer` (credential redaction, sensitive-data logging, KMS, TLS). With a PR number,
`pr-summary-reviewer` also runs ("what does this PR do, and is it a good change?").

## References

JIRA: <https://jira.mongodb.org/projects/EF/>. The driver boundary is `BsonSerializerFactory` +
`IMongoClientWrapper`.
