---
name: test-all
description: Build and run tests against all three EF version targets (EF8, EF9, EF10)
argument-hint: "[optional: test filter or project name] [--model haiku|sonnet|opus]"
allowed-tools: Bash(dotnet *), Bash(docker info), Bash(printenv:*), Bash(*/tests/tools/fetch-crypt-shared.sh*), Bash(CRYPT_SHARED_LIB_PATH=* MONGODB_BINARIES=* dotnet *), Read, Glob, Grep, Agent
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

A variable counts as a **real external database** only when it matches what
`TestServer.GetOrInitialize` treats as a usable connection: it is set, **not
empty or whitespace-only** (the harness treats blank as unset), and **not
`"Disabled"` compared case-insensitively** (`Disabled` / `disabled` / `DISABLED`
are all the skip-Atlas sentinel, not a connection). Apply this same test in both
checks below.

2. Database connectivity — Run `printenv MONGODB_URI`, then check that either:
    - Docker is available (`docker info` succeeds), OR
    - `MONGODB_URI` is a real external database (per the rule above).
      `ATLAS_URI` does **not** satisfy this check: it only supplies the Atlas-specific
      server, while the *default* server every test uses comes from `MONGODB_URI` or a
      testcontainer (`TestServer.cs`). So without Docker or `MONGODB_URI`, the run
      can't start even if `ATLAS_URI` is set.
      If neither holds, stop with: "No database available. Start Docker or set `MONGODB_URI`."

3. Shared-database detection — Run `printenv MONGODB_URI` and `printenv ATLAS_URI`.
   If `MONGODB_URI` **or** `ATLAS_URI` is a real external database (per the rule
   above), the test runs hit that **shared database** instead of a per-process
   testcontainer — record `SHARED_DB = true`, which forces serial execution in
   Phase 2. Otherwise `SHARED_DB = false`: each EF version gets its own
   testcontainer, so parallel execution is safe. (An empty/whitespace value or
   `ATLAS_URI=Disabled` keeps `SHARED_DB = false`.)

4. Encryption libraries — the CSFLE / Queryable Encryption tests skip unless `CRYPT_SHARED_LIB_PATH` (crypt_shared
   library) **and** `MONGODB_BINARIES` (folder containing `mongocryptd`) are set; with only the first, the
   `Mongocryptd` cases fail instead. Run `printenv CRYPT_SHARED_LIB_PATH` and `printenv MONGODB_BINARIES`:
    - Both non-empty: record `ENCRYPTION_ENV = ""` (nothing to add; sub-agents inherit them).
    - Otherwise run `{working directory}/tests/tools/fetch-crypt-shared.sh` (absolute path) (same libraries CI downloads; cached after the first
      run, so later runs are quick; the first can take a minute or two). Its **stdout** is two `export NAME='value'`
      lines — record `ENCRYPTION_ENV = "CRYPT_SHARED_LIB_PATH='<value>' MONGODB_BINARIES='<value>'"` from them.
    - If the script fails (offline, no Python or git), do **not** stop: record `ENCRYPTION_ENV = ""`, and remember to
      state in the final summary that encryption tests were skipped and why.

## Phase 2: Build & Test via Sub-agents

Run one build + test pass per EF version (EF8, EF9, EF10), each via the Agent
tool with the `model` parameter set. **How they are scheduled depends on
`SHARED_DB` from Phase 1:**

- **`SHARED_DB = false`** (testcontainers) — spawn all three sub-agents **in
  parallel** (one assistant message with three `Agent` blocks). Each EF version
  builds into a separate output dir and gets its own testcontainer, so there is
  no contention.
- **`SHARED_DB = true`** (`MONGODB_URI` / `ATLAS_URI` set) — run the three
  sub-agents **serially**: dispatch one, wait for it to finish, then dispatch the
  next. The three versions would otherwise all hit the same database
  concurrently and race on shared global state (the test suite disables
  intra-assembly parallelization for exactly this reason — see
  `tests/.../FunctionalTests/Usings.cs`). Never run them in parallel when a
  shared database is configured.

Each agent's prompt must run the build and test commands below. Both operate on
the **whole solution `$SLN`**, which is required: it runs **all** test projects —
`MongoDB.EntityFrameworkCore.UnitTests`, `MongoDB.EntityFrameworkCore.FunctionalTests`,
and `MongoDB.EntityFrameworkCore.SpecificationTests`. Do not narrow to a single
project unless the user passed an explicit filter (see Arguments).

1. Build:  dotnet build $SLN -c "Debug EF{version}" -v quiet
2. Test:   `$ENCRYPTION_ENV` dotnet test  $SLN -c "Debug EF{version}" --no-build
   --logger "console;verbosity=normal" -v quiet
   (`$ENCRYPTION_ENV` is the literal prefix from Phase 1 step 4, written inline before `dotnet` so it applies to that
   process; omit it when empty. Pass it to every sub-agent verbatim.)

(If the user passed a filter/project arg, append it — e.g. `--filter "<expr>"`
or the project path — to the test command; otherwise run the full solution so
unit, functional, and specification tests all execute.)

### Execution hygiene (include verbatim in every sub-agent's prompt)

Each sub-agent runs its build+test in the **foreground** and reports only **after
the test process has exited** with a summary:

- **Do NOT background the run** — no `&`, no `nohup`, no background-run tool, no
  "I'll report once the background task notifies me." Run the command, block until
  it exits, then parse. A reply sent while the run is still going is a failed
  dispatch, not a status — the controller must re-run it.
- **Write the output to a log file and parse the log** (not scrollback), so the counts survive: `dotnet test … > <logfile> 2>&1`.
- **A whole-solution run prints THREE summary blocks — one per test assembly** (`UnitTests`, `FunctionalTests`, `SpecificationTests`). Search the log for each `Passed!`/`Failed!` block and sum the three per-assembly `Passed`/`Failed`/`Skipped` totals.
  two assemblies — `SpecificationTests` is usually last and is roughly half the
  tests, so reading only it undercounts by thousands.
- `--no-build` above is correct **because Phase 2 built first**. If the build step
  did not run in this same invocation, drop `--no-build` — a stale binary produces
  a false green.

## Important Rules

- Always use absolute paths — never `cd` into directories.
- Quote configuration names — they contain spaces ("Debug EF8").
- Parallelism depends on `SHARED_DB`: safe only when each EF version gets its own
  testcontainer (`SHARED_DB = false`). When `MONGODB_URI`/`ATLAS_URI` point at a
  shared database (`SHARED_DB = true`), run the versions serially — builds still
  go to separate output dirs, but their tests must not hit the shared database
  concurrently.
- Run the full solution by default — unit, functional, and specification tests
  must all run unless the user passed an explicit filter.
- Continue on failure — if one version fails, still report the others.

## Phase 3: Console Summary

Populate the summary table with the actual results from each sub-agent.
For each EF version, parse the `dotnet test` output and extract the real
`Passed`, `Failed`, and `Skipped` totals from the test summary. A whole-solution
run prints a **separate summary block per test assembly (three total)** — sum all
three. If a sub-agent reports only a single block, the run is incomplete or it
read the wrong block; send it back rather than reporting a partial count.
Set the `Build` column to the actual build result for that version (`OK` or `FAILED`).

Use this format for the console summary:

| Version | Build  | Passed | Failed | Skipped |
|---------|--------|--------|--------|---------|
| EF8     | <actual> | <actual> | <actual> | <actual> |
| EF9     | <actual> | <actual> | <actual> | <actual> |
| EF10    | <actual> | <actual> | <actual> | <actual> |

Add one line under the table stating whether encryption tests ran: "Encryption: ran (crypt_shared + mongocryptd)" or
"Encryption: SKIPPED — <reason>" (the test output also contains a `WARNING: CRYPT_SHARED_LIB_PATH is not set` line when
they skip; if a sub-agent's log shows it, report it here).

If any version had failures, list failing test names grouped by version.
