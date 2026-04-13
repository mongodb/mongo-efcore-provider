---
name: test-all
description: Build and run tests against all three EF version targets (EF8, EF9, EF10)
argument-hint: "[optional: test filter or project name] [--model haiku|sonnet|opus]"
allowed-tools: Bash(dotnet *), Bash(docker info), Read, Glob, Grep, Agent
---

# Test All EF Versions

`$SLN` refers to the solution file path: `{working directory}/MongoDB.EFCoreProvider.sln`

## Arguments

Parse `$ARGUMENTS` for:
1. --model <model> — If present, extract and remove it.
   Use as the `model` parameter when spawning agents. If absent, use `haiku`.
2. Remaining text — Optional filter:
   - If empty, run all tests across all three versions.
   - If it looks like a project name (e.g. "UnitTests"), run only that project.
   - If it looks like a test filter, pass it via `--filter` to `dotnet test`.

## Phase 1: Pre-flight Checks (MUST pass before anything else)

1. net10.0 SDK — Run `dotnet --list-sdks` and verify a 10.x SDK is installed.
   If missing, stop with: "net10.0 SDK is not installed."

2. Database connectivity — Check that either:
   - Docker is available (`docker info` succeeds), OR
   - `MONGODB_URI` environment variable is set.
   If neither: stop with: "No database available. Start Docker or set `MONGODB_URI`."

## Phase 2: Parallel Build & Test via Sub-agents

Spawn three sub-agents in parallel (one per EF version: EF8, EF9, EF10)
using the Agent tool. Set the `model` parameter on each agent.

Each agent's prompt must include the full build and test commands:

  1. Build:  dotnet build $SLN -c "Debug EF{version}" -v quiet
  2. Test:   dotnet test  $SLN -c "Debug EF{version}" --no-build
                   --logger "console;verbosity=normal" -v quiet

## Important Rules

- Always use absolute paths — never `cd` into directories.
- Quote configuration names — they contain spaces ("Debug EF8").
- Parallel is safe — each EF version builds into a separate output dir.
- Continue on failure — if one version fails, still report the others.

## Phase 3: Console Summary

Populate the summary table with the actual results from each sub-agent.
For each EF version, parse the `dotnet test` output and extract the real
`Passed`, `Failed`, and `Skipped` totals from the final test summary
(for example, the line containing `Passed:`, `Failed:`, and `Skipped:`).
Set the `Build` column to the actual build result for that version (`OK` or `FAILED`).

Use this format for the console summary:

| Version | Build  | Passed | Failed | Skipped |
|---------|--------|--------|--------|---------|
| EF8     | <actual> | <actual> | <actual> | <actual> |
| EF9     | <actual> | <actual> | <actual> | <actual> |
| EF10    | <actual> | <actual> | <actual> | <actual> |

If any version had failures, list failing test names grouped by version.
