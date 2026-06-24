# Query conformance baseline — main (Atlas-inclusive)

Reflects main's provider behavior (this branch changes only `benchmarks/` + docs — no `src/`).
Config: Debug EF10. Filter: `FullyQualifiedName~Query`. Captured 2026-06-23, re-verified 2026-06-24.

**Run with BOTH `MONGODB_URI` and `ATLAS_URI` unset.** The test infrastructure then has TestContainers
boot `mongodb/mongodb-atlas-local`, so the Atlas-gated tests run **genuinely against Atlas Search** —
no external server needed, and the whole run is self-contained. (When `ATLAS_URI` is unset an
atlas-local container backs the `IsAtlas` tests irrespective of `MONGODB_URI`; leaving `MONGODB_URI`
unset too runs the non-Atlas tests against a container as well.)

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| SpecificationTests (Query) | 4526 | 0 | 18 |
| FunctionalTests (Query) | 593 | 0 | 45 |

**Atlas-gated subset** (live vector search against atlas-local), included in the spec numbers above:

| Atlas Query tests | Passed | Skipped |
|---|---|---|
| `VectorSearch*` (spec) | 114 | 4 |

The 4 skips are `[ConditionalTheory(Skip = "Pre-filter on nested reference … pending C# driver 3.9.0 fix")]`.
The remaining skips are non-Atlas (e.g. encryption tests gated on `CRYPT_SHARED_LIB_PATH`).

## Repro commands

```bash
# Leave BOTH MONGODB_URI and ATLAS_URI unset → TestContainers boots mongodb/mongodb-atlas-local,
# so the Atlas (vector-search) tests run for real.
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"

dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"
```

Note: the broader live-Atlas surface — Storage `SearchIndexTests` / `SearchIndexExamplesTests`
(which create Atlas search indexes) — is outside the `~Query` filter and is a separate baseline concern.
