# Native owned single-reference whole-entity — design

*Epic EF-322 (native LINQ query provider). Phase 2 of the materializer work, slice 1.*
*Branch `EF-322-owned-ref-whole-entity-native`, stacked on the native tip `e38587f` (`origin/NativeQueryOngoing`).*
*A JIRA number should be filed; this doc will be updated with it.*

---

## 1. Problem

A whole-entity query over an entity that has an **owned single-reference navigation** (e.g. `Blog` owns
`Address`) — `ctx.Blogs.ToList()`, or with a `Where`/`OrderBy` on the blog's own root properties —
currently **falls back to driver-LINQ**, even under `Native` mode. EF auto-includes the owned navigation,
producing an `IncludeExpression`/operator the native slot population does not accept, so the gate marks the
query non-representable (`Route → Fallback`). Confirmed by the note at
`tests/.../Query/NativeMaterializerNullabilityTests.cs:206` ("an owned-reference query is NOT yet routed to
the native/streaming path — the gate marks it non-representable and falls back to driver-LINQ even under
Native mode").

This is the **largest current gap in the SP7 one-pass materializer's reach**: because owned-nav
whole-entity is not native, the Phase-1 one-pass path never fires for it, so the streaming win covers only
*flat* whole-entity today.

**Key insight: the downstream is already built.** Owned data is *embedded*, so:
- the native `{}`/filter/sort pipeline already returns the whole document, owned sub-documents included —
  **no `$lookup`/stage is needed**;
- `StreamingEligibility.IsEligible` **already admits** owned single-references (recursively);
- `MongoStreamingEntityMaterializerRewriter` **already materializes** owned reference sub-documents (built,
  currently dormant because nothing native reaches it), and the DOM shaper materializes them too.

So the fix is a **gate/translation change** — stop rejecting the auto-included owned nav — after which the
existing pipeline, DOM shaper, and (via Phase 1) the one-pass streaming shaper all just work.

## 2. Goal & success criteria

**Goal.** Make a whole-entity query over an entity with **owned single-reference** navigation(s) go native,
and therefore stream via the Phase-1 one-pass materializer.

**In scope:** the whole-entity query with no filter, or `Where`/`OrderBy` on the **root** entity's own
properties. Owned single-reference navs, including (spike to confirm) a nested owned reference that itself
owns a further reference — aligned with what `StreamingEligibility` already admits.

**Success bar:**
- The shape goes native (succeeds under `MongoQueryMode.NativeOnly`) and streams (the one-pass path fires).
- **`Native` results equal `DriverLinq` results** (there IS a driver-LINQ oracle for owned whole-entity) —
  across the owned edge cases: present owned sub-doc, absent/null owned sub-doc, a **required** owned
  reference (missing → same throw as driver-LINQ / DOM), a nested owned reference, and a shared-type owned
  reference.
- No-tracking **and** tracked both correct (tracked round-trips).
- Zero regressions across EF8 / EF9 / EF10.

**This changes the native/streaming eligibility set** (unlike Phase 1) — see §6.

## 3. Approach

**A — minimal gate acceptance (recommended).** Identify where the auto-included owned-nav
`IncludeExpression` is marked non-representable, and stop rejecting it **when the only non-native aspect is
an owned (embedded) single-reference nav auto-include**. `Route` stays `WholeEntity`; the `{}`/filter/sort
pipeline is unchanged (owned data is already in the document); the DOM shaper and the one-pass streaming
shaper materialize the owned sub-document with no new machinery. Narrow: an entity that *also* has an owned
collection or a non-owned navigation still falls back (deferred).

**B — explicit native owned-include IR** (analogous to the owned-`SelectMany` path): build a dedicated
translation path for the owned-nav include. Rejected as unnecessary — embedded owned data needs no pipeline
stage; A reuses the existing pipeline/shaper unchanged. Kept as a fallback only if the spike shows A cannot
be made safe (e.g. the include machinery cannot be cleanly admitted without an IR change).

**C — all owned shapes at once** (collections + nested + owned sub-property predicates): out of scope; each
is a separate follow-on slice.

## 4. Slice 0 — throwaway de-risking spike

A throwaway branch, discarded after a written findings doc. It must settle:
- **The exact gate-rejection trigger** for owned-ref whole-entity — *where* `MarkNotNativelyRepresentable`
  fires for the auto-included owned nav (the `NativeSlotPopulator` catch-all seeing the include operator?
  nav-expansion? a specific `IncludeExpression` check?), so Slice 1 changes the right, narrowest place.
- **That approach A is correct end-to-end:** with the rejection removed, does the existing DOM shaper and
  the one-pass streaming shaper produce entities equal to the driver-LINQ oracle, across the §2 edge cases
  (present / absent-null / required-missing-throws / nested owned ref / shared-type owned)? Does it stream
  (`NativeOnly` succeeds)?
- **The blast radius of the eligibility change:** enumerate the spec/functional tests that flip from
  asserting fallback to asserting native (`NativeMaterializerNullabilityTests` owned cases,
  `NativeGateRoutingTests` owned cases, Northwind spec tests over owned-nav entities).
- **The precise admit-set:** which owned shapes A safely admits (single ref; nested single ref; shared-type)
  and which must still be excluded (owned collection present on the same entity; non-owned nav present; TPH;
  owned sub-property predicate — the separate translator gap), aligned with `StreamingEligibility`.

**Gate:** if A proves unsafe (the include cannot be admitted without wrong data or a shaper mismatch), fall
back to approach B and re-scope.

## 5. Slice 1 — the gate change

Informed by the spike: make owned single-reference whole-entity native at the identified site, admitting
exactly the spike's safe owned-ref set and no more. Deliverables:
- The narrow gate change (approach A).
- Functional tests: `Native↔DriverLinq` parity across the §2 edge cases; a `NativeOnly` success assertion
  (proves native + streaming); a tracked round-trip; a streamed-vs-DOM equality check.
- Update the flipped tests (owned cases that asserted fallback now assert native) **with correctness
  verification** — not just deleting the throw assertion.
- `#if`-clean across EF8/EF9/EF10; all touched types `internal`.

## 6. This changes the eligibility set — handling the flips

Unlike SP7 (materialization-only), this makes a **new query shape native**, so the `NativeOnly` spec
pass-set **will change** (owned-ref whole-entity shapes move fallback→native; the pass count rises, the
fallback count drops). Per the provider's versioning rubric this is **not a breaking change** — a
hard-throw/graceful-fallback shape becoming native with unchanged results is explicitly non-breaking
(native default is not a break; MQL is not contract). But it must be handled deliberately:
- Every flipped test is updated to assert the new native behavior **and** verified to return correct data
  (the `Native↔DriverLinq` oracle makes this checkable).
- The `NativeOnly` sweep is re-baselined (record the new pass/fail/skip; confirm the delta is exactly the
  owned-ref whole-entity shapes, nothing unexpected).

## 7. Non-goals

- Owned **collection** whole-entity, **nested-beyond-what-the-spike-admits** owned refs, TPH-with-owned —
  separate follow-on slices.
- Owned **sub-property predicates/sorts** (`Where(e => e.Address.City == …)`) — a distinct *translator* gap
  (dotted owned field-path rendering), explicitly deferred.
- **Reference (cross-collection) Include** streaming — a different Phase-2 item (the dormant `$lookup`
  machinery), not this slice.
- No change to the one-pass materializer itself (Phase 1) beyond letting it now be *reached* for owned-ref
  shapes.

## 8. Testing & verification

- **Parity + edge cases:** `Native == DriverLinq` for owned-ref whole-entity across present / absent-null /
  required-missing-throw / nested owned ref / shared-type owned; no-track and tracked.
- **Proves native + streams:** `NativeOnly` succeeds for the shape.
- **Flips handled:** the updated tests assert native with verified-correct data; the `NativeOnly` spec
  sweep re-baselined with the delta explained.
- **Full `/test-all` EF8/EF9/EF10 green** (foreground, per-version isolated testcontainers).
- No perf claim here (this is coverage, not perf) — but the shape now benefits from the Phase-1 one-pass
  win once native.

## 9. Open questions (resolved by the spike)

- Exactly where/why the owned-nav auto-include is marked non-representable, and the narrowest place to
  admit it.
- Whether nested owned references (owned ref that owns a further ref) are covered by the same gate change
  or need a follow-on.
- Whether an entity carrying BOTH an owned ref and an owned collection (or a non-owned nav) can be partially
  admitted or must wholesale fall back (expected: fall back until the respective follow-on slices).
