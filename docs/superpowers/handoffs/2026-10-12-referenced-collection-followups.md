# Handoff: referenced-collection aggregates (EF-322c), follow-ups

**Branch** `EF-322c`, pushed; HEAD `2bffc78f`, working tree clean, **nothing pending a user decision**. At that commit EF8, EF9 and EF10
build with 0 warnings and pass every project (unit 1,708 / 1,708 / 1,701; spec 4,714 / 5,027 / 4,768; functional 6,736 / 6,782 / 6,857;
skips are the expected encryption and Atlas-gated ones).

The design is `docs/superpowers/specs/2026-10-08-native-referenced-collection-aggregates-design.md` (see its **Status and deviations**
section). The invariants, with the reasoning, are in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` under *Aggregates and quantifiers
over a referenced (cross-document) collection*; read that before touching any of this. This file is only the follow-up list.

## What exists (so the follow-ups make sense)

A correlated subquery over another collection (`c.Orders...`, a `GroupJoin` group, `db.Orders.Where(o => o.CustId == c.Id)`) is read from a
`$lookup` array that is **not unwound**:

| Piece | Where |
|---|---|
| Correlation recognized strictly (navigation FK, or 1..N equalities of two stored-alike scalar properties, per-outer-part null guards) | `NativeCorrelationMatcher` (`TryMatchCorrelatedCollection`, `TryMatchRawKeyCorrelation`, `TryMatchCorrelationKeys`, `BuildCorrelatedArrayLookup`) |
| The array as a `LookupExpression` (`IsCorrelatedArraySource`; element filter = `InnerPredicate`; `ElementSort`/`ElementSkip`/`ElementLimit`; `CompositeKey` for several pairs) | `Expressions/LookupExpression.cs`, rendered by `MongoPipelineFactory.RenderLookup` |
| Scalar aggregates, quantifiers, counts (projection, `Where`, `OrderBy`) | `MongoExpressionTranslator.ReferencedCollectionResolver`, `...MethodCalls.cs` (`TranslateReferencedArrayAggregate`, `TranslateReferencedComputedAggregate`), `NativeReferencedCollectionBinder`; nodes `MongoArrayReduceExpression`, `MongoMappedArrayReduceExpression` |
| Element leaves: `ToList` / `FirstOrDefault` of the group | `NativeReferencedElementMatcher` (shared recognizer), `NativeProjectionBinder.TryTranslateReferencedElementLeaf` (emit), `MongoProjectionBindingExpressionVisitor.TryBindReferencedElementLeaf` (read) |
| Driver-LINQ bridge for the aggregate family | `MongoEFToLinqTranslatingExpressionVisitor.ReferencedCollection.cs` |

Two rules explain most of the code and are easy to break: **(a)** the emit side stages these lookups in EVERY mode, so the bridge must
either read them correctly or refuse (`IsNativeOnlyArray`); the first version of the bridge silently returned empty lists; **(b)** the
read side binds an element leaf only if the emit side recorded its projection alias on the lookup (`ElementLeafAliases`), never by
re-deriving admissibility.

Do **not** treat a pinned spec test as a goal: the three remaining `NorthwindJoinQueryMongoTest.GroupJoin_aggregate_anonymous_key_selectors*`
pins all aggregate `Sum(o => o.CustomerID.Length)`, a string `Length` selector the repo deliberately declines (a possibly-null string
length would be read as 0 where LINQ throws). They will not flip however many of the items below are done.

## Working notes (from this session)

* Build and test one version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test ... --no-build` (a filter such
  as `--filter "FullyQualifiedName~NativeReferencedCollectionElementTests"`; theory display names like `(mode: DriverLinq)` cannot be
  filtered). The three versions can run in parallel in the background, each with its own testcontainer (Docker must be running, about
  20 minutes for the functional project). **Check no `testhost` is left over first** or the EF8/EF9 build fails on a locked DLL and the
  tests silently run stale binaries; look for `build-done errors=0` before trusting a run.
* After a baseline change run `EF_TEST_REWRITE_BASELINES=1` with a tight `--filter` (see the SpecificationTests `AGENTS.md`); it did not
  rewrite inside `#if EF8` regions, so write those baselines by hand.
* Stage explicit file paths, never `git add tests` or `-A`: a stray edit in an IDE-open file (a typo in `NorthwindFunctionsQueryMongoTest.cs`)
  was committed that way once.
* Commit style: `EF-322: ...`, with the `Co-Authored-By` trailer. Tests first as their own commit, then the implementation.
* The MQL is not proof of nativeness; use `MongoQueryMode.NativeOnly` (see `Query/AGENTS.md` Testing).
* EF versions differ: EF10 guards every outer part of a `GroupJoin`'s rewrite with `!= null`; EF8/EF9 keep the anonymous key whole
  (`Equals(new { .. }, new { .. })`). Test oracles that touch null key parts are therefore version-dependent
  (`GroupJoinGuardsNullKeyParts` in `NativeAnonymousKeyJoinTests`).
* A pre-existing local branch `EF-322c-presquash2` holds the old unsquashed history; it can be deleted.

---

## 1. Element leaves in the driver-LINQ bridge  (largest; the only item where a mode refuses something it should answer)

**Today.** `c.Orders.Where(f).OrderBy(k).Skip(n).Take(m).ToList()`, `.FirstOrDefault(pred)` and `Select(o => o.X).ToList()` over a group
run natively. Under explicit `MongoQueryMode.DriverLinq` (and a `Native` fallback) they throw: `MongoEFToLinqTranslatingExpressionVisitor`'s
constructor refuses any pending lookup with `ElementLeafAliases.Count > 0 || HasElementStages`. The refusal exists because the bridge
renders a lookup as a plain `$lookup` and nothing reads it; before the guard it returned **empty lists silently**.

**What the bridge does for a plain navigation list.** `Orders = c.Orders.ToList()` works in every mode through
`MongoProjectionBindingExpressionVisitor.Lookup.cs` `TryBindProjectedCollectionNavigation`: a `CollectionShaperExpression` over an
`ObjectArrayProjectionExpression` reading the `_lookup_<Nav>` array. It accepts only `ToList(Select(Where(root, corr), identity))`. Filtered
and ordered lists reach `ExtractFilteredIncludePipeline`, which turns `OrderBy/ThenBy/Skip/Take` with constant arguments into baked
`PipelineStages` and **throws `TranslationFailed` for any `Where`** (a filtered Include).

**Approaches.**
* *Client-side tail (smallest).* In the bridge, give the shaper the PLAIN array (the bridge already stages its own plain arrays,
  `StageBridgeOwnedLookups`) and re-apply `Where`/`OrderBy`/`Skip`/`Take`/`FirstOrDefault` over the materialized entities, as the native read side
  already does for a scalar `Select` (`IsSelfContainedLambda`). Costs: it fetches whole groups; the lambdas must be compiled outside a
  query, so a **query parameter in a filter or key cannot be used** (decline those; a constant filter is fine); ordering of strings
  would be CLR ordering, not the server's, so decline ordering by a string or compare to the native answer.
* *Server-side.* Teach `ExtractFilteredIncludePipeline` to carry a translated `Where` (there is no native translator on that path) or build the stages
  with the native translator when the query is otherwise bridge-bound. Larger, and the parameter story is the hard part
  (`InnerPredicate` is rendered through the placeholder table; `PipelineStages` are baked).

**Where to change.** The refusal in the bridge constructor (narrow it to the shapes you do not answer); `TryBindReferencedElementLeaf`
returns `false` unless `Select.Route == NativeRoute.Projection`, so the non-native route needs its own arm; the staged lookup's alias is
`ElementLeafAliases`-keyed and the bridge owns plain lookups with different aliases, so the read side needs the plain alias for the
correlation.

**Tests.** `NativeReferencedCollectionElementTests` runs every case through `Assert3`, which tolerates a `DriverLinq` refusal. Once a shape is
answered, make that case strict (see `Strict3` in `NativeReferencedCollectionBridgeTests`); keep a test that the shapes you still decline fail
loudly, never empty. Mutation-check the guard (remove it and see the empty-list failure).

## 2. Sharing one lookup array across element leaves  (optimization; probably do NOT do)

**Today.** Each distinct (filters, ordering, skip, limit) of a correlation stages its own `$lookup` with the filter, sort and limit inside its
sub-pipeline (`TryStageKeyedArrayLookup`, alias `_fN`). Identical stages are already deduplicated (same alias, compared up to lambda
parameter renaming). Aggregates over the same group read a plain array, so `new { N = c.Orders.Count, L = c.Orders.OrderBy(..).ToList() }`
does two lookups.

**The idea.** One plain array per correlation, and derive each leaf in `$project` with `$filter` / `$sortArray` (server 5.2+) / `$slice`.
It needs an aggregation-dialect node that yields an array (a sibling of `MongoFilteredSizeExpression`, which only yields the size; both
sites need the dispatcher-matrix entries in `MongoExpressionNodeCoverageTests`) and the element-scoped rendering that
`RenderFilteredSize`/`RenderMappedArrayReduce` already do.

**Why I would not.** The current design pushes the filter and the limit INTO the `$lookup`, so a `FirstOrDefault` or a `Take(3)` over a large
group transfers three documents, not the group. Deriving from a full array would load every group document for every outer document, a
regression in the common case to save an extra `$lookup` in the uncommon one. Only do it with a measured case, and keep the pushed-down
form when a limit or an element filter is present.

## 3. Constant and computed key parts  (adds capability; flips no pin)

**Shapes.** EF10 rewrites `GroupJoin(o, c => new { c.CustomerID, c.City }, o => new { o.CustomerID, City = "London" })` into
`!(c.CustomerID == null) && !(c.City == null) && c.CustomerID == o.CustomerID && c.City == "London"`, and
`new { c.CustomerID, Year = 1996 } equals new { o.CustomerID, Year = o.OrderDate.Value.Year }` into
`... && 1996 == o.OrderDate.Value.Year`. EF8/EF9 keep the anonymous objects whole (`TryExpandAnonymousKeys` already expands the members).

**Today.** `NativeCorrelationMatcher.TryMatchCorrelationKeys` requires every equality to be (outer property, inner property); `TryAssignSides`
fails when one side is a constant or a computed expression, so the whole correlation declines (loud in NativeOnly, "could not be
translated" otherwise).

**Design sketch.** Classify each conjunct by what it reads:
* reads only the outer row (`c.City == "London"`): a condition on the outer document, constant per outer row. Put it in the composite
  `let`/`$expr` match as `$eq: ["$$kI", <value>]` (add the local field to the `let`), or gate the array to `[]`. A parameterized value goes
  through the placeholder table.
* reads only the element (`1996 == o.OrderDate.Value.Year`): an element filter. Translate it with the element translator
  (`new MongoExpressionTranslator(targetEntityType, param)`) and AND it into `InnerPredicate`.
* property vs property: the existing key part.
Null semantics: an outer-only condition with the `!= null` guard means null matches nothing; keep it as a guard on the part.

**Cautions.** The bridge's own plain arrays (`StageBridgeOwnedLookups`) cannot carry either kind; an element filter is applied as
`Enumerable.Where` after the plain array, but an outer-only condition needs the array gated. A constant part makes the key a composite one
even with a single property pair, so the alias and `IsBridgeReadableArray` logic must account for it. Add tests in
`NativeAnonymousKeyJoinTests` next to the composite ones, and keep `Malformed_composite_correlations_decline_loudly` for what must still decline
(`ComparedToAConstant` there is exactly this shape, and would need to move).

## 4. Parameterized `Skip` / `Take` on an element leaf

**Today.** `NativeReferencedElementMatcher.TryPeelConstantPaging` accepts only a `ConstantExpression`. `Take(n)` with a captured variable is a
query parameter (EF8/EF9 a prefixed `ParameterExpression`, EF10 a `QueryParameterExpression`; `NativeQueryParameter` hides the difference),
so the shape declines. The values are baked ints on the lookup (`ElementSkip`, `ElementLimit`) rendered into the cached template.

**What is needed.**
1. Carry the parameter on the lookup (a `MongoExpression`, like `InnerPredicate`) instead of an `int?`, and render `$skip`/`$limit` through
   the placeholder table in `RenderLookup`.
2. **`$limit: 0` is a server error and a negative value is invalid.** The top-level pipeline normalizes this at `Build` time
   (`Query/AGENTS.md`, *Paging*: "Normalization covers top level and `$unionWith`, not `$lookup` sub-pipelines"), so the lookup form needs
   the same per-execution rule: a zero limit must produce an EMPTY array (an always-false `$match` stage), a negative `Take` throws as LINQ does.
   This is the part to design first.
3. The alias identity (`_fN`) must tell parameters apart, not only their values (compare the parameter names / expressions, as the filters
   are compared); `TryBindReferencedElementLeaf` compares `l.ElementLimit == ...` and must change with it.
4. Keep the "outermost operators only" rule: a `Where` after `Skip`/`Take` filters the page and must still decline.

**Tests.** Extend `NativeReferencedCollectionElementTests` with `NativeModeAssert.TwiceWithDifferentValues` over `Take(n)` with values `3`,
`0` and (loud) `-1`, on one context so the second run hits the compiled-query cache, in NativeOnly and Native.

---

## Not in scope here / known and accepted

* Integer division in a computed aggregate selector fails under `DriverLinq` (the driver turns `int / int` into a fractional `$divide` and
  throws on truncation). Loud, and documented in `Query/AGENTS.md`.
* Element-leaf declines worth knowing: TPH-derived target (a keyed lookup has no discriminator narrowing), an eager cross-collection
  navigation on the element type, `Take` on a `FirstOrDefault`, a non-positive constant `Take`, a second `OrderBy`, an ordering key that is not a
  plain default-stored scalar in CLR order, and a `Where` written after `Skip`/`Take`.
* Several element leaves of one element type in a projection work only because each carries its projection member as an `Identity`
  (`ArrayAliasProjectionExpression`, `RootReferenceExpression`); a new array-shaped leaf must do the same.
