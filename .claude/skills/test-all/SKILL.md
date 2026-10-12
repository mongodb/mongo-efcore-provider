---
name: test-all
description: Build and run tests against all three EF version targets (EF8, EF9, EF10)
argument-hint: "[optional: test filter or project name] [--model haiku|sonnet|opus]"
allowed-tools: Bash(dotnet *), Bash(docker info), Bash(printenv:*), Read, Glob, Grep, Agent
---

# Test All EF Versions

`$SLN` = `{working directory}/MongoDB.EFCoreProvider.sln`

## Arguments

Parse `$ARGUMENTS`:
1. `--model <model>`: extract and remove it; use it as the `model` for spawned agents (default `haiku`).
2. Remaining text is an optional filter: empty runs everything on all three versions; a project name (e.g. "UnitTests") runs only that project; otherwise pass it via `--filter`.

## Phase 1: Pre-flight (must pass first)

A variable is a **real external database** only if it is set, not empty/whitespace, and not `"Disabled"` (case-insensitive; that is the skip-Atlas sentinel). This mirrors `TestServer.GetOrInitialize`.

1. `dotnet --list-sdks` must show a 10.x SDK, else stop: "net10.0 SDK is not installed."
2. `docker info` succeeds OR `MONGODB_URI` (via `printenv`) is a real external database, else stop: "No database available. Start Docker or set `MONGODB_URI`." `ATLAS_URI` alone does not satisfy this: the default server every test uses comes from `MONGODB_URI` or a testcontainer.
3. `SHARED_DB = true` if `MONGODB_URI` **or** `ATLAS_URI` is a real external database (tests then hit a shared database); otherwise `false` (each EF version gets its own testcontainer).

## Phase 2: Build & test via sub-agents

One build+test pass per EF version (EF8, EF9, EF10), each via the Agent tool with `model` set.

- `SHARED_DB = false`: spawn all three **in parallel** (one message, three `Agent` blocks); separate output dirs and containers mean no contention.
- `SHARED_DB = true`: run **serially** (dispatch, wait, dispatch next). The versions would race on shared global state in the same database (intra-assembly parallelization is disabled for that reason, `tests/.../FunctionalTests/Usings.cs`).

Each agent runs, against the whole `$SLN` (all of `UnitTests`, `FunctionalTests`, `SpecificationTests`; don't narrow unless the user passed a filter):

1. `dotnet build $SLN -c "Debug EF{version}" -v quiet`
2. `dotnet test  $SLN -c "Debug EF{version}" --no-build --logger "console;verbosity=normal" -v quiet`

Append the user's `--filter "<expr>"` or project path to the test command if given. Drop `--no-build` if the build step did not run in the same invocation (stale binaries give a false green).

### Execution hygiene (include verbatim in every sub-agent's prompt)

Run build+test in the **foreground** and report only **after the test process has exited**:

- Do NOT background the run (no `&`, `nohup`, background-run tool, or "I'll report when it notifies me"). A reply sent while the run is going is a failed dispatch; the controller must re-run it.
- Write output to a log file and parse the log, not scrollback: `dotnet test ... > <logfile> 2>&1`.
- A whole-solution run prints **three** summary blocks (one per test assembly: `UnitTests`, `FunctionalTests`, `SpecificationTests`). Find each `Passed!`/`Failed!` block and sum the `Passed`/`Failed`/`Skipped` totals. `SpecificationTests` is usually last and about half the tests, so reading only it undercounts by thousands.

## Rules

- Use absolute paths; never `cd`. Quote configuration names ("Debug EF8").
- Run the full solution by default.
- Continue on failure: if one version fails, still report the others.

## Phase 3: Console summary

Fill the table from real results. Sum all three assembly blocks per version; if a sub-agent reports a single block, the run is incomplete or it read the wrong block, so send it back. `Build` is `OK` or `FAILED`.

| Version | Build  | Passed | Failed | Skipped |
|---------|--------|--------|--------|---------|
| EF8     | <actual> | <actual> | <actual> | <actual> |
| EF9     | <actual> | <actual> | <actual> | <actual> |
| EF10    | <actual> | <actual> | <actual> | <actual> |

If any version had failures, list failing test names grouped by version.
