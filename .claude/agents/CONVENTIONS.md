# Reviewer conventions (shared)

Referenced by every `*-reviewer` agent and by `review-ef-core-provider`'s dispatch prompts; point here instead of restating.

## Report shape

**Verdict**: `approve` (no concerns), `flag` (non-blocking suggestions or nits), or `escalate` (blocking: public-API break, annotation-key rename, behavior change affecting stored documents, multi-EF break, spec-conformance regression, security regression).

**Findings**: bulleted, each `<file>:<line> — [fix-in-code|external-action][blocking|substantive|nit] **<TL;DR>** — <one-sentence problem> — <one-sentence fix or action>`. The TL;DR is a bold headline of at most 8 words (e.g. `**Annotation key renamed**`). Use repo-relative paths. At most **5** findings per pass: sort blocking, substantive, nit and drop the lowest rather than padding.

Tag 1, who can act:
- `[fix-in-code]`: resolvable by a mechanical in-tree change (edit, add a test, fix a throw type or `#if` branch) without external information.
- `[external-action]`: needs something outside the codebase (JIRA lookup, CI-matrix check, call-site audit outside the diff, `BREAKING-CHANGES.md` wording, or infra you lack: Atlas-only, no `CRYPT_SHARED_LIB_PATH`, multi-EF divergence needing `/test-all`). When unsure, prefer this tag.

Tag 2, importance:
- `[blocking]`: warrants an `escalate` verdict and stops the merge.
- `[substantive]`: a real concern the fixer should address; when unsure between this and `[nit]`, pick this.
- `[nit]`: cosmetic (unused import, typo); safe to defer.

**Tests run / repros**: every `dotnet test --filter` / `dotnet run` repro you executed with pass/fail, and for each functional finding the repro code plus observed output. `none` if no functional findings and nothing run.

Hard limit: 400 words, excluding repro code/output (uncapped).

## Verification requirement

A *functional* finding is a claim about runtime behavior (thrown exception, wrong translation or result, wrong persisted document shape, behavior-changing `#if` branch, lost `CancellationToken`, redaction that doesn't fire), as opposed to a naming/comment/style nit or a source-level signature observation.

Reproduce every functional finding before reporting it, with a minimal failing test or small `dotnet run` repro. `TestServer` auto-starts MongoDB containers when `MONGODB_URI`/`ATLAS_URI` are unset (see root `AGENTS.md`), so there is rarely an excuse to skip. If the repro doesn't reproduce, don't report it.

Defer to `[external-action]` only when a repro genuinely can't run locally: no Atlas available, `CRYPT_SHARED_LIB_PATH` unset for encryption paths, or a claimed divergence across EF8/EF9/EF10 needing `/test-all`. One EF configuration suffices to confirm a bug on a single version.

Reviewers may narrow or extend this in their own file.

**Clean up.** Delete every file you created to reproduce a finding; leave the working tree as found. Never commit a verification test. Reviewers have no `Edit`/`Write` tool beyond that; fixes are applied by the parent/fixer.
