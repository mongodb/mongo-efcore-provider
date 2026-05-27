# Per-area `AGENTS.md` & reviewer sub-agents — architecture

This document describes how the MongoDB EF Core Provider repository is partitioned into functional areas for the purpose of agent-aware tooling (Claude Code and similar). Each area gets a focused `AGENTS.md` (with a sibling `CLAUDE.md` pointing at it) so that subdirectory auto-loading scopes context to what's relevant, plus one read-only reviewer sub-agent in `.claude/agents/`. The `/review-ef-core-provider` skill (`.claude/skills/review-ef-core-provider/SKILL.md`) is the orchestrator that fans out to those reviewers.

The pattern is adapted from the analogous work in the MongoDB C# driver (`mongo-csharp-driver` PR #1993). EF-specific adaptations are called out where they differ.

## Why this exists

The provider is a single `src/` project but with sharply distinct internal layers — Query / LINQ translation, Storage / update pipeline, Metadata / conventions, Serializers, Change-tracking, Public API / DI, Diagnostics, Value generation — plus two cross-cutting features (Atlas Vector Search and Queryable Encryption) that span several of those layers. Before this layout, the only repo-specific guidance was the root `AGENTS.md` (the file you write your `dotnet build` invocations into). That file is general-purpose, so an agent debugging a query-translation problem under `Query/Visitors/` got the same context as one auditing transaction state-machine transitions under `Storage/`. The per-area files keep the root file lean while letting deep areas carry their own invariants, pitfalls, and review checklists.

The reviewer sub-agents encode area-specific review focus — what to flag, what counts as a breaking change for *this* layer (annotation-key rename, default-value change, EF-version `#if` omission, etc.), and what tests to run.

## Convention

- Every area has both `AGENTS.md` (substantive content) **and** `CLAUDE.md` (a one-line `@AGENTS.md` pointer, or `@../OtherDir/AGENTS.md` when two sibling directories share content).
- Each `AGENTS.md` carries a YAML frontmatter block with `area`, `scope` (globs), `reviewer-agent`, `adjacent-areas`. Claude Code doesn't parse it, but it's useful for humans and tooling.
- Reviewer sub-agents live at `.claude/agents/<name>-reviewer.md`. They are read-only (`tools: Read, Grep, Glob, Bash`); they report findings, they do not patch.

## Functional areas (8 directory-scoped + 2 feature-scoped)

| # | Area | `AGENTS.md` location | Sub-agent |
|---|---|---|---|
| 0 | Repo router | `AGENTS.md` (root) | — |
| 1 | Query / LINQ translation | `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | `query-reviewer` |
| 2 | Storage, update pipeline & transactions | `src/MongoDB.EntityFrameworkCore/Storage/AGENTS.md` | `storage-reviewer` |
| 3 | Metadata, attributes & conventions | `src/MongoDB.EntityFrameworkCore/Metadata/AGENTS.md` | `metadata-reviewer` |
| 4 | Serialization & change-tracking | `src/MongoDB.EntityFrameworkCore/Serializers/AGENTS.md` (+ `src/MongoDB.EntityFrameworkCore/ChangeTracking/CLAUDE.md` cross-ref) | `serialization-reviewer` |
| 5 | Public API, DI & options | `src/MongoDB.EntityFrameworkCore/Extensions/AGENTS.md` (+ `src/MongoDB.EntityFrameworkCore/Infrastructure/CLAUDE.md` and `src/MongoDB.EntityFrameworkCore/Design/CLAUDE.md` cross-refs) | `public-api-reviewer` |
| 6 | Diagnostics — events & logging | `src/MongoDB.EntityFrameworkCore/Diagnostics/AGENTS.md` | `diagnostics-reviewer` |
| 7 | Value generation | `src/MongoDB.EntityFrameworkCore/ValueGeneration/AGENTS.md` | `value-generation-reviewer` |
| 8 | Spec conformance & test infrastructure | `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` (+ `tests/MongoDB.EntityFrameworkCore.FunctionalTests/CLAUDE.md` cross-ref) | `spec-conformance-reviewer` |
| F1 | Atlas Vector Search (feature) | *no dedicated `AGENTS.md`* — file-pattern scoped (`VectorIndex*`, `BinaryVector*`, `VectorSearch*`) | `vector-search-reviewer` |
| F2 | Client-Side Encryption (feature) | *no dedicated `AGENTS.md`* — file-pattern scoped (`CryptProvider.cs`, `QueryableEncryption*`) | `encryption-reviewer` |

### Why two feature reviewers instead of merging them into area reviewers

Vector search and encryption are *features*, not directories. Each spans Query, Metadata, Storage, and the public-API surface (and, for encryption, the `Infrastructure/QueryableEncryptionSchemaProvider` plumbing too). Treating them as area reviewers would force a fake directory home for files that legitimately live in three places; treating them as cross-cutters would make them run on every diff regardless. File-pattern scoping is the right compromise: they run *when relevant*, alongside the area reviewers, without owning a directory.

## Cross-cutting reviewers (3)

These reviewers have no per-area `AGENTS.md` and no path-scoping. They apply a single hygiene lens across the **entire** diff on every `/review-ef-core-provider` invocation, regardless of which files changed.

| Sub-agent | Concern |
|---|---|
| `api-stability-reviewer` | Public-API / breaking changes — signatures, defaults, visibility, annotation-key stability, `MongoEventId` numbering, silent behavior changes on unchanged signatures |
| `ef-conformance-reviewer` | EF Core integration — multi-version (EF8/EF9/EF10) compatibility, interface contract conformance, service registration, build-vs-runtime model hygiene |
| `security-reviewer` | Credential exposure, TLS surfaces, sensitive-data logging gate (`ShouldLogSensitiveData()`), KMS/encryption material leakage in events or exceptions |

### Why these three (and why no `async-reviewer`)

The C# driver uses `security-reviewer` + `api-stability-reviewer` + `async-reviewer`. The EF Core provider replaces the third with `ef-conformance-reviewer`, because:

1. The provider doesn't own a paired-sync/async public surface (EF Core does). Its async hygiene is straightforward — `ConfigureAwait(false)` is used uniformly, `CancellationToken` flows from EF to driver without substitution, and there's no `Foo`/`FooAsync` invariant for the provider to enforce. A dedicated async reviewer would mostly land `approve` verdicts.
2. By contrast, **multi-EF-version compatibility is a load-bearing concern** unique to provider authors — `#if EF8 || EF9` discipline, service-registration completeness, build-vs-runtime model boundaries, annotation-name hygiene. There's no equivalent in the driver, where async-vs-spec-conformance is the dominant cross-cutting concern. `ef-conformance-reviewer` exists to keep this lens applied across every diff.

If async issues become a recurring problem, adding `async-reviewer` later is straightforward — define the agent file and add a row to the cross-cutters table in both `AGENTS.md` and `SKILL.md`.

## PR-summary reviewer (external PR mode only)

One additional reviewer runs only when `/review-ef-core-provider` is invoked with a PR number (external PR mode). It does not run for local branch reviews or external clone reviews because there is no PR body to read.

| Sub-agent | Concern |
|---|---|
| `pr-summary-reviewer` | Holistic "what does this PR do, and is it a good change?" — pulls the PR body via `gh pr view`, synthesizes across the whole diff, and gives an opinion (`looks good` / `mixed` / `concerns`). |

Its output is rendered at the top of the consolidated report so the reader sees the high-level take before line-level findings.

## External clone mode

`/review-ef-core-provider <path>` reviews a branch checked out in another clone of the repo, using the current clone's reviewer briefs and `AGENTS.md` files. This exists because reviewer briefs evolve faster than upstream PRs: when the agent definitions or per-area `AGENTS.md` files have moved on but a PR branch hasn't yet picked them up, cloning twice — one clone on the up-to-date agent branch, one clone on the PR branch — lets the skill run from the up-to-date clone while the diff and source code come from the PR clone. The parent agent's working directory stays in the current clone (so guidance loads from here), file lists are passed to reviewers as absolute paths under the other clone, and git commands inside reviewer prompts use `git -C "<clone>" …`.

## `--iterate` mode

`/review-ef-core-provider --iterate [--max-iterations N]` repeats review → fix → review in `<diff-repo>` until two consecutive **clean** reviews or the iteration cap is reached. It's only valid in local-range and external-clone modes — external PR mode is rejected because we don't push fixes back to PR branches. Several convergence mechanisms keep the loop short:

- **Two-tag findings.** Every reviewer finding carries `[fix-in-code|external-action]` (who can act) *and* `[blocking|substantive|nit]` (how important). The reviewer self-classifies severity at emit time using the rubric in the dispatch prompt — the parent does *not* re-classify. The clean check requires no `[fix-in-code][blocking]` and no `[fix-in-code][substantive]` findings; `[fix-in-code][nit]` and any `[external-action]` finding (regardless of severity) are surfaced but do not block convergence.
- **5-finding cap per reviewer per pass.** Reviewers emit at most 5 findings, sorted blocking → substantive → nit, with extras dropped. This bounds the noise floor.
- **Narrowed scope after iteration 1.** Iteration 1 scans the full `<base>...<head>` diff. From iteration 2 onward, the file set narrows to files the previous fixer commit touched plus files still carrying an unresolved `[blocking]`/`[substantive]` finding. Unchanged files stop regenerating stale nits.
- **Carry-forward of `[nit]` findings.** Every `[fix-in-code][nit]` finding in iteration N is passed to iteration N+1's reviewers as a *do-not-re-emit* note, breaking the most common tail-chase pattern.
- **`[external-action]` findings don't pin the loop.** JIRA lookups, CI matrix checks, `BREAKING-CHANGES.md` wording, "worth a multi-EF test" — these are surfaced in every iteration's report and in the closing summary, but they can't be mechanically fixed in-loop.
- **No-op fixer ends the loop.** If the fixer decides nothing in the current report is mechanically actionable, the loop stops with outcome `no actionable findings remaining` and prints the outstanding `[external-action]` items.

The fixer is dispatched as the `general-purpose` agent (Read/Write/Edit/Bash) and commits with the message `[review-iter <N>] Address review findings`. It never pushes. By default it applies `[fix-in-code][blocking]` and `[fix-in-code][substantive]` findings only; `[fix-in-code][nit]` findings are skipped (they may be picked up as a drive-by when the same file is already being edited for a substantive fix).

## Boundary decisions worth knowing

- **Serializers + ChangeTracking are one area, two directories.** `ChangeTracking/CLAUDE.md` re-points at `Serializers/AGENTS.md`. The two concerns are tightly coupled in practice — a new collection type needs both a serializer choice and a value-comparer — and one reviewer keeps that pairing honest.
- **Extensions + Infrastructure + Design are one area, three directories.** `Extensions/` is where the public-facing fluent API lives; `Infrastructure/` is where `MongoOptionsExtension` and the model validator live; `Design/` is one file for `dotnet ef` tooling support. All three are reviewed by `public-api-reviewer`. The `CLAUDE.md` in `Infrastructure/` and `Design/` re-point at `Extensions/AGENTS.md`.
- **Metadata vs. Extensions split for fluent API.** Builder extensions (`.ToCollection(...)`, `.HasElementName(...)`) live under `Extensions/` but their semantics live in `Metadata/` annotations and conventions. A new annotation is a `metadata-reviewer` concern; the builder method surfacing it is a `public-api-reviewer` concern. Most PRs touch both; that's expected.
- **`ValueGeneration` is small but has its own reviewer.** It would be tempting to fold this into `Metadata`. We don't — value-generation selection has a specific ordering invariant (`OwnedTypeOrdinalKey` → `ObjectId` → `string`-stored-as-`ObjectId` → base), and confusing it with the convention that *decides* whether generation runs (which lives in Metadata) leads to silent regressions.
- **Vector search and encryption are feature reviewers, not area reviewers.** See the table commentary above.
- **`spec-conformance-reviewer` does not own per-area test folders.** Per-area test folders under `tests/.../FunctionalTests/Query/`, `Storage/`, etc. mirror the `src/` layout and are reviewed by the matching `src/` area reviewer. `spec-conformance-reviewer` owns the cross-cutting test infrastructure (`Utilities/`, `Usings.cs`) and the EF Core specification-tests inheritance discipline.

## File templates

### `AGENTS.md` skeleton (per area)

```
---
area: <human name>
scope: [<glob>, <glob>]
reviewer-agent: <kebab-name>
adjacent-areas: [<name>, <name>]
---

# <Area> — AGENTS.md

## Scope
<one paragraph: what's in, what's out>

## Key entry points
- `<Type or file>` — <one-line role>

## Architecture notes
<call flow, key invariants, threading/async model, lifecycle>

## Boundaries with adjacent areas
- vs <area>: <where the line is, who owns the shared type>

## Common pitfalls
<2-6 bullets of recurring mistakes specific to this area>

## How to test
<filter expressions, env vars, sample test paths>
```

### Sub-agent skeleton (`.claude/agents/<name>.md`)

```
---
name: <kebab-name>-reviewer
description: Reviews changes to <area>. Use proactively when modifying <glob list>. Boundary with <adjacent>: <one line>.
tools: Read, Grep, Glob, Bash
model: inherit
---

You are the <Area> reviewer for the MongoDB EF Core Provider.

## Authoritative context
Read `<path-to-area-AGENTS.md>` first; then root `AGENTS.md` for build/test commands.

## Review focus
- <invariant 1>
- <invariant 2>
- <breaking-change / wire-shape / spec-conformance concern>

## Pass discipline
- Emit at most 5 findings per pass; prioritize `[blocking]` > `[substantive]` > `[nit]`. Drop the lowest-severity ones if you have more candidates.
- Do not run tests in this pass. If a test would be useful, tag the finding `[external-action]` so the user can run it.

## Escalate to user (do not auto-approve) when
- <public-API break, annotation-key rename, behavior change affecting stored documents, etc.>
```

All reviewers get `Read, Grep, Glob, Bash` (read-only review). None get `Edit/Write` — reviewers report, never patch. Tests are *not* run in-pass — the `/review-ef-core-provider --iterate` loop is short and `dotnet test` is too slow to fit; tests live in `[external-action]` findings the user runs out-of-loop.

## Verification

If you change the layout — add an area, move a file, rename a reviewer — re-run these checks:

1. **Auto-load chain.** Open a file in three representative areas (e.g. `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, `src/MongoDB.EntityFrameworkCore/Storage/MongoDatabaseWrapper.cs`, `src/MongoDB.EntityFrameworkCore/Metadata/Conventions/MongoConventionSetBuilder.cs`). In a fresh agent session, confirm the root `AGENTS.md` and the matching area `AGENTS.md` both surface in context — and that *unrelated* area files (e.g. Diagnostics) do not.
2. **Convention compliance.** `find . -name CLAUDE.md` — every file is one line and starts with `@`. `find . -name AGENTS.md` — every per-area file has the YAML frontmatter block and the standard section headers.
3. **Sub-agent dispatch.** Open a PR or staged diff that touches one area and confirm the matching reviewer is the obvious match by description + globs.
4. **Test-filter sanity.** From each area's "How to test" section, copy one `dotnet test … --filter` command and run it. It should pass on a clean main checkout.
5. **No build breakage.** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` succeeds — these are documentation-only changes.
6. **All three EF versions build.** Invoke the `/test-all` skill (or run the same builds manually for `Debug EF8` and `Debug EF9`).

## Adding a new area

1. Pick a natural directory root for the area (or a glob set if it doesn't fit a directory — i.e. it's a feature like vector search).
2. Drop `AGENTS.md` (with frontmatter and the standard sections) and a sibling `CLAUDE.md` containing `@AGENTS.md`.
3. Add a reviewer file at `.claude/agents/<name>-reviewer.md` following the skeleton above.
4. Update the **Functional areas** table in the root `AGENTS.md` and the **Functional areas** table above.
5. Add a path-pattern row for the new reviewer in the **Step 2** table inside `.claude/skills/review-ef-core-provider/SKILL.md` so the `/review-ef-core-provider` skill dispatches to it automatically.
6. Run the verification checks.
