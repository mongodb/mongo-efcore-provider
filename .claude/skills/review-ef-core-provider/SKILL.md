---
name: review-ef-core-provider
description: Fan-out review of the current branch, an external PR, or a branch in another clone — runs each per-area reviewer over the files it owns and aggregates findings. Requires the superpowers plugin (errors out if it is not available).
argument-hint: "[--all] [--model opus|sonnet|haiku] [<PR#> | <clone-path> [<base-ref> [<head-ref>]] | <base-ref> [<head-ref>]]"
allowed-tools: Bash, Read, Glob, Grep, Write, Agent, Skill
---

# /review-ef-core-provider

Run the reviewer sub-agents (`.claude/agents/*-reviewer.md`) over a diff range in parallel and produce one consolidated report. Modes:

- **Local range**: diff the current repo's branch.
- **External PR**: fetch a GitHub PR and diff against its base.
- **External clone**: diff a branch in a *different* clone, running with the current clone's reviewer briefs and `AGENTS.md` files (for branches that lack the latest agent updates).

User args: `$ARGUMENTS`

## Step 0: Require the superpowers plugin (hard gate)

1. Check the available-skills list for `superpowers:requesting-code-review`.
2. If missing, **stop immediately**: no arg parsing, diffing or dispatch. Emit one error saying `/review-ef-core-provider` requires the superpowers plugin to be installed and enabled, and end the response.
3. If present, invoke `superpowers:requesting-code-review` to frame the run (this review *is* that request), then continue.

## Step 1: Determine scope

Parse `$ARGUMENTS`:
- `--all`: queue every reviewer regardless of diff; skip step 2 filtering.
- `--model <name>` (`opus`, `sonnet` or `haiku`): pass as `model` on every `Agent` dispatch in step 3. If absent, omit it so each reviewer uses its frontmatter default.
- Remaining non-flag tokens: try external PR mode, then external clone mode, then local range mode.

**External PR mode**: the first token is a PR number (`297` or `#297`).
1. `gh pr view <PR#> --json number,title,url,baseRefName,headRefOid`.
2. Parse `<owner>/<repo>` from the PR URL.
3. `git remote -v`; pick the first remote whose fetch URL contains `<owner>/<repo>` (case-insensitive, HTTPS or SSH); else try `upstream`, then `origin`.
4. `git fetch <remote> refs/pull/<PR#>/head`; head SHA = `git rev-parse FETCH_HEAD`.
5. `git fetch <remote> <baseRefName>`.
6. **base ref** = `<remote>/<baseRefName>`; **head ref** = the SHA; **diff-repo** = `git rev-parse --show-toplevel`. Record PR number, title, URL and remote for the report header.

**External clone mode**: the first token is an existing directory.
1. `<clone>` = absolute path of it; use absolute forms everywhere.
2. Verify with `git -C "<clone>" rev-parse --show-toplevel`; if it fails, tell the user it is not a git checkout and stop.
3. Head label = `git -C "<clone>" rev-parse --abbrev-ref HEAD`, or `--short HEAD` if that returns `HEAD` (detached).
4. Next tokens: **base ref** (default `main`), **head ref** (default `HEAD`).
5. **diff-repo** = `<clone>`. Run every git command as `git -C "<diff-repo>" ...` and pass reviewers absolute paths (`<diff-repo>/<repo-relative-path>`). The parent's working directory stays in the current clone so its `AGENTS.md` files and briefs load; that is the point of the mode.

**Local range mode**: tokens are refs in the current repo: **base ref** (default `main`), **head ref** (default `HEAD`); **diff-repo** = `git rev-parse --show-toplevel`.

The diff range is `<base>...<head>` (three-dot, merge-base). Examples: no args is `main...HEAD`; `297` or `#297` an external PR; `HEAD~3` the last three commits; `abc123 def456` an arbitrary range; `~/code/efcore-pr release/10.0` that clone's `HEAD` against its `release/10.0`.

Unless `--all`, run `git -C "<diff-repo>" diff --name-only <base>...<head>`. If empty, tell the user the range has no changes and dispatch nothing.

## Step 2: Map changed files to reviewers

A file may match several reviewers; queue all. Files matching none go in "Unmapped changes" in the report.

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
| `vector-search-reviewer` | files matching `VectorIndex*`, `VectorSearch*`, `BinaryVector*`, or `VectorSimilarity*` across `src/` and `tests/`; vector-search helpers in `MongoDatabaseFacadeExtensions`, `MongoQueryableExtensions`, `MongoIndexBuilderExtensions`, `MongoPropertyBuilderExtensions` |
| `encryption-reviewer` | `src/MongoDB.EntityFrameworkCore/CryptProvider.cs`; `src/MongoDB.EntityFrameworkCore/QueryableEncryptionType.cs`; files matching `QueryableEncryption*` across `src/` and `tests/`; encryption properties on `MongoOptionsExtension` (`KeyVaultNamespace`, `KmsProviders`, `CryptExtraOptions`, `CryptProvider*`); `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Encryption/**` |

**Test folders** mirror `src/` and route to the matching `src/` area reviewer (`Query/`, `Storage/`, `Update/`, `Metadata/`, `Mapping/`, `Serialization/`, `ValueGeneration/`, `Design/`). `Encryption/` routes to `encryption-reviewer`; `FunctionalTests/Utilities/` to `spec-conformance-reviewer`. `SpecificationTests/<Area>/` routes to both the area reviewer (assertion logic) and `spec-conformance-reviewer` (fixture/inheritance discipline).

**Reviewer definitions**: a change to `.claude/agents/<name>-reviewer.md` is reviewed by that same reviewer (cross-cutters too). Exception: `pr-summary-reviewer` only runs in external PR mode, so changes to its file are reviewed only via `/review-ef-core-provider <PR#>`.

New reviewers under `.claude/agents/` need a row here.

## Cross-cutting reviewers (always run, unless the diff is doc-only)

Run on every invocation, additional to the table above; each applies one lens across the whole diff.

| Reviewer | Concern |
|---|---|
| `api-stability-reviewer` | Public-API / breaking changes |
| `ef-conformance-reviewer` | EF Core integration, multi-version compat, service registration, build-vs-runtime model |
| `security-reviewer` | Credential exposure, sensitive-data logging gate, KMS leaks, TLS, connection-string redaction |

**Skip condition.** Without `--all`, if every changed file matches a doc-only pattern (`*.md`, `*.txt`, `docs/**`, `LICENSE*`, `.github/**` excluding `.github/workflows/**`), skip all three and write `Skipped — diff is documentation-only.` under `## Cross-cutting findings`. Any non-doc file cancels the skip.

## PR-summary reviewer (external PR mode only)

In external PR mode also dispatch `pr-summary-reviewer`, in parallel with the rest; its output goes at the top of the report (before `## Summary`). Not run in the other modes (no PR body), and not subject to the doc-only skip.

## Step 3: Dispatch reviewers in parallel

Emit a **single** assistant message with one `Agent` block per reviewer (matched area + feature reviewers, cross-cutters unless skipped, and `pr-summary-reviewer` in PR mode); only same-message calls run concurrently. `subagent_type: <reviewer-name>`; set `model` only if `--model` was given.

Substitute `<base>`, `<head>`, `<diff-repo>` (the other clone in clone mode) and file lists as **absolute paths** `<diff-repo>/<repo-relative-path>` in every template.

### Area / feature reviewer prompt

```
You are running as part of a multi-area branch review. The diff range is <base>...<head> in the repo at <diff-repo>.

Files in scope for this iteration that fall in your area (absolute paths):
- <abs-file1>
- <abs-file2>
…

Read those files at their current state. Run git commands with `git -C "<diff-repo>" …` (e.g. `git -C "<diff-repo>" diff <base>...<head> -- <repo-relative-path>`) — the parent agent's working directory may not be <diff-repo>. Pull in adjacent context only as needed to judge the change.

Follow the report shape, tags, finding cap, and verification requirement in `.claude/agents/CONVENTIONS.md`. In short: verify every functional finding by running code before you report it — scaffold repros with `Bash` (you have no `Edit`/`Write`), clean up any repro file afterward, and include the repro and observed output in your report.
```

### Cross-cutter prompt

Pass the *full* changed-file list.

```
You are running as a cross-cutting reviewer in a multi-area branch review. The diff range is <base>...<head> in the repo at <diff-repo>.

Your concern is not scoped to a directory — it is a single hygiene lens applied across the diff. Files in scope (absolute paths):
- <abs-file1>
- <abs-file2>
…

Use `git -C "<diff-repo>" diff <base>...<head>` to see the full picture and `git -C "<diff-repo>" diff <base>...<head> -- <repo-relative-path>` to focus. Read files at their current state where context matters. Skip files clearly irrelevant to your concern.

Follow the report shape, tags, finding cap, and verification requirement in `.claude/agents/CONVENTIONS.md`. Findings must be specific to your concern — do not duplicate what an area reviewer would catch.
```

### PR-summary prompt (external PR mode only)

Pass the PR fields and the *full* changed-file list.

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

## Step 4: Aggregate

After all reviewers return, show only one consolidated report (no raw transcripts).

Heading:
- **External PR**: `# PR review: #<number> — <title>`, then the PR URL and `diff range: <base>...<head>` on separate lines.
- **External clone**: `# Clone review: <head-label> in <clone>`, then `diff range: <base>...<head>`.
- **Local range**: `# Branch review: <base>...<head>`

```
# <heading from above>

## PR summary
(External PR mode only — paste the `pr-summary-reviewer`'s Description, Assessment, and Verdict verbatim. Omit in other modes.)

## Summary
N reviewers ran (M area+feature + up to 3 cross-cutting [+ 1 PR summary in external PR mode]). X approved, Y flagged, Z escalated. (PR-summary verdict is not counted.)

## Escalations
For each `escalate` verdict from any reviewer — reviewer name, then its findings verbatim. Omit if none.

## Cross-cutting findings
Group by reviewer (`api-stability-reviewer`, `ef-conformance-reviewer`, `security-reviewer`): verdict + findings as bullets, or `<reviewer> — clean` if it approved. If the doc-only skip applied, write `Skipped — diff is documentation-only.` instead.

## Area + feature findings
For each area or feature reviewer that ran: verdict + findings as bullets if `flag`, or `<reviewer> — clean` if `approve`. (Escalations are already covered above.)

## Unmapped changes
Files that matched no area or feature reviewer (a coverage signal only; cross-cutters cover the whole diff). Omit if none.
```

**Save to file.** Save in the parent agent's current working directory (the current clone), never `<diff-repo>` when those differ.

- **External PR**: stem `review<number>` (e.g. `review297`). Always saved.
- **External clone**: stem `review-<sanitized-head-label>`, replacing every character outside `[A-Za-z0-9._-]` with `-` (`EF-308a` gives `review-EF-308a`; `feature/foo` gives `review-feature-foo`). Always saved.
- **Local range**: do not save; inline only.

When saving: list files matching `<stem>[a-z].md`, use the lowest unused letter (`a` if none), write the report to `<stem><letter>.md` with the Write tool, and end your response with one line, e.g. `Saved to review297a.md`.

## Notes

- Reviewers cannot fix the tree (`tools: Read, Grep, Glob, Bash`); treat suggested fixes as work for the user or a follow-up. They *must* use `Bash` to verify functional findings (`dotnet test`, throwaway repro via here-doc, `git diff`) and delete any repro file before returning. See `.claude/agents/CONVENTIONS.md`.
- Pass an explicit `<base-ref>` for non-`main` bases (`/review-ef-core-provider release/10.0`) and a second positional arg to cap the range end (`HEAD~5 HEAD~1`).
- In PR mode the remote is inferred from the PR URL, so forks and any named remote work without flags.
- Clone mode runs no `pr-summary-reviewer`; run `/review-ef-core-provider <PR#>` separately for a PR summary.
- Model defaults are per reviewer, not inherited from the session: `value-generation-reviewer` and `diagnostics-reviewer` use `haiku`; the other area, feature and cross-cutting reviewers `sonnet`; `pr-summary-reviewer` `inherit`. `--model` overrides all uniformly.
- One EF configuration suffices to confirm a behavioral bug; `/test-all` is only needed for *divergence across* EF8/EF9/EF10, which (like Atlas-unavailable and encryption-infra paths) stays `[external-action]`.
