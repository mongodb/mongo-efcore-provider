# Native owned single-reference whole-entity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a whole-entity query over an entity with **owned single-reference** navigation(s) go native (and therefore stream via the Phase-1 one-pass materializer), with `Native == DriverLinq` results.

**Architecture:** Owned data is embedded, so the native `{}`/filter/sort pipeline already returns it, `StreamingEligibility` already admits owned single-refs, and both the DOM shaper and the Phase-1 one-pass streaming shaper already materialize owned reference sub-documents (currently dormant). The only blocker is the **gate**: EF auto-includes the owned nav, and the native slot population marks the resulting `IncludeExpression`/operator non-representable (`Route → Fallback`). This slice is a **narrow gate change** (approach A) that stops rejecting an owned, embedded single-reference auto-include — after which the existing pipeline + shapers just work. Spike-led: Task 1 pins the exact rejection site before any production edit.

**Tech Stack:** .NET 10 / EF Core 10 (+ EF8/EF9 via build configs), MongoDB C# driver 3.9.0, the provider's native translation path (`Query/Visitors/*`, `Query/NativeTranslation/*`).

## Global Constraints

- **Results unchanged; `Native == DriverLinq`.** Owned whole-entity has a working driver-LINQ oracle — every owned shape this slice makes native must return byte-identical entities to the driver-LINQ path (values, present/absent owned sub-doc, required-missing throw, nested owned ref, shared-type owned), no-track AND tracked.
- **This CHANGES the native/streaming eligibility set** (unlike SP7). The `NativeOnly` spec pass-set will shift (owned-ref whole-entity moves fallback→native): the pass count rises, the fallback count drops. Per the versioning rubric this is **not** a breaking change (hard-throw/graceful-fallback → native, results unchanged, native default is not a break, MQL is not contract). Flipped tests must be updated to assert native **with verified-correct data**, and the `NativeOnly` sweep re-baselined with the delta explained.
- **Narrow admit-set.** Admit ONLY an owned *single-reference* embedded auto-include (and, if the spike confirms, a nested owned single-reference). An entity that ALSO has an owned **collection**, a **non-owned** navigation, is **TPH**, or uses an owned **sub-property predicate** (`e.Address.City`) must still fall back — those are separate deferred slices. Align the admit-set with `StreamingEligibility.IsEligible`.
- **Multi-version:** builds + passes under `Debug EF8`, `Debug EF9`, `Debug EF10`; `#if`-guard any version-divergent surface; all touched types stay `internal`; preserve file BOMs; `<Nullable>enable</Nullable>`.
- **Proving native:** assert `MongoQueryMode.NativeOnly` succeeds (fallback throws). MQL shape does not distinguish native from fallback for whole-entity.
- **Delivery:** stacked-PR workflow — at finish, squash to one slice commit, keep a `-presquash` backup, plain-FF push onto `origin/NativeQueryOngoing` (with the user's go). Do not push without approval.

---

### Task 1: Spike — pin the gate trigger + prove approach A + enumerate flips (throwaway)

Throwaway investigation on a scratch branch; discarded after a findings doc. NOT TDD.

**Files:**
- Scratch branch `owned-ref-spike` off `EF-322-owned-ref-whole-entity-native`.
- Inspect: `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, `Query/NativeTranslation/NativeSlotPopulator.cs`, `Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (the gate / `ClassifyNativeDisposition`), `Query/NativeTranslation/StreamingEligibility.cs`, `Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs` (owned-ref recursion — already built).
- Findings doc: `.superpowers/sdd/EF-322-owned-ref-whole-entity-spike.md` (gitignored scratch).

**Interfaces:**
- Produces (for Task 2): `rejectionSite` (exact file:method where the owned-ref auto-include → `MarkNotNativelyRepresentable`), `admitCondition` (the narrowest predicate that admits an owned single-ref embedded auto-include while excluding owned-collection / non-owned-nav / TPH), `approachAWorks` (bool: DOM + one-pass shaper materialize correctly once admitted), `nestedCovered` (bool), `flips` (the list of tests that flip fallback→native).

- [ ] **Step 1: Reproduce the fallback and locate the rejection.** Cut the scratch branch. With a fixture entity owning a single reference (e.g. reuse `NativeMaterializerNullabilityTests.WithOwned` or `NativeGateRoutingTests` `Address` shape), run the whole-entity query under `MongoQueryMode.NativeOnly` and confirm it throws `NativeTranslationNotSupportedException`. Instrument / read to find the EXACT site that calls `MarkNotNativelyRepresentable` (or drives `Route → Fallback`) for the auto-included owned nav. Record `rejectionSite`.

- [ ] **Step 2: Prove approach A end-to-end.** Make the minimal throwaway edit at `rejectionSite` to STOP rejecting the owned single-ref embedded auto-include (Route stays `WholeEntity`). Run the whole-entity query and compare, per row, against the `DriverLinq` result for: present owned sub-doc; absent/null owned sub-doc; a **required** owned reference with the sub-doc missing (must throw the SAME exception as driver-LINQ); a **nested** owned reference (owned ref that owns a further ref); a **shared-type** owned reference. Confirm `NativeOnly` now succeeds (native) AND that it takes the one-pass STREAMING path (not just DOM) for the eligible shapes. Record `approachAWorks`, `nestedCovered`.

- [ ] **Step 3: Map the admit-set boundary.** Confirm the throwaway edit does NOT wrongly admit: an entity with an owned COLLECTION, an entity with a NON-OWNED nav, a TPH entity, or an owned SUB-PROPERTY predicate (`e.Address.City`) — each must still fall back. Record the narrowest `admitCondition` that achieves this (aligned with `StreamingEligibility`).

- [ ] **Step 4: Enumerate the flips.** Run the `NativeOnly` EF10 spec sweep + the owned-related functional tests; list every test that flips from asserting fallback to asserting native (`NativeMaterializerNullabilityTests` owned cases, `NativeGateRoutingTests` owned cases, Northwind spec over owned-nav entities). Record `flips`.

- [ ] **Step 5: Write findings + discard scratch.** Write `.superpowers/sdd/EF-322-owned-ref-whole-entity-spike.md` (rejectionSite, admitCondition, approachAWorks, nestedCovered, flips, and a go/no-go: proceed with A, or fall back to approach B). Revert all scratch code; delete `owned-ref-spike`. **STOP for review.**

---

### Task 2: The gate change — owned single-reference whole-entity goes native

**Files:**
- Modify: `rejectionSite` from Task 1 (the narrowest admit of an owned single-ref embedded auto-include — expected in `NativeSlotPopulator` / the QMTEV include handling / the gate).
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — a new `NativeOwnedReferenceWholeEntityTests.cs` (or nearest existing gate test file).

**Interfaces:**
- Consumes: `rejectionSite`, `admitCondition` (Task 1).
- Produces (for Task 3): the shape now goes native; a fixture entity owning a single reference that Task 3's parity tests reuse.

- [ ] **Step 1: Write the failing "goes native" test.** In `NativeOwnedReferenceWholeEntityTests.cs`, add a test that seeds an entity owning a single reference and asserts the whole-entity query (`AsNoTracking().ToList()`) **succeeds under `MongoQueryMode.NativeOnly`** (today it throws). Include a variant with a root-property `Where` and one with a root-property `OrderBy`.

- [ ] **Step 2: Run to verify it fails.** Run: `MONGODB_URI=mongodb://localhost:27017/?replicaSet=rs0 ATLAS_URI=Disabled dotnet test tests/.../FunctionalTests/*.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedReferenceWholeEntity"` — expect FAIL (`NativeTranslationNotSupportedException`).

- [ ] **Step 3: Make the narrow gate change.** At `rejectionSite`, apply `admitCondition`: admit the owned single-reference embedded auto-include so `Route` stays `WholeEntity` (approach A) — the smallest change that stops the rejection for exactly this shape, leaving owned-collection / non-owned-nav / TPH / owned-sub-property-predicate still rejected. No pipeline/shaper changes (owned data is embedded; existing shapers handle it).

- [ ] **Step 4: Run to verify it passes.** Re-run Step 2's command — expect PASS. Also confirm an owned-collection / non-owned-nav entity STILL throws under `NativeOnly` (add a quick guard test) so the admit-set stayed narrow.

- [ ] **Step 5: Build all three EF versions.** `for c in "Debug EF8" "Debug EF9" "Debug EF10"; do dotnet build MongoDB.EFCoreProvider.sln -c "$c" 2>&1 | tail -1; done` — all build.

- [ ] **Step 6: Commit.** `git add -A && git commit -m "EF-322: owned single-reference whole-entity goes native (gate admits embedded owned auto-include)"`

- [ ] **Step 7: STOP for review.**

---

### Task 3: Parity + edge-case coverage, and update the flipped tests

**Files:**
- Test: `NativeOwnedReferenceWholeEntityTests.cs` (parity + edge cases).
- Modify: the flipped tests from Task 1's `flips` list (`NativeMaterializerNullabilityTests` owned cases, `NativeGateRoutingTests` owned cases, and any Northwind spec overrides).

**Interfaces:**
- Consumes: the now-native shape (Task 2); `flips` (Task 1).

- [ ] **Step 1: Write the parity + edge-case tests.** For an owned single-reference entity, assert `Native == DriverLinq` (same result set, compared deterministically under an `OrderBy` on a root key) for: present owned sub-doc; absent/null owned sub-doc; a **required** owned reference missing (both modes throw the SAME exception); a **nested** owned reference (if `nestedCovered`); a **shared-type** owned reference; a **tracked** query (entities tracked + a mutate/`SaveChanges` round-trip); and a streamed-vs-DOM equality check. Use plain xUnit `Assert.*`.

- [ ] **Step 2: Run the new tests.** Run the `~NativeOwnedReferenceWholeEntity` filter — expect PASS. (If `nestedCovered` is false, assert the nested case still falls back cleanly and file it as a follow-on.)

- [ ] **Step 3: Update the flipped tests — verify correctness, don't just delete throws.** For each test in `flips`, change the assertion from "throws / falls back" to "goes native and returns correct data", verifying the newly-native result equals the expected/seed data (use the `Native == DriverLinq` oracle). Do NOT merely remove a `Throws` assertion.

- [ ] **Step 4: Run the updated flipped tests.** Run the affected classes (`~NativeMaterializerNullability`, `~NativeGateRouting`, plus any spec class touched) under EF10 — expect PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "EF-322: owned-ref whole-entity parity/edge-case tests + update flipped fallback assertions"`

- [ ] **Step 6: STOP for review.**

---

### Task 4: Three-version validation, NativeOnly re-baseline, AGENTS.md as-built

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (as-built note: owned single-reference whole-entity now native + streams; the narrow admit-set; still-deferred owned shapes).
- Possibly: `docs/native-query-status-EF-322.md` (update the NativeOnly count if it tracks it).

**Interfaces:** none (validation + docs).

- [ ] **Step 1: Full 3-version `/test-all` (controller runs foreground).** Invoke `/test-all` (EF8/EF9/EF10, per-version isolated testcontainers). Expect all green; record per-version counts.

- [ ] **Step 2: Re-baseline the `NativeOnly` sweep.** Run the EF10 `NativeOnly` spec sweep (`MONGODB_EF_NATIVE_ONLY=1`); record the new pass/fail/skip and confirm the delta vs the pre-slice baseline (2192/2397/19) is exactly the owned-ref whole-entity shapes that flipped native — nothing unexpected.

- [ ] **Step 3: Write the AGENTS.md as-built note + commit.** Document: owned single-reference whole-entity now native (gate admits the embedded owned auto-include; approach A, no pipeline stage) and streams via the Phase-1 one-pass path; the narrow admit-set (single ref [+ nested if covered]; excludes owned collection / non-owned nav / TPH / owned sub-property predicate); the eligibility-set change (NativeOnly re-baselined, not a break). `git add -A && git commit -m "EF-322: owned-ref whole-entity as-built note + NativeOnly re-baseline"`

- [ ] **Step 4: STOP for review.** Report 3-version results + the NativeOnly delta. After approval: whole-branch review, then squash + push per the stacked-PR workflow (keep a `-presquash` backup; no push without the user's go).

---

## Self-Review

- **Spec coverage:** §1 problem → Task 1 (pin trigger) + Task 2 (gate change); §2 success/parity → Task 3; §4 spike → Task 1; §5 gate change → Task 2; §6 eligibility change / flips → Task 3 (update flips) + Task 4 (re-baseline); §7 non-goals → Global Constraints (narrow admit-set) + Task 2 Step 4 (guard test); §8 testing → Tasks 3–4.
- **Placeholder scan:** Task 2's exact edit site is intentionally spike-determined (`rejectionSite`/`admitCondition` are concrete Task-1 outputs, named as interfaces) — this is a spike-led plan, not a vague requirement; the tests and admit-set boundary are concrete.
- **Type/interface consistency:** `rejectionSite`/`admitCondition`/`nestedCovered`/`flips` produced by Task 1 are consumed by Tasks 2–3; the fixture entity from Task 2 is reused in Task 3.
- **Delivery note:** if the spike returns `approachAWorks = false`, Task 2 pivots to approach B (explicit owned-include handling) and this plan's Task 2 is re-written before proceeding.
