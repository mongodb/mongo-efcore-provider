# EF-322 step 3 (projection long tail) — Task 0 spike findings

*Run 2026-08-06 against `NativeQueryOngoing` at `f4d50b5a` (clean tree, tree still clean at the end — the
throwaway worktree has been removed and `git worktree list` verified; the three `.claude/worktrees/agent-*`
worktrees belong to other sessions and were not touched). Input: `docs/native-query-status-EF-322.md` §9.8
item 3, §7, §9.1, §9.7, plus `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`.*

**Tagging convention, applied strictly.** Every claim below is one of:
**MEASURED** (I ran it this session; the command is in §7 and the output is quoted or reproducible) ·
**READ** (established by reading source at `f4d50b5a`; no execution) ·
**INFERRED** (drawn from MEASURED/READ facts but not itself observed) ·
**UNVERIFIED** (I did not establish it — said so explicitly so the next agent does not mistake it for settled).

**Trap compliance, stated up front.** (a) Every bucket count below classifies `Assert.Throws`/`Assert.ThrowsAny`
**before** substring-matching the message. I ran the naive version too, and it reproduced the documented
over-count exactly: the projection bucket comes out **1030** naively and **881** correctly — a 149-case error,
larger than the 82→88 one recorded in the previous slice. (b) No claim here rests on MQL shape as a routing
signal; every routing claim is a `MONGODB_EF_NATIVE_ONLY=1` run. (c) Both `MONGODB_URI` and `ATLAS_URI` were
unset for every run, so TestContainers booted its own `mongodb/mongodb-atlas-local` — confirmed real by the
VectorSearch numbers reproducing (114 passed under `Native`, 94/20 under `NativeOnly`, matching §4).

---

## Headline — five findings, in order of how much they change the slice

1. **The 873/881 bucket's LABEL is wrong for 363 of its members, and the doc's own sizing of step 3 is wrong by
   about 8×.** MEASURED, by instrumenting the *actual* decline site rather than reading the exception message:
   of the 881 cases in the "Non-entity projection not natively representable" bucket, **only 360 have the
   projection binder as their sole decline**. 363 declined somewhere else *first* (untranslatable `Where`
   predicates, GroupBy key/aggregate binders, scalar-aggregate binders, `Distinct`, post-terminal slot guards)
   and are in this bucket only because the query happens to *return* a non-entity type. Those are joins/GroupBy/
   predicate-breadth work wearing a projection label.
2. **"One structural change unblocking bare scalars, bare entities and bare arrays together" does not size to
   873. It sizes to about 100.** MEASURED: lifting the bare-body boundary, with the translator exactly as it is
   today, addresses **78** spec cases outright and **100** if a bare *value* leaf is admitted too. A further
   104 need the boundary **plus** a composition-site relaxation. Everything else in the 881 needs per-feature
   translator work, entity-leaf projection support, or is not projection work at all.
3. **The boundary is TWO sites that must move in lockstep, and moving only the obvious one is measured to be a
   REGRESSION, not a partial win.** I mutated the emit-side gate to admit a bare field-resolvable body. Under
   `NativeOnly` failures went **2241 → 2245** (worse), and under **default `Native`, 0 → 105 failures** —
   97 of them a hard `ArgumentException` at query-compile time
   (`Expression of type 'QueryingEnumerable<BsonDocument,BsonDocument>' cannot be used for return type
   'IAsyncEnumerable<UInt32>'`). The read-side half is
   `MongoProjectionBindingRemovingExpressionVisitor`'s `projection.Alias is null ⇒ return DocParameter` branch —
   **proven to be the site by mutation** (replacing that `return` with a distinctive throw makes
   `Select_scalar_primitive` fail with exactly that throw).
4. **"Bare entities" is not part of this boundary at all.** MEASURED with a purpose-built flat model:
   `Select(x => x)` and `Where(...).Select(x => x)` **already succeed under `NativeOnly`** — a bare entity
   projection has an entity result CLR type, so it never reaches `VisitProjectedQuery` and is not in the 881
   bucket (there are **zero** `BAREBODY`/entity cases in the whole bucket, MEASURED). What is blocked is an
   entity leaf *inside* a `new {...}` — a different mechanism (the mixed shaper), which is where §9.5's EF-356
   silent-wrong-data bug already lives. Bare **arrays** *do* share the boundary: bare primitive array
   (`Select(b => b.Tags)`) and bare owned-collection array (`Select(b => b.Posts)`) both decline at the same
   emit-side site (MEASURED).
5. **§4/§9.8's account of the 16 residual `VectorSearch` cases is exactly right, including the subtle half.**
   MEASURED: 4 are `VectorSearch_with_projection` declining as `BAREBODY/FIELD_OK` (family A — the bare-scalar
   boundary), and 12 are the entity-and-score shapes declining on the **entity leaf**
   (`LEAF/new/NO_XLATE/ENTITY/...`), not the score leaf. Step 3 "carries" them only if it does **both** halves;
   the boundary alone carries 4 of 16.

---

## Baseline re-measurement (§7 is stale and is superseded here)

MEASURED, EF10 specification suite at `f4d50b5a`:

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4593** | **0** | 17 | 4610 |
| `NativeOnly` | **2352** | **2241** | 17 | 4610 |

So **2241** spec tests currently require driver-LINQ (§7 says 2395, measured at pre-rebase `229294f`; seven
joins slices and the VectorSearch slice have landed since). Buckets, `Assert.*` classified first:

| Count | Cause | §7's figure |
|---:|---|---:|
| **881** | "Query projects a non-entity result" | 873 |
| **696** | "Query is not natively representable" | 794 |
| **457** | `Assert.Throws` / `ThrowsAny` exception-type mismatch | 559 |
| **82** | `Assert.Contains` message-text mismatch | 26 |
| **54** | Reference-nav `$lookup` not supported | 54 |
| **48** | `ArgumentOutOfRangeException` (index) — shaper gap | 66 |
| **13** | Non-constant regex (EF-247) | 13 |
| **8** | `Not` over an unsupported subtree | 8 |
| **2** | `Throws_on_concurrent_query_first` MQL string | 2 |
| **2241** | **total** | 2395 |

The `Assert.Throws`/`Assert.Contains` *pair* moved 585 → 539, consistent with §7.2's own caution to treat the
pair as stable rather than each row. **UNVERIFIED:** I did not re-derive §7.1's per-class table.

---

## Q1 — what IS the bare-projection boundary, mechanically?

**Answer: it is two sites, on opposite sides of the shaper, and the docs describe only the first.**

**Site 1 — emit side. MEASURED + READ.**
`NativeProjectionBinder.TryPopulateNativeProjection` (`NativeTranslation/NativeProjectionBinder.cs:62`) opens
with `switch (selector.Body)`, whose only arms are `NewExpression` (with `Members != null`) and
`MemberInitExpression`; everything else hits `default: return false`. The caller,
`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` (`:278–281`), turns that `false` into
`MarkNotNativelyRepresentable()`. So the doc's phrasing — *"a bare selector body never populates
`Projection`"* — is true but understates it: the binder does not merely leave `Projection` empty, it actively
drives `Route` to `Fallback`. MEASURED via instrumentation: every bare-body case in the 881 reports
`site=…TranslateSelect:280`, `route=Fallback`.

**Site 2 — read side, and this is the one nothing in the docs names. MEASURED (by mutation).**
Even with site 1 opened, the shaper still resolves the bare body to a *whole document*.
`MongoQueryExpression.ApplyProjection` (`Expressions/MongoQueryExpression.cs:98–112`) registers each mapped
projection with `AddToProjection(expression, projectionMember.Last?.Name)`. For a bare selector body the
`ProjectionMember` is the empty one, so `Last` is `null`, and `AddToProjection`'s fallback
(`(expression as IAccessExpression)?.Name`) also yields `null` — so the `ProjectionExpression.Alias` is
`null`. `MongoProjectionBindingRemovingExpressionVisitor.VisitExtension`'s `ProjectionBindingExpression`
case then hits (`Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:81–85`):

```csharp
// Alias is null when the projection's expression has no natural name —
// the BsonDoc itself is the value.
if (projection.Alias is null)
{
    return DocParameter;
}
```

so the compiled DOM shaper's return type is `BsonDocument`, `CompileShapedQuery`'s
`projectedType = shaperLambda.ReturnType` is `BsonDocument`, and the executor it builds is
`QueryingEnumerable<BsonDocument, BsonDocument>` — which EF Core then rejects against the query's real return
type. **Proven by mutation:** replacing that `return DocParameter` with
`throw new InvalidOperationException("SPIKE-NULL-ALIAS-DOCPARAM-BRANCH")` makes
`NorthwindSelectQueryMongoTest.Select_scalar_primitive` fail with exactly that message.

**Downstream of "`Select.Projection` is empty", concretely (READ).** `Route` computes `Fallback`;
`MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery` finds `projectedEntityType == null` (the result
CLR type is not an entity) and routes to `VisitProjectedQuery`, whose `Route == NativeRoute.Projection` branch
therefore does not fire, so it falls through to
`ThrowIfNativeOnlyForbidsFallback(queryMode, "Query projects a non-entity result")` (`:343`) and then to the
driver push-down (`ProjectionAnalyzer.CanPushDown`) or the mixed client-side shaper.

**So what would "the one structural change" have to be? (INFERRED from the above, plus the measurement in
finding 3.)** Three things in lockstep, not one:

1. a new arm in `TryPopulateNativeProjection`'s `switch` admitting a bare body, routed through the existing
   `TryTranslateLeaf` so every leaf kind it already knows (field, owned count, filtered count, arithmetic,
   owned array, `__score`) is reachable from a bare body for free;
2. an **agreed synthetic alias** for the single result value, written on *both* sides — the emit side
   (`Select.Projection`) and the shaper side (`ApplyProjection`/`AddToProjection`) — because these are two
   independently-derived alias spaces and this is the same alias-agreement hazard `Query/AGENTS.md`'s
   array-valued-projections note documents. (The driver-LINQ path's own precedent name is `_v`, visible in the
   `{ "$project" : { "_id" : 0, "_v" : null } }` stage §7 quotes.)
3. a read-side arm so a single-alias projection materializes the *value*, not `DocParameter`.

Do (1) without (2)+(3) and the result is measured in finding 3: a hard crash under default `Native`, i.e. a
graceful fallback turned into a loud failure for 105 spec cases. **Note it is loud, not silent** — the type
mismatch is caught by `Expression.Lambda` at compile time. That is a meaningful safety property for this slice
and is worth preserving deliberately rather than by luck.

---

## Q2 — RE-DERIVED composition of the (881, not 873) bucket

**Method (MEASURED).** In a throwaway worktree I recorded, per query: every `MarkNotNativelyRepresentable()`
call site (via `[CallerMemberName]`/`[CallerLineNumber]`, first and full de-duplicated list), a sub-reason from
`NativeProjectionBinder` (which arm declined, the node kind, and whether the *existing* translator can already
express the expression as a field or as a value), and the shaper's result kind — and surfaced all of it **in
the `NativeOnly` exception message**, so the `.trx` carries the classification and attribution is per test
method and exact rather than by sampling. The instrumented run reproduced the baseline exactly
(2241 failed / 2352 passed / 17 skipped), so the instrumentation is behaviour-preserving.

### Q2.1 — causal split: is this bucket even a projection bucket?

| Cases | Causal position of the projection binder |
|---:|---|
| **360** | it is the **sole** decline — genuinely projection work |
| **158** | it declined **first**, but a composition site also declined — needs projection work **and** a composition relaxation |
| **363** | **something else declined first** (262 of them never reached a projection decline at all) |

The 363's *first* decline sites, MEASURED (names from source):

| Cases | Site | What it really is |
|---:|---|---|
| 85 | `NativeSlotPopulator:107` | a `Where` **predicate** the translator cannot express — predicate breadth |
| 62 | `TranslateSelect:197` | `NativeGroupByBinder.TryBindGroupProjection` declined — **GroupBy** |
| 58 | `TranslateGroupBy:1448` | computed key / element selector / user result selector — **GroupBy** |
| 56 | `BindAggregateOrFallback:1368` | `NativeCardinalityBinder.TryBindAggregate` declined — **scalar aggregates** |
| 34 | `NativeSlotPopulator:94` | post-terminal slot operator |
| 24 | `NativeSlotPopulator:118` | an **`OrderBy` key** the translator cannot express |
| 20 | `TranslateDistinct:1318` | `Distinct` |
| 10 | `TranslateGroupBy:1441` | `GroupBy` after a terminal |
| 8 | `TryTranslateSetOperation:2448` | set-op scope gate |
| 4 | `NativeSlotPopulator:215` | the catch-all (`Contains`/`ElementAt`/`Last`/`Cast`/…) |
| 2 | — | no decline recorded |

**INFERRED consequence:** roughly **41%** of the "projection long tail" is other people's work. It will be
closed by GroupBy breadth (§9.7 item 5), predicate breadth, and the aggregate binder — not by step 3. Anyone
sizing step 3 off "873" is over-counting by about 2.4×.

The 158's *other* sites (a case can have more than one): set operation 68, scalar aggregate 44, `Distinct` 40,
reducer 16, `Where` 10, `OrderBy` 8, other 2. **INFERRED:** these are the "projection then composed with X"
shapes — `Select(...).Union(...)`, `Select(...).Distinct()`, `Select(...).Count()`. They need the projection
fix *and* the corresponding scope gate widened, exactly as slice C1/C2 did for set operations.

### Q2.2 — family breakdown (what kind of work each case actually needs)

Families, MEASURED. "already a FIELD / already a VALUE" means the **existing** `MongoExpressionTranslator`
already resolves the expression — no new translator needed.

| Family | Whole 881 | Sole-cause 360 | What it needs |
|---|---:|---:|---|
| **A.** BARE body, already a resolvable **field** | 178 | **78** | the boundary, and nothing else |
| **B.** BARE body, already a translatable **value** | 56 | **22** | the boundary + admitting a value leaf |
| **C.** BARE body, translator **cannot** express it | 166 | 128 | the boundary **+** a per-feature translator |
| **D.** WRAPPED body, **entity / entity-collection** leaf | 93 | 58 | entity-leaf projection (a second mechanism) |
| **E.** WRAPPED body, translatable value leaf (gate only) | 12 | 12 | leaf-gate widening only |
| **F.** WRAPPED body, untranslatable non-entity leaf | 114 | 62 | per-feature translators only — **boundary irrelevant** |
| **Z.** no projection decline recorded | 262 | 0 | not projection work |

**The answer the brief asks for.** The "one structural change" of Q1, with the translator exactly as it stands:

- unblocks **78** sole-cause cases outright (family A);
- **100** if a bare *value* leaf is admitted too (A + B) — an incremental gate widening, not new translation;
- a further **104** (A + B inside the 158) need the boundary **plus** a composition-site relaxation, so they
  are reachable but belong to a second slice;
- leaves **190** sole-cause cases (C + F) needing **per-feature translator work**, **58** needing the
  **entity-leaf** mechanism, and **363** that are not projection work at all.

**So: ~100 of 881 from the structural change; ~250 more from per-feature translators + entity leaves; the rest
is elsewhere.** That is the number that should size step 3, not 873.

### Q2.3 — what the per-feature tail actually consists of

MEASURED, sole-cause families C and F by node kind, with the recurring named members:

| Cases (sole) | Feature | Notes |
|---:|---|---|
| 32 | `Conditional` (`?:`) / `Coalesce` (`??`) | one `$cond`/`$ifNull` translator covers both; the single biggest self-contained item |
| ~16 | **date-part extraction** | `DateTime.Year/Month/Day/Hour/Minute/Second/Millisecond/DayOfYear/TimeOfDay`, `AddMinutes` |
| 20 | `Add` on `String` / numerics | `$concat` and the arithmetic-leaf gate widened to a bare body |
| 16 | `Convert` (type-changing casts) | includes `Cast_on_top_level_projection_brings_explicit_Cast` |
| ~24 | `MemberAccess` the translator declines | **mostly a navigation hop** (`o.Customer.City`) — this is **joins**, not projection |
| ~20 | `TransparentIdentifier.Outer/Inner` entity leaves | **joins** again (`join … select new { a, b }`) |
| ~18 | `Enumerable.ToList/ToArray/AsEnumerable/FirstOrDefault` | subquery materialization in a projection — its own feature |
| 10 | `EF.Property` leaf | small, self-contained gate item |
| 12+6 | `New`/`MemberInit` **nested inside** a projection | nested anonymous/DTO shapes |
| 10 | `NewArrayInit`/`ListInit` | array-literal projections |
| 4 | genuine **client methods** (`ClientMethod`) | can never be native — these stay fallback forever, or become a documented decline |

**INFERRED, and it matters for scoping:** at least ~44 of the sole-cause "computed" tail is really
cross-collection navigation in a projection, i.e. it will be closed by the joins work stream (§9.8 step 1's
remainder), not by a projection translator.

---

## Q3 — does one change really unblock scalars, entities and arrays together?

**Partly. It is true for scalars and arrays; it is FALSE for entities, and the entity half is the bigger one.**

**Bare scalar — yes.** MEASURED (probe, `NativeOnly`): `Select(b => b.Title)` declines at
`TranslateSelect:280` with `proj=BAREBODY/FIELD_OK/SCALAR/MemberAccess`, `nsites=1`.

**Bare array — yes, same site, but two different sub-cases.** MEASURED:
- primitive collection `Select(b => b.Tags)` → `BAREBODY/FIELD_OK/SCALAR_ARRAY/MemberAccess` — identical
  disposition to a bare scalar, so it comes free with the scalar fix (family A);
- owned entity collection `Select(b => b.Posts)` → `BAREBODY/NO_XLATE/ENTITY_ARRAY/Extension`, result shaper
  `CollectionShaperExpression`. Same **gate**, but the leaf is a `MaterializeCollectionNavigationExpression`
  that only the array arm of `TryTranslateLeaf` handles — and that arm's alias-agreement rule requires the
  alias to equal the navigation's containing element name. **INFERRED:** routing the bare body through
  `TryTranslateLeaf` makes it reachable, but the synthetic alias chosen in Q1's step (2) must then be the
  element name (`"Posts"`), not a generic `_v` — otherwise slice 8's alias-agreement invariant is violated by
  construction. That is a real design constraint the slice must not discover late.
- also on the same site: bare owned-collection `.Count` (`BAREBODY/VALUE_OK/SCALAR/Call/Queryable.Count`),
  confirming §4/§5's account that this is the SP3-wide boundary and not count-specific.

**Bare entity — NO, and this is a correction to §9.8.** MEASURED with a purpose-built flat model:
`Select(x => x)` returns 4 rows and `Where(x => x.Name != null).Select(x => x)` returns 2 rows, **both under
`MongoQueryMode.NativeOnly`** — i.e. already native. Mechanism (READ): the result CLR type *is* an entity type,
so `VisitShapedQuery`'s `projectedEntityType` lookup succeeds and the query never enters `VisitProjectedQuery`
at all. Corroborating MEASUREMENT: there are **zero** `BAREBODY`/`ENTITY` cases anywhere in the 881.

What is genuinely blocked, and what §9.8 probably means, is an **entity leaf inside a projection**:
`Select(x => new { E = x })` declines at `TranslateSelect:280` with `LEAF/new/NO_XLATE/ENTITY/Extension`
(MEASURED). That is 93 cases in the 881 (58 sole-cause) and is a **different mechanism** — it is the mixed
shaper, the same code path that carries §9.5's EF-356 (mixed whole-entity + computed projection returns
silently wrong values). **This changes the slice's shape fundamentally:** step 3 is not one boundary, it is a
boundary plus an entity-leaf projection mechanism, and the second one has a known silent-wrong-data bug sitting
in it.

---

## Q4 — interactions and dependencies

### (a) The 16 residual `VectorSearch` cases — §9.8's claim VERIFIED, with the caveat sharpened

MEASURED (all 16 are in the 881; the other 4 of the 20 `NativeOnly` `VectorSearch` failures are EF-382's
pre-filter gap and are not in this bucket):

| Cases | Test method | Decline classification |
|---:|---|---|
| 4 | `VectorSearch_with_projection` | `BAREBODY/FIELD_OK/SCALAR/MemberAccess/String\|Book.Author` — **family A** |
| 4 | `VectorSearch_with_projection_of_entity_and_score` | `LEAF/new/NO_XLATE/ENTITY/Extension/Book` |
| 4 | `…_of_entity_and_score_using_EF_Property` | `LEAF/new/NO_XLATE/ENTITY/Extension/Book` |
| 4 | `VectorSearch_with_projection_of_constructed_entity_and_score` | `LEAF/new/NO_XLATE/ENTITY/MemberInit/Book` |

So §4's statement — *"what declines them is the entity leaf beside the score leaf, not the score leaf"* — is
**confirmed by direct measurement of the declining node**, not merely inferred. Note the 8
`VectorSearch_with_projection_of_score` cases are **absent** from this bucket: the VectorSearch slice's
`__score` leaf work already made them native, which is the positive control for that claim.

**Consequence for §9.8's "carries 16 of the 20 with it":** true only of the full step 3. The bare-scalar
boundary alone carries **4**; the other **12** need the entity-leaf mechanism.

### (b) EF-362 and EF-365

- **EF-362** (`OwnsOne`-hop array leaf, `Select(b => new { b.Title, b.Home.Notes })`) — **READ:** it is a
  `LEAF` case in a *wrapped* body, so the bare-body boundary does not touch it either way. Its own note is
  explicit that it needs a path-preserving `$project` (`{"Home.Notes": "$Home.Notes"}`) rather than a
  relaxation. **INFERRED interaction, and it is a real one:** if step 3 introduces a synthetic-alias scheme
  (Q1 step 2) that decouples the emit alias from the document path, that is *the same mechanism* EF-362 needs.
  Worth deciding deliberately whether step 3's alias scheme is designed to subsume EF-362 or to sidestep it.
  **UNVERIFIED:** I did not build or probe a path-preserving `$project`.
- **EF-365** (`CanRender` guard on a filtered-count projection leaf) — **READ:** orthogonal to the boundary
  (it is a wrapped leaf), but it is on the same `TryTranslateLeaf` path step 3 will be editing heavily, and its
  ticket already records that deleting one call site converts a hard-fail into a working fallback. **INFERRED:**
  cheapest to fold into whichever step-3 slice touches `TryTranslateLeaf`, since it is a deletion plus a
  re-baseline, and its "breadth still to verify" list (`Contains`/`$in`, unary `Not`, bare nullable bool, mixed
  projection) overlaps exactly with the leaf-kind breadth step 3 has to establish anyway.

### (c) Does the one-pass streaming materializer (SP7 Phase 1) constrain the design? **No. READ.**

`VisitProjectedQuery`'s `Route == NativeRoute.Projection` branch calls `CompileShapedQuery(...)` with
`allowStreaming: false` (`MongoShapedQueryCompilingExpressionVisitor.cs:294–301`), and `CompileShapedQuery`
gates streaming on `allowStreaming && nativeFactory != null && …`. So every native projection already
materializes through the DOM shaper, and a bare-scalar projection would too. SP7 Phase 1 neither helps nor
constrains step 3. **INFERRED corollary worth recording:** step 3 therefore *widens the set of queries that are
native but non-streaming*, which is a (small) allocation regression relative to nothing, and a reason SP7
Phase 2's reducer/aggregate streaming and step 3 will eventually want to be considered together — but not a
blocker in either direction.

### (d) The bulk path (`ExecuteUpdate`/`ExecuteDelete`) — **no coupling found. READ.**

The EF9+ bulk region (`#if !EF8`, `MongoShapedQueryCompilingExpressionVisitor.cs:1077–1334`) builds its
`MongoBulkPlan` delegates from `BuildFilter`/`BuildUpdate`/`BuildIdDocumentQuery`/`RenderSelfReferencingValue`,
each of which constructs a `MongoEFToLinqTranslatingExpressionVisitor` directly. Grepping that whole region for
`Select.Projection`, `NativeProjectionBinder`, `Select.Route` and `NativePipeline` returns **nothing**. So §9.2's
"the bulk path shares the bridge" remains true and unchanged, but it shares the *bridge*, not the projection
machinery — step 3 has no bulk-path interaction beyond the general cutover fact.

---

## Recommended slice shape and size

**Step 3 should be SEVERAL slices, not one.** §9.7's "it is not one feature" is correct and this spike sharpens
it: the 881 is at least four independent work streams, and the largest single one (363 cases) is not projection
work at all. Recommended decomposition and order:

**3a — the bare-projection boundary (the "structural change").** Emit-side arm + agreed synthetic alias +
read-side single-value arm, moved in **lockstep** (finding 3 measured what happens otherwise). Routes the bare
body through the existing `TryTranslateLeaf` so every leaf kind already supported becomes reachable from a bare
body. Expected win: **78–100** spec cases, plus the bare owned/primitive array and bare `.Count` residuals §5
lists (invisible to Northwind, provable only functionally). Size: **comparable to EF-368/EF-372 — a medium
slice.** The alias-agreement question (Q3) and the loud-vs-silent property of the read-side mismatch both need
pinning tests. Must include the array-leaf alias constraint from Q3 or it will be discovered as a silent bug.

**3b — entity leaf in a projection.** `new { E = x }`, `new { Book = e, Score = … }`, `new { Book = new Book{…}, Score = … }`.
Expected win: **58 sole-cause** (93 in the bucket), **plus 12 of the 16 `VectorSearch` residuals**. This is
where **EF-356** lives, so 3b should either fix EF-356 or explicitly pin it — shipping an entity-leaf widening
on top of a known silent-wrong-data mixed shaper is the highest-risk thing in the whole of step 3. Size:
**larger than 3a**, and the one to spike separately.

**3c — the computed-leaf translator tail**, itself split by feature and ordered by measured size:
`Conditional`/`Coalesce` (32) → date parts (~16) → `Add`/`$concat` (20) → `Convert`/casts (16) →
`EF.Property` (10) → array literals (10) → subquery materialization (~18). Each is small, independent and
testable; none is worth a slice on its own, several together are. Note `Convert` overlaps §4's Guard-A
integer-`Divide` decision and the 10 spec cases §9.3 says cannot be re-baselined by the usual instrument.

**3d — composition relaxations** (`Select(...)` then `Union`/`Distinct`/aggregate/reducer): **104** cases,
and only reachable once 3a exists. Mechanically the same shape as slice C1/C2 did for set operations.

**NOT step 3, and should be re-attributed in `docs/native-query-status-EF-322.md`:** the 363. They belong to
GroupBy breadth (130), predicate/sort breadth (109), the scalar-aggregate binder (56), `Distinct` (20), the
post-terminal guards (34) and the operator catch-all (4). Several of these are cheaper than anything in step 3
and are currently hidden behind a label that says "projection".

**Recommended immediate order:** 3a → 3b → 3d → 3c, with the GroupBy/predicate re-attribution filed as its own
work rather than absorbed. 3a first because it is the smallest thing that is a genuine structural change and
because 3d depends on it; 3b before 3c because it is where the correctness risk is and because it unlocks the
VectorSearch residual.

---

## Owner rulings — ANSWERED 2026-08-06

*Settled by the owner on the strength of this spike. Treat as decisions, not suggestions.*

1. **Is step 3 still next, given the ~8× re-sizing?** → **Yes — proceed with 3a.** The boundary is a genuine
   structural prerequisite: 3d's 104 cases depend on it, it carries 4 of the 16 `VectorSearch` residuals, and
   it has to happen regardless of how the rest is ordered. **But the ranking for step 4 onward is no longer
   trustworthy** and is to be re-derived separately — if this bucket's label was 41% wrong, §9.7's other
   labels are suspect by the same method.
2. **Does 3b fix EF-356 or pin it?** → **Fix it in 3b.** 3b opens the mixed shaper, which is exactly where
   EF-356's silent-wrong-data path lives; shipping new code over a known-broken path is not acceptable.
   This raises 3b's risk, which the spike already rates the highest in step 3 — plan accordingly.
3. **Should 3a's alias scheme subsume EF-362?** → **Yes — design 3a's aliasing with EF-362 in view.** It wants
   the same alias/path decoupling; design the scheme once rather than twice.
4. The 4 genuine client-method cases that can never go native → the §9.3 re-baseline bucket, not a defect.

---

## Questions only the owner can settle

1. **Is step 3 still "the next work", given that 41% of its headline number belongs to GroupBy, predicate
   breadth and the aggregate binder?** Re-attributing the 363 makes step 3 (~518 cases across four slices)
   comparable in size to, not obviously larger than, the GroupBy/predicate items §9.7 ranks fifth. If the
   ordering was chosen because "873 is the largest bucket", the premise no longer holds.
2. **Does 3b fix EF-356 or pin it?** Widening entity-leaf admission on a mixed shaper with a live
   silent-wrong-data bug is exactly the direction §9.5 and `Query/AGENTS.md`'s GOVERNING HAZARD note warn
   against. Fixing EF-356 first is the conservative call and is not currently scheduled.
3. **Should 3a's synthetic alias scheme be designed to subsume EF-362?** They want the same decoupling of
   projection alias from document path. Doing it once is cheaper; doing it now makes 3a bigger.
4. **A smaller one, flagged so it is not decided by accident:** 4 sole-cause cases are genuine *client methods*
   (`ClientMethod`) that can never go native. They will still fail under `NativeOnly` after all of step 3.
   They should be moved to the exception-shape/re-baseline bucket (§9.3) rather than counted as coverage.

---

## §7 — Reproduction

Everything below was run this session at `f4d50b5a`, with both `MONGODB_URI` and `ATLAS_URI` **unset**.
All mutations lived in a throwaway worktree which has been **removed** (`git worktree list` verified).
Nothing under `src/` in the main tree was touched.

```bash
S=<scratch>/step3-spike
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"

# Baseline, both axes
env -u MONGODB_URI -u ATLAS_URI dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory $S
#   -> Passed 4593 / Failed 0 / Skipped 17 / Total 4610
env -u MONGODB_URI -u ATLAS_URI MONGODB_EF_NATIVE_ONLY=1 dotnet test … \
  --logger "trx;LogFileName=nativeonly.trx" --results-directory $S
#   -> Passed 2352 / Failed 2241 / Skipped 17 / Total 4610
#   VectorSearch subset: Native 114 passed / 4 skipped; NativeOnly 94 passed / 20 failed  (Atlas really ran)

# Instrumented sweeps (throwaway worktree)
git worktree add $S/wt f4d50b5a
#  (1) MongoSelectDefinition.MarkNotNativelyRepresentable gains [CallerMemberName]/[CallerLineNumber]/
#      [CallerFilePath] and records SpikeDeclineSite (first) + SpikeAllSites (deduped list).
#  (2) NativeProjectionBinder records SpikeProjectionReason at each `return false`, classifying the node
#      (BAREBODY vs LEAF), whether the EXISTING translator resolves it (FIELD_OK / VALUE_OK / NO_XLATE),
#      and the node's kind (SCALAR / SCALAR_ARRAY / ENTITY / ENTITY_ARRAY).
#  (3) MongoShapedQueryCompilingExpressionVisitor appends all of it to the two NativeOnly throw reasons,
#      so the .trx carries the classification and attribution is per test method.
#  -> reproduced the baseline EXACTLY (2241/2352/17), i.e. behaviour-preserving.

# Mutation M1 — open the emit-side gate ONLY (finding 3)
#  NativeProjectionBinder: new `case MemberExpression bareMember when translator.TryTranslateField(...)`
#  arm adding a single MongoProjection aliased by the field's element name.
#  -> NativeOnly 2241 -> 2245 failed (WORSE);  Native 0 -> 105 failed
#     (97 x ArgumentException "QueryingEnumerable<BsonDocument,BsonDocument> cannot be used for return
#      type IAsyncEnumerable<UInt32>", 4 x "expected a translation failure", 2 index OORE, 2 other)

# Mutation M2 — prove the read-side site
#  MongoProjectionBindingRemovingExpressionVisitor: replace `if (projection.Alias is null) return DocParameter;`
#  with a distinctive throw.
#  -> NorthwindSelectQueryMongoTest.Select_scalar_primitive fails with SPIKE-NULL-ALIAS-DOCPARAM-BRANCH

# Q3 functional probe (bare scalar / bare entity / bare arrays / wrapped variants, Native + NativeOnly)
#  throwaway tests/.../Query/Step3SpikeProbeTests.cs, run with the filter FullyQualifiedName~Step3SpikeProbeTests

git worktree remove --force $S/wt
```

### Files mutated and now gone

| File | Change | Fate |
|---|---|---|
| `src/…/Query/Expressions/MongoSelectDefinition.cs` | caller-info capture on `MarkNotNativelyRepresentable` | removed with the worktree |
| `src/…/Query/NativeTranslation/NativeProjectionBinder.cs` | decline sub-reason + translator-capability probe + mutation M1 | removed with the worktree |
| `src/…/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` | classification appended to the two throw reasons | removed with the worktree |
| `src/…/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` | mutation M2 | removed with the worktree |
| `tests/…/FunctionalTests/Query/Step3SpikeProbeTests.cs` | Q3 probe | removed with the worktree |

### Known limitations of this measurement, stated so they are not over-read

- **The probe's own fixture had noise.** Two runs against the same `TemporaryDatabaseFixture` collection
  double-inserted, and the owned-collection model produced unrelated tracking/`BlogId` errors. Every claim I
  draw from the probe rests on the **decline classification and the `NativeOnly` routing**, not on the row
  counts or the materialization errors. The one row-count claim I do make (bare entity succeeding under
  `NativeOnly`) is from the clean flat model, in both a bare and a `Where`-composed form.
- **Family boundaries are the instrumentation's, not a semantic taxonomy.** "FIELD_OK" means
  `TryTranslateField` succeeded *at the moment of decline*, with the translator constructed on the query root.
  It is a good proxy for "the boundary alone would be enough" but I did not verify each of the 78 individually.
- **The 78/100 figures are `NativeOnly` spec cases**, i.e. `(method × async)` pairs, not distinct queries —
  33 distinct test methods for the 78. Same convention as §7.
- **I did not re-derive §7.1's per-class table**, and I did not run the EF8/EF9 axes at all.
- **UNVERIFIED:** that a correctly-implemented 3a actually turns the 78 green. M1 measured that the *incomplete*
  change does not; I did not build the complete one. That is the first thing 3a's own Task 0 should establish.
