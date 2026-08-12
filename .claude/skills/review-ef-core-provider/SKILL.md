---
name: review-ef-core-provider
description: Fan-out review of the current branch, an external PR, or a branch in another clone — runs each per-area reviewer over the files it owns and aggregates findings. Requires the superpowers plugin (errors out if it is not available).
argument-hint: "[--all] [--model opus|sonnet|haiku] [<PR#> | <clone-path> [<base-ref> [<head-ref>]] | <base-ref> [<head-ref>]]"
allowed-tools: Bash, Read, Glob, Grep, Write, Agent, Skill
---

# /review-ef-core-provider

Run the per-area reviewer sub-agents (`.claude/agents/*-reviewer.md`) over a diff range in parallel and produce one consolidated report. Three modes:

- **Local range mode** — diff the current repo's branch.
- **External PR mode** — fetch a GitHub PR and diff against its base.
- **External clone mode** — diff a branch in a *different* clone of this repo, while running with the current clone's reviewer briefs and `AGENTS.md` files. Useful when the branch being reviewed lacks the latest agent/architecture updates that live in the current clone.

User args: `$ARGUMENTS`

## Step 0 — Require the superpowers plugin (hard gate)

This skill depends on the **superpowers** plugin and will not run without it. Before doing anything else:

1. Check the session's available-skills list (the `<system-reminder>` skill listing) for an entry named `superpowers:requesting-code-review`.
2. If it's missing, **stop immediately**. Do not parse args, diff, or dispatch reviewers. Emit exactly one error message to the user saying `/review-ef-core-provider` requires the superpowers plugin to be installed and enabled. Then end the response.
3. If present, invoke `superpowers:requesting-code-review` now to frame the entire run — this review *is* the code-review request it describes — then continue to Step 1.

## Step 1 — Determine scope

Parse `$ARGUMENTS`:
- If `--all` is present, queue every reviewer in the tables below regardless of diff. Skip step 2's filtering.
- If `--model <name>` is present, capture `<name>` (must be one of `opus`, `sonnet`, `haiku`) and pass it on every `Agent` dispatch in step 3. If absent, omit the `model` parameter so each reviewer falls back to its own frontmatter setting (see each agent file — most default to `sonnet`, the narrowest/most mechanical ones to `haiku`, and `pr-summary-reviewer` to `inherit`).
- Examine the remaining non-flag tokens in priority order: first try external PR mode, then external clone mode, then local range mode.

  **External PR mode** — if the first non-flag token looks like a PR number (a bare integer such as `297`, or a `#`-prefixed integer such as `#297`), treat this as an external PR review:
  1. Run `gh pr view <PR#> --json number,title,url,baseRefName,headRefOid` and capture the JSON.
  2. Parse `<owner>/<repo>` from the PR URL (e.g. `https://github.com/mongodb/mongo-efcore-provider/pull/297` → `mongodb/mongo-efcore-provider`).
  3. Run `git remote -v` and find the remote whose fetch URL contains `<owner>/<repo>` (case-insensitive; works for both HTTPS and SSH URLs). If multiple remotes match, pick the first. If none match, fall back to trying `upstream` then `origin`.
  4. Run `git fetch <remote> refs/pull/<PR#>/head` to bring the PR's head commit into `FETCH_HEAD`.
  5. Capture the head SHA: `git rev-parse FETCH_HEAD`.
  6. Ensure the base branch is locally available: `git fetch <remote> <baseRefName>` (safe even if already present). The tracking ref will be `<remote>/<baseRefName>`.
  7. Set **base ref** = `<remote>/<baseRefName>` and **head ref** = the SHA from step 5.
  8. Set **diff-repo** = the absolute path of the current repo (`git rev-parse --show-toplevel`).
  9. Record the PR metadata (number, title, URL) and the remote name for display in the step 4 report header.

  **External clone mode** — otherwise, if the first non-flag token resolves to an existing directory (`test -d "<token>"`), treat it as the path to another clone of this repo and review the branch checked out there:
  1. Capture an absolute path: `<clone> = $(cd "<token>" && pwd)` (or `realpath`). All subsequent git commands and file paths must use this absolute form.
  2. Confirm it's a git repo by running `git -C "<clone>" rev-parse --show-toplevel`. If that fails, stop and tell the user that `<clone>` is not a git checkout.
3. Capture a head label for display and filename use: `git -C "<clone>" rev-parse --abbrev-ref HEAD`. If it returns the literal `HEAD` (detached), fall back to `git -C "<clone>" rev-parse --short HEAD`.
  4. Collect the remaining non-flag tokens (after the path) in order:
     - First → **base ref** (default: `main`)
     - Second → **head ref** (default: `HEAD`)
  5. Set **diff-repo** = `<clone>`. Every git command in the rest of this skill must be run with `git -C "<diff-repo>" …`, and every file path passed to reviewers must be absolute (`<diff-repo>/<repo-relative-path>`). The parent agent's own working directory stays in the current clone so that `AGENTS.md` files and reviewer briefs continue to load from there — that's the point of this mode.

  **Local range mode** — otherwise, the remaining non-flag tokens are refs in the current repo. Collect them in order:
  - First token → **base ref** (default: `main`)
  - Second token → **head ref** (default: `HEAD`)
  - Set **diff-repo** = the absolute path of the current repo (`git rev-parse --show-toplevel`).

Use `<base>...<head>` as the diff range throughout (three-dot syntax finds the merge base). Examples:
- `/review-ef-core-provider` → `main...HEAD` in the current repo
- `/review-ef-core-provider 297` → external PR #297 (fetches and diffs against its base branch)
- `/review-ef-core-provider #297` → same as above
- `/review-ef-core-provider HEAD~3` → `HEAD~3...HEAD` in the current repo (last 3 commits only)
- `/review-ef-core-provider abc123 def456` → `abc123...def456` in the current repo (arbitrary commit range)
- `/review-ef-core-provider --all HEAD~5 HEAD~2` → all reviewers, commits HEAD~5 through HEAD~2 in the current repo
- `/review-ef-core-provider ~/code/efcore-pr` → review the current branch of the clone at `~/code/efcore-pr` against its `main`
- `/review-ef-core-provider ~/code/efcore-pr release/10.0` → review that clone's `HEAD` against its `release/10.0`
- `/review-ef-core-provider ~/code/efcore-pr origin/main feature-x` → review `feature-x` in that clone against `origin/main`

If `--all` was not passed, run `git -C "<diff-repo>" diff --name-only <base>...<head>`. If the result is empty, stop and tell the user the range has no changes — do not dispatch reviewers.

## Step 2 — Map changed files → reviewers

Match each changed file against the table. A file may match more than one reviewer; queue all matches. Track files that match nothing — they go in an "Unmapped changes" section of the final report so coverage gaps are visible.

| Reviewer | Path patterns |
|---|---|
| `query-reviewer` | `src/MongoDB.EntityFrameworkCore/Query/**` |
| `storage-reviewer` | `src/MongoDB.EntityFrameworkCore/Storage/**` |
| `metadata-reviewer` | `src/MongoDB.EntityFrameworkCore/Metadata/**` |
| `serialization-reviewer` | `src/MongoDB.EntityFrameworkCore/Serializers/**`; `src/MongoDB.EntityFrameworkCore/ChangeTracking/**` |
| `public-api-reviewer` | `src/MongoDB.EntityFrameworkCore/Extensions/**`; `src/MongoDB.EntityFrameworkCore/Infrastructure/**`; `src/MongoDB.EntityFrameworkCore/Design/**` |
| `diagnostics-reviewer` | `src/MongoDB.EntityFrameworkCore/Diagnostics/**` |
| `value-generation-reviewer` | `src/MongoDB.EntityFrameworkCore/ValueGeneration/**`; `src/MongoDB.EntityFrameworkCore/Metadata/Conventions/MongoValueGenerationConvention.cs` |
| `spec-conformance-reviewer` | `tests/MongoDB.EntityFrameworkCore.SpecificationTests/**`; `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/**`; `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Usings.cs` |
| `vector-search-reviewer` | files matching `VectorIndex*`, `VectorSearch*`, `BinaryVector*`, or `VectorSimilarity*` across `src/` and `tests/`; `src/MongoDB.EntityFrameworkCore/Metadata/VectorIndexOptions.cs`; `src/MongoDB.EntityFrameworkCore/Metadata/VectorIndexBuilder.cs`; vector-search helpers in `MongoDatabaseFacadeExtensions`, `MongoQueryableExtensions`, `MongoIndexBuilderExtensions`, `MongoPropertyBuilderExtensions` |
| `encryption-reviewer` | `src/MongoDB.EntityFrameworkCore/CryptProvider.cs`; `src/MongoDB.EntityFrameworkCore/QueryableEncryptionType.cs`; files matching `QueryableEncryption*` across `src/` and `tests/`; encryption properties on `MongoOptionsExtension` (`KeyVaultNamespace`, `KmsProviders`, `CryptExtraOptions`, `CryptProvider*`); `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Encryption/**` |

**Test-folder mapping:** per-area test folders mirror the `src/` layout — `tests/.../FunctionalTests/Query/`, `Storage/`, `Update/`, `Metadata/`, `Mapping/`, `Serialization/`, `ValueGeneration/`, `Design/` — and route to the corresponding `src/` area reviewer. The `Encryption/` test folder routes to `encryption-reviewer`, and the test utilities at `tests/.../FunctionalTests/Utilities/` route to `spec-conformance-reviewer`. `SpecificationTests/<Area>/` test folders route to both the matching area reviewer *and* `spec-conformance-reviewer` (the area owns the assertion logic; spec-conformance owns the fixture / inheritance discipline).

**Meta-mapping for reviewer definitions:** a change to `.claude/agents/<name>-reviewer.md` is reviewed by that same reviewer (e.g. `.claude/agents/query-reviewer.md` → `query-reviewer`). The reviewer is best placed to judge whether its own brief still accurately characterizes the area. Cross-cutting reviewer definitions map to themselves the same way. Exception: `pr-summary-reviewer` only runs in external PR mode, so a local-range or external-clone-mode change to `.claude/agents/pr-summary-reviewer.md` won't be self-reviewed — that file's review happens when the PR is run through `/review-ef-core-provider <PR#>`.

If new area reviewers are added under `.claude/agents/`, update this table.

## Cross-cutting reviewers (always run, unless the diff is doc-only)

These three reviewers run on every invocation regardless of which files changed. They look across the whole diff for one specific concern. They are *additional to* — not part of — the path-mapping table above.

| Reviewer | Concern |
|---|---|
| `api-stability-reviewer` | Public-API / breaking changes (signatures, defaults, visibility, annotation keys, `MongoEventId` numbering, behavior changes on unchanged signatures) |
| `ef-conformance-reviewer` | EF Core integration correctness — multi-version (EF8/EF9/EF10) compat, service registration, annotation hygiene, build-vs-runtime model |
| `security-reviewer` | Credential exposure, sensitive-data logging gating, KMS plumbing leaks, TLS surfaces, connection-string redaction |

**Skip condition.** If `--all` was not passed and every file in the diff (`git -C "<diff-repo>" diff --name-only <base>...<head>`) matches a doc-only pattern — `*.md`, `*.txt`, `docs/**`, `LICENSE*`, `.github/**` excluding workflow files (`.github/workflows/**` still counts as code) — none of these three carry any signal: skip dispatching all three and note it in the aggregated report (`## Cross-cutting findings` → `Skipped — diff is documentation-only.`) instead of spending three agent dispatches on files none of them can say anything about. Any single non-doc file in the diff cancels the skip and all three run as normal.

## PR-summary reviewer (external PR mode only)

In external PR mode, also dispatch the `pr-summary-reviewer` agent. It produces a holistic description of the PR (what it does, why) plus an opinion on whether it's a good change. It runs in parallel with everything else, and its output goes at the top of the consolidated report (before `## Summary`). Skip it in local range mode and external clone mode — there is no PR body to read. It is not subject to the doc-only skip condition above (a doc-only PR still deserves a summary/verdict).

## Step 3 — Dispatch reviewers in parallel

**Critical**: emit a single assistant message containing one `Agent` tool-use block per dispatched reviewer — the matched area + feature reviewers from step 2, the cross-cutting reviewers (unless skipped), *and* (in external PR mode only) `pr-summary-reviewer`. Multiple `Agent` calls in the same message run concurrently; sequential calls do not. Use `subagent_type: <reviewer-name>` for each. If `--model <name>` was parsed in step 1, set `model: <name>` on every block; otherwise omit the field and let each reviewer use its own frontmatter default.

In every template below, substitute:
- `<base>`, `<head>` — the diff range refs.
- `<diff-repo>` — the absolute path captured in step 1 (the current repo for local-range / external-PR mode; the other clone for external-clone mode).
- File lists — always passed as **absolute paths** of the form `<diff-repo>/<repo-relative-path>` so reviewer `Read` calls land on the right tree regardless of mode. Don't pass bare repo-relative paths.

### Area / feature reviewer prompt template

For each area or feature reviewer, use this template (substitute `<base>`, `<head>`, `<diff-repo>`, and the per-reviewer file list as absolute paths):

```
You are running as part of a multi-area branch review. The diff range is <base>...<head> in the repo at <diff-repo>.

Files in scope for this iteration that fall in your area (absolute paths):
- <abs-file1>
- <abs-file2>
…

Read those files at their current state. Run git commands with `git -C "<diff-repo>" …` (e.g. `git -C "<diff-repo>" diff <base>...<head> -- <repo-relative-path>`) — the parent agent's working directory may not be <diff-repo>. Pull in adjacent context only as needed to judge the change.

Follow the report shape, tags, finding cap, and verification requirement in `.claude/agents/CONVENTIONS.md` (also linked from your own agent definition). In short: verify every functional finding by running code before you report it — scaffold repros with `Bash` (you have no `Edit`/`Write`), clean up any repro file you create afterward, and include the repro and observed output in your report (uncapped, doesn't count against the word limit).
```

### Cross-cutter prompt template

For each cross-cutting reviewer, use this template (substitute `<base>`, `<head>`, `<diff-repo>`, and the *full* changed-file list as absolute paths):

```
You are running as a cross-cutting reviewer in a multi-area branch review. The diff range is <base>...<head> in the repo at <diff-repo>.

Your concern is not scoped to a directory — it is a single hygiene lens applied across the diff. Files in scope (absolute paths):
- <abs-file1>
- <abs-file2>
…

Use `git -C "<diff-repo>" diff <base>...<head>` to see the full picture and `git -C "<diff-repo>" diff <base>...<head> -- <repo-relative-path>` to focus. Read files at their current state where context matters. Skip files that are clearly irrelevant to your concern.

Follow the report shape, tags, finding cap, and verification requirement in `.claude/agents/CONVENTIONS.md` (also linked from your own agent definition). Findings must be specific to your concern — do not duplicate what an area reviewer would catch.
```

### PR-summary prompt template (external PR mode only)

For `pr-summary-reviewer`, use this template (substitute the PR fields, `<base>`, `<head>`, `<diff-repo>`, and the *full* changed-file list as absolute paths):

```
You are running as the PR summary reviewer in a multi-area branch review of an external pull request.

PR: #<number> — <title>
URL: <url>
Diff range: <base>...<head> in the repo at <diff-repo>

All files changed in this range (absolute paths):
- <abs-file1>
- <abs-file2>
…

Pull the PR body yourself with `gh pr view <number> --json body,labels,additions,deletions,changedFiles,author` to get the author's stated rationale. Use `git -C "<diff-repo>" diff <base>...<head>` for the diff and read files at their current state where context matters.

Produce a report in exactly the shape specified in your agent definition (Description / Assessment / Verdict; 500-word cap). Do not duplicate the per-area or cross-cutting reviewers' work.
```

## Step 4 — Aggregate

After all reviewers return, produce one consolidated report in this shape and show only this to the user (do not paste raw sub-agent transcripts).

For the report heading:
- **External PR mode**: `# PR review: #<number> — <title>` followed by the PR URL and `diff range: <base>...<head>` on separate lines.
- **External clone mode**: `# Clone review: <head-label> in <clone>` followed by `diff range: <base>...<head>` on a separate line. (`<head-label>` is the branch name or short SHA captured in step 1; `<clone>` is the absolute path.)
- **Local range mode**: `# Branch review: <base>...<head>`

```
# <heading from above>

## PR summary
(External PR mode only — paste the `pr-summary-reviewer`'s Description, Assessment, and Verdict verbatim. Omit this section in local range mode and external clone mode.)

## Summary
N reviewers ran (M area+feature + up to 3 cross-cutting [+ 1 PR summary in external PR mode]). X approved, Y flagged, Z escalated. (PR-summary verdict is not counted in those totals — it's a separate lens.)

## Escalations
For each `escalate` verdict from any reviewer — reviewer name, then its findings verbatim. Omit section if none.

## Cross-cutting findings
Group by reviewer (`api-stability-reviewer`, `ef-conformance-reviewer`, `security-reviewer`). For each: verdict + findings as bullets, or `<reviewer> — clean` if it approved. If the doc-only skip condition applied, write `Skipped — diff is documentation-only.` instead of the three reviewer entries.

## Area + feature findings
For each area or feature reviewer that ran: verdict + findings as bullets if `flag`, or `<reviewer> — clean` if `approve`. (Escalations are already covered above.)

## Unmapped changes
Files from the diff that didn't match any area or feature reviewer. (Cross-cutters cover the diff regardless, so this is purely a coverage signal for the area mapping.) Omit section if none.
```

**Save to file** — after composing the report, determine the output filename. The save location is always the parent agent's current working directory (the current clone), never `<diff-repo>` when those differ.

- **External PR mode**: stem is `review<number>` (e.g. `review297`). Always saved.
- **External clone mode**: stem is `review-<sanitized-head-label>`, where the head label is the branch name or short SHA captured in step 1 with every character that is not `[A-Za-z0-9._-]` replaced by `-` (e.g. branch `EF-308a` → `review-EF-308a`; branch `feature/foo` → `review-feature-foo`). Always saved.
- **Local range mode**: do not save — the report only appears inline.

When saving:
1. List files in the current directory matching `<stem>[a-z].md`.
2. Find the lowest letter (`a`–`z`) not already taken; use `a` if none exist yet.
3. Write the full report to `<stem><letter>.md` in the current directory using the Write tool.
4. Tell the user the filename at the end of your response (one line, e.g. `Saved to review297a.md`).

## Notes

- **This skill hard-depends on the superpowers plugin** (Step 0). If `superpowers:requesting-code-review` is not available, the skill errors out before doing any work.
- Reviewers must not *fix* the source tree: each one is configured with `tools: Read, Grep, Glob, Bash` and has no `Edit` / `Write` / `Patch` tool, so they can't apply a fix — treat any suggested fix as something the user or a follow-up change applies, not the reviewer. They *can* and *must* use `Bash` to verify functional findings, which includes running `dotnet test`, scaffolding a throwaway repro test or project via a here-doc, and `git diff`. Any repro file a reviewer creates is temporary and must be deleted before the reviewer returns, leaving the working tree exactly as found.
- A diff against a feature branch's own base is the typical use; pass an explicit `<base-ref>` for non-`main` cases (e.g. `/review-ef-core-provider release/10.0`). Pass a second positional arg to cap the end of the range (e.g. `/review-ef-core-provider HEAD~5 HEAD~1`).
- In external PR mode, the remote is inferred from the PR's repo URL, so PRs on forks (`upstream`, `origin`, or any named remote) are handled automatically without any extra flags.
- In external clone mode, the parent agent stays in the current clone so that `AGENTS.md` files, reviewer briefs, and any other repo guidance load from *here*, while the source code being reviewed and all git history come from `<clone>`. This is the whole point of the mode: review code on a branch that doesn't yet carry the latest agent/architecture updates, using the up-to-date briefs from the current clone. The pr-summary-reviewer does not run in this mode (no PR body to fetch); if you also want a holistic PR summary, run `/review-ef-core-provider <PR#>` separately.
- **Model defaults are per-reviewer, not inherited from the parent session** (see each agent file's frontmatter): `value-generation-reviewer` and `diagnostics-reviewer` (narrow, mechanical checks) default to `haiku`; the rest of the area, feature, and cross-cutting reviewers default to `sonnet`; `pr-summary-reviewer` (open-ended holistic judgment, no fixed rubric) defaults to `inherit`. Pass `--model <name>` to override all of them uniformly for a given run.
- Test-running by reviewers is **required for functional findings**, not opportunistic — see the verification requirement in `.claude/agents/CONVENTIONS.md`. One EF configuration suffices to confirm a behavioral bug; the `/test-all` multi-EF matrix is only needed when the concern is *divergence across* EF8/EF9/EF10, which — along with Atlas-only and encryption-infra paths — stays `[external-action]`.
