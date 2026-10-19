# Handoff: referenced-collection aggregates (EF-322c), follow-ups

**Branch** `EF-322c`, HEAD `0fb65273`, working tree clean, **nothing pending a user decision**. Follow-ups 1, 3 and 4 below are DONE
(commits `cf7ef2a0`, `426bd2f2`, `0fb65273`, each preceded by its failing-tests commit); only follow-up 2 remains, and the advice is not to do it.
At HEAD EF8, EF9 and EF10 build with 0 warnings and pass every project (passed: unit 1,708 / 1,708 / 1,701; spec 4,714 / 5,027 /
4,768; functional 6,770 / 6,816 / 6,891; skips are the expected encryption and Atlas-gated ones).

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

## 1. Element leaves in the driver-LINQ bridge  (DONE, `cf7ef2a0`)

Not done as the client-side tail this section proposed. The shaper the bridge feeds is the NATIVE one (it reads each leaf from the member's
alias in a whole document), so a tail would have had to be applied only on the bridge path, and `Skip` is not idempotent over an
already-paged array. Instead the bridge renders each element leaf's lookup natively (`RenderElementLeafLookup`: a one-stage
`MongoPipelineFactory`, built with this execution's parameters) and copies its array to every alias in `ElementLeafAliases` with `$set`
(`EmitLookupStages`). A filter parameter is therefore read per execution and ordering is the server's. The constructor refuses a lookup
with element stages but no leaf, and an alias that is dotted or a stored field of the root (the `$set` would overwrite it).
`NativeReferencedCollectionElementTests` runs every shape strictly in all three modes.

Known limit: a computed sibling leaf (`N = c.Orders.Count`) beside an element leaf cannot be read from whole documents, so it fails at
compile time under `DriverLinq` (loud; the native modes answer it). Fixing it means reading computed leaves in the mixed reader.

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

## 3. Constant and computed key parts  (DONE, `0fb65273`)

`NativeCorrelationMatcher.TryMatchCorrelation` classifies each equality of a correlation that is not outer-vs-inner: on the ELEMENT alone
against a constant or query parameter it is an element filter (`r.Zone == 1`, `1996 == r.OrderDate.Year`, `r.Zone + 1 == 2`); on the OUTER row
alone (`c.City == "London"`) it gates the group (`LookupExpression.OuterConditions`, rendered as a `let` plus a `$match $expr` in
`RenderLookup`). Only equalities are admitted, so `r.Price > 100` is still never dropped. EF10's GroupJoin rewrite spelling
(`object.Equals((object)x, (object)y)`, a guard on a constant) is normalized. The bridge applies element filters over its plain array and leaves
an outer gate unresolved, so it fails loudly under `DriverLinq`.

Still declined: a computed expression on BOTH sides (`c.Year == o.OrderDate.Year`: a key part with a computed inner side), a null constant, and
any non-equality conjunct. The `GroupJoin_aggregate_anonymous_key_selectors*` pins stay: they also aggregate `Sum(o => o.CustomerID.Length)`.
Gotcha found on EF8/EF9: a query parameter is a `ParameterExpression` there, so `TryAssignSides` must not take it for the element
(`IsDependentRoot`).

## 4. Parameterized `Skip` / `Take` on an element leaf  (DONE, `426bd2f2`)

`ElementSkip`/`ElementLimit` are `MongoExpression`s (a constant or a query parameter) rendered through the placeholder table; parameters are
told apart by name (`LookupExpression.SameElementCount`). `MongoPipelineFactory.NormalizePagingStages` now recurses into `$lookup`
sub-pipelines: zero is an empty array, a negative count throws. A non-positive CONSTANT `Take` still declines. The bridge gets it through the
native rendering. Tests: `Parameterized_Take/Skip_*` in `NativeReferencedCollectionElementTests`, all three modes.

## Not in scope here / known and accepted

* Integer division in a computed aggregate selector fails under `DriverLinq` (the driver turns `int / int` into a fractional `$divide` and
  throws on truncation). Loud, and documented in `Query/AGENTS.md`.
* Element-leaf declines worth knowing: TPH-derived target (a keyed lookup has no discriminator narrowing), an eager cross-collection
  navigation on the element type, `Take` on a `FirstOrDefault`, a non-positive constant `Take`, a second `OrderBy`, an ordering key that is not a
  plain default-stored scalar in CLR order, and a `Where` written after `Skip`/`Take`.
* Several element leaves of one element type in a projection work only because each carries its projection member as an `Identity`
  (`ArrayAliasProjectionExpression`, `RootReferenceExpression`); a new array-shaped leaf must do the same.
