# Native reference-collection-nav `Count` in predicates — design

## Motivating example

`NorthwindIncludeNoTrackingQueryMongoTest.Include_with_complex_projection_does_not_change_ordering_of_projection`
(and its `NorthwindIncludeQueryMongoTest`/`NorthwindStringIncludeQueryMongoTest` siblings) run on the
driver-LINQ fallback today:

```csharp
context.Set<Customer>()
    .Include(c => c.Orders)
    .Where(c => c.ContactTitle == "Owner")
    .OrderBy(c => c.CustomerID)
    .Select(c => new { Id = c.CustomerID, TotalOrders = c.Orders.Count })
    .Where(e => e.TotalOrders > 2)
```

Under `MongoQueryMode.NativeOnly` this throws `NativeTranslationNotSupportedException: Query projects a
non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.`

## Root cause (confirmed empirically, not just by code reading)

An isolated spike (`MONGODB_EF_NATIVE_ONLY=1`, throwaway test, discarded — not part of this design) bisected
the query into pieces:

| Shape | Goes native today? |
|---|---|
| `Select(c => new { Id, TotalOrders = c.Orders.Count })` alone, no trailing `Where` | Yes |
| `Select(c => new { Id, Combo = c.CustomerID + c.City }).Where(e => e.Combo == "...")` (computed scalar member, filtered after Select) | Yes — EF Core's own query compiler substitutes the projected member back to its defining expression before the provider ever sees a projected alias |
| `Where(c => c.Orders.Count > 2)` — **no `Select` at all** | **No** |
| `Select(...).Where(e => e.TotalOrders > 2)` (the actual failing shape) | **No** — same failure as the row above, because of the same substitution behavior that made the `Combo` case work: the predicate body EF hands the provider is `Queryable.Count(Queryable.Where(Set<Order>(), correlationPredicate)) > 2`, identical to the no-`Select` case |

This rules out the two hypotheses considered and rejected during design (recorded here so the rejection isn't
re-litigated):

- **"Post-projection `Where` is unsupported in general"** — false. Only a `Where` whose predicate, after EF's
  own substitution, still contains an untranslatable sub-expression fails; the substitution itself is not the
  provider's concern.
- **"Bare (unfiltered) reference-collection `.Count` needs a new Select-leaf recognizer"** — false. The
  existing Select-leaf recognizer (`NativeProjectionBinder.TryTranslateProjectedCollectionCount`) already
  handles it, because EF's nav-expansion rewrites *any* collection-navigation `.Count`/`.LongCount` (bare or
  user-filtered) into the same `Count(Where(root, correlationPredicate))` shape — the `Where` inside is EF's
  own FK-correlation plumbing, not evidence of a user filter.

The actual gap: **`MongoExpressionTranslator`'s general predicate/comparison translation only recognizes a
collection-navigation `Count` for *owned/embedded* collections** (`TryResolveOwnedCollectionPath` requires an
embedded chain; see `MongoExpressionTranslator.cs` ~line 2007). For a *reference* (cross-document) collection
nav, there is no equivalent path outside the Select-leaf-only `TryTranslateProjectedCollectionCount`. This
blocks `Count` in **any** predicate context — a `Where`, whether or not preceded by a `Select` — not just this
one test.

`MongoExpressionTranslator` cannot fix this by itself: it holds no reference to `MongoQueryExpression` and so
cannot register the `$lookup` a reference-collection count requires. The fix has to live at the call site that
already holds `mongoQ` — mirroring exactly how the existing Select-leaf recognizer works.

## Scope

**In scope:** a reference-collection-nav `Count`/`LongCount` (unfiltered — i.e. only EF's own FK-correlation
predicate, no additional user filter) inside a `Where` predicate comparison, wherever `NativeSlotPopulator`'s
`Where` arm runs.

**Out of scope (explicitly, matching the existing Select-leaf recognizer's own boundary):**
- A genuinely user-filtered `.Where(userPred).Count()` over a reference collection (e.g.
  `c.Orders.Where(o => o.OrderDate > x).Count()`) — `NativeCorrelationMatcher.TryMatchCorrelatedCollection`
  only recognizes the pure FK-correlation equality shape, and this design does not extend that matcher's own
  matching power, only reuses it from a second call site. The existing Select-leaf recognizer has the identical
  limitation today.
- Count predicates inside `OrderBy`/`Any`/`All`/quantifier contexts other than `Where`. If a future case needs
  one of these, it reuses the same shared helper from that call site — no design change, just another wiring
  point.
- `ProjectionAliasScope` / any general "post-projection `Where`" mechanism — ruled out above; not needed.

## Design

**One new shared static helper**, living alongside `NativeProjectionBinder.TryTranslateProjectedCollectionCount`
(same file, since it reuses that method's lookup-construction and cross-leaf dedupe logic almost verbatim) —
tentatively `TryTranslateReferenceCollectionCountComparison(MongoQueryExpression mongoQ, ParameterExpression
outerParameter, Expression comparisonBody, out MongoExpression? result)`:

1. Matches `comparisonBody` as a `BinaryExpression` with a relational/equality `NodeType`, where exactly one
   operand is `TryMatchCountExpression`-shaped (the same extraction `MongoExpressionTranslator`'s owned-count
   arm already uses) and the other operand translates via the ordinary `MongoExpressionTranslator.TryTranslateValue`.
2. Requires the matched count's target navigation to resolve via
   `NativeCorrelationMatcher.TryMatchCorrelatedCollection(requireEmbedded: false)` — i.e. explicitly the
   reference-collection case; the owned case is already handled elsewhere and must not be double-matched.
3. Builds a `LookupExpression` and applies the *same* cross-leaf alias-collision dedupe rule
   `TryTranslateProjectedCollectionCount` already documents (interchangeable with an existing same-alias
   lookup of the same `PipelineKind`; declines on a kind mismatch), then commits it directly via
   `mongoQ.AddLookup(...)` (no staging list needed — this is a single top-level attempt per predicate, unlike
   the multi-leaf commit block in `TryPopulateNativeProjection`).
4. Returns `MongoBinaryExpression(MongoSizeExpression(lookupAlias, ...), operator, translatedOtherOperand)`.

**`NativeSlotPopulator`'s `Where` arm** gets one new `else if`, tried after the existing general
`translator.TryTranslate(predicate.Body, ...)` attempt fails and after the existing join-scope/list-Contains
arms (same position other narrow shape-specific arms occupy today) — calling the new helper and, on success,
`mongoQ.Select.AddPredicateConjunct(result)`.

**No changes** to `MongoExpressionTranslator`, `MongoSelectLowerer`, `MongoQueryLanguageRenderer`, or
`MongoAggregationExpressionRenderer` — `MongoBinaryExpression` over a `MongoSizeExpression` operand already
renders correctly today (proved by the owned-collection-count-in-predicate case), and `PipelineOps` already
emit in arrival order, so a `$match` depending on a `$lookup` staged in the same step naturally lands after it.

## Error handling / safety

Every existing guard this design reuses stays exactly as strict as it is today:
- The FK-correlation-only matcher boundary (no filtered-count support) — unchanged, just called from one more
  place.
- The `PipelineKind`-mismatch lookup-collision decline (a `CorrelatedReducer` lookup for the same nav is NOT
  interchangeable with this plain-array lookup) — reused verbatim.
- Anything this new arm doesn't match falls through unchanged to whatever the existing chain already does
  (decline to fallback under `Native`, throw under `NativeOnly`) — no new decline paths, no behavior change for
  shapes untouched by this work.

## Testing plan

- **Unit** (`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`): a new test class (working
  name `NativeReferenceCollectionCountPredicateTests`) asserting `NativeOnly` success for
  `Where(c => c.Orders.Count > n)` in isolation (no `Select`), plus the decline-boundary cases (a genuinely
  filtered `.Where(pred).Count()`, an ambiguous/absent navigation) still correctly decline.
- **Differential correctness** (per `Query/AGENTS.md`'s convention for native shapes that can change results):
  a `[Theory]` comparing the native result to an in-memory LINQ oracle over fixtures with zero/one/many related
  rows.
- **Spec** (`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/`): regenerate the `AssertMql` baseline
  for `Include_with_complex_projection_does_not_change_ordering_of_projection` (and its
  `NorthwindIncludeQueryMongoTest`/`NorthwindStringIncludeQueryMongoTest` siblings, if they share the shape) on
  all three EF versions, confirming the data assertion passes unchanged (only the MQL shape/native routing
  changes) before regenerating.
- **Full three-EF-version suite** (unit + functional + specification) to confirm zero regressions, matching
  this branch's standing verification convention.

## Open question for the implementation plan

Whether the new helper should live as a `NativeProjectionBinder` static (co-located with
`TryTranslateProjectedCollectionCount`, which it shares logic with) or as a `NativeCorrelationMatcher` static
(co-located with the matcher it calls) is a naming/organization detail, not a design decision — leave it to
whichever reads cleaner once the code is in front of the implementer.
