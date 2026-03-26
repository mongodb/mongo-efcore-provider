---
name: test-all
description: Build and run tests against all three EF version targets (EF8, EF9, EF10)
argument-hint: "[optional: test filter or project name]"
allowed-tools: Bash(dotnet *), Bash(docker *), Write, Read, Glob, Grep
---

# Test All EF Versions

Build and run tests against all three EF Core version targets: **EF8**, **EF9**, and **EF10**.

## Arguments

`$ARGUMENTS` is an optional filter:
- If empty, run all tests across all three versions.
- If it looks like a test project name (e.g., "UnitTests", "FunctionalTests", "SpecificationTests"), run only that project.
- If it looks like a test filter (e.g., a class or method name), pass it via `--filter` to `dotnet test`.

## Phase 1: Pre-flight Checks (MUST pass before anything else)

Run these checks first. If **any** fail, stop immediately and report the failure — do not proceed to build or test.

1. **net10.0 SDK** — Run `dotnet --list-sdks` and verify a 10.x SDK is installed. If missing, stop with a clear error: "net10.0 SDK is not installed. Install it from https://dotnet.microsoft.com/download/dotnet/10.0".

2. **Database connectivity** — Check that either:
   - Docker is available (`docker info` succeeds) so Testcontainers can work, OR
   - `MONGODB_URI` environment variable is set.
   If neither condition is met, stop with: "No database available. Either start Docker or set MONGODB_URI=mongodb://localhost:27017".

Report pre-flight status before continuing:
```
Pre-flight checks:
  net10.0 SDK: OK (10.x.xxx)
  Database: OK (Docker available) | OK (MONGODB_URI set)
```

## Phase 2: Parallel Build

Build all three versions **in parallel** — each config outputs to its own `bin/` subfolder so there are no conflicts.

```
dotnet build E:/src/mongo-ef/main/MongoDB.EFCoreProvider.sln -c "Debug EF{version}" -v quiet
```

Run all three `dotnet build` commands simultaneously using parallel Bash tool calls.

If any build fails, report the failure but **still run tests for versions that built successfully**.

## Phase 3: Parallel Test

After builds complete, run tests for all successfully-built versions **in parallel**.

Test (no filter):
```
dotnet test E:/src/mongo-ef/main/MongoDB.EFCoreProvider.sln -c "Debug EF{version}" --no-build --logger "console;verbosity=normal" -v quiet
```

Test (with project filter):
```
dotnet test E:/src/mongo-ef/main/tests/MongoDB.EntityFrameworkCore.{project} -c "Debug EF{version}" --no-build --logger "console;verbosity=normal" -v quiet
```

Test (with test name filter):
```
dotnet test E:/src/mongo-ef/main/MongoDB.EFCoreProvider.sln -c "Debug EF{version}" --no-build --filter "{filter}" --logger "console;verbosity=normal" -v quiet
```

## Important Rules

- **Always use absolute paths** — never `cd` into directories.
- **Quote configuration names** — they contain spaces (e.g., `"Debug EF8"`).
- **Parallel is safe** — each EF version builds into a separate output directory (`bin/Debug EF8/`, etc.).
- **Continue on failure** — if one version fails build or tests, still run the remaining versions.
- Do NOT set `MONGODB_URI` unless the pre-flight check determined Docker is unavailable and `MONGODB_URI` is already set.

## Phase 4: Write Results Files

Write a markdown results file for **each EF version** to `E:/src/mongo-ef/main/artifacts/test-results/`. Create the directory if it doesn't exist.

Filename: `test-results-ef{version}.md` (e.g., `test-results-ef8.md`)

Each file should contain:

```markdown
# Test Results — EF{version}

**Date:** {ISO 8601 timestamp}
**Configuration:** Debug EF{version}
**Overall:** PASS | FAIL

## Summary

| Metric       | Count |
|-------------|-------|
| Passed       | N     |
| Failed       | N     |
| Skipped      | N     |
| Total        | N     |

## Build

{Build output or "OK"}

## Failed Tests

{List each failed test with its error message, or "None" if all passed}

## Full Output

<details>
<summary>Complete test output</summary>

```
{raw dotnet test console output}
```

</details>
```

## Phase 5: Console Summary

After writing result files, present a **summary table** to the user:

```
| Version | Build  | Tests Passed | Tests Failed | Tests Skipped | Results File |
|---------|--------|-------------|-------------|---------------|--------------|
| EF8     | OK     | 142         | 0           | 3             | artifacts/test-results/test-results-ef8.md |
| EF9     | OK     | 145         | 2           | 1             | artifacts/test-results/test-results-ef9.md |
| EF10    | OK     | 148         | 0           | 0             | artifacts/test-results/test-results-ef10.md |
```

If any version had failures, list the failing test names grouped by version below the table.

If a build failed, show the error in the Build column and "skipped" in the test columns.
