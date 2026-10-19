# Native paging (`Skip`/`Take`) recorded before a confirmed join — design

**Ticket:** to be filed (working title: native post-join paging). Motivated by
`NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties`, which — even
after the native-chained-join-scalar-projection plan landed — still falls back to driver-LINQ under
`MongoQueryMode.Native` and throws `NativeTranslationNotSupportedException` under `NativeOnly`.

## Problem

```csharp
(from o in ss.Set<Order>()
 join ca in ss.Set<Customer>() on o.CustomerID equals ca.CustomerID
 join cb in ss.Set<Customer>() on o.CustomerID equals cb.CustomerID
 orderby o.OrderID
 select new { o.OrderID, CustomerIDA = ca.CustomerID, CustomerIDB = cb.CustomerID,
              ContactNameA = ca.ContactName, ContactNameB = cb.ContactName })
    .Skip(10).Take(5)
```

Confirmed live (`MONGODB_EF_NATIVE_ONLY=1`, single-test filter, current `HEAD`) that this still throws
`NativeTranslationNotSupportedException: Query projects a non-entity result` — the SAME exception the native-
chained-join-scalar-projection plan fixed the *other* cause of. The scalar-leaf projection itself now binds
correctly (proven by `NativeJoinTests.Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly`, the
identical shape *without* trailing `Skip`/`Take`) — this design is about the SEPARATE thing still blocking it.

## Root cause (measured, not assumed)

This is **not** `NativeSlotPopulator`'s "Post-CONFIRMED-JOIN slot-operator guard"
(`HasConfirmedJoinLookup && IsSevenSlotOperator(...)`) — that guard exists for an operator recorded *after* a
join's confirming `Select` has already run, and per its own comment block is measured to be **unreachable** for
this shape: EF Core's nav-expansion defers a join's result selector as a *pending selector* applied last, and
**hoists a trailing `Skip`/`Take` to be recorded BEFORE that pending selector runs** — the same hoisting
already documented (and relied upon) by
`MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope`'s own `HasPaging` conjunct:

```csharp
// MongoQueryableMethodTranslatingExpressionVisitor.cs, IsSingleEligibleNativeJoinScope
if (mongoQueryExpression.Select.HasPaging || mongoQueryExpression.Select.Cardinality?.Reducer != null)
{
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            return false;
        }
    }
}
```

Confirmed by re-running the target spec test under `NativeOnly` after the scalar-projection plan landed: it
still throws with the SAME generic `VisitProjectedQuery`/`ThrowIfNativeOnlyForbidsFallback` message, meaning
`Route` never reaches `Projection` at all — i.e. the CONFIRMING gate above declined the join outright, before
`NativeJoinScopeProjectionBinder.TryBindProjection` (this plan's own new code) ever got a chance to run.

**Why this gate exists, and why it is correct as far as it goes:** `MongoSelectLowerer.Lower` emits
`Select.PipelineOps` ($match/$sort/$skip/$limit) **before** `AppendLookupStages`'s `$lookup`/`$unwind` block.
Because EF hoists `Skip`/`Take` ahead of the join's pending selector, by the time this gate runs,
`Select.HasPaging` is already `true` — i.e. the `$skip`/`$limit` are *already* sitting in `PipelineOps`, about
to be lowered **before** the `$lookup`. That is only correct when the `$unwind` that follows is guaranteed
row-count-preserving (1:1): a LEFT-OUTER join over a non-collection (reference) navigation, whose
`$unwind` uses `preserveNullAndEmptyArrays: true` and therefore neither drops nor duplicates rows. For anything
else — a plain (INNER) join, or a join over a collection navigation — the `$unwind` can drop unmatched rows
(inner join) or multiply matched ones (collection nav), so paging emitted *before* it operates on the wrong
row set: `Owners.Join(Orders, …).Take(2)` was MEASURED (see the same method's own comment) to emit
`{$limit: 2}, {$lookup}, {$unwind}` and return **three** rows instead of two.

**The gap this design closes:** today, when a join isn't in that narrow 1:1-safe set, the gate simply
`return false`s — declining the *entire* join confirmation (and therefore the whole query) rather than fixing
the placement. But the fix is available and general: instead of requiring the pre-existing `$skip`/`$limit`
(and whatever `$match`/`$sort` sit alongside them in the same `PipelineOps` list) to *already* be in a safe
position, **move the whole snapshot to run after `$lookup`/`$unwind` instead of before it**, and let the join
confirm.

## Why moving the WHOLE snapshot (not just the paging ops) is safe

`PipelineOps` is a single ordered list — arrival order is emission order — and it can contain more than
`$skip`/`$limit`: a `Where`/`OrderBy` composed before the `Skip`/`Take` in the user's query is *also* hoisted
ahead of the pending selector by the same EF mechanism, landing in the SAME list, in the SAME relative order
(e.g. `$match`, `$sort`, `$skip`, `$limit`). Splitting the list at "the first paging op" and moving only the
tail would be one option, but it's unnecessary: **every op `NativeSlotPopulator`'s pre-confirmation arms can
possibly record here is either already proven position-invariant across `$lookup`/`$unwind`, or is exactly the
paging op we're moving for**:

- **`$match`/`$sort`** — these are only ever recorded here from a `Where`/`OrderBy` whose predicate/key
  resolved against the OUTER root scope only (`NativeSlotPopulator`'s pre-confirmation `Where`/`OrderBy` arms
  build their translator from `mongoQ.CollectionExpression.EntityType`, the root type — there is no join-scope
  path available yet before confirmation). A root-scope-only predicate/sort is unaffected by whether it runs
  immediately before or immediately after `$lookup`/`$unwind`: neither stage removes, duplicates, or renames
  any ROOT-entity field the predicate/sort could reference (a `$lookup` only *adds* a new array field; an
  `$unwind` `$unwind`s that new field, again touching nothing else). This is the EXACT reasoning
  `IsSingleEligibleNativeJoinScope`'s own comment already gives for why `$match`/`$sort` "commute with the
  join" — this design does not invent a new justification, it applies the existing one to justify moving them
  too, not just leaving them be.
- **`$skip`/`$limit`** — row-count/row-order operations. Unlike `$match`/`$sort`, these are the ONE op kind
  that does NOT commute across a row-count-changing stage: moving a `$skip`/`$limit` across `$lookup`/`$unwind`
  changes which rows it keeps whenever the `$unwind` is not guaranteed 1:1 (an inner join drops unmatched
  rows; a collection navigation multiplies matched ones) — that is the whole premise of this fix, not an
  incidental detail. They are moved specifically to run AFTER the join because that is what makes them apply
  to the JOINED row count, matching LINQ semantics (`Skip`/`Take` compose over the fully-joined sequence the
  user wrote them against) — NOT because position is irrelevant to them.

**What must NOT move past `$project`:** if the confirming `Select` populates `Select.Projection` (a wrapped
scalar/whole-entity leaf projection — this plan's motivating shape), the deferred ops must land BEFORE that
`$project`, not after — a `$match`/`$sort` referencing a root-entity field name (e.g. `"OrderID"`) would read
`null`/missing from the RESHAPED, alias-keyed projected document if it ran after `$project`. `$skip`/`$limit`
would be equally correct on either side of `$project` (a projection doesn't change row count or order), but
there is no reason to special-case them separately from `$match`/`$sort` when "before `$project`, after
`$lookup`/`$unwind`" is already correct for all four op kinds and requires no split.

This is exactly the existing `PostJoinOps` list's own emission slot (immediately after
`AppendLookupStages`, before the `Grouping`/`Cardinality`/`Projection` blocks) — this design adds a
**structurally distinct, separately-triggered** list occupying that SAME slot, rather than repurposing
`PostJoinOps` itself (which is specifically gated by `JoinInnerAccessConfirmedFromWhere`, a different
confirming path with different semantics — conflating the two triggers under one name would obscure which
one actually fired, the exact anti-pattern `Query/AGENTS.md`'s "a node kind needing different handling at 3+
call sites must be a sealed sibling type, not a bool flag" invariant warns against).

## Scope

**In:**

- A `Join`/`LeftJoin` (single join, or a chain of 2+) whose confirming `Select` is a whole-entity leaf, a
  wrapped whole-entity-leaves-only projection, or (native-chained-join-scalar-projection plan) a
  scalar/computed-leaves-over-a-chain projection, where a `Skip`/`Take` (and any `Where`/`OrderBy` composed
  *before* it in the same hoisted batch) was recorded into `PipelineOps` ahead of confirmation, and NOT every
  join in the chain is already in the existing 1:1-safe (left-outer, non-collection navigation) set.
- Both `Skip` alone and `Take` alone, and the two composed together in either order.
- Any chain depth (the per-join eligibility loop already iterates `mongoQueryExpression.Joins` — this design
  changes what happens on a failed check, not the loop itself).

**Out (explicitly deferred, not silently unsupported):**

- **A reducer (`First`/`FirstOrDefault`/`Single`/...) composed in the same position** — `Select.Cardinality?.Reducer
  != null` keeps declining exactly as today when the join isn't 1:1-safe. A reducer's own `$limit` interacts
  with `NativeCardinalityBinder.TryBindReducer`'s own separate confirmation path (which has its own measured
  reachable hazard, per `Query/AGENTS.md`), and deferring it correctly is a different, not-yet-analyzed
  problem. This design touches `IsSingleEligibleNativeJoinScope`'s **paging-only** branch; the reducer branch
  is untouched.
- **A `Where`/`OrderBy`/`ThenBy`/`Skip`/`Take` composed AFTER the confirming `Select`** (i.e., a genuinely
  post-confirmation operator, not one hoisted ahead of confirmation) — still declines via
  `NativeSlotPopulator`'s `HasConfirmedJoinLookup` guard, untouched by this design, per the native-chained-
  join-scalar-projection plan's own already-pinned regression test.
- Widening the 1:1-safety CRITERION itself (e.g. trying to prove a particular inner join happens to be
  row-count-preserving via a unique-FK check) — irrelevant once paging is simply moved to run after the join,
  which is safe unconditionally.

## Design

### Component 1 — `MongoSelectDefinition`: a new deferred-ops list + a one-time "defer now" move

```csharp
// MongoSelectDefinition.cs, alongside PostJoinOps/PostGroupOps
private readonly List<MongoSelectOp> _postLookupPagingOps = [];

/// <summary>
/// The ordered filter/sort/page operations that were recorded into <see cref="PipelineOps"/> BEFORE a join's
/// confirming Select ran (EF Core hoists a trailing Skip/Take — and any Where/OrderBy composed before it in
/// the same batch — ahead of a join's pending result selector), then moved here, in their original recorded
/// order, by <see cref="DeferPipelineOpsPastConfirmedJoin"/> once
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope</c> determined the join
/// is NOT already in the narrow pre-lookup-safe (left-outer, non-collection) set. The lowerer emits these
/// verbatim immediately after the confirmed join's own $lookup/$unwind block(s), before any $project — see
/// docs/superpowers/specs/2026-09-22-native-post-join-paging-design.md for why moving the WHOLE snapshot
/// (not just the paging ops) there is safe. Empty for every query that never took this path.
/// </summary>
public IReadOnlyList<MongoSelectOp> PostLookupPagingOps => _postLookupPagingOps;

/// <summary>
/// Moves the ENTIRE current <see cref="PipelineOps"/> snapshot into <see cref="PostLookupPagingOps"/>, in
/// order, and clears <see cref="PipelineOps"/>. Called exactly once, by
/// <c>IsSingleEligibleNativeJoinScope</c>, at the moment it decides a join with recorded pre-confirmation
/// paging is eligible only because the paging is being relocated — never called when the join is in the
/// pre-existing 1:1-safe set (that case keeps the original, more efficient before-$lookup placement
/// unchanged). Nothing is recorded into <see cref="PipelineOps"/> after this point for the SAME select:
/// <c>NativeSlotPopulator</c>'s own <c>HasConfirmedJoinLookup</c> guard (unrelated to, and untouched by, this
/// mechanism) continues to decline any FURTHER slot operator reached after confirmation.
/// </summary>
internal void DeferPipelineOpsPastConfirmedJoin()
{
    _postLookupPagingOps.AddRange(_pipelineOps);
    _pipelineOps.Clear();
}
```

### Component 2 — `IsSingleEligibleNativeJoinScope`: defer instead of declining for the paging-only case

Replace the combined `HasPaging || Cardinality?.Reducer != null` check with two separate branches — the
reducer branch keeps declining unchanged; the paging-only branch defers instead:

```csharp
if (mongoQueryExpression.Select.Cardinality?.Reducer != null)
{
    // Reducer case: unchanged, out of scope for this design — see spec's "Out" list.
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            return false;
        }
    }
}
else if (mongoQueryExpression.Select.HasPaging)
{
    var everyJoinPreLookupSafe = true;
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            everyJoinPreLookupSafe = false;
            break;
        }
    }

    // Every join already safe for the existing before-$lookup placement: keep it there, unchanged —
    // preserves today's MQL baseline (and its efficiency) for Projection_take_projection/
    // Projection_skip_projection/Projection_skip_take_projection exactly.
    if (!everyJoinPreLookupSafe)
    {
        mongoQueryExpression.Select.DeferPipelineOpsPastConfirmedJoin();
    }
}
```

### Component 3 — `MongoSelectLowerer`: emit `PostLookupPagingOps` right after the lookup block

At the existing step 2b (immediately after `AppendSelectOpStages(select.PostJoinOps, stages, sortFields);`,
inside the `if (select.SetOperation == null)` block):

```csharp
AppendLookupStages(query, stages);
AppendSelectOpStages(select.PostJoinOps, stages, sortFields);
// New: paging (and any Where/OrderBy hoisted alongside it) deferred past a confirmed join whose $unwind
// isn't guaranteed row-count-preserving — see MongoSelectDefinition.PostLookupPagingOps.
AppendSelectOpStages(select.PostLookupPagingOps, stages, sortFields);
```

Placed after `PostJoinOps` (not before): the two lists are mutually exclusive in every currently-reachable
shape (a join confirmed via a bare `Where`-Inner-access null check vs. one confirmed via a `Select`), but
should either ever co-occur for a shape neither list's own trigger anticipated, running `PostJoinOps` first
preserves its documented invariant ("the `Where` predicate itself... must run before... the reducer's own
`$limit`") without this design needing to reason about interleaving the two.

### Data flow (target query)

```
Join #1, Join #2 → TranslateJoinCore: both eligible, JoinScope rebuilt as a 2-level chain (unchanged, prior plan)
OrderBy(o.OrderID) → NativeSlotPopulator: root-scope-only sort → PipelineOps = [Sort]  (unchanged, prior plan)
[pending selector reached AFTER Skip/Take, per EF's hoisting] Skip(10) → PipelineOps = [Sort, Skip(10)]
                                                                Take(5) → PipelineOps = [Sort, Skip(10), Limit(5)]
Select(chain-scalar projection) → TranslateSelect → IsSingleEligibleNativeJoinScope:
    HasPaging == true (Skip/Limit present); neither join is left-outer-reference (both are plain Join) →
    everyJoinPreLookupSafe == false → DeferPipelineOpsPastConfirmedJoin():
        PostLookupPagingOps = [Sort, Skip(10), Limit(5)]; PipelineOps = []
    → eligible=true → NativeJoinScopeProjectionBinder.TryBindProjection (unchanged, prior plan) → binds,
      confirms both joins, Route = Projection
Lower → PipelineOps (empty, no-op) → $lookup/$unwind ×2 → PostJoinOps (empty, no-op) →
        PostLookupPagingOps: $sort, $skip: 10, $limit: 5 → $project (the 5 scalar fields)
```

## Durable invariants this design must not violate

- **A gate must call the same structural predicate its companion rewrite relies on.** The per-join
  1:1-safety loop is unchanged (same conjuncts, same order) — this design only changes what happens when the
  loop's answer is "no," not the loop itself.
- **MQL shape/exact pipeline stage placement is not contract for a supported query.** The existing 1:1-safe
  carve-out's MQL baseline (`Projection_take_projection` et al.) is preserved byte-for-byte because that path
  is untouched; the NEWLY-supported shapes get a new (and, per `Query/AGENTS.md`'s own rubric, non-contractual)
  pipeline shape.
- **A recognizer must not mutate then decline.** `DeferPipelineOpsPastConfirmedJoin` is called only once
  eligibility is otherwise fully decided (after the loop determines deferral is needed, immediately before
  `IsSingleEligibleNativeJoinScope` returns `true`) — never speculatively, and never on a path that can still
  return `false` afterward for an unrelated reason (the method's remaining checks all precede this branch).
