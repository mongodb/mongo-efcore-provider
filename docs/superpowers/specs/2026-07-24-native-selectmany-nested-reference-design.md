# Native nested (2-level) reference `SelectMany` (EF-347)

## Summary

Make a **nested, two-level, cross-collection reference** `SelectMany` go native:

```csharp
from o in db.Owners from r in o.Refs from x in r.SubRefs
select new { o.Name, r.Tag, x.Label };
```

Both `o.Refs` and `r.SubRefs` are cross-collection (non-owned) reference collection
navigations. Today the SECOND `SelectMany` hits `TranslateSelectMany`'s
`HasTerminalOperator` guard (the first `SelectMany` already set `UnwindSource`) and returns
`null`, so the whole query hard-fails. This slice makes it emit two chained
`$lookup`+`$unwind` stages, for a projected result and a bare-entity result.

This is the largest remaining slice in the EF-347 SelectMany tail. It is stacked on the
current tip `4fa2162` (owned correlated-beyond-outer).

## Scope

**In scope — goes native:** exactly TWO levels of chained cross-collection reference
`SelectMany`, unfiltered, for:
- a **projected** result across all three scopes (`new { o.Name, r.Tag, x.Label }` / DTO), and
- a **bare-entity** result (`select x` — the level-2 leaf entity).

**Out of scope / unchanged (still deferred):**
- Three-or-more levels of nesting (a third chained `SelectMany` still hard-fails).
- Owned or mixed nesting (owned-in-owned, owned-in-reference, reference-in-owned).
- Any inner `.Where` filter or correlation-beyond-FK on either level.
- A computed projection leaf.

## Spike (done)

Findings: `.superpowers/sdd/EF-347-nested-ref-spike.md`. EF8/9/10 byte-identical. Verdict:

1. **Chained, not rewritten.** EF keeps it as TWO sequential `TranslateSelectMany` (2-arg)
   calls — no `Join`/`GroupJoin`/`LeftJoin`.
2. **Level 1** is the ordinary single-level correlated-subquery shape
   (`Where(EntityQueryRootExpression<Mid>, o => o.Id == m.OwnerId)`) with a trivial
   `TransparentIdentifier(Outer=o, Inner=m)` result selector — a direct reuse of the existing
   `TryBindReferenceNavUnwind`/`NativeCorrelationMatcher`.
3. **Level 2** is `Where(EntityQueryRootExpression<Leaf>, ti => ti.Inner.Id == l.MidId)` — the
   SAME correlated-subquery structure, but the correlation LHS is a **two-hop
   transparent-identifier member access** `ti.Inner.Id` (where `ti` is level-2's own outer
   parameter, bound to level-1's `TransparentIdentifier(Outer, Inner)` result, and `.Inner` is
   the unwound Mid element from level 1). Its result selector is trivial again, so the final
   result would be a **doubly-nested** `TransparentIdentifier(Outer=ti, Inner=leaf)`.
4. **Final projection** was never reached live (level-2 hits the terminal guard first);
   inferred shapes: projected `ti.Outer.Outer.Name` / `ti.Outer.Inner.Tag` / `ti.Inner.Label`;
   bare-entity `ti.Inner` (the leaf).
5. **Tractability:** level 1 reuses existing machinery; level 2 needs two new bits — (a) the
   terminal guard becomes a conditional carve-out, and (b) correlation recognition must accept a
   transparent-identifier-rooted PK chain (`ti.Inner.Id`) as the correlation LHS. No
   feasibility-changing surprises.

## Approach

### 1. Unwind-chain IR

`MongoSelectDefinition.UnwindSource` (a single slot) becomes an ordered `UnwindSources` list.
The ~50 existing single-source read sites (24 in QMTEV: `UnwindSource != null`,
`is { WholeElement: true }`, `.InnerEntityType`, etc.) keep working via a **last-source shim**:
- `UnwindSource => UnwindSources.LastOrDefault()` (a computed property preserving the old name),
- `HasTerminalOperator` uses `UnwindSources.Count > 0` instead of `UnwindSource != null`.

This is behavior-preserving for every single-level shape, because every current consumer cares
about the **terminal** (last) unwind source. Level-1 bind appends the first source; level-2 bind
appends the second. The exact list-vs-shim mechanics (whether to keep `UnwindSource` settable for
back-compat or route all writes through an `AddUnwindSource`) are finalized in the plan; the
invariant is that single-level behavior is byte-identical.

### 2. Terminal-guard carve-out

`TranslateSelectMany`'s `HasTerminalOperator` guard
(`MongoQueryableMethodTranslatingExpressionVisitor.cs:1444`) is relaxed for exactly one case: a
second **reference** `SelectMany` whose collection selector correlates off the
immediately-preceding `SelectMany`'s own unwound element (recognized structurally by the level-2
recognizer below). The carve-out is narrow: it fires only when the sole terminal so far is a
reference unwind chain of length 1 and the new collection selector is a recognized nested
reference correlation. Everything else composed after a `SelectMany` — a third level, a
`Where`/`GroupBy`/`Distinct`/set-op, an owned nesting — still hits the guard and hard-fails,
exactly as today.

### 3. Level-2 correlation recognition

Extend the correlation recognition (a new sibling to, or a generalization of,
`NativeCorrelationMatcher.TryMatchCorrelatedCollection`) to accept a correlation whose LHS is a
**transparent-identifier-rooted PK chain** `ti.Inner.<pk>` — the level-1 unwound element's key —
rather than only a bare `ParameterExpression`. Recognition is identity-based: the `ti` parameter
plus the `.Inner` accessor identify the prior unwound scope; the `<pk>` member resolves against
the level-1 target (Mid) entity type. On a match, it resolves the level-2 reference nav (Leaf),
registers a second `ForceUnwind` `LookupExpression` correlated off the level-1 unwind scope
(localField `_lookup_Mids._id`, foreignField `MidId`, `as _lookup_Leaves`), and appends a second
`Reference` `MongoUnwindSource` (scope `_lookup_Leaves`, inner entity type Leaf). Mutation happens
only after the match is confirmed (no-partial-mutation invariant).

### 4. Chained lowering

`MongoSelectLowerer` emits the unwind sources in list order, reusing the existing per-source
`AppendLookupStages`: `$lookup(Mids)` → inner-join `$unwind(_lookup_Mids)` →
`$lookup(Leaves, localField `_lookup_Mids._id`)` → inner-join `$unwind(_lookup_Leaves)` → the
projection `$project` (projected form) or plain `$replaceRoot{newRoot: "$_lookup_Leaves"}`
(bare-entity form). `$lookup` `localField` supports the dotted `_lookup_Mids._id` path. Both
`$unwind`s use `preserveNullAndEmptyArrays: false` (inner-join `SelectMany` semantics — a Mid with
no Leaves, or an Owner with no Mids, contributes no rows).

### 5. N-scope projection binder

Generalize `TryBindTransparentIdentifierProjection` (today two-scope: `ti.Outer`/`ti.Inner`) to
walk the doubly-nested transparent identifier and map accessor-chain depth to the right scope:
- `ti.Outer.Outer.<m>` → owner scope, document root (`$Name`),
- `ti.Outer.Inner.<m>` → level-1 scope, `$_lookup_Mids.<elem>`,
- `ti.Inner.<m>` → level-2 scope, `$_lookup_Leaves.<elem>`.

Each leaf's scope is determined by counting its `Outer`/`Inner` accessor chain against the unwind
chain (depth 0 = owner root; each additional unwind source adds a prefix), then translated against
that scope's entity type and prefixed with that scope's path — the same structurally-separate,
identity-routed pattern the two-scope binder already uses, extended from 2 to N scopes.

### 6. Bare-entity result

`select x` (a bare level-2 leaf) reuses the reference **whole-element** path over the **last**
unwind source: `IsWholeElementRepresentable(leafType, Reference)` gate, plain
`$replaceRoot{newRoot: "$_lookup_Leaves"}` (`mergeOwnerKeySentinels: false` — the leaf is an
ordinary keyed entity), and the standard DOM shaper rooted at the leaf entity type. Tracking works
with no `AsNoTracking()` (the leaf is a normal top-level entity, not owned). The whole-OUTER shapes
(`select o` / `select r`) keep declining, as for the single-level reference case.

## Decline & no-oracle

A cross-collection `SelectMany` has no driver-LINQ oracle, so every decline — a third level, an
unsupported nested shape, a whole-outer result — hard-fails in every mode
(`Native`/`DriverLinq`/`NativeOnly`). Native successes are proven via `MongoQueryMode.NativeOnly`
succeeding plus an expected in-memory result set, not `Native == DriverLinq` parity.

## Not a breaking change

All touched types are `internal`. The `UnwindSource` → `UnwindSources` change keeps the
`UnwindSource` name as a last-source shim, and single-level behavior is byte-identical (verified by
the existing SelectMany suite staying green). The change is hard-fail → native for a
previously-unsupported shape; results for supported shapes unchanged; emitted MQL is not contract.
Identical EF8/9/10 (no `#if`).

## Files (anticipated)

- `src/…/Query/Expressions/MongoSelectDefinition.cs` — `UnwindSources` list + `UnwindSource`
  last-source shim + `HasTerminalOperator` on `Count > 0`; an `AddUnwindSource` writer.
- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` — level-2 nested-reference recognition
  (transparent-identifier-rooted PK correlation); append the second unwind source.
- `src/…/Query/NativeTranslation/NativeCorrelationMatcher.cs` — accept a
  transparent-identifier-rooted PK chain as the correlation LHS (or a new sibling matcher).
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — the terminal-guard
  carve-out for the nested reference case; wire level-2 into `TranslateSelectMany`; the N-scope
  `TryBindTransparentIdentifierProjection` generalization; the doubly-nested unwrap `Select`.
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — emit the unwind sources in list order.
- `src/…/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — whole-element shaper over
  the last unwind source (bare-entity) — mostly reused, kind-agnostic.
- `src/…/Query/AGENTS.md` — as-built note.
- `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` — a 3-entity nested fixture
  (`NestOwner`/`NestMid`/`NestLeaf`) + projected / bare-entity / zero-children-per-level /
  parametrized / MQL tests.
- `tests/…/UnitTests/Query/NativeTranslation/…` — chain IR, level-2 recognition, N-scope binder.

## Verification

- Full 3-version `/test-all` (EF8/EF9/EF10) GREEN 0-fail before squash — controller runs it
  foreground, summing all three per-assembly blocks; the existing single-level SelectMany suite
  staying green is the last-source-shim additive proof.
- `MongoQueryMode.NativeOnly` spec sweep — no regressions vs the `4fa2162` baseline.
- Subagent-driven development, one task at a time, **stop for review after every task**.
- Final opus whole-branch review; squash to one commit above `4fa2162` with a presquash backup;
  the user drives the fast-forward push of `origin/NativeQueryOngoing` (`4fa2162` → new tip).
