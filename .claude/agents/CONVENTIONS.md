# Reviewer conventions (shared)

Referenced by every `*-reviewer` agent definition and by `review-ef-core-provider`'s dispatch prompts. This text is intentionally not duplicated in either place — point here instead of restating it.

## Report shape

**Verdict**: one of `approve`, `flag`, `escalate`.
- `approve` = no concerns
- `flag` = non-blocking suggestions or nits
- `escalate` = blocking concern that needs user attention before merge (public-API break, annotation-key rename, behavior change affecting stored documents, multi-EF break, spec-conformance regression, security regression)

**Findings**: bulleted list. Each bullet: `<file>:<line> — [fix-in-code|external-action][blocking|substantive|nit] **<TL;DR>** — <one-sentence problem> — <one-sentence fix or action>`.

`<TL;DR>` is a terse headline (**≤8 words**), bold, right after the tags. It's a label, not the explanation — e.g. `**May throw NullReferenceException**`, `**Annotation key renamed**`.

Tag 1 — who can act:
- `[fix-in-code]` — resolvable by an in-tree code change (edit, add a test, fix a throw type, fix an `#if` branch) the fixer agent can make mechanically without external information.
- `[external-action]` — needs something outside the codebase: a JIRA lookup, CI-matrix verification, auditing call sites outside the diff, a spec-wording check, `BREAKING-CHANGES.md` wording, or infra you don't have (Atlas-only, encryption without `CRYPT_SHARED_LIB_PATH`, multi-EF divergence needing `/test-all`). When unsure, prefer this tag — it surfaces the concern without claiming the fixer can address it.

Tag 2 — how important:
- `[blocking]` — should land an `escalate` verdict and stop the merge.
- `[substantive]` — real concern the fixer should address. When unsure between this and `[nit]`, prefer this (conservative — the fixer just acts on it).
- `[nit]` — mechanical cosmetic (unused import, typo, missing trailing newline). Doesn't affect behavior; safe to defer indefinitely.

Use repo-relative paths in findings. Emit at most **5** findings per pass — if you have more, sort by tag (blocking → substantive → nit) and drop the lowest-priority ones rather than padding the list.

**Tests run / repros**: list every `dotnet test --filter` / `dotnet run` repro you actually executed, with pass/fail, and — for each functional finding — the repro code/commands plus the observed output (assertion message, exception, or wrong value). Write `none` if you reported no functional findings and ran nothing.

Hard limit: 400 words total, **excluding** repro code/output blocks (those are uncapped).

## Verification requirement

A *functional* finding is any claim about runtime behavior — a thrown exception, wrong translation or result, wrong persisted document shape, a behavior-changing `#if` branch, lost `CancellationToken` propagation, a redaction that doesn't fire, etc. — as opposed to a naming/comment/style nit or a source-level signature observation.

You must reproduce every functional finding by running code before reporting it: add a minimal failing test or a small `dotnet run` repro and run it. The functional-test harness (`tests/.../FunctionalTests/Utilities/TestServer.cs`) auto-starts a MongoDB testcontainer via Docker when `MONGODB_URI`/`ATLAS_URI` are unset, so `dotnet test` always runs here with no manual setup — there is rarely an excuse to skip this. If your repro doesn't reproduce the problem, don't report it. Include the repro and observed output in the report (uncapped, doesn't count against the word limit).

Only defer to `[external-action]` when a repro genuinely can't run locally: Atlas-only features (e.g. vector search), encryption paths needing infra that isn't installed (`CRYPT_SHARED_LIB_PATH` unset), or a claim of *divergence* across EF8/EF9/EF10 that needs the full `/test-all` matrix. One EF configuration is enough to confirm a behavioral bug on a single version — that must be verified, not deferred.

Individual reviewers may narrow or extend this default (e.g. `security-reviewer`'s secret-exposure repro, `vector-search-reviewer`'s Atlas-only exception, `ef-conformance-reviewer`'s multi-EF-divergence exception) — those area-specific overrides live in the reviewer's own file, not here.

**Clean up after yourself.** Delete any file you created to reproduce a finding, leaving the working tree exactly as found. Verifying a finding never means committing a test.

Reviewers have no `Edit`/`Write` tool — they can never apply a fix, only report one. Any suggested fix is something the parent/fixer applies.
