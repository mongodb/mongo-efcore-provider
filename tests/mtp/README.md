# Running the EF11 SpecificationTests with `dotnet test`

The EF11 `SpecificationTests` use xUnit v3 4.x, which only supports Microsoft.Testing.Platform (MTP). On SDK 10+, `dotnet test`
runs an MTP project only when the nearest `global.json` opts in. A repo-root opt-in would break the xUnit v2 projects (the
EF8-EF10 `SpecificationTests`), so the opt-in lives here instead: `dotnet test` reads the `global.json` nearest to the directory
it is run from.

```bash
cd tests/mtp
dotnet build ../../MongoDB.EFCoreProvider.sln -c "Debug EF11"
dotnet test --project ../MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF11" --no-build
dotnet test --project ../MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF11" --no-build --filter-class "*NorthwindWhereQueryMongoTest"
```

Use this only for the EF11 SpecificationTests. MTP-mode `dotnet test` does not run the xUnit v2 projects (EF8-EF10
`SpecificationTests`); run those from the repo root as usual. `UnitTests` and `FunctionalTests` run either way.
