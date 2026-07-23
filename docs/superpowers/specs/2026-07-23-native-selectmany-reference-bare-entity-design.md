# EF-347 — Native bare whole reference-entity `SelectMany` result

Date: 2026-07-23
Branch: `EF-347-selectmany-ref-bare-entity` (stacked off `origin/NativeQueryOngoing` tip `dfda01e`)
Epic: EF-322 native LINQ query rewrite · Ticket: EF-347 (remaining SP6 relational operators)

## Summary

Make a **bare whole reference-entity** result from `SelectMany` over a **cross-collection
reference collection** navigation go native — the canonical
`from c in q from o in c.Orders select o` (and equivalents), where `Orders` is a **non-owned,
FK-joined** collection navigation (`HasMany().WithOne().HasForeignKey()`) and the query returns
the whole joined `Order` entity, not a projection.

Today this exact shape throws `NotSupportedException` at translation time in **every**
`MongoQueryMode` (`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`, the
whole-inner-entity decline at lines ~314-345, which admits only `Kind == Owned`). This slice
extends that gate to also admit the **reference** kind, routing the whole-inner reference-entity
result through the `$lookup` → inner-join `$unwind` → `$replaceRoot` pipeline plus the standard
root-level entity shaper for the unwound element.

This is the direct cross-collection analogue of the just-completed **bare owned-collection-element**
slice (`dfda01e`, `2026-07-23-native-selectmany-bare-owned-element-design.md`) and the deferred
"bare-entity result" follow-up called out by the **projected reference SelectMany** slice
(slice 5, `afb4964`, `2026-07-16-native-selectmany-reference-projected-design.md`). It reuses the
`$lookup`+`$unwind` machinery from slice 5 and the `WholeElement` recognition/shaper machinery from
the owned-element slice — the one seam both left explicitly open ("`WholeElement` is only ever set
for Owned in this slice (reference is deferred)").

## Scope

### In scope

A bare whole reference-collection **entity** result, native, in all three user spellings that EF's
nav-expansion normalizes to the identical tree (`UnwindSource` set with `Kind == Reference`,
`Projection` empty, trailing selector `ti => ti.Inner`):

- `q.SelectMany(c => c.Orders)` (1-arg)
- `from c in q from o in c.Orders select o` (query syntax)
- `SelectMany(c => c.Orders, (c, o) => o)` (explicit result selector, bare inner)

`Orders` is a **cross-collection reference (non-owned) single-level collection navigation**,
FK-correlated (the FK correlation only — no extra user filter). **Terminal-only.** DOM
materialization, consistent with every prior SelectMany slice.

**Tracking is supported and tested.** Unlike an owned collection element (which EF Core refuses to
track without its owner — the owned slice is `AsNoTracking`-only), a reference `Order` is a real,
independently-trackable entity with its own stored primary key, so a tracking query returns tracked
`Order` instances. Delivering and verifying this is part of the slice (spike-confirmed — see Task 1).

### Explicitly deferred (unchanged by this slice)

- **`select c`** — the whole **outer** (principal) entity, replicated once per child (trailing
  selector `ti => ti.Outer`). Different shaper (the principal document, no `$replaceRoot`). Keeps
  throwing the (already narrowed) `NotSupportedException`.
- **Reference entity with an EAGER-LOADED (auto-included) navigation** — a reference `Order` that
  itself owns/eager-loads a further navigation declines cleanly (the same re-rooted-`ProjectionMember`
  limitation the owned slice hit — an auto-included nav reaches EF's `IncludeExpression` machinery and
  binds against the wrong projection; tracked as the owned slice's EF-353 follow-up, shared root
  cause). **A plain (lazy) inverse back-reference navigation is NOT deferred** — it materializes as
  null (it is never auto-included) and goes native; this is the common reference shape (the primary
  fixture's `RefItem.Owner` back-reference), so the nav guard MUST be narrowed for reference to reject
  only eager-loaded navs, not blanket-reject any navigation. (Spike-confirmed — Task 1.)
- **Filtered / correlated-beyond-FK inner** (`c.Orders.Where(userPred)` — an extra predicate
  conjunct beyond the FK correlation), **computed projection leaf**, **nested reference SelectMany**
  — all keep their current behavior (slice 5 defers these; unchanged here).
- **Any operator composed after** the bare-entity reference SelectMany — keeps hard-failing via the
  existing SelectMany-after-terminal guard (`TranslateSelectMany` returns `null` when
  `HasTerminalOperator`; committed as `4e30ad2`).

### Behavior change — additive, not a break

The whole-inner reference-entity shape hard-failed (`NotSupportedException`) in every mode before
this slice; it now goes native with correct results. Per the versioning rubric (AGENTS.md):
hard-fail → native, with unchanged query results, is not a breaking change, and the emitted MQL is
not part of the contract. All touched types are `internal`.

## Approach (Approach A — reuse the owned `WholeElement` mechanism, add a Reference arm)

Chosen over B (a bespoke reference-entity shaper / separate route — more code, no reuse) and C (no
`$replaceRoot`; materialize the reference entity in its nested `_lookup_<Nav>` slot — not the
natural MongoDB shape, needs a bespoke nested-path shaper, diverges from the owned slice). B is the
pivot **only if** the Task 1 spike shows the standard root shaper cannot materialize a reference
entity from root.

The reference case is *simpler* than the owned case it mirrors: an owned collection element has a
**shadow key** (owner FK + synthesized ordinal, not stored in the document), which forced the owned
slice's `$mergeObjects` sentinel dance (`__ownerKey`/`__ord`, `includeArrayIndex`) to carry a
non-null key past EF Core's null-owned-key guard. A reference `Order` has a **real stored primary
key** in its own collection, so none of that is needed — a plain `$replaceRoot` suffices and the
standard shaper reads the key from root the ordinary way.

### 1. Recognition — QMTEV

`NativeSelectManyBinder.TryBindReferenceNavUnwind` already fires for the bare reference-nav
collection selector (it recognizes the FK-correlated `Queryable.Where(EntityQueryRoot, …)` subquery
purely on structure, selector-independent — exactly as the owned `TryBindBareNavUnwind` does), sets
`MongoUnwindSource` with `Kind == Reference` and `InnerScopePath == "_lookup_<Nav>"`, registers the
`ForceUnwind` `LookupExpression`, and leaves `Projection` empty. `BuildBareNavWrappedShaper` builds
the `TransparentIdentifier(Outer, Inner)` wrapped shaper (the `Inner` shaper being a
`StructuralTypeShaperExpression` over the reference target entity type).

The change is at the whole-inner-entity gate in `TranslateSelect` (lines ~314-345). Today:

```csharp
if (wholeEntityMember is { Member.Name: "Inner" }
    && wholeElementCandidateUnwind.Kind == MongoUnwindSourceKind.Owned      // ← Owned only
    && IsWholeElementRepresentable(wholeElementCandidateUnwind.InnerEntityType))
{
    wholeElementCandidateUnwind.WholeElement = true;
}
```

Relax the `Kind` check to admit `Reference` as well (both `Owned` and `Reference`). On success set
`WholeElement = true` and fall through to the generic shaper fold (which resolves
`TransparentIdentifier(outer, item).Inner` → the element shaper). The whole-**outer**
(`ti => ti.Outer`) and any other non-`Inner` whole-entity trailing selector keep throwing the
(already narrowed) `NotSupportedException` via the `else if` branch — no change to that branch.

`Route` stays `NativeRoute.Projection`/`WholeEntity` as it already resolves for a reference
`UnwindSource` (slice 5); `WholeElement` only tells the lowerer to emit `$replaceRoot` and (via
`UnwindSource != null`'s membership in `HasTerminalOperator`) keeps the shape terminal-only. No new
gate/disposition signal.

### 2. Lowering — plain `$replaceRoot` variant

For a `Reference` + `WholeElement` source, slice 5's `AppendLookupStages` already emits the
`$lookup` + inner-join `$unwind` (`MongoLookupStage` + `MongoUnwindStage(preserveNullAndEmptyArrays:
false)`) ahead of the `UnwindSource` block. The `UnwindSource` block then appends a
`MongoReplaceRootStage` that promotes the unwound element to root:

```
{ $replaceRoot: { newRoot: "$_lookup_<Nav>" } }
```

— a **plain** `$replaceRoot`, no `$mergeObjects`/sentinels (the reference entity carries its own
real `_id` and fields).

- `MongoReplaceRootStage` gains a mode discriminator: the existing **sentinel-merge** form (Owned —
  `$mergeObjects` with `__ownerKey`/`__ord`) vs. a new **plain** form (Reference — bare
  `{ newRoot: "$<path>" }`). Simplest: a `bool MergeOwnerKeySentinels` (or a `Kind`) on the stage,
  defaulting to the existing owned behavior; `MongoPipelineFactory` renders the plain arm when it is
  not set.
- The lowerer's `UnwindSource` block emits `MongoUnwindFieldStage` for `Owned` only (unchanged); for
  `WholeElement` it appends the appropriate `MongoReplaceRootStage` — sentinel-merge for `Owned`,
  plain for `Reference`. The projected reference SelectMany path (`Projection` populated,
  `WholeElement` false — slice 5) is unaffected: no `$replaceRoot`, still `$lookup`→`$unwind`→
  `$project`.

### 3. Materialization + tracking (spike-gated) — the crux

After `$replaceRoot`, the reference `Order` is the root document. `MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery`'s `WholeElement` branch is **already
kind-agnostic** — it roots the standard `MongoProjectionBindingRemovingExpressionVisitor` at
`wholeElementUnwind.InnerEntityType` (the `Order` type) with `allowStreaming: false`, regardless of
Owned/Reference. And `CreateGetValueExpression`'s owner-key/ordinal sentinel read is gated on
`property.IsOwnedTypeKey()`, which a reference primary key does **not** satisfy — so a reference
entity reads its `_id` and properties from root through the ordinary property path, with **no
sentinel handling**. So materialization is expected to need **no production change** beyond the
lowering/recognition above.

**Task 1 is a spike** to confirm this against the real code before any production change:

- The standard root shaper materializes a reference entity from the re-rooted document (Approach A
  holds), or a bespoke re-rooted reference shaper is needed (pivot to B).
- **Tracking semantics** — that a *tracking* query for this shape returns tracked `Order`
  instances (the new capability vs. owned), and whether any provider action is required. (Reference
  entities are ordinary root/dependent-of-a-different-kind entities with real keys, so EF should
  track them without the owned-entity restriction; the spike confirms.)
- The `$lookup` → `$unwind` → `$replaceRoot` composition (that `_lookup_<Nav>` survives the
  `$unwind` as a single sub-document `$replaceRoot` can promote).
- The driver-LINQ fallback behavior (slice 5 established reference SelectMany has **no** driver-LINQ
  oracle — the driver's LINQ v3 provider rejects cross-collection SelectMany — so this shape
  hard-fails in every mode when out of scope; the spike re-confirms for the bare-entity form).

The spike outcome confirms Approach A or pivots to B. Either way lowering and recognition are
unchanged.

### 4. Representability guard — `IsWholeElementRepresentable`

Four guards today (all owned-motivated): (1) the element has **no navigations of its own** (blanket);
(2) no real property whose element name collides with the `__ownerKey`/`__ord` sentinel fields; (3)
the same for complex-property element names; (4) every owned-key property has default serialization.

- **Narrow guard (1) for reference to EAGER-LOADED navigations only.** The owned crash the blanket
  guard prevents is specifically an **auto-included** nested navigation reaching EF's
  `IncludeExpression` machinery (owned navs are always eager-loaded). A reference entity's plain
  inverse back-reference (`RefItem.Owner`) is **not** eager-loaded, is never materialized without an
  explicit `Include`, and shapes fine as null — so the blanket `!GetNavigations().Any()` would
  wrongly decline the common reference entity (which almost always carries the inverse nav). For
  reference, reject only when the element has an **eager-loaded** navigation
  (`innerEntityType.GetNavigations().Any(n => n.IsEagerLoaded)`); the owned path keeps its blanket
  check (every owned nav is eager-loaded anyway, so the two are equivalent for owned, and keeping the
  blanket form there is the minimal, lowest-risk change). Spike-confirmed (Task 1) that a bare
  reference entity with a lazy inverse nav materializes correctly.
- **Skip guards (2)/(3)/(4) for reference** — they exist only to protect the owned `$mergeObjects`
  sentinel merge and the synthesized owner/ordinal shadow keys. Reference merges **no** sentinels and
  has **no** owned-type shadow keys, so a reference property named `__ownerKey`/`__ord` is harmless
  and a reference PK's serialization is read the ordinary way — these must **not** falsely decline a
  reference entity.

`IsWholeElementRepresentable` therefore becomes **kind-aware**: pass the `MongoUnwindSourceKind` in
(or split into a shared entry that applies the eager-loaded-nav check for both kinds and the
owned-only sentinel/shadow-key checks for `Owned`). The nav check differs by kind (blanket for owned,
eager-loaded-only for reference); the sentinel/shadow-key checks apply for `Owned` only.

Both guards, where they apply, fail closed into the SAME `NotSupportedException` the whole-outer
decline throws, at translation time, in every `MongoQueryMode`.

## Verification

**No driver-LINQ oracle.** Reference SelectMany hard-fails in every mode today AND the driver's own
LINQ v3 provider rejects cross-collection SelectMany (established by slice 5). Correctness is proven
**against expected in-memory results under `MongoQueryMode.NativeOnly`** — the same no-oracle
pattern as projected reference SelectMany (slice 5), the owned bare-element slice, and
Intersect/Except (set-ops slice A): assert the native query succeeds under `NativeOnly` and its
result set equals the expected joined entities.

Functional tests (`tests/.../FunctionalTests/Query/NativeSelectManyTests.cs`), reusing slice 5's
`RefOwnerItemDbContext` fixture:

- All three spellings (1-arg, query-syntax, explicit-result-selector) return the flattened
  reference-entity set, native under `NativeOnly`.
- A principal with **zero children** contributes zero rows (inner-join semantics —
  `preserveNullAndEmptyArrays: false`, as slice 5 already emits).
- A shared outer/inner member name proves the element reads root-relative (no principal-scope leak).
- **A tracking query returns tracked entities** — the notable new capability; contrast the owned
  slice's `Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode`. Assert the
  returned entities are tracked (`ChangeTracker` entries present) and that a subsequent
  `SaveChanges` on a mutated returned entity persists.
- A bare reference entity carrying a plain **lazy inverse back-reference** (the fixture's
  `RefItem.Owner`) goes native and materializes (the back-nav null) — this is implicitly the primary
  success case, since the fixture entity already has that nav.
- **A reference entity with an EAGER-LOADED navigation declines cleanly** (narrowed guard 1) in
  every mode — needs a fixture reference entity with an `[AutoInclude]`/`.AutoInclude()` navigation
  (or an owned sub-navigation) to exercise the decline.
- `select c` (whole outer) still throws (narrowed decline, unchanged).
- Composition after the bare-entity reference SelectMany still hard-fails in every mode.
- **MQL:** `$lookup` (correct `from`/`localField`/`foreignField`/`as`) → `$unwind
  "$_lookup_<Nav>"` (`preserveNullAndEmptyArrays: false`) → `$replaceRoot { newRoot:
  "$_lookup_<Nav>" }` (plain, no `$mergeObjects`).

**Suite bar:** full 3-version `/test-all` (EF8/EF9/EF10) green with 0 failures before squash, plus a
`NativeOnly` spec sweep showing the change is purely additive (zero regressions). The Northwind
SelectMany tests are cross-collection reference — this slice MAY move some bare-entity shapes to
native; confirm zero regressions either way.

## Files (anticipated)

- `Query/Expressions/MongoUnwindSource.cs` — `WholeElement` doc update (no longer "Owned-only").
- `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — admit `Kind == Reference`
  at the whole-inner-entity gate; make `IsWholeElementRepresentable` kind-aware (narrow the nav guard
  to eager-loaded navs for reference; keep the blanket nav check + all sentinel/shadow-key checks for
  owned).
- `Query/NativeTranslation/Stages/MongoReplaceRootStage.cs` — mode discriminator (sentinel-merge vs.
  plain).
- `Query/NativeTranslation/MongoPipelineFactory.cs` — render the plain `$replaceRoot` arm.
- `Query/NativeTranslation/MongoSelectLowerer.cs` — emit the plain `MongoReplaceRootStage` for
  `Reference` + `WholeElement` (owned path unchanged).
- Materializer file(s) per the spike outcome — expected **none** (sentinel read already
  `IsOwnedTypeKey()`-gated; shaper branch already kind-agnostic); Task 1 confirms.
- `Query/AGENTS.md` — as-built note under the reference SelectMany section; deferred-list update.
- `tests/.../FunctionalTests/Query/NativeSelectManyTests.cs` — functional coverage (above).

## Risks

- **Tracking semantics for a bare reference entity** (primary new risk) — resolved by the Task 1
  spike; reference entities are ordinary trackable entities, but confirm EF hands us a trackable
  `StructuralTypeShaperExpression` for this shape and that no provider action is needed. Multi-version
  (EF8/EF9/EF10) behavior must be checked (a prior slice hit an EF8-only `CS9174` / API-shape
  difference — run the full 3-version build early).
- **Reference materialization from root** — low (reuses the proven owned root shaper, sentinels
  naturally skipped); de-risked by the spike, Approach B is the pivot.
- **`MongoReplaceRootStage` mode discriminator must not regress the owned sentinel form** — the
  owned `$mergeObjects` rendering stays the default; covered by the owned slice's retained tests.
- **`IsWholeElementRepresentable` kind-conditioning** — the nav guard must be **narrowed** for
  reference (eager-loaded navs only, so the common lazy-back-reference case is NOT falsely declined)
  while the owned path keeps its blanket nav check plus all three sentinel/shadow-key checks; covered
  by retained owned tests + a new reference eager-loaded-nav-decline test + the primary success tests
  (whose fixture entity carries a lazy back-reference). **Spike (Task 1) confirms empirically that a
  bare reference entity with a lazy inverse navigation materializes correctly** before this narrowing
  is relied on.
