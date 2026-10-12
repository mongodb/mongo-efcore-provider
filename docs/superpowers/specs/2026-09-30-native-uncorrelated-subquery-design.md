# Native uncorrelated scalar subquery in `Where` (EF-220 shape A) — design

Status: **design gate (Task 8, Step 0) — awaiting owner sign-off before any implementation.** Docs only; no
`src/` or `tests/` change accompanies this note.

## Motivating example

The EF-220 Jira repro and `NorthwindWhereQueryMongoTest.Where_shadow_subquery_FirstOrDefault`:

```csharp
context.Set<Employee>()
    .Where(e => e.Title == context.Set<Employee>().OrderBy(e2 => e2.Title).FirstOrDefault()!.Title)
```

Today this throws in every mode. Under the default `Native` mode the native path marks the query non-native,
silently falls back to driver-LINQ, and the driver throws `ExpressionNotSupportedException`; under
`NativeOnly` it throws at the gate; under `DriverLinq` the driver throws. EF Core relational renders the same
query as one statement with a scalar subquery (`WHERE [e].[FirstName] = (SELECT TOP(1) ... ORDER BY ...)` in
the SQL Server baseline for `Where_query_composition`).

## 1. Problem and scope

"Uncorrelated" means the subquery references **no parameter of any enclosing lambda**. It may reference EF
query parameters (closure captures) and constants. Correlated subqueries are the `NativeCorrelationMatcher`
domain and stay out of this design; the two are disjoint by construction, because the recognizer only accepts a
subtree with no free enclosing-lambda parameter.

### In scope (phase 1)

An uncorrelated subquery **inside a `Where` predicate** on the root query (no join scope recorded yet), whose
shape after EF preprocessing is:

```
Terminal( [Select(s => <scalar member>)] ( Op*( EntityQueryRootExpression ) ) )
  Op       ∈ Where, OrderBy/OrderByDescending, ThenBy/ThenByDescending, Skip, Take
  Terminal ∈ First, FirstOrDefault, Single, SingleOrDefault   (requires the Select)
           | Count, LongCount, Any                             (with or without predicate; no Select)
           | Contains(value)                                   (requires the Select; rewritten as Any)
```

Why the `Select` is there: EF's `SubqueryMemberPushdownExpressionVisitor` (run from `QueryTranslationPreprocessor`
and from nav-expansion) rewrites `src.FirstOrDefault().Title` into `src.Select(s => s.Title).FirstOrDefault()`,
moving a predicate overload's predicate into a `Where` first (read in EF source,
`src/EFCore/Query/Internal/SubqueryMemberPushdownExpressionVisitor.cs:153-220`). So the provider sees a
scalar-valued reducer, not an entity-valued one followed by member access. `EF.Property<T>(subquery, "Title")`
is pushed down the same way (same file, `:89`).

Spec tests expected to move (names verified by grep; see §5): `Where_shadow_subquery_FirstOrDefault`,
`Where_query_composition`, probably `Where_query_composition2`, and `Where_subquery_on_bool` if `Contains` stays
in phase 1.

### Out of scope

- **Sub-shape B**: a cross join over unrelated roots (`from c in Customers from o in Orders ...`, about 60 spec
  tests tagged EF-220). It needs its own design and is entangled with reference-Include and EF-X022 work.
- **EF-X022**: a filtered or paged join inner.
- **Correlated subqueries** (`Where_query_composition_is_null`, `Where_subquery_expression_same_parametername`,
  `Select_Subquery_Single`, ...): `NativeCorrelationMatcher` owns these.
- **Entity-valued results**: `subquery.FirstOrDefault() == null` or entity equality against a subquery. These
  need key-projection rewriting; deferred.
- **`Min`/`Max`/`Sum`/`Average` terminals**: mechanically cheap (a `$group` in the sub-pipeline), but each
  behaves differently on an empty source (`Sum` gives 0; `Max`/`Min`/`Average` throw for a non-nullable type in
  C# and give NULL in SQL), which needs its own semantics table. Offered as phase 1b (owner decision 4).
  `Sum_over_uncorrelated_subquery` is also out: its subquery sits inside an aggregate selector, not a `Where`.
- **A subquery in `Select`, `OrderBy`, a GroupBy key/element or a join key selector.** A `Select` leaf needs the
  temp field to stay alive until the `$project` and has to go through `NativeProjectionBinder`'s commit block.
  That is phase 2 at the earliest.
- **Nested subqueries** (`Where_subquery_recursive_trivial`: an uncorrelated `Any` inside an uncorrelated `Any`).
  Phase 1 declines when the sub-chain itself needs this feature.
- **A `Where` recorded after a join scope, post-group, or post-Distinct.** The recognizer requires
  `JoinScope == null && Joins.Count == 0 && !HasConfirmedJoinLookup`; the existing post-terminal guards
  (`NativeSlotPopulator.cs:139-165`) already stop the other cases before the `Where` arm runs.
- A subquery root over a **TPH-derived** entity type (phase 1 declines rather than re-deriving discriminator
  narrowing for the sub-pipeline), and a root whose stored member comparison isn't equality-faithful (§4.5).

## 2. Current behaviour (verified by reading code on `EF-322c` at `a40599d5`; the failing run itself was not re-executed)

The brief's claims are corrected where the code differs.

1. **The `Where` lambda is never handed to EF's subquery machinery.** QMTEV's `VisitMethodCall`
   (`Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:204-305`) doesn't route `Where` through
   `base.VisitMethodCall`. It calls `NativeSlotPopulator.PopulateNativeSlots(...)` directly (`:289`) with the raw
   `MethodCallExpression`. `TranslateWhere` is deliberately inert (`:3515-3516`, rationale at `:3024-3026`). So
   the subquery arrives as an untranslated LINQ subtree inside `predicate.Body`.
2. **`NativeSlotPopulator`'s `Where` arm does have a final `else`.** The brief says "`~:180-186` has no `else`";
   that is wrong. The arm spans `NativeTranslation/NativeSlotPopulator.cs:180-291`:
   - it tries `translator.TryTranslate` (`:191`), then the join-scope arms, then the reference-collection
     `Count` arm (`:284-288`);
   - it ends with `else mongoQ.Select.MarkNotNativelyRepresentable();` (`:289-290`).

   `MongoExpressionTranslator` has no case for a `Queryable` method over an `EntityQueryRootExpression`, so every
   attempt fails and the query is correctly marked non-native. The silent part is only the (correct) fallback.
3. **`TranslateFirstOrDefault` (`:2035-2037`, returns `null`) and `CreateSubqueryVisitor` (`:2019-2020`, throws
   `NotSupportedException`) are not on this path.** EF core calls `CreateSubqueryVisitor` only from
   `QueryableMethodTranslatingExpressionVisitor.TranslateSubquery` (EF source `:682-698`). Relational providers
   reach that from their SQL translator; this provider has none. The top-level reducer is handled by
   `NativeSlotPopulator`'s `TryGetReducerKind` arm (`:337-346`) via `NativeCardinalityBinder.TryBindReducer`.
   That arm synthesizes `$limit: 2` for `Single*`, which is wrong for a subquery (§3.1), so it must not be reused
   as-is.
4. **The fallback fails in the bridge/driver.** `MongoEFToLinqTranslatingExpressionVisitor.cs:527-548`:
   - the first `EntityQueryRootExpression` becomes `_source`;
   - a later root of the **same** entity type also becomes `_source` (`:540-543`), so the driver receives
     `Where(e => e.Title == <driver queryable>.OrderBy(..).Select(..).FirstOrDefault())` and (per the Jira and
     the brief) throws `ExpressionNotSupportedException`;
   - a root of a **different** type throws `InvalidOperationException("Unsupported cross-DbSet query ...")`
     (`:545-548`).

   Both are loud. This design doesn't change the bridge.
5. **`LookupExpression` is the wrong vehicle as-is.** It is navigation/FK-shaped:
   - `ToLookupStageDocument` always emits either `localField`/`foreignField` or a leading FK `$expr` `$match`
     (`Expressions/LookupExpression.cs:323-359`);
   - `PipelineStages` is a compile-time `List<BsonDocument>` with no placeholder support (`:203`), so a
     parameterized sub-predicate can't be carried;
   - `AppendLookupStages` throws on any kind it doesn't know (`MongoSelectLowerer.cs:491-499`);
   - lookups are lowered after `PipelineOps` (`:58` then `:67`), too late for a `$match` in `PipelineOps`;
   - `query.Lookups` feeds many gates: `IsJoinQuery`/`InnerCollections.Count` (`:433`), the set-op
     `Lookups.All(l => l.InjectAfterRoot)` checks (QMTEV `:3442-3472`), and `AddLookup`'s alias merge.

   The one precedent for "a `Where` needs its own lookup", `NativeReferenceCollectionCountPredicateBinder`, handles
   the ordering problem by flipping `ActiveOps` to `PostJoinOps` (`MarkReferenceCollectionCountPredicateConfirmed`,
   binder `:87-99`). `Route` then has to decline that flag combined with a set op, grouping, join scope or unwind
   (`MongoSelectDefinition.cs:1080`ff). That works, but it widens `Route` for every new such feature.

## 3. Execution options

### 3.1 Semantics any option must meet (the oracle is EF relational, not LINQ-to-objects)

- **Empty result.** EF relational renders `First`/`Single` inside a subquery as `TOP(1)` and never throws:
  `RelationalQueryableMethodTranslatingExpressionVisitor.TranslateSingleOrDefault` applies
  `_subquery ? 1 : 2` (EF source `:1440-1463`). An empty subquery yields SQL NULL, so `First`, `FirstOrDefault`,
  `Single` and `SingleOrDefault` all mean "first row's value, or null". LINQ-to-objects would throw for
  `First`/`Single`. Proposed: match relational (owner decision 2).
- **Null vs missing.** Verified on 6.0/7.0/8.0 (scratch probe, deleted):
  - `$arrayElemAt` of an empty `$lookup` array is *missing*, and aggregation `$eq: [null, <missing>]` is
    **false**;
  - `$eq` of missing vs missing is true, and `$ifNull`-normalizing both sides makes null ≡ missing.

  C# `e.Title == null-result` is true for a null `Title`. So the scalar must be read `$ifNull: [..., null]`, and
  the node must report `MayBeNull` so the existing top-level `$ifNull` wrapping and relational-operator guards
  apply (Query `AGENTS.md`, "Rendering" invariants).
- **Non-nullable value results.** Pushdown of `FirstOrDefault` over an `int` member yields `(int?)` wrapped in
  `Convert(..., int)` (EF source `:217-219`). LINQ-to-objects gives `0` on empty; SQL gives NULL, so the
  comparison is false. Phase 1 **declines** a non-nullable value-type result (owner decision 3).
- **Ordering ties.** Ties under `OrderBy(k).First()` are arbitrary in both SQL and MongoDB. Server sort puts
  null/missing first ascending, same as SQL Server (verified: `$sort: {Title: 1}` returns the null-`Title`
  documents first). Every option evaluates the subquery once per execution, so all outer rows see the same
  winner.
- **`Count`**: `$count` over empty emits **no document**, so the read is `$ifNull: [..., 0]`. **`Any`**:
  `$limit: 1`, read as `$size` > 0 (`$ifNull`-wrapped per the `$size` invariant, although `$lookup`'s `as` is
  always an array).
- **Tracking and Include**: the result is a scalar and is never materialized or tracked. The outer query's
  Include lookups are recorded after the `Where` (EF appends Include last) and lowered after `PipelineOps`, so
  they never see the temp field (§4.3).

### (a) Per-document `$lookup` with a `let`-free sub-pipeline, then `$match`, then `$unset` — **recommended**

```
{ $lookup: { from: "Employees", pipeline: [ {$sort:{Title:1}}, {$limit:1}, {$project:{_id:0, v:"$Title"}} ], as: "__sq0" } },
{ $match:  { $expr: { $eq: [ {$ifNull:["$Title",null]}, {$ifNull:[{$arrayElemAt:["$__sq0.v",0]},null]} ] } } },
{ $unset:  "__sq0" }
```

**Cost: the sub-pipeline does *not* run per outer document. Verified empirically** (scratch `mongosh` probe
against `mongo:6.0`, `7.0` and `8.0` containers, 2,000 documents, deltas of
`serverStatus().metrics.queryExecutor.scannedObjects`):

| Pipeline | docs scanned | reading |
|---|---:|---|
| `let`-free `$lookup` + `$match` (above shape) | 4,000 | outer scan + **one** inner scan |
| same, but sub-pipeline references `$$x` from `let` | 4,002,000 | inner re-run per outer doc |
| `let` declared but unused | = outer + one inner | still cached |
| `$count` / `$group:{$max}` sub-pipelines | = outer + one inner | cached |

`explain("executionStats")` shows `collectionScans: 1` for the `$lookup` stage. This matches the server's
documented behaviour: the uncorrelated prefix of a `$lookup` sub-pipeline is cached and replayed. From the
MongoDB `$lookup` docs, **not verified here**: the cache is abandoned above a size limit (~100 MB), and a
sub-pipeline containing `$sample`, `$sampleRate` or `$rand` is always re-run. Phase-1 sub-pipelines end in at
most one tiny document (`$limit:1`, `$count`) and the allow-listed operators emit none of those stages, so the
cache always applies. **Not verified on 5.0** (the minimum supported server; the image pull stalled in this
environment). The implementer should re-run the probe on 5.0, or the owner accepts 6.0+ evidence (owner
decision 5).

**Index use.**
- The inner pipeline can use indexes: `$sort` + `$limit` over an indexed field examined 1 key.
- The compared outer field **cannot** use an index, because it is compared in `$expr` to a per-document field.
- Independent conjuncts still can: verified on 6.0 and 8.0, the server split `{$and:[{g:3}, {$expr:...__sq0...}]}`
  and pushed `g: 3` into the `$cursor` as an `IXSCAN` ahead of the `$lookup`.

So the cost is: one inner evaluation, plus a scan of whatever the other predicates leave, plus a cheap
per-document attach of a ≤1-element array. That is acceptable for the target shapes. For a query whose
**only** selective predicate is the subquery comparison, (c) would be index-backed and (a) is not.

**Fit.**
- Single round trip, no Storage change.
- Placeholders work if the sub-pipeline is rendered through the shared `PlaceholderTable` the way `$unionWith`
  operands already are (`MongoPipelineFactory.RenderUnionWith`, `:462-475`).
- The commit-point rule is easy to honour (§4.2).

### (b) Hoist once: `$facet` / `$documents` / preceding single-document lookup

- `$facet` would put the entire outer result into one document, which is capped at 16 MB. That rules it out
  for row-returning queries.
- `$documents` (5.1+) is a literal-document source and can't read a collection.
- Any other "compute once, attach to every row" construction (`$group` everything + `$unwind`) destroys
  streaming and memory bounds.

Because (a) is already evaluated once by the server, (b) buys nothing. **Rejected.**

### (c) Separate preceding round trip, value inlined as a per-execution parameter

- **Pros:**
  - the outer predicate becomes query-dialect `{Title: <param>}`, so it is index-usable;
  - the subquery can be anything the native path handles at top level, including entity results.
- **Precedent:** EF itself does this in one narrow case. When a subquery hides behind a closure-captured
  `Expression<>` variable, the funcletizer evaluates it early. See the existing Mongo baseline for
  `Where_subquery_expression` (three round trips, the last with `$expr: true`) and the SQL Server baseline's
  `@Any='True'`.
- **Cons:**
  - two round trips per execution;
  - not atomic: the two reads see different snapshots unless the user runs in a snapshot session or
    transaction;
  - it needs an execution-time hook in which Storage runs a pre-query and feeds `MongoPipelineFactory.Build`'s
    parameter values. Today Query never executes anything (Query `AGENTS.md`, "Layering"), and the hook would
    also need async/cancellation plumbing through `QueryingEnumerable`;
  - logged MQL becomes two commands.

It also departs from EF relational, which sends one statement for every phase-1 shape. **Not recommended
for phase 1**; it stays an option if index use on the compared field turns out to matter (owner decision 1).

### (d) Keep failing loudly (null option)

Zero risk. The current behaviour is already loud in every mode (§2.4). The cost is the unresolved Jira and 2–4
spec tests. This is the right answer if the owner judges an M–L change too expensive for this yield.

## 4. Proposed design for (a)

### 4.1 Recognition: `NativeUncorrelatedSubqueryBinder` (new static class)

It is called from `NativeSlotPopulator`'s `Where` arm as a new `else if`, placed **after**
`NativeReferenceCollectionCountPredicateBinder` (`:284-288`) and before the final `else` (`:289`). It is tried
only when every existing arm has declined, so no currently-native query changes shape. Gate:
`mongoQ.Select.JoinScope == null && mongoQ.Joins.Count == 0 && !mongoQ.Select.HasConfirmedJoinLookup`.

Steps:

1. **Find candidates.** Walk `predicate.Body`, tracking the set of enclosing lambda parameters: the `Where`
   parameter plus any lambda entered on the way down. A candidate is a `MethodCallExpression` on
   `Queryable` whose method is an allowed terminal (§1) and whose source chain bottoms out in an
   `EntityQueryRootExpression`, possibly behind a nav-expansion-injected query-filter `Where`.
   - It must reference **none** of the tracked parameters. This test is version-agnostic. It deliberately
     doesn't mean "no free `ParameterExpression`": on EF8/EF9, query parameters are free
     `ParameterExpression`s, and on EF10 they are `QueryParameterExpression`s.
   - An optional `Convert` wrapper from pushdown is peeled.
   - No candidates means decline.
2. **Translate the source chain** into a *fresh* `MongoQueryExpression`, built the way QMTEV builds a root
   (`CreateShapedQueryExpression`, QMTEV `:1522-1531`). For each allowed `Op`, from the innermost outwards, call
   the existing `NativeSlotPopulator.PopulateNativeSlots(freshShaped, def, call)`. This reuses every existing
   predicate, sort and paging gate verbatim and never touches the outer `mongoQ`. The result must pass one
   shared predicate, `IsPipelineOpsOnlySubquery(select)`:
   - `Route == WholeEntity`;
   - no `Lookups`, `Joins`, `VectorSearch`, `Cardinality`, `Grouping` or `SetOperation`;
   - no unwind sources, no `PostJoinOps`, no case-mapping leaf;
   - no nested uncorrelated op (phase 1).

   **The gate calls this predicate; it doesn't restate it** (Query `AGENTS.md`).
3. **Translate the terminal.**
   - For a `First`/`Single` family: translate the pushed-down `Select` body with
     `MongoExpressionTranslator.TryTranslateValue` against the sub-root, then append `$limit: 1` (never 2) and
     `$project: {_id: 0, v: <value>}`.
   - `Count`/`LongCount`: `$count: "v"`, after the predicate overload's `Where`.
   - `Any`: `$limit: 1`.
   - `Contains(x)`: an `Any` whose sub-predicate is `<selected value> == x`, built through the translator so
     null-equality and `$literal` rules apply.
   - Decline a non-nullable value-type `First` family result (§3.1) and a derived-type root.
4. **Translate the outer predicate.**
   - Clone `predicate.Body` with each candidate replaced by a `UncorrelatedScalarPlaceholderExpression`
     (a LINQ `Extension` node, same `Type`) that points at its staged descriptor.
   - `MongoExpressionTranslator` gets one new case mapping that placeholder to a new sealed node,
     `MongoUncorrelatedScalarExpression(Alias, Kind, Type)`.
   - Split the top-level `&&`: conjuncts that don't reference a placeholder go through the ordinary path (query
     dialect, index-first). Only the referencing conjuncts go into the new op. The split is exact because a
     `$match` has nothing to short-circuit (same reasoning as `NativeSlotPopulator.cs:266-269`).
   - If any referencing conjunct fails to translate, decline.
5. **Commit point.** Only now, after every step above has succeeded:
   - allocate aliases;
   - `AddPredicateConjunct` the non-referencing conjuncts;
   - append **one** `MongoUncorrelatedScalarMatchOp` to `ActiveOps`.

   Nothing is written to `mongoQ` before this point: steps 2–4 work on a fresh query, clones and locals ("A
   recognizer must not mutate then decline").

### 4.2 IR: one op, not a lookup

```csharp
internal sealed record MongoUncorrelatedScalar(string Alias, string From, MongoQueryExpression Source,
    UncorrelatedScalarKind Kind, MongoExpression? Value /* First-family / Contains operand */);
internal sealed record MongoUncorrelatedScalarMatchOp(
    IReadOnlyList<MongoUncorrelatedScalar> Scalars, MongoExpression Predicate) : MongoSelectOp;
```

Keeping the scalars off `query.Lookups` means every lookup-count gate, `AddLookup` alias merge,
`AppendLookupStages` branch and the `Route` exception list stay untouched. It needs no `ActiveOps` flip and no
new `Route` clause. Because the op is not a `MongoMatchOp`, `AddPredicateConjunct`'s tail merge
(`MongoSelectDefinition.cs:176-184`) can neither fold a later `Where` into it nor fold it into an earlier
`$match` that runs before the `$lookup`.

### 4.3 Lowering, rendering and the temp field

- `MongoSelectLowerer.AppendSelectOpStages` (`:343-386`) gets one arm: for each scalar, a new
  `MongoUncorrelatedLookupStage(From, SubStages, Alias)`; then `MongoMatchStage(Predicate)`; then the existing
  `MongoUnsetStage(aliases)`. `SubStages` come from lowering `Source` with the same lowerer (its own
  `SyntheticSortFieldAllocator` scope) plus the terminal stages.
- `MongoPipelineFactory` renders the new stage as `{$lookup: {from, pipeline, as}}`. The sub-stages go through
  the **shared** `PlaceholderTable`, as `RenderUnionWith` does, so EF parameters inside the subquery substitute
  per execution. `NormalizePagingStages` (`:589`ff) currently recurses only into `$unionWith`
  (`:617-621`); it must also recurse into this `$lookup`'s `pipeline`, or `Take(p)` with `p = 0` inside the
  subquery is a server error (Query `AGENTS.md`: "Normalization covers top level and `$unionWith`, not
  `$lookup`").
- **Aliases** are `__sq0`, `__sq1`, ... (double-underscore sentinel convention, as with `__sort`). They are
  allocated at the commit point by a per-`MongoSelectDefinition` counter that skips the root entity's top-level
  element names, via the same `AddTopLevelElementNames` walk `ReservedElementNames` uses (`MongoSelectLowerer.cs:533-558`,
  made `internal`). That makes them deterministic for `AssertMql`.
- **No leak by construction.** The field exists only across three adjacent stages the op emits: `$lookup`,
  `$match`, `$unset`. By the time any `$sort`, `$group`, `$project`, set-op dedup, Include `$lookup` or the
  materializer runs, it is gone. In a set-op operand the op renders inside the operand pipeline and is unset
  there. Tests pin this (§5).
- **Render.** The value read is `{$ifNull: [{$arrayElemAt: ["$__sqN.v", 0]}, null]}`. `Count` uses `0` in place
  of `null`. `Any` renders `{$gt: [{$size: {$ifNull: ["$__sqN", []]}}, 0]}`. The node is aggregation-only:
  `IsQueryDialectRenderable` is false, so its conjunct is rendered inside `$expr`, the dialect of last resort,
  per the index-first dialect convention.
- **Negator.** `$eq`/`$ne` over the node invert exactly (both sides are null-normalized). Relational operators
  use the existing `$not`-wrap/null-guard rules via `MayBeNull` (true for the `First` family, false for
  `Count`/`Any`). `MongoExpressionNodeCoverageTests` must list the node for the negator, the classifier and both
  renderers.

### 4.4 Fallback, DriverLinq, EF versions

- **Unsupported shapes** fall through to the existing final `else` → `MarkNotNativelyRepresentable()`. There are
  no new decline sites and no behaviour change outside the admitted shapes: under `Native` they still fall back
  and throw in the driver, and under `NativeOnly` they still throw at the gate.
- **`DriverLinq`** is untouched and stays loud (§2.4). Explicit `DriverLinq` tests pin that (Query `AGENTS.md`:
  new shapes are tested under explicit `DriverLinq`). The fallback's exception type isn't contract.
- **Not a break.** Anything that newly goes native used to throw in every mode, so nothing can change results
  and there is no EF query-cache concern. This is covered by the "Native default not a break" ruling.
- **EF8/EF9/EF10.**
  - The member pushdown exists in all three (read in current EF source; the implementer should assert the
    pushed-down tree per version in a unit test).
  - The recognizer is version-agnostic by design (step 1).
  - `PopulateNativeSlots` already hides the version-specific parameter shapes.
  - No visitor-signature `#if` is needed, because no QMTEV override changes and `CreateSubqueryVisitor` stays
    as it is.

### 4.5 Stored-form safety

Equality between the outer operand and the subquery value is only exact when both are stored the same way. The
node therefore carries the source `IProperty` of the selected member, and the comparison declines unless:
- for equality or `Contains`: the pair passes the existing `IsStoredEqualityFaithfulKeyPair` (QMTEV `:2541`,
  already used for anonymous join keys);
- for relational operators: both sides pass `StoredOrdering.PreservesClrOrdering`.

The implementer must make that predicate reachable from the binder, not copy it.

## 5. Test plan and spec baselines

New functional class `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeUncorrelatedSubqueryTests.cs`:

- **Differential `[Theory]`s** against an in-memory LINQ oracle adjusted to EF-relational empty semantics
  (`NativeModeAssert`), run under `NativeOnly`. Data covers:
  - an empty subquery source;
  - a single match, and ties under the sort key;
  - null and **missing** values of the compared field on both sides;
  - a subquery predicate using an EF parameter, and `Take(p)` with `p = 0`;
  - a sub-root of a different collection;
  - `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Count`, `LongCount`, `Any` and `Contains`, each
    combined with an independent conjunct.
- **Jira repro** (`Where(e => e.Title == ...OrderBy(e2 => e2.Title).FirstOrDefault()!.Title)`) under
  `NativeOnly`.
- **Explicit `DriverLinq`**: the same queries still throw (same-type root and cross-type root).
- **No-leak tests**: `__sq0` is absent from materialized tracked entities and from a `GroupBy` key/element
  after the `Where`. A projection after the `Where` doesn't see it. A set-op operand carrying the op works. An
  outer reference and collection Include after the `Where` still works.
- **Decline boundaries (mutation-checked)**: each must still mark non-native:
  - a correlated subquery;
  - a nested subquery;
  - a non-nullable value-type result;
  - `Select`/`OrderBy` placement;
  - a `Where` after a join, and a join-scope `Where`;
  - a derived-type root;
  - an unfaithful stored pair.

  Remove the gate and each test must fail ("prove guard tests discriminate by mutation").
- **Unit** (`tests/…UnitTests/Query/NativeTranslation/`): recognizer tree shapes per EF version,
  commit-after-all-gates (the outer `mongoQ` is untouched on decline), alias allocation that skips a mapped
  `__sq0` element name, and node coverage.

**Spec baselines.** Names verified by grep in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/`.
Each changes from `AssertTranslationFailed` to a passing assertion plus a regenerated `AssertMql`, on EF8, EF9
and EF10.

| Test | File:line | Expectation |
|---|---|---|
| `Where_shadow_subquery_FirstOrDefault` | `NorthwindWhereQueryMongoTest.cs:391` | passes (tagged EF-220) |
| `Where_query_composition` | `NorthwindMiscellaneousQueryMongoTest.cs:618` | passes (tagged EF-216; same shape) |
| `Where_query_composition2` | `NorthwindMiscellaneousQueryMongoTest.cs:903` | probably passes; `First()` over `select new { Foo = e2 }` then `.Foo.FirstName` depends on pushdown reducing to a member, so verify in Step 1 |
| `Where_subquery_on_bool` | `NorthwindMiscellaneousQueryMongoTest.cs:2070` | passes only if `Contains` stays in phase 1 |

**Brief corrections:**
- The brief lists a `NorthwindMiscellaneousQueryMongoTest` override for `Where_shadow_subquery_FirstOrDefault`.
  None exists; it lives only in `NorthwindWhereQueryMongoTest`.
- Only that one test is tagged EF-220 for shape A. The other EF-220 tags are sub-shape B (Include/cross-join,
  `SelectMany`).

**Stay failing, unchanged:**
- `Where_subquery_recursive_trivial` (:1937, nested);
- `Where_query_composition_is_null`/`_is_not_null`/`_entity_equality_*` (correlated);
- `Where_query_composition3`-`6` (EF expects client-eval failure);
- `Sum_over_uncorrelated_subquery` (`NorthwindAggregateOperatorsQueryMongoTest.cs:736`).

The implementer must check whether any of the four rows above sits inside an `#if EF8 || EF9` region before
editing. Then run the full EF8/EF9/EF10 suite: this is a dispatch change in the `Where` arm, and the
`ef436-groupjoin-and-scope-verification` lesson applies.

## 6. Risks, size, owner decisions

**Risks.**
- *Low spec yield.* Two to four spec tests; the value is mainly the user-facing Jira shape and a reusable
  mechanism for phase 1b/2.
- *No index on the compared field* (§3a), a real regression versus (c) for "the subquery comparison is the only
  filter" workloads. It is not a regression versus today, which throws.
- *5.0 cache behaviour* not measured here.
- *Sharded or Queryable Encryption collections.* `$lookup` from a sharded `from` needs 5.1+, and QE restricts
  `$lookup`. Both are shared with every existing native lookup, so they add no new class of risk, but the
  encryption reviewer should see the change.
- *Re-entrant `PopulateNativeSlots` on a fresh query* is new. It has never run outside QMTEV's
  `VisitMethodCall`, so any hidden dependency on QMTEV instance state would surface here. Reading shows it is
  static over the `ShapedQueryExpression`; confirm in the Step 1 unit tests.

**Size.** Honest estimate **M–L**: roughly 350–500 lines of `src` (binder, op, stage, node with its four
dispatchers, factory and normalization changes) plus 400–600 lines of tests and baselines across three EF
versions.

**Owner decisions:**

1. **Execution strategy:** approve (a), the single-pipeline cached `$lookup` + `$match` + `$unset`, over (c) the
   pre-query round trip, accepting that the compared field is not index-backed. Or choose (d) and park EF-220 A.
2. **Empty/cardinality semantics:** `First`/`Single` inside a subquery follow EF relational (`$limit: 1`, empty
   ⇒ null, never throw), not LINQ-to-objects.
3. **Non-nullable value-type results** (`FirstOrDefault()` of an `int`): decline in phase 1 (proposed), or
   adopt relational NULL semantics.
4. **Phase-1 terminal set:** `First*`/`Single*`/`Count`/`LongCount`/`Any`/`Contains` (proposed). Say whether
   `Contains` stays, and whether `Min`/`Max`/`Sum`/`Average` join now or as 1b.
5. **Evidence bar:** is 6.0/7.0/8.0 evidence of the uncorrelated sub-pipeline cache enough, or must 5.0 be
   probed before Step 3?
