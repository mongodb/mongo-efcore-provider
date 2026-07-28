# Native array-valued projections (EF-322 owned-data slice 8)

**Branch:** `EF-360`, stacked on `7c199e4` (EF-358, the projection-path null-collection normalization slice).
**Status:** design approved 2026-07-29; **revised after the de-risking spike**, which measured two of the
original assumptions false. See `2026-07-29-native-array-valued-projections-spike-findings.md`.

> **Revision note — read this before trusting any earlier summary of this slice.** The first draft framed
> this as "simultaneously a fallback → native widening and a bug fix", on the belief that
> `Select(b => new { b.Title, b.Posts })` threw `ArgumentException` in every mode. **It does not throw.** It
> returns correct data under `Native` and `DriverLinq` via the mixed shaper and declines cleanly under
> `NativeOnly`. This slice is a **pure fallback → native widening**. The `ArgumentException` — the real
> EF-360 — fires only when the projected **element type has a navigation of its own**, which this slice
> declines structurally and a **following** slice fixes. See §2 and §3.3.

## 1. What this slice does

Makes an **owned entity-collection leaf inside a terminal anonymous-type / DTO projection** go native — a
`$project` that emits the embedded array server-side, read back by the DOM shaper through the projection
**alias** rather than through the navigation's document path.

```csharp
ctx.Blogs.AsNoTracking().Select(b => new { b.Title, b.Posts })
// today:  aggregate([])                 — whole document fetched, projection folded client-side
// after:  [{ $project: { _id: 1, Title: "$Title", Posts: "$Posts" } }]     (see §5.1 on _id)
```

**The value is server-side field limiting.** Today this shape falls back and the emitted pipeline is
literally `aggregate([])` (spike Q5, captured MQL) — every field of every document crosses the wire, including
arrays the caller did not ask for, and the projection is folded in process. Going native sends only the
requested fields. Results are unchanged; this is a bandwidth and allocation win, not a correctness fix.

It also removes one entry from the standing EF-322 owned-data follow-on list
(`docs/native-query-status-EF-322.md:275`, "Array projections").

### 1.1 In scope

Every row below additionally requires that the **projected element type has no navigations of its own** —
see §3.1's representability guard and §3.3 for why.

| Shape | Example |
|---|---|
| Owned collection leaf, anonymous type | `Select(b => new { b.Title, b.Posts })` |
| Owned collection leaf, named DTO (`MemberInit` branch) | `Select(b => new TitlePosts { Title = b.Title, Posts = b.Posts })` |
| The `EF.Property` spelling | `Select(b => new { b.Title, P = EF.Property<List<Post>>(b, "Posts") })` — measured to normalize to the identical tree (Q2), so no separate handling |
| Several array leaves side by side | `Select(b => new { b.Posts, b.Drafts })` |
| Array leaf alongside scalar / arithmetic / `$size`-count leaves | `Select(b => new { b.Title, N = b.Posts.Count, b.Posts })` |
| Owned collection reached through one or more `OwnsOne` hops | `Select(b => new { b.Title, b.Home.Notes })` |
| Non-`List` collection navigation (e.g. `HashSet<Post>`) | materialized through the navigation's own `IClrCollectionAccessor` |

No-tracking only. A tracking query keeps hitting EF Core's **own** guard ("owned entities cannot be tracked
without their owner") under `Native`/`DriverLinq`; under `NativeOnly` the provider's decline fires first, so
the exception type differs by mode (Q5, measured). The provider adds no guard of its own.

### 1.2 Out of scope — with each kind's *measured* current disposition

The original draft treated these as one undifferentiated "out-of-scope array leaf" set that the slice would
make decline gracefully. They are three unrelated dispositions, and only one of them is the provider's to
change:

| Leaf kind in the same projection | Measured today | This slice |
|---|---|---|
| **Primitive collection** (`b.Tags`) | **Already native.** Arrives as a plain `PropertyExpression` typed `List<string>` and is accepted by the existing plain-field branch; correct for all five array states | untouched — it never reaches the new branch |
| **Reference (cross-collection) collection** (`c.Orders`) | Hard-fails in **all three modes** with EF Core's own `InvalidOperationException` "could not be translated", raised before the provider's binder | **must stay byte-identical** — see §3.3's constraint |
| **Element type has a navigation** (nested owned collection **or** nested owned single reference) | Hard-fails in **all three modes**, `ArgumentException` at `MongoProjectionBindingExpressionVisitor.cs:661`. **This is EF-360.** | declined structurally (§3.1); fixed by the **next** slice |
| **Bare array projection** (`Select(b => b.Posts)`) | Works (mixed path), correct for every array state after EF-358; declines cleanly under `NativeOnly` | untouched — blocked by a different boundary, see §2 |

## 2. Two findings that reshaped this slice

### 2.1 The bare spelling is blocked by a second, unrelated boundary

`Query/AGENTS.md` and `docs/native-query-status-EF-322.md:275` both describe array projections as blocked on
the DOM-shaper alias read-back **alone**. That is true of the wrapped spelling and false of the bare one.

`NativeProjectionBinder.TryPopulateNativeProjection` (`NativeProjectionBinder.cs:48-84`) switches on
`NewExpression` and `MemberInitExpression` and returns `false` for anything else. A bare `Select(b => b.Posts)`
body is a `MaterializeCollectionNavigationExpression` (confirmed by instrumentation, Q2), so it hits
`default: return false` and declines **before the shaper is reached** — the same SP3-wide bare-projection
boundary that keeps `Select(b => b.Posts.Count)` on the fallback path, and not count-specific.

### 2.2 EF-360's defect is the element, not the array

The prior fixtures that "confirmed" EF-360 — `ProjectedCollectionNormalizationTests.Post` and
`NativeOwnedCollectionCountTests.Post` — **both give the element a nested owned collection** (`Post.Comments`).
That was the trigger, not the array leaf. Isolated (Q5b), the `ArgumentException` fires for a nested owned
collection *and* for a nested owned single reference, identically, and not at all for an element with no
navigations.

Root cause, traced: an element navigation makes EF's nav-expansion emit the auto-include as a
`Queryable.Select` *inside* the `MaterializeCollectionNavigationExpression`'s subquery. That reaches
`MongoProjectionBindingExpressionVisitor.VisitMethodCall`'s `Queryable.Select` arm (`:517-532`), which rebuilds
it as `Expression.Call(EnumerableMethods.Select, …)` typed **`IEnumerable<T>`**; `MatchTypes` leaves it alone
(the same `TryGetItemType()` short-circuit the EF-357 comment at `:534-546` documents); `Expression.New`'s
member-type validation then rejects `IEnumerable<T>` for a `List<T>` member at `:661`.

Same `MatchTypes` root-cause family as EF-357, reached from a different direction — a **different site and a
different message** from the `IQueryable<T>`-parameter `ArgumentException` the count-projection slice recorded
as an untaken follow-on.

**Consequence for the docs:** the two locations above, plus
`ProjectedCollectionNormalizationTests.cs:339-341` (whose comment asserts the anonymous shape "is an
`ArgumentException` in every mode … confirmed by direct probe"), state the blocking reason incorrectly and
must be corrected in place when this lands. That comment is a textbook instance of the masked-fixture failure
mode: it was written against a fixture whose `Post` declares `List<Comment> Comments`.

## 3. Mechanism

### 3.1 Emit side

`NativeProjectionBinder.TryTranslateLeaf` gains a branch for an owned-collection array leaf.

**The leaf is a `MaterializeCollectionNavigationExpression`, never a `MemberExpression`** — measured, and
identically so for the anonymous, `EF.Property` and `MemberInit` spellings (Q2). So the branch must unwrap the
wrapper first, and it is **structurally disjoint** from every existing leaf branch (all of which require a
`MemberExpression` or a `MethodCallExpression`), which is the mechanical reason to expect §5.2's
ordering-mutation check to pass.

- **The navigation** comes straight off `MaterializeCollectionNavigationExpression.Navigation` — no separate
  resolution step needed.
- **The array path** comes from the existing `MongoExpressionTranslator.TryResolveOwnedCollectionPath`, applied
  to the wrapper's underlying member-access chain (that resolver wants a rooted member-access chain, so it must
  be handed the unwrapped access, not the wrapper). Reusing it is the point: it already walks a rooted chain
  through one or more embedded single-reference hops, and its requirement that the **final hop be an embedded
  collection navigation** is already the structural protection against a mapped scalar sharing a navigation's
  name — the same protection the `.Count` predicate slice documents.
- **The result node** is a `MongoElementRefExpression` (a raw document-element reference with no backing
  `IProperty`, rendering as `"$" + Path`). No new expression node and no new renderer arm on the emit side:
  `MongoProjectStage` → `MongoPipelineFactory.RenderProject` → `MongoAggregationExpressionRenderer` already
  renders it, and a `"$path"` **string** is a field-path expression, so there is no bare-value
  inclusion/exclusion-flag hazard.
- **Acceptance is on the helper's own `bool`, not a node-kind sniff.** `MongoElementRefExpression` is shared
  with the GroupBy flattening projection, so `result is MongoElementRefExpression` is not a sufficient gate,
  unlike the count branch's `is MongoSizeExpression`.

**Representability guard — decline when the element type has any navigation.** `!elementEntityType.GetNavigations().Any()`,
mirroring the precedent in `IsWholeElementRepresentable`'s owned-navigation guard. This is what keeps EF-360
**exactly as it is today** rather than perturbing it: that shape crashes at shaper-build time in every mode
before the native/fallback split matters, and admitting it here would either still crash or change the crash.
Declining leaves it byte-identical for the next slice to fix deliberately. The guard is a plain `return false`
(the projection falls back), which for these shapes lands on the same every-mode `ArgumentException` they hit
today — not a graceful oracle, but not a change either.

**No converter guard on the array itself.** The dotted-leaf converter guard on *scalar* leaves exists because
the DOM shaper reads a dotted scalar back **raw** through a single-hop field resolver. An array leaf is
different in kind: each element is read back as a `BsonDocument` and materialized by the ordinary entity
shaper, which applies each property's own converter and `BsonRepresentation`. Q4 confirmed that mechanism on
every path that exists today, in both modes — but **not** through an alias, which is unmeasurable on
unmodified `src/`. The decision stands; see §6 for the test obligation it creates.

### 3.2 Read-back side

New internal expression node:

```csharp
internal sealed class ArrayAliasProjectionExpression : Expression, IPrintableExpression
{
    string Alias { get; }                                // the $project output alias the shaper reads by
    INavigation Navigation { get; }                       // for GetCollectionAccessor / DeclaringEntityType
    EntityProjectionExpression InnerProjection { get; }   // over the element IEntityType
}
```

A **sibling** of `ObjectArrayProjectionExpression`, not an extension of it. That node's whole contract is
"read this navigation at its document path": its `Name` derives from `GetContainingElementName()`, its
`Equals`/`GetHashCode` key on `Name`/`AccessExpression`/`InnerProjection`, and `InnerProjection` is fixed to an
`EntityProjectionExpression` over a `RootReferenceExpression`. Adding a second, incompatible addressing mode to
it was considered and rejected.

**Registration.** `MongoProjectionBindingExpressionVisitor`'s collection-navigation arm registers the new node
through `AddToProjection(node, alias)` and wraps the resulting `ProjectionBindingExpression` in a
`CollectionShaperExpression` — **gated on `_queryExpression.Select.Route == NativeRoute.Projection`**. That
guard is load-bearing in exactly the way the file's existing `Route == Projection` guards are: it confines the
registration to the fully-native path so mixed and fallback shapes fall through to the mixed shaper unaffected.
Unconditional registration would silently defeat the `TranslateSelect`/`TranslateGroupBy` post-terminal guards.

**The shaper.** In `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case, the
hard cast

```csharp
:141   objectArrayProjection = (ObjectArrayProjectionExpression)projection.Expression;
```

becomes a switch with three outcomes: the existing `ObjectArrayProjectionExpression` arm; a new
`ArrayAliasProjectionExpression` arm sourcing its array from `BsonBinding.CreateGetBsonArray(DocParameter, node.Alias)`;
and `default:` throwing `TranslationFailed`, as the surrounding `switch` already does for an unrecognized
`CollectionShaperExpression.Projection`.

**A correction to the first draft's reasoning about this site.** The draft justified the new arm partly by
claiming the mixed path fails at `:141`'s hard cast. It does not: instrumentation shows every collection shaper
the spike observed — mixed projection path *and* whole-entity `Include` path alike — takes the switch's
**second** arm (`case ObjectArrayProjectionExpression`), because `Projection` is that node directly rather than
a `ProjectionBindingExpression`. The mixed path does not fail at all. `:141` is still the right site for the new
arm, but **which shape reaches `:141`'s `ProjectionBindingExpression`/`GetProjection` arm was NOT MEASURED** —
so the pre-existing first arm's coverage must be established before assuming a mutation there would be caught
(§6).

Everything downstream is unchanged and must stay that way:

- The new array source sits **upstream** of the existing EF-358 `Coalesce(bsonArray, new BsonArray())` at the
  **point of use** (`:183`). `CreateGetBsonArray` returns `null` for both a missing field and an explicit BSON
  null, so the new arm genuinely needs that coalesce — it is not defensive here.
- The materialized collection is still built through the navigation's own `IClrCollectionAccessor` via
  `PopulateCollection`, never a hand-made `List<T>`, so a `HashSet<Post>` navigation is correct for free.
- The **cross-visitor contract** is untouched: nothing here moves the coalesce into
  `BsonDocumentInjectingExpressionVisitor`'s collection assignment, whose right-hand side must remain the
  `Expression.TypeAs` `UnaryExpression` that `VisitBinary` hard-casts. Folding it there throws
  `InvalidCastException` for every collection shaper in every mode.

### 3.3 EF-360 is decoupled from this slice

The approved re-scope: this slice is the widening; EF-360 is the next slice.

EF-360 is **not** "an anonymous projection with an entity-collection leaf throws". It is: **an anonymous or DTO
projection containing a collection leaf whose element type has a navigation of its own throws
`ArgumentException` in every mode**, at `MongoProjectionBindingExpressionVisitor.cs:661`, via the
`Queryable.Select`-rebuild → `MatchTypes` short-circuit path traced in §2.2. It reproduces for a nested owned
collection and a nested owned single reference alike, and the **bare** spelling of the same query on the same
model works fine. The ticket must be re-filed with that root cause; its current description describes a shape
that does not fail.

**A hard constraint the next slice inherits, discovered here.** Five spec tests (10 cases) assert
`AssertTranslationFailed` on **reference**-collection array projections:

- `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_simple`
- `…_simple_followed_by_ordering_by_scalar`, `…_multi_part`, `…_multi_part2`
- `NorthwindFunctionsQueryMongoTest.Order_by_length_twice_followed_by_projection_of_naked_collection_navigation`

Neither test class declares its own `AssertTranslationFailed` (grep-verified), so they use EF Core's upstream
helper, which requires an `InvalidOperationException` carrying "could not be translated" — exactly what a
reference-collection leaf raises today. **Replacing that with a provider-authored `NotSupportedException` flips
all ten Passed → Failed on both axes, on the exception *type*, which `EF_TEST_REWRITE_BASELINES` cannot fix.**
So: leave the reference-collection leaf's failure byte-identical, in this slice and the next.

## 4. Spike: done

All six questions in the previous draft's §4 are answered in
`2026-07-29-native-array-valued-projections-spike-findings.md`. Verdicts: Q1 confirmed on one axis and false on
the other (§5.1); Q2 confirmed cleanly (§3.1); Q3 measured — the mixed shaper materializes the leaf correctly
(§3.3); Q4 supported but not proven through an alias (§6); Q5 **measured false**, the headline finding (§2.2);
Q6 baseline `Native` 4589/0/19 and `NativeOnly` 2194/2395/19, with all 2395 `NativeOnly` failures being
`Native` passes.

**Four residual `NOT MEASURED` items, each now a plan task rather than an assumption:**

1. Owner-key emission for an array reached through `OwnsOne` hops (`b.Home.Notes`) against an owner-key-less
   document. The dotted leaf itself works today; the composition with §5.1's fix is unmeasured.
2. Which shape reaches `:141`'s `ProjectionBindingExpression` arm — needed before trusting a mutation check
   there.
3. `Union` dedup over projected documents containing arrays (§5.3).
4. The converter round-trip **through an alias** (§6).

## 5. Details settled

### 5.1 The owner key must be emitted into the `$project`

**Measured (Q1):** for a **shadow-key** owned collection the element shaper reads the owner key through
`_ownerMappings` and, against a document with no `_id`, fails per-row at materialization with
`InvalidOperationException: Document element is missing for required non-nullable property 'Id'`
(`Storage/BsonBinding.cs:229`, via `MongoProjectionBindingRemovingExpressionVisitor.cs:992`). For an
**explicit-declared-key** owned collection (`p.HasKey(...)`) it works, and the owner-key read is never even
emitted.

So the array-leaf branch **emits the root `_id` into the projection**. `RenderProject`'s `_id: 0` correctly
disappears once the projection emits `_id`, and the extra projected `_id` is inert for the result shape — the
shaper reads by alias (confirmed by Q1's `_id`-kept-view control returning identical counts). Declining for
shadow-key element types was the alternative and would decline the overwhelmingly common case. The failure mode
if the key is *not* emitted is a hard per-row throw, not silent wrong data — the one mercy here.

### 5.2 Sibling leaves

The array branch composes with the existing leaf branches because `TryTranslateLeaf` is the one shared per-leaf
entry point for every `Select.Projection` population site. Q2 establishes the branches are structurally
disjoint; §6's mutation check verifies the ordering claim rather than asserting it, per the precedent that two
previously-claimed load-bearing orderings in this area measured false.

Sibling composition works today, as a fallback, for both `new { b.Title, N = b.Posts.Count, b.Posts }` and
`new { b.Title, b.Posts, b.Drafts }` — and the count leaf does **not** go native when a sibling declines, per
the binder's all-or-nothing contract. That is the parity oracle those shapes get.

### 5.3 Set-op and SelectMany positions

`TryTranslateLeaf` is also reached from a projected set-op **operand**, a trailing projection **after** a set
op, and (via a different binder) a SelectMany trailing projection, so an array leaf becomes admissible there as
an incidental widening. `ProjectionShapesMatch` compares top-level alias sets, so an operand pair carrying array
leaves is structurally fine. Whether whole-document value-equality over projected documents containing arrays
behaves sensibly for `Union` dedup is **NOT MEASURED** — one probe during implementation. If it looks at all
doubtful, the array branch declines in a set-op-operand position and that decline is tested.

## 6. Verification strategy

Written against the branch-review lesson that green tests hid EF-358's blocking bug through nine review rounds
because every fixture was masked three different ways — and against the fact that §2.2's masked fixture is
precisely what made the original framing of *this* slice wrong.

- **The gate is `Native == DriverLinq` parity over the full state matrix.** This is a change from the first
  draft, which assumed a crash → native framing and reached for an in-memory oracle. Q5 establishes the
  in-scope shapes have a **working oracle in both modes today**, so parity is available and is the stronger
  bar. Keep an in-memory differential oracle as well for the array-state matrix, since it is what catches a
  case where *both* paths agree and are wrong.
- **Fixtures deliberately un-masked.** No `= []` initializer on any collection navigation under test. Element
  types with **no** navigations for the in-scope cases; a separate element type **with** one, and another with
  a nested owned *single reference*, for the EF-360 decline. A `HashSet<Post>` navigation so the
  `IClrCollectionAccessor` path is genuinely exercised.
- **Array-state matrix, seeded as raw BSON:** missing / explicit BSON null / empty / single / multi, plus
  elements with missing and null scalar fields.
- **Routing proven only under `NativeOnly`.** Never MQL shape. Decline tests assert
  `NativeTranslationNotSupportedException` under `NativeOnly` **and** correct values under `Native`;
  every-mode hard-fail tests assert the same exception in all three.
- **Owner-key coverage on both axes** — shadow-key and explicit-`HasKey` — since that is where Q1 split. Plus
  residual item 1: the `OwnsOne`-hop composition.
- **The converter test must read from the alias.** Q4 proved element converters work on the paths that exist
  today; it could not prove the alias path. So the slice owns a new test over a `ConvPost`-shaped fixture (a
  `Guid` with non-default `BsonRepresentation` and a value-converted enum on the element), asserting exact
  values through the **native** route — not a reuse of existing-path coverage.
- **Every new gate mutation-verified in both directions:** the `Route == Projection` guard, the array-leaf
  acceptance branch, the element-navigation representability guard, the new shaper arm, and the coalesce
  placement. A mutation that turns no test red means the line is unprotected — which is what happened to the
  count-projection binder gate. Residual item 2 applies to the pre-existing `:141` arm specifically.
- **Comment and doc sweep last, read first.** By *behaviour*, not ticket vocabulary — including the three
  locations §2.2 names as currently wrong.

## 7. Multi-version and versioning

No `#if` expected — every touched type is `internal`, and nothing here depends on an EF-version-specific
visitor signature. Confirmed by the three-version sweep at the end.

**Not a break, cleanly.** This is a fallback → native routing flip with **unchanged results**, plus changed
emitted MQL for a supported query — both explicitly carved out by the rubric at the top of `Query/AGENTS.md`.
Unlike EF-358 there is **no materialized-value change**, so **no `BREAKING-CHANGES.md` entry is needed**. (The
first draft reserved judgement here because it believed the slice changed a throw into a value; it does not —
the shapes it touches already return correct values.)

**Predicted spec delta: zero**, because Northwind has no owned collections at all and every array-valued
projection in the suite is a reference-collection leaf this slice does not touch. Per the lesson the `All` slice
paid for, that prediction is **re-measured on both axes after implementation** — `NativeOnly` pass/fail *and*
`Native`-mode emitted MQL, per test — not trusted.

## 8. As-built deltas (appended after implementation — the sections above are left as written)

Recorded so a reader of §3 does not go looking for code that was never needed, or trust a placement that was
measured broken. Nothing above is edited; this section is the correction layer.

1. **No new array-source branch was needed — §3.2 is wrong on its central mechanism claim.** §3.2 specified a new
   `ArrayAliasProjectionExpression` arm "sourcing its array from
   `BsonBinding.CreateGetBsonArray(DocParameter, node.Alias)`". **That code does not exist.** The pre-existing
   chain already performs the alias read end to end: `BsonDocumentInjectingExpressionVisitor` emits the
   `bsonArrayN` variable assignment exactly as before, `MongoProjectionBindingRemovingExpressionVisitor.VisitBinary`
   resolves `fieldName = projection.Alias` from the `ProjectionExpression`, and
   `CreateGetValueExpression(..., typeof(BsonArray))` dispatches to `BsonBinding.CreateGetBsonArray` itself. What
   was actually required was (a) a `VisitBinary` arm admitting the new node kind and (b) widening the
   `CollectionShaperExpression` switch's hard cast to the shared `IArrayProjectionExpression` interface.
2. **The node carries NO `Alias` property** — contrary to the §3.2 sketch, which listed `string Alias { get; }`
   first. The alias flows from the `ProjectionExpression` the post-processor builds from the `ProjectionMember`,
   the identical mechanism every scalar leaf uses, which makes the emit-side and shaper-side alias spaces agree
   **by construction** instead of by a guard. A second, independently-derived copy of the same name is exactly how
   alias divergence gets reintroduced. `IArrayProjectionExpression.ArrayFieldName` is consequently always `null`
   for the alias node, with a `?? throw` on the (provably unreachable) document-path branch so a future violation
   is loud rather than silently reading the wrong array.
3. **The registration site moved — the plan's Step 6 placement was measured BROKEN, not merely suboptimal.**
   Registering inside `VisitMember`'s navigation switch cannot work: reaching that switch first visits the OWNER
   shaper, whose `StructuralTypeShaperExpression` case calls `AddToProjection`, and `ApplyProjection()` returns
   EARLY when `Projection.Any()` — so no projection member is ever rewritten to `Constant(index)` and every
   SIBLING leaf dies in `GetProjectionIndex` with an `InvalidOperationException` at shaper-compile time, in all
   modes. Registration is instead a new private `TryBindNativeArrayProjection`, called at the top of
   `VisitExtension`'s `MaterializeCollectionNavigationExpression` case — the same register-before-descending
   position the count leaf (`VisitMethodCall`) and the arithmetic leaf (`Visit`) already use.
4. **§1.1's `OwnsOne`-hop row was DROPPED from this slice and filed as EF-362.** For a hop the `$project` alias is
   necessarily FLAT (`"Notes"`) while the document path is NESTED (`"Home.Notes"`), so the alias-agreement
   conjunct is satisfied yet the invariant "the alias read and the document-path read resolve to the same place"
   *cannot* hold — on a fallback path the top-level `"Notes"` read misses and yields a silently EMPTY collection.
   It needs a second mechanism (a path-preserving `$project` emitting `{"Home.Notes": "$Home.Notes"}`, which
   MongoDB renders as nested output, plus retaining the document-path read), not a relaxed conjunct. It stays a
   clean decline pinned by a mutation-verified tripwire test.
5. **Two admissibility rules were ADDED in response to measured bugs, neither anticipated by §3 or §5.2 — but
   the two bugs were NOT the same kind, and that distinction matters.** Rule (a) was found via a measured
   **silent** wrong-data bug. Rule (b) was found via a measured **throw**; its *silent* variant (a computed
   leaf whose alias collides with a real element name) is **mechanism-derived, not executed**, and is pinned by
   the colliding-alias test rather than having been observed first.
   (a) *Alias agreement* — `IsNativeArrayProjectionLeaf` requires the alias to equal the navigation target's
   containing element name, on top of root-declared. Found because `Select(b => new { b.Title, P = b.Posts })` (a
   renamed alias) returned the correct 1 element under `Native`/`NativeOnly` and **0, silently, under explicit
   `DriverLinq`**; the plain `b.Posts` spelling masked it. (b) *Sibling readability* —
   `IsWholeDocumentReadableLeaf` requires every non-array sibling to be whole-document-readable when an array leaf
   is present. Found because admitting an entity-collection leaf broke the previously-implicit
   "`Route == Projection` ⇒ every leaf is pushdownable" invariant, making
   `Select(b => new { b.Title, N = b.Posts.Count, b.Posts })` **throw** under explicit `DriverLinq` where
   pre-slice it returned correct values — and, when a computed leaf's alias collides with a real element name,
   return silently wrong data. §5.2 predicted sibling leaves would need attention but not this mechanism.
   The sibling rule is deliberately **broader** than strictly necessary (it also declines a plain-member sibling
   whose alias merely differs, which both shapers would have handled): an optimization gap, not a defect, since it
   can only turn an admit into a decline.
6. **§3.2's own self-correction about the `:141` arm was right, and its open question is now settled.** It flagged
   that "which shape reaches `:141`'s `ProjectionBindingExpression`/`GetProjection` arm was NOT MEASURED". It has
   been: with that arm made to throw, **only this slice's 21 tests fail — zero of the 4589 EF10 spec tests that
   RAN, and zero other functional tests, reach it.** (Stated on the tests that RAN deliberately: 19 spec tests
   were skipped, so "zero of 4608" would be an overstated hard bound.) The arm was genuinely dead before this
   slice, so this slice's tests are now the sole net for it.
7. **§7's predictions held.** Zero spec delta on both axes (`Native` 4589/0/19, `NativeOnly` 2194/2395/19 — exact
   match to the branch-start baseline); no `#if`; three-version sweep 0 failures (EF8 7747, EF9 8108, EF10 7705);
   not a break, and no `BREAKING-CHANGES.md` entry added.
