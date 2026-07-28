# Spike A — reference `Include` on the native path (EF-322 joins work stream)

> **Provenance.** Measured 2026-07-31; committed 2026-08-03 when the EF-368 slice branch was created.
> Companion document: `2026-08-03-native-reference-include-spike-findings-spec-delta.md` (spike B, the
> measured spec-test delta). Both were run read-only against `NativeQueryOngoing` tip `365391f`, i.e.
> **before** EF-366 and EF-367 landed — so spike B's baseline figures include the 12 `NorthwindGroupByQueryMongoTest`
> failures that EF-366 subsequently fixed. Referenced `probe-*.txt` raw outputs and TRX files were retained
> only in the local (gitignored) `.superpowers/` tree, not committed.

Read-only architecture spike. Branch `NativeQueryOngoing`, tip `365391f`, rebased onto `upstream/main`
(`58e05a0`, driver 3.9.0 → 3.10.0). `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → exit 0
before any measurement was trusted. No `src/` or `tests/` file was modified in the end state; the
throwaway probe used for the experiments below was deleted and `git status` is clean.

**How the experiments were run.** A single throwaway xUnit probe class was added to
`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` (that project has
`InternalsVisibleTo`, so it can reach `MongoQueryExpression`, `MongoSelectLowerer`,
`NativeSlotPopulator`, `StreamingEligibility` directly) and run under **all three** configurations
(`Debug EF10`, `Debug EF9`, `Debug EF8`). It (a) dumped the post-nav-expansion expression tree by
resolving `IQueryCompilationContextFactory` + `IQueryTranslationPreprocessorFactory` off the context and
calling `preprocessor.Process(queryable.Expression)`; (b) executed the query in each `MongoQueryMode`
against an unreachable server (`mongodb://localhost:1/?serverSelectionTimeoutMS=150`) so a *compile-time*
decline surfaces as the provider's own exception while a *successful* compile surfaces as a driver
`TimeoutException` — a clean two-valued discriminator that needs no database; (c) captured the emitted
MQL via `LogTo` (the MQL is logged before the driver call fails); and (d) exercised the lowerer at the IR
level. Raw output is preserved alongside this file as `probe-out.txt`, `probe-EF9.txt`, `probe-EF8.txt`,
`probe2-EF{10,9,8}.txt`.

---

## Q1. What the lowerer's catch-all rejects, and what `IsStreamableReference` admits

### Answer

For a single-level reference `Include`, **the lowerer's catch-all `else` is unreachable and the
`IsStreamableReference` arm is fully built and correct** — given the IR, it emits exactly
`$lookup` + `$unwind` to `_lookup_<Nav>`. Reference `Include` is rejected **much earlier**, at
translation time: `MongoSelectDefinition.Route` is driven to `Fallback` while the QMTEV is still
visiting the chain, so the gate never calls the lowerer at all. `IsStreamableReference` returns **true**
for a single-level reference Include today; its three conjuncts only exclude collection navs, a
filtered-Include sub-pipeline (unreachable for a reference), and a transitive `ThenInclude` hop.

### Evidence

**The lowerer arm works, measured at IR level** (probe `P3_ir_level_lowering`, EF10; identical on EF9/EF8):

```
### IR: IsJoinQuery=True UsesDriverJoinFields=True pendingLookups=0 Lookups=1
   lookup nav=Customer As=_lookup_Customer local=CustomerId foreign=_id
          IsStreamableReference=True IsNativeCollectionLookup=False ForceUnwind=False
### IR: Route(before)=WholeEntity
### IR: LOWERED OK -> MongoLookupStage, MongoUnwindStage
### IR: PIPELINE = [{ "$lookup" : { "from" : "Customers", "localField" : "CustomerId",
                      "foreignField" : "_id", "as" : "_lookup_Customer" } },
                    { "$unwind" : { "path" : "$_lookup_Customer",
                      "preserveNullAndEmptyArrays" : true } }]
```

The probe built `new MongoQueryExpression(orderEntityType)` and called only
`AddInnerCollection(customerEntityType)` — i.e. exactly what `TranslateJoinCore` does
(`Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1295`,
`outerQueryExpression.AddInnerCollection(innerQueryExpression.CollectionExpression.EntityType);`) — then
lowered and rendered. So: **dormant-but-built, and it is the arm at
`NativeTranslation/MongoSelectLowerer.cs:253` (`if (lookup.IsStreamableReference)`).**

The lookup itself is *synthesized*, not registered: `Lookups` is not a slot, it is
`GetStreamingReferenceLookups()` recomputed per access
(`Expressions/MongoQueryExpression.Lookup.cs:210`, `public IReadOnlyList<LookupExpression> Lookups => GetStreamingReferenceLookups();`),
whose driver-LeftJoin branch (`:112`–`:138`) builds one `LookupExpression` per inner collection reached by
a direct single-reference navigation off the root. **That synthesis branch is itself gated on
`UsesDriverJoinFields` being true** (`:107`) — a fact that becomes load-bearing in Q3.

**The actual rejecting site, measured** (probe `P2`/`P5`, all three EF configurations):

```
### EXECUTE ref Include NativeOnly mode=NativeOnly
   -> NativeTranslationNotSupportedException: Query is not natively representable
      and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
   at ...MongoShapedQueryCompilingExpressionVisitor.ThrowIfNativeOnlyForbidsFallback(...):line 724
   at ...MongoShapedQueryCompilingExpressionVisitor.TryBuildNativeFactory(...):line 572
   at ...MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery(...):line 394
```

`Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:569` is
`if (ClassifyNativeDisposition(mongoQueryExpression, mode) != NativeDisposition.Native || ...Route == NativeRoute.ScalarAggregate)`
— it returns `null` **before** `TryBuildPipeline` (`:593`) ever constructs a `MongoSelectLowerer`. The
lowerer is not on the stack. Under `Native` the same query instead compiled successfully and failed only
at the driver socket, confirming it took the driver-LINQ fallback.

**Two independent translation-time sites set `Route = Fallback`.** Both are provable from the code; the
first is also directly measured.

1. **`NativeSlotPopulator`'s catch-all.** `Join`/`GroupJoin`/`LeftJoin` are not whitelisted. Measured by
   calling the internal predicate directly (probe `P4`):
   `### WHITELIST: Join=False GroupJoin=False LeftJoin=False Select=True`
   (`NativeTranslation/NativeSlotPopulator.cs:169` `IsNativeRepresentableSlotOperator`; the catch-all is
   `:142`–`:151`). `PopulateNativeSlots` runs on the join node because the QMTEV's join cases fall
   through to it after `base.VisitMethodCall`
   (`MongoQueryableMethodTranslatingExpressionVisitor.cs:156`).
2. **`TranslateSelect`'s projection else-branch.** The `Select(ti => Include(ti.Outer, Nav, ti.Inner))`
   that always follows the join matches **none** of the four pass-through predicates: `IsTransparentIdentifierSelector`
   (`:412`, needs a `NewExpression`), `IsSingleLevelCollectionIncludeSelector` (`:618`, needs
   `EntityExpression == selector.Parameters[0]` *and* `navigation.IsCollection` — here `EntityExpression`
   is `ti.Outer` and the nav is a reference), `IsTransparentIdentifierMemberAccessSelector` (`:455`, needs
   the body to be a `MemberExpression`), `IsOwnedEmbeddedIncludeSelector` (`:654`, needs
   `navigation.IsEmbedded()`). So the `else if` at `:237` fires, `HasTerminalOperator` is false, and
   `NativeProjectionBinder.TryPopulateNativeProjection` is handed an `IncludeExpression` body it cannot
   bind → `MarkNotNativelyRepresentable()` (`:270`).

Separating which of the two fires first would require mutating `src/`, which this spike does not do —
but it does not matter for slicing: **both must be opened, and each is a one-line-scale change**
(whitelist the join operator; add a reference-Include pass-through predicate).

> **Doc inaccuracy found.** `IsTransparentIdentifierMemberAccessSelector`'s XML doc
> (`MongoQueryableMethodTranslatingExpressionVisitor.cs:450`) asserts "`TranslateJoinCore` already
> unconditionally marks the outer side non-native at the join itself". It does **not**: reading
> `TranslateJoinCore` (`:1268`–`:1440`) the only `MarkNotNativelyRepresentable`/`MarkGroupByFallbackUnsafe`
> calls are inside the `IsGroupBy` / `IsDistinct` branches (`:1281`, `:1291`). The unconditional marking
> is `NativeSlotPopulator`'s catch-all, in a different file. Worth correcting when the slice lands,
> because the comment is used to justify skipping a guard.

### `IsStreamableReference`, conjunct by conjunct

`Expressions/LookupExpression.cs:129`:
`IsReference && !HasPipeline && !LocalField.StartsWith(LookupAliasPrefix, Ordinal)`.

| Conjunct | Made false by | Concrete LINQ |
|---|---|---|
| `IsReference` (`:123`, `!Navigation.IsCollection`) | a collection navigation | `db.Customers.Include(c => c.Orders)` — but that never produces a `LookupExpression` via this path at all (see below); the collection-Include lookup is registered by `MongoProjectionBindingExpressionVisitor.Lookup.cs` and satisfies `IsNativeCollectionLookup` instead. |
| `!HasPipeline` (`:120`, `PipelineStages.Count > 0`) | a filtered-Include sub-pipeline | **Not reachable for a reference.** Every writer of `PipelineStages` is in `MongoProjectionBindingExpressionVisitor.Lookup.cs` (`:544`, `:626`, `:651`, `:658`, `:734`) and every one writes into a **parent collection** lookup's pipeline (a nested collection, or a nested reference *inside* a collection lookup). A top-level reference lookup — the only kind the synthesis branch produces — is created by `new LookupExpression(nav)` with an empty `PipelineStages`. Treat this conjunct as defence-in-depth for the reference slice, not a live decline. |
| `!LocalField.StartsWith("_lookup_")` | a transitive/`ThenInclude` reference hop | `db.Orders.Include(o => o.Customer).ThenInclude(c => c.Region)` — the second-or-later join rewrites `lookup.LocalField` to `"_lookup_<through>." + LocalField` at `MongoQueryableMethodTranslatingExpressionVisitor.cs:1404`, which is exactly the prefix this conjunct rejects. This is the one genuinely reachable decline, and it is the correct one: multi-hop reference chains are out of scope for a single-level slice. |

### Dormant-but-built vs absent, per component

| Component | Status |
|---|---|
| Lowerer `$lookup` + `$unwind` emission for a reference lookup (`MongoSelectLowerer.cs:253`) | **Built, correct, dormant.** Measured end-to-end to a rendered pipeline. |
| Reference-lookup synthesis (`MongoQueryExpression.Lookup.cs:99`) | **Built, dormant.** Also unit-covered by `StreamingReferenceLookupsTests`. |
| Join-coverage guard (`MongoSelectLowerer.cs:245`) | **Built**, and satisfied (1 lookup, 1 inner collection) — it is not what rejects. |
| Streaming (one-pass) reference-Include materialization (`MongoStreamingEntityMaterializerRewriter`'s `LookupReferencePlan`, `:151`, `:317`–`:322`) | **Built, dormant.** Reads `_lookup_<Nav>`; a BSON-Null lookup field yields a null navigation. |
| **DOM** shaper read-back of `_lookup_<Nav>` for a *reference* | **Present but structurally unreachable for this shape** — see Q3. This is the real gap. |
| Gate whitelist entry for `Join`/`LeftJoin` | **Absent.** |
| `TranslateSelect` pass-through predicate for a reference-Include selector | **Absent.** |
| Inner-vs-left `$unwind` semantics selection for a reference Include | **Absent.** The lowerer hard-codes `preserveNullAndEmptyArrays: true` — see Q2's hazard. |

### Confidence / what would falsify this

High. The rejection site is a captured stack trace, not inference; the lowerer's competence is a
rendered pipeline, not inference. Falsified if a *different* model shape (composite PK — note
`LookupExpression.GetFieldPath` (`:95`) emits `_id.<prop>` for a composite PK while
`MongoExpressionTranslator` rejects composite-PK member access, so composite-PK reference Include is
untested here; shared/TPH hierarchies; a shadow reference navigation) routes differently. Only
single-property-`ObjectId`-PK models were probed.

---

## Q2. Where the join gate lives, and whether the constrained shape is separable

### Answer

The gate is `NativeSlotPopulator.IsNativeRepresentableSlotOperator`
(`NativeTranslation/NativeSlotPopulator.cs:169`) — measured `Join=False GroupJoin=False LeftJoin=False` —
plus `TranslateSelect`'s projection else-branch; the Query `AGENTS.md` statement is **substantively
correct** (with the file/line correction that the marking happens in `NativeSlotPopulator`, not
`TranslateJoinCore`). Reference `Include` nav-expands to `Queryable.Join` for a **required** FK and
`Queryable.LeftJoin` for an **optional** FK — *not* uniformly `LeftJoin`, correcting the `AGENTS.md`
wording — always followed by `Select(ti => Include(ti.Outer, Nav, ti.Inner))`. The shape **is cleanly
separable** from general join support by a recognizer keyed on that trailing `IncludeExpression`, which
no user-authored `Join`/`GroupJoin`/`LeftJoin` can produce.

### Evidence — what nav-expansion actually produces (measured, EF10 / EF9 / EF8)

Required FK (`ObjectId CustomerId`), all three EF versions, byte-identical modulo printer formatting:

```
DbSet<UniOrder>()
    .Join(inner: DbSet<UniCustomer>(),
          outerKeySelector: u => EF.Property<ObjectId?>(u, "CustomerId"),
          innerKeySelector: u0 => EF.Property<ObjectId?>(u0, "Id"),
          resultSelector: (o, i) => new TransparentIdentifier<UniOrder, UniCustomer>(Outer = o, Inner = i))
    .Select(u => Include(Entity: u.Outer, Navigation: Customer, u.Inner))
```

Optional FK (`ObjectId? CustomerId`): identical except the operator is `LeftJoin`.
Filter/sort compose **ahead** of the join (`.Where(...).OrderBy(...).Join(...).Select(...)`), which matches
the lowerer's existing order (`AppendSelectOpStages` then `AppendLookupStages`,
`MongoSelectLowerer.cs:64`/`:71`) — no reordering work needed.

Collection `Include`, for contrast, produces **no join at all**:
`DbSet<Customer>().Select(c => Include(c, Orders, MaterializeCollectionNavigation(Subquery: DbSet<Order>().Where(o => ...FK...))))`.
That is why collection Include was separable via a selector predicate and why reference Include was not:
the reference case has a real join node in the chain.

### Multi-version difference — the one genuinely important one (measured)

| | EF8 | EF9 | EF10 |
|---|---|---|---|
| Required FK → operator | `System.Linq.Queryable.Join` | same | same |
| Optional FK → operator | `Microsoft.EntityFrameworkCore.Internal.QueryableExtensions.LeftJoin` | same | **`System.Linq.Queryable.LeftJoin`** (BCL, .NET 10) |
| Optional-FK reference Include, today | `InvalidOperationException` "could not be translated" **in every mode** | same | works via driver-LINQ fallback |

The EF8/EF9 `LeftJoin` is an EF-**internal** method, so it fails the QMTEV's allowed-method-source check
(`Queryable` / `MongoQueryableExtensions` / `MongoDB.Driver.Linq.MongoQueryable`) and never reaches
`TranslateLeftJoin` — which is exactly why the `case nameof(Queryable.LeftJoin)` at
`MongoQueryableMethodTranslatingExpressionVisitor.cs:128` is `#if !EF8 && !EF9`, and why
`QueryModeGateIncludeTests` is a wholly `#if !EF8 && !EF9` file. **Consequence for slicing:** a native
recognizer that matches on the trailing `IncludeExpression` shape rather than on
`QueryableMethods.LeftJoin` could make the optional-FK reference Include work on EF8/EF9 too — turning a
hard-fail into a working query. That is an *improvement*, not a break, but it is scope, and it should be
an explicit decision rather than a side effect.

### The correctness hazard nobody has flagged yet: inner-join vs left-join `$unwind`

Captured fallback MQL (probe `P7`, driver-LINQ path under `Native`):

- **Required FK** (all three EF versions):
  `[{$project:{_outer:"$$ROOT",_id:0}}, {$lookup:{from:"UniCustomers",localField:"_outer.CustomerId",foreignField:"_id",as:"_inner"}}, {$unwind:"$_inner"}, {$project:{...}}]`
  — `$unwind` with **no** `preserveNullAndEmptyArrays` ⇒ **INNER join**.
- **Optional FK** (EF10): the same with `{$unwind:{path:"$_inner",preserveNullAndEmptyArrays:true}}` ⇒ **LEFT join**.

The native lowerer's reference arm emits `preserveNullAndEmptyArrays: true` **unconditionally**
(`MongoSelectLowerer.cs:256`, `new MongoUnwindStage(lookup)`; the flag defaults to `true` —
`MongoUnwindStage`'s `PreserveNullAndEmptyArrays` default, per the EF-347 slice-5 note in
`Query/AGENTS.md`). So for the **required-FK** case, going native as-is would **change the result set**:
an order whose `CustomerId` matches no customer document is *dropped* today and would be *returned with a
null navigation* natively. MongoDB has no referential integrity, so a dangling required FK is an ordinary
data state, not a contrived one. This is a `Native != DriverLinq` row-count divergence on the default
mode — precisely the class of regression the versioning rubric's "not a break" carve-out does **not**
cover, and the same family as the two silent-wrong-data bugs found in owned-data slice 8.

**It is cheap to fix and must be in the slice's first task:** the recognizer already knows the operator
(`Join` vs `LeftJoin`) and equivalently the FK's requiredness, so pass
`preserveNullAndEmptyArrays: navigation-is-optional` into `MongoUnwindStage`. I have **not** confirmed
which of "the LINQ operator" or "`navigation.ForeignKey.IsRequired`" is the right discriminator on all
three EF versions — that is one of the two open items below.

### Separability — yes, with a provably disjoint recognizer

Discriminating predicate (a reference-Include join), all three parts checkable at
`TranslateSelect`/`TranslateJoinCore` time:

1. the trailing `Select`'s body is `IncludeExpression { Navigation: INavigation nav, EntityExpression: MemberExpression { Member.Name: "Outer" } }` whose `NavigationExpression` is the sibling `ti.Inner`, and the lambda parameter's type name starts with `TransparentIdentifier`;
2. `!nav.IsCollection && !nav.IsEmbedded()`, `nav.DeclaringEntityType` is the query root, and `nav.TargetEntityType` is the join's inner collection entity type;
3. the join's key selectors are exactly the FK correlation for `nav` — `EF.Property(outer, <fk-prop>)` vs `EF.Property(inner, <principal-key-prop>)`, single-property (the `LookupExpression` constructor only reads `Properties[0]`/`PrincipalKey.Properties[0]`, `LookupExpression.cs:45`–`:56`, so a composite FK must decline).

**Why this is provably disjoint from a user join.** A user-authored `Join`/`GroupJoin`/`LeftJoin` result
selector is a *user* lambda; EF never wraps a user result selector in an `IncludeExpression`.
`IncludeExpression` is synthesized only by EF's own `NavigationExpandingExpressionVisitor` when applying a
pending include, and the `ti.Outer` / `ti.Inner` pairing plus condition (3)'s FK identity pins it to one
navigation. This is the same shape of argument EF-347 used for reference `SelectMany` (recognize the
FK-correlated subquery, decline everything else) and the same shape EF-339 used for collection Include
(`IsSingleLevelCollectionIncludeSelector`). Admitting it does **not** open the general join path: a user
join still falls through both the whitelist and the selector predicate and still marks non-native.

The recognizer must additionally decline, each for a reason already established in the codebase:
a **second** join on the same query (`InnerCollections.Count > 1` — the multi-join flat mode with its
`_lookup_` prefixing at `:1404`); a `LocalField` already `_lookup_`-prefixed (`ThenInclude`); a composite
FK/PK (per (3), and `GetFieldPath`'s `_id.<prop>` form); a source that already `HasTerminalOperator`
(the invariant every own-`Translate`-override operator in `Query/AGENTS.md` is required to honour); and a
`VectorSearch` / grouped / distinct source (already handled by existing guards).

### EF-317 markers: throwaway vs live

There are **three** `TODO(EF-317)` markers in `src/` (the brief said four — worth noting):

| Marker | File | Disposition |
|---|---|---|
| "Join rewriting for the C# driver's LINQ provider" | `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs:37` | **Throwaway** per the settled owner ruling. |
| "Cross-collection `$lookup` Include machinery" | `Query/Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs:34` | **Live and load-bearing** — this is where the collection-Include `$lookup`s are registered and where `PipelineStages` are built; the native collection-Include slice depends on it. |
| "Cross-collection `$lookup` workaround state" | `Query/Expressions/MongoQueryExpression.Lookup.cs:23` | **Live** — `Lookups`/`GetStreamingReferenceLookups`/`UsesDriverJoinFields` are read by the native lowerer *and* both shapers. Cannot be deleted with the driver join path. |

The **main** bridge file `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (as opposed to its
`.LeftJoin.cs` partial) is **live**: `MongoShapedQueryCompilingExpressionVisitor` constructs it at `:875`
(the read fallback) *and* at `:1056` and `:1173`, which sit inside `BuildIdDocumentQuery<TSource>` /
`BuildFilter<TSource>` / `BuildUpdate<TSource>` — the EF9+ bulk `ExecuteUpdate`/`ExecuteDelete` plan
builders. Retiring the read fallback does not retire that file.

### Confidence / what would falsify this

High on the tree shapes, the gate, and the MQL (all measured across three EF versions). **Medium** on
separability: the recognizer is designed, not built, and the disjointness argument rests on "EF never
`IncludeExpression`-wraps a user result selector", which is a claim about EF Core's
`NavigationExpandingExpressionVisitor` I verified only for the shapes probed. The smallest experiment that
would settle it: dump the post-preprocessing tree for a *user* `Join`/`LeftJoin`/`GroupJoin` (with and
without a downstream `Include`) and confirm no trailing `IncludeExpression(ti.Outer, ...)` appears —
about 15 lines in the same probe harness.

---

## Q3. What the DOM shaper needs, and whether SP7 P2.3 falls out free

### Answer

**It does not fall out free, and the DOM shaper is the blocker, not streaming.** For a lone reference
Include the emit side and the DOM read side are *structurally guaranteed to disagree*: the lookup-alias
synthesis fires **only when** `UsesDriverJoinFields` is `true`, and the DOM shaper reads `_outer`/`_inner`
**exactly when** `UsesDriverJoinFields` is `true`. Streaming's reference-Include machinery *is* built and
consistent with the native pipeline, but `StreamingEligibility` **rejects any root whose reference target
carries a non-owned collection navigation** — i.e. any model with an inverse `Customer.Orders`, which is
the ordinary case and is Northwind's case — so streaming would be dormant for most real reference
Includes even after translation stops falling back. And the reference read-back has a genuine
silent-wrong-data mode: a missing `_lookup_<Nav>` reads as a **silent null navigation**.

### Evidence — the `UsesDriverJoinFields` contradiction

`UsesDriverJoinFields` (`Expressions/MongoQueryExpression.Lookup.cs:176`) is
`_innerCollections.Count > 0 && !_pendingLookups.Any(l => l.ForceUnwind)`. For a lone reference Include,
`TranslateJoinCore` calls `AddInnerCollection` (`:1295`) and registers **no** pending lookup — `AddLookup`
is reached only for a *second-or-later* join (`isSecondOrLaterJoin = InnerCollections.Count > 1`, `:1394`,
then `:1407`/`:1422`). Measured: `UsesDriverJoinFields=True pendingLookups=0 Lookups=1`.

Now the two consumers:

- **Emit side.** `GetStreamingReferenceLookups()` returns the synthesized `_lookup_<Nav>` lookups only in
  the `UsesDriverJoinFields == true` branch (`:107`–`:138`). So the native pipeline emits
  `_lookup_Customer`.
- **DOM read side.** `GetCrossCollectionFieldName` is
  `_queryExpression.UsesDriverJoinFields ? "_inner" : accessExpression.Name`
  (`Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:607`), and the root entity is read from
  `"_outer"` (`:381`–`:385` and again at `:676`–`:677`). So the DOM shaper reads `_outer`/`_inner`.

The `else` branch of `:607` — `accessExpression.Name`, which **is** the `_lookup_<Nav>` alias baked in by
`EntityProjectionExpression.BindNavigation` (`Expressions/EntityProjectionExpression.cs:91`–`:96`,
`var lookupAlias = LookupExpression.GetLookupAlias(navigation); ... new NavigationObjectAccessExpression(navigation, ParentAccessExpression, false, lookupAlias)`)
— is therefore the *right* code, reached only in the "multi-join flat mode". **So the reference
read-back exists in the DOM shaper; it is simply on the wrong side of a flag that this shape always sets
the other way.** That is a materially different finding from "the DOM shaper only handles the array case":
the EF-339 collection-Include array read-back is a *different* node (`ObjectArrayProjectionExpression`, same
`BindNavigation` method, `:93`), and the reference path is not missing, it is mis-flagged.

**Implication for the slice, and it is the central design decision.** The clean fix is to make emit and
read flip **together at translation time**: have the recognizer register a real reference
`LookupExpression` via `AddLookup` (with `forceUnwind` set, or `UsesDriverJoinFields` widened to account
for a registered reference lookup), so that `UsesDriverJoinFields` becomes `false` and *both*
`GetStreamingReferenceLookups()` (which then returns the pending lookups directly, `:102`–`:105`) and
`GetCrossCollectionFieldName` agree on `_lookup_<Nav>`. **But that also changes the driver-LINQ fallback**,
because `MongoEFToLinqTranslatingExpressionVisitor`'s `StripJoinForLookup` path
(`...LeftJoin.cs:576`) is selected by the same state — the fallback would then emit the flat
`$lookup` shape instead of the `_outer`/`_inner` LeftJoin shape. That is *self-consistent* (emit and read
still agree in fallback mode), and per the rubric changed MQL is not a break, but it is a bigger blast
radius than any recent slice and it will re-baseline the `_outer`/`_inner` assertions in
`QueryModeGateIncludeTests.Reference_include_falls_back_to_driver_linq_under_Native_mode`
(`tests/.../Query/QueryModeGateIncludeTests.cs:143`–`:146`) and the spec Northwind Include baselines.

The alternative — leaving `UsesDriverJoinFields` alone and making the shaper alias-aware only on the
native path — is exactly the **governing hazard** already recorded in `Query/AGENTS.md`: the shaper is
built at *translation* time and native-vs-fallback is decided *later*, so a shaper that assumes native
would be handed whole `_outer`/`_inner` documents on any late fallback. Do not take that route.

### Evidence — streaming is built, but `StreamingEligibility` gates it off for ordinary models

The streaming rewriter's reference plumbing is complete: `LookupReferencePlan`
(`NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs:151`) with
`ElementName = LookupExpression.GetLookupAlias(navigation)` (`:321`), planned one level deep off the root
only (`allowLookupReferences`, `:285`–`:316`), and its doc states a BSON-Null lookup field yields a null
navigation (`:148`–`:149`). `AllPendingLookupsAreStreamable` returns **true** for this shape (measured:
`Lookups=1 streamable=True`, and `IsStreamableReference=True`).

But `StreamingEligibility.IsEligible` is the binding constraint, and the measurement is decisive:

```
### IR (bidirectional Order/Customer with Customer.Orders):
    StreamingEligibility.IsEligible(Order) = False
    StreamingEligibility.IsEligible(Customer) = False
### VARIANT IR (unidirectional, no inverse collection):
    IsEligible(UniOrder)=True IsEligible(UniCustomer)=True IsEligible(OptOrder)=False
```

The cause is `NativeTranslation/StreamingEligibility.cs:79`–`:82`:
`if (!navigation.TargetEntityType.IsOwned() && navigation.IsCollection) return false;` — applied
**recursively** to the reference target (`:72`). So `Order` is ineligible purely because `Customer` has an
inverse `Orders` collection. Note this is *independent of the query*: eligibility is a property of the
root entity type, so it is already false today for any bidirectionally-mapped model.

**Therefore SP7 P2.3 does not fall out of this slice for free.** What is missing, precisely:

1. The DOM path must be made to work first (the `UsesDriverJoinFields` decision above), because it is the
   path most reference Includes will take.
2. To make streaming actually *fire* for a reference Include on a realistic model, `StreamingEligibility`
   would have to stop rejecting a root because a *reference target* has an inverse collection. That is
   sound in principle — the rewriter plans the target with `allowLookupReferences: false` and would simply
   `continue` past a non-owned collection... except it currently **throws** for a non-owned collection
   (`MongoStreamingEntityMaterializerRewriter.cs:302`–`:307`) before reaching that. So this is a real,
   separate change to two files, with its own correctness argument (an un-included inverse collection must
   materialize as an empty/unpopulated navigation, not throw).
3. Only then is P2.3 "activate the dormant path" rather than "build the missing gate".

### Silent-wrong-data hazards in the read-back

- **Reference nav miss is SILENT.** In the `IsCrossCollectionAccess` branch the shaper sets
  `fieldRequired = false` explicitly (`MongoProjectionBindingRemovingExpressionVisitor.cs:370`) and the
  nested read at `:682`–`:685` also passes `false`. A missing `_lookup_<Nav>` (or a missing `_inner`)
  therefore reads as `null` ⇒ **navigation silently null, no exception**. This is the exact analogue of
  the collection case's "null → empty" silent miss recorded in `Query/AGENTS.md`, and it is arguably worse
  because a null reference navigation is indistinguishable from a legitimately absent related row.
- **Root entity miss is LOUD.** `fieldRequired` is initialized `true` (`:304`) and the root is read as
  `CreateGetValueExpression(DocParameter, "_outer", required, typeof(BsonDocument))` (`:677`), so a native
  pipeline read by a `_outer`-expecting shaper fails noisily rather than silently. Small comfort: the
  *nav* half fails silently, so a partial mismatch (root right, nav wrong) is silent.
- **Row-count divergence** from the unconditional `preserveNullAndEmptyArrays: true` — see Q2. This is
  the one hazard that produces wrong data with *no* alias mismatch at all, so it survives any amount of
  shaper-alias care.
- **`GetStreamingReferenceLookups`' ambiguity bail-out is a decline, not a hazard, and must be preserved:**
  synthesis matches by *target type*, so two reference navigations off the root to the same entity type
  (`Doc.Author` / `Doc.Editor` → `Person`) returns `Array.Empty` (`:128`–`:133`). A recognizer that
  registers the lookup explicitly (the option above) removes that ambiguity by construction — a genuine
  improvement, but it means the bail-out stops being the thing that protects that model, so the
  recognizer must handle it deliberately.

### Confidence / what would falsify this

High on the DOM/emit contradiction and on `StreamingEligibility` (both direct measurements of the shipped
code). Medium-high on "the fix is to register a pending lookup": I read `StripJoinForLookup`'s selection
logic but did **not** execute the driver-LINQ flat mode for a *single* reference Include — it is only
exercised today for multi-join queries. If the flat fallback turns out not to work for a lone reference
Include, the slice needs a third option (a mode-aware alias decision made at compile time and pushed into
the shaper *after* the gate decides, which is a bigger refactor). **That is the single most important
open unknown.**

---

## Implications for slicing

### Recommended decomposition

**Task 0 (spike, ~1 day) — settle the one blocking unknown.** Does the driver-LINQ fallback still produce
correct results for a *lone* reference Include when `UsesDriverJoinFields` is forced `false` (i.e. a real
reference `LookupExpression` is registered)? Smallest prototype: in a throwaway worktree, make
`TranslateJoinCore` call `AddLookup(new LookupExpression(nav, forceUnwind: true))` for the recognised
single-reference-Include shape and run the reference-Include functional tests plus the Northwind Include
spec suite under `DriverLinq` only. If green, Task 1 below is unblocked; if not, redesign around a
compile-time alias decision. **Do not skip this** — it determines whether the whole slice is a
translation-side change or a shaper-architecture change.

**Task 1 — recognizer + the unwind-semantics fix, together.** Add the reference-Include recognizer
(Q2's three-part predicate), whitelist `Join`/`LeftJoin`/`GroupJoin` in
`IsNativeRepresentableSlotOperator` **only** when the recognizer matched (or gate the catch-all on a
provenance flag rather than widening the whitelist unconditionally — the latter would silently admit user
joins), add the `TranslateSelect` pass-through predicate, register the lookup so emit and read agree, and
**pass the correct `preserveNullAndEmptyArrays` through** for required vs optional FK. The unwind fix must
ship in the same task as the recognizer: shipping the recognizer alone lands the row-count divergence.
Gate the task on a differential test over a **dangling-FK** seed (a row whose FK matches no document),
required and optional, asserting `Native == DriverLinq` — that seed is the one that would have caught the
divergence, and no existing fixture has it.

**Task 2 — DOM read-back parity breadth.** Tracked vs no-tracking, `Where`/`OrderBy`/`Skip`/`Take`
composed before the Include (measured to compose ahead of the join, so this should be nearly free), a
self-referential reference (`Employee.Manager` — the rewriter's own comments call this out as the case
that corrupts a shared locals scope), a shared-CLR/TPH target, and the two-navigations-to-one-target
ambiguity model.

**Task 3 — explicit declines, each with a tripwire test.** `ThenInclude` / transitive reference
(`LocalField` `_lookup_`-prefixed), two reference Includes on one query (`InnerCollections.Count > 1`),
composite FK or composite PK, reference Include composed after a terminal (`HasTerminalOperator`),
reference Include + collection Include on the same query, filtered Include. Each must decline *cleanly*
and keep its current disposition byte-identical.

**Task 4 (separate slice, not part of this one) — streaming.** `StreamingEligibility`'s
reference-target-with-inverse-collection rejection plus the rewriter's non-owned-collection throw. Only
after Tasks 1–3 land. Do **not** plan this as "activate the dormant path"; it is a gate change with its
own correctness story.

**Optional, decide explicitly — EF8/EF9 optional-FK reference Include.** Recognizing the shape rather than
the `QueryableMethods.LeftJoin` constant would turn a hard-fail into a working query on EF8/EF9. Genuine
improvement, real scope; the owner should choose rather than discover it.

### Blocking unknown for a further spike

Only one: **Task 0 above** (does the flat-`$lookup` driver-LINQ fallback work for a lone reference
Include?). Everything else in this document is either measured or a design choice with a known fallback.

### Two doc corrections to fold in when the slice lands

1. `Query/AGENTS.md`'s Include note says reference Include "nav-expands to a `LeftJoin`". Measured: it is
   `Queryable.Join` for a **required** FK and `LeftJoin` only for an **optional** one, on all three EF
   versions — and that distinction is the source of the inner-vs-left `$unwind` hazard, so the imprecision
   is not cosmetic.
2. `IsTransparentIdentifierMemberAccessSelector`'s XML doc
   (`MongoQueryableMethodTranslatingExpressionVisitor.cs:450`) claims `TranslateJoinCore` unconditionally
   marks the outer side non-native. It does not; `NativeSlotPopulator`'s catch-all does.
