# Query conformance baseline — main — 2026-06-23

main @ 05fd4059ad2b643bcb0894c7793358a32d791232. Config: Debug EF10. Filter: FullyQualifiedName~Query.

This branch changes only benchmarks/ and docs (no src/ changes), so these counts reflect main's provider behavior.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| SpecificationTests (Query) | 4526 | 0 | 18 |
| FunctionalTests (Query) | 593 | 0 | 45 |

## Repro commands

```bash
MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"

MONGODB_URI="mongodb://localhost:27017/?replicaSet=rs0" dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query"
```

Repro: the two `dotnet test … --filter "FullyQualifiedName~Query"` commands in the plan (Task 6).
