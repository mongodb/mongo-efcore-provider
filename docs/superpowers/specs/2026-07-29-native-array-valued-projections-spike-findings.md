# Spike findings — native array-valued projections (EF-322 owned-data slice 8, EF-360)

Throwaway de-risking spike gating the "native array-valued projections" slice. All spike code was
discarded; only this document was committed.

**Environment.** Branch `EF-360` at `db513ea` — a **docs-only** commit on top of `7c199e4`
(`git show --stat db513ea` = one file, the design doc), so `src/` is byte-identical to the `7c199e4`
the spec's §4 preamble names. EF10 (`Debug EF10`). `MONGODB_URI`/`ATLAS_URI` both unset, so
TestContainers booted an isolated `mongodb/mongodb-atlas-local` container per test process.

Throwaway probe class `SpikeArrayProjectionTests` in
`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`, following the
`NativeOwnedCollectionCountTests` conventions (`[XUnitCollection("QueryTests")]`, primary-constructor
`IClassFixture<TemporaryDatabaseFixture>`, nested POCOs + `static readonly Action<ModelBuilder>`,
unique collection names, `MongoDbContextOptionsBuilder(b).UseQueryMode(mode)`, `SpyLoggerProvider`
MQL capture, plain `Assert.*`). **Every collection navigation in the probe fixtures was declared
WITHOUT a `= []` initializer**, and all fixture data was seeded as raw BSON so missing / explicit-BSON-null
arrays are genuinely absent / genuinely null.

Temporary `src/` instrumentation (four `File.AppendAllText` call sites: `NativeProjectionBinder`
selector-body + per-leaf, `MongoProjectionBindingRemovingExpressionVisitor`'s
`CollectionShaperExpression` arm and its owner-key branch, `MongoProjectionBindingExpressionVisitor.VisitNew`'s
per-argument types) was applied, measured, and reverted with `git checkout -- src/`.
`git status --short` is empty apart from this document.

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
env -u MONGODB_URI -u ATLAS_URI EF360_PROBE_LOG=<path> dotnet test <FunctionalTests.csproj> \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpikeArrayProjectionTests"
env -u MONGODB_URI -u ATLAS_URI                      dotnet test <SpecificationTests.csproj> -c "Debug EF10" --no-build
env -u MONGODB_URI -u ATLAS_URI MONGODB_EF_NATIVE_ONLY=1 dotnet test <SpecificationTests.csproj> -c "Debug EF10" --no-build
```

**How Q1's "document with no owner key" was constructed.** A stored MongoDB document cannot lack
`_id`, so the projected document the slice would produce was simulated with a **read-only MongoDB
view** whose pipeline is exactly the `$project` from §1 of the design
(`{create: <view>, viewOn: <coll>, pipeline: [{$project: {_id: 0, Title: 1, Posts: 1}}]}`), queried
through `GetCollection<Blog>(<view>)`. The view was verified to genuinely have no `_id`
(`Q1 raw view doc (no _id) = { "Title" : "a_missing" }`), and a scalar-only projection off the same
view was run as a control. This exercises the **real** DOM element shaper against a **real**
owner-key-less document.

---

## Summary of verdicts

| # | Claim the design rests on | Verdict |
|---|---|---|
| **Q1** | Owned element materialization may need an owner key present in the document it reads | **CONFIRMED for a shadow-key owned collection** (hard failure, with file:line) — **MEASURED FALSE for an explicit-declared-key one** (works fine, and the owner-key read is never even emitted) |
| **Q2** | The array leaf may arrive wrapped in `MaterializeCollectionNavigationExpression` | **CONFIRMED** — it is exactly that, identically for the anonymous, `EF.Property` and `MemberInit`/DTO spellings |
| **Q3** | The mixed shaper may or may not materialize an entity-collection leaf | **MEASURED: it materializes it CORRECTLY**, for every array state, under both `Native` and `DriverLinq`. §3.3's *first* branch applies |
| **Q4** | No converter guard is needed on the array leaf | **SUPPORTED, not proven.** Element-level value converters and non-default `BsonRepresentation` round-trip correctly on every existing path. What that does and does not prove is spelled out below |
| **Q5** | `Select(b => new { b.Title, b.Posts })` throws `ArgumentException` identically in **all three** modes, before `MongoQueryMode` is read | **MEASURED FALSE — this is the headline finding.** The **in-scope** shape does **not** throw at all: it returns correct data under `Native` and `DriverLinq` and declines cleanly under `NativeOnly`. The `ArgumentException` fires only when the projected **element type has a navigation of its own** — the case §1.2 declares **out of scope** |
| **Q6** | — (baseline) | `Native`: **4589 pass / 0 fail / 19 skip**. `NativeOnly`: **2194 pass / 2395 fail / 19 skip**. Total 4608 each |

**Two design assumptions were measured false (Q5 outright, Q1 by half), and the consequence is that
§1's "this is simultaneously a fallback → native widening and a bug fix" is wrong: for everything
§1.1 puts in scope it is a pure fallback → native widening, and the bug lives entirely in the
§1.2 out-of-scope set.** Details below.

---

## Q5 — exact current failure (read this first; it reframes the slice)

### Q5a — the in-scope shape does NOT fail

Model: `Blog { ObjectId Id; string Title; List<Post> Posts; List<Post> Drafts; List<string> Tags }`,
`OwnsMany(b => b.Posts)` + `OwnsMany(b => b.Drafts)`. **`Post` has NO navigations of its own**
(`Post { string? Heading; int? Rank }`). Seed = the five-state matrix (missing / explicit BSON null /
empty / single / multi). Verbatim:

```
Q5 Native ANON new { b.Title, b.Posts }: OK
Q5 DriverLinq ANON new { b.Title, b.Posts }: OK
Q5 NativeOnly ANON new { b.Title, b.Posts }: MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException
Q5 NativeOnly ANON new { b.Title, b.Posts }   MESSAGE: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.

Q5 Native DTO new TitlePosts { ... }: OK
Q5 DriverLinq DTO new TitlePosts { ... }: OK
Q5 NativeOnly DTO new TitlePosts { ... }: MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException
Q5 NativeOnly DTO new TitlePosts { ... }   MESSAGE: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
```

With values (from Q3's run of the identical query, ordered by `Title`, states
`a_missing / b_null / c_empty / d_single / e_multi`):

```
   a_missing=0|b_null=0|c_empty=0|d_single=1|e_multi=2
Q3 Native anon-with-entity-collection: OK
   a_missing=0|b_null=0|c_empty=0|d_single=1|e_multi=2
Q3 DriverLinq anon-with-entity-collection: OK
```

Emitted MQL under `Native` (captured via `SpyLoggerProvider`):

```
   MQL: Executed MQL query
EFTest-2026-07-29T19-22-45-1.Q5_exact_current_failure_26d34b36.aggregate([])
```

So today the in-scope shape: **falls back**, fetches the whole document (`aggregate([])`), and folds
the projection client-side through `MongoMixedProjectionBindingRemovingExpressionVisitor`, returning
**correct** values including the EF-358 empty-collection normalization for the missing / explicit-null
rows.

**Tracking**, same shape:

```
Q5 Native TRACKING new { b.Title, b.Posts }: System.InvalidOperationException
Q5 Native TRACKING new { b.Title, b.Posts }   MESSAGE: A tracking query is attempting to project an owned entity without a corresponding owner in its result, but owned entities cannot be tracked without their owner. Either include the owner entity in the result or make the query non-tracking using 'AsNoTracking'.
Q5 DriverLinq TRACKING new { b.Title, b.Posts }: System.InvalidOperationException   (identical message)
Q5 NativeOnly TRACKING new { b.Title, b.Posts }: NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
```

§1's tracking paragraph is therefore **CONFIRMED** for `Native`/`DriverLinq` (EF Core's own owned-tracking
guard) with one refinement worth recording: under `NativeOnly` the provider's own decline fires
**first**, so the EF Core guard is never reached and the exception type differs by mode.

### Q5b — what actually throws the `ArgumentException`

The prior fixtures that "confirmed" EF-360
(`ProjectedCollectionNormalizationTests.Post`, `NativeOwnedCollectionCountTests.Post`) both give the
element a nested owned collection (`Post.Comments`). Isolating that variable:

```
Q5b Native NESTED-COLLECTION element  new { b.Title, b.Posts }: System.ArgumentException
Q5b Native NESTED-COLLECTION element  new { b.Title, b.Posts }   MESSAGE:  Argument type 'System.Collections.Generic.IEnumerable`1[...+NestedPost]' does not match the corresponding member type 'System.Collections.Generic.List`1[...+NestedPost]' (Parameter 'arguments[1]')
Q5b DriverLinq NESTED-COLLECTION element  new { b.Title, b.Posts }: System.ArgumentException   (identical message)
Q5b NativeOnly NESTED-COLLECTION element  new { b.Title, b.Posts }: System.ArgumentException   (identical message)

Q5b Native NESTED-REFERENCE element  new { b.Title, b.Posts }: System.ArgumentException
Q5b Native NESTED-REFERENCE element  new { b.Title, b.Posts }   MESSAGE:  Argument type 'System.Collections.Generic.IEnumerable`1[...+RefPost]' does not match the corresponding member type 'System.Collections.Generic.List`1[...+RefPost]' (Parameter 'arguments[1]')
Q5b DriverLinq / NativeOnly NESTED-REFERENCE element: System.ArgumentException   (identical message)
```

So the trigger is **"the projected element type has ANY navigation of its own"** — a nested owned
**collection** (`OwnsMany(Posts, p => p.OwnsMany(Comments))`) *or* a nested owned **single reference**
(`OwnsMany(Posts, p => p.OwnsOne(Geo))`). Both fail identically. The bare form on the same nested
model still works: `Q5b Native NESTED-COLLECTION element bare Select(b=>b.Posts): OK   0|2`.

**Root cause, traced (not guessed).** Stack:

```
   at System.Linq.Expressions.Expression.ValidateNewArgs(...)
   at System.Linq.Expressions.Expression.New(...)
   at System.Linq.Expressions.NewExpression.Update(IEnumerable`1 arguments)
   at ...MongoProjectionBindingExpressionVisitor.VisitNew(NewExpression) in .../MongoProjectionBindingExpressionVisitor.cs:line 661
   at ...MongoProjectionBindingExpressionVisitor.Visit(Expression)              :line 83
   at ...MongoProjectionBindingExpressionVisitor.Translate(...)                 :line 60
   at ...MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect(...)  :line 351
```

and the per-argument instrumentation at the throwing line:

```
  [INSTR VisitNew] arg0 argType=System.String
                        visitedKind=ProjectionBindingExpression visitedType=System.String afterMatchTypes=System.String
  [INSTR VisitNew] arg1 argType=System.Collections.Generic.List`1[...+NestedPost]
                        visitedKind=MethodCallExpression2
                        visitedType=System.Collections.Generic.IEnumerable`1[...+NestedPost]
                        afterMatchTypes=System.Collections.Generic.IEnumerable`1[...+NestedPost]
```

An element with a navigation makes EF's nav-expansion emit the auto-include as a `Queryable.Select`
*inside* the `MaterializeCollectionNavigationExpression`'s subquery. That reaches
`MongoProjectionBindingExpressionVisitor.VisitMethodCall`'s `nameof(Queryable.Select)` arm
(`MongoProjectionBindingExpressionVisitor.cs:517-532`), which rebuilds it as
`Expression.Call(EnumerableMethods.Select, shaper, lambda)` — typed **`IEnumerable<T>`**.
`MatchTypes` leaves that alone (its `TryGetItemType()` short-circuit — the *same* mechanism the
EF-357 comment at `:534-546` documents), and `Expression.New`'s member-type validation then rejects
`IEnumerable<T>` for a `List<T>` anonymous-type member at
`MongoProjectionBindingExpressionVisitor.cs:661`. An element with **no** navigation produces no inner
`Select`, so `Visit` returns the `CollectionShaperExpression` itself — already typed `List<T>` — and
validation passes.

This is the **same `MatchTypes` root-cause family** the count-projection slice recorded as an untaken
follow-on, reached from a different direction. It is **not** the `IQueryable<T>`-parameter
`ArgumentException` that slice measured; the message and the failing site are different.

### What Q5 changes about the design

1. **§1's "this is simultaneously a fallback → native widening and a bug fix" is wrong.** For every
   shape §1.1 lists in scope, the current behaviour is a *working* graceful fallback. The slice is a
   pure fallback → native widening there. This matters because it changes the acceptance bar: the
   slice now has a **working `Native`/`DriverLinq` oracle** for the in-scope shapes, so
   `Native == DriverLinq` parity over the full state matrix is available and should be the gate,
   rather than the crash → native framing §6 assumes.
2. **§1 and §2's "throws `ArgumentException` in all three query modes (EF-360), at shaper-build time,
   before `MongoQueryMode` is read" is false as a description of the in-scope shape.** It is true only
   of the element-has-a-navigation case, which §1.2 bullet 4 ("a nested collection inside the projected
   element") declares out of scope — and it is *broader* than that bullet says, since a nested owned
   **single reference** on the element fails identically.
3. **§2's comparison table is wrong on its first row.** `Select(b => new { b.Title, b.Posts })` is not
   "blocked by DOM-shaper alias read-back + EF-360's shaper-type mismatch"; it is not blocked at all
   today — it is only *not native*. The DOM-shaper alias read-back is the sole thing standing between
   it and native routing. (§2's second row, and its finding that the bare form is additionally blocked
   by the SP3-wide bare-projection boundary, are unaffected and were confirmed:
   `[INSTR binder] selector.Body kind=MaterializeCollectionNavigationExpression` → the binder's
   `default: return false`.)
4. **§3.3's premise needs rewriting.** "EF-360 is the `ArgumentException` an anonymous projection with
   an entity-collection leaf throws in every mode" is false. EF-360 is the `ArgumentException` an
   anonymous projection throws when the **projected element type carries a navigation**. Making the
   in-scope shapes native does **not** fix it "for those shapes" — those shapes never had it.
5. **The scope split in §1.1/§1.2 is inverted relative to the defect.** If closing EF-360 is a goal of
   this slice, the element-with-a-navigation case has to move *in* (or EF-360 has to be explicitly
   decoupled from this slice). As written, the slice can land in full and EF-360 will still reproduce.

---

## Q1 — owner-key availability

### Q1a — shadow-key owned collection: the owner key IS needed

Controls first (base collection, and a view that keeps `_id`, both over the five-state matrix):

```
  [INSTR shaper:141] shaperType=MongoMixedProjectionBindingRemovingExpressionVisitor CollectionShaper.Projection kind=ObjectArrayProjectionExpression
  [INSTR ownerkey] entityType=Blog.Posts#Post property=BlogId principal=Blog.Id ownerMapped=True
   counts=0,0,0,1,2
Q1 shadow BASE(_id present) Select(b=>b.Posts): OK
  ... (same two instrumentation lines) ...
   counts=0,0,0,1,2
Q1 shadow VIEW(_id KEPT) Select(b=>b.Posts): OK

Q1 raw view doc (no _id) = { "Title" : "a_missing" }
   titles=a_missing,b_null,c_empty,d_single,e_multi
Q1 shadow VIEW(no _id) Select(b=>b.Title) [control]: OK
```

Then the same query over the `_id`-less view:

```
  [INSTR shaper:141] shaperType=MongoMixedProjectionBindingRemovingExpressionVisitor CollectionShaper.Projection kind=ObjectArrayProjectionExpression
  [INSTR ownerkey] entityType=Blog.Posts#Post property=BlogId principal=Blog.Id ownerMapped=True
Q1 shadow VIEW(no _id) Select(b=>b.Posts): System.InvalidOperationException
Q1 shadow VIEW(no _id) Select(b=>b.Posts)   MESSAGE: Document element is missing for required non-nullable property 'Id'.
```

**Where it fails**, verbatim stack head:

```
   at MongoDB.EntityFrameworkCore.Storage.BsonBinding.GetPropertyValue[T](BsonDocument document, IReadOnlyProperty property)
        in .../src/MongoDB.EntityFrameworkCore/Storage/BsonBinding.cs:line 229
   at lambda_method373(Closure, BsonDocument, Int32)
   at ...MongoProjectionBindingRemovingExpressionVisitor.PopulateCollection[TEntity,TCollection](IClrCollectionAccessor accessor, IEnumerable`1 entities)
        in .../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:line 992
   at ...QueryingEnumerable`2.Enumerator.MoveNextHelper() in .../Query/QueryingEnumerable.cs:line 200
```

So it is a **materialization-time, per-row** failure inside the element shaper, not a translation-time
one. The `[INSTR ownerkey]` line shows the shaper resolving the element's shadow FK `BlogId` to its
principal `Blog.Id` and reading it from the owner document (`ownerMapped=True`, i.e. via `_ownerMappings`
exactly as §4/Q1 anticipates).

The same failure reproduces for the **actual in-scope shape** off an `_id`-less view, and for the
converter model:

```
Q5c VIEW(no _id) Native  new { b.Title, b.Posts }: System.InvalidOperationException
Q5c VIEW(no _id) Native  new { b.Title, b.Posts }   MESSAGE: Document element is missing for required non-nullable property 'Id'.
Q4 VIEW(no _id) bare array projection: System.InvalidOperationException
Q4 VIEW(no _id) bare array projection   MESSAGE: Document element is missing for required non-nullable property 'Id'.
Q1 shadow VIEW(no _id) whole-entity ToList: System.InvalidOperationException
Q1 shadow VIEW(no _id) whole-entity ToList   MESSAGE: Document element is missing for required non-nullable property 'Id'.
```

### Q1b — explicit declared key: the owner key is NOT needed

Model `OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId))`. Base control, then the `_id`-less view:

```
  [INSTR shaper:141] shaperType=MongoMixedProjectionBindingRemovingExpressionVisitor CollectionShaper.Projection kind=ObjectArrayProjectionExpression
   counts=0,2
Q1 keyed BASE(_id present) Select(b=>b.Posts): OK
  [INSTR shaper:141] shaperType=MongoMixedProjectionBindingRemovingExpressionVisitor CollectionShaper.Projection kind=ObjectArrayProjectionExpression
   counts=0,2
   ids=|1:kh1,2:kh2
Q1 keyed VIEW(no _id) Select(b=>b.Posts): OK
```

Note what is **absent**: there is **no `[INSTR ownerkey]` line at all** on either keyed run. With a
declared key the element's owner FK is never materialized, so the owner-key read is not merely
tolerable — it is never emitted.

### Q1 verdict and what it changes

**CONFIRMED for a shadow-key owned collection; MEASURED FALSE for an explicit-declared-key one.**
§4/Q1's premise holds, but only on one of the two axes it asked about.

**The fix is to emit the owner key into the `$project`, not to decline.** §5.1 already establishes
this is a one-line change and that `RenderProject`'s `_id: 0` correctly disappears once the projection
emits `_id`; §5.1's "either way the shaper reads by alias, so an extra projected `_id` is inert for the
result shape" is confirmed by the Q1a control (`Q1 shadow VIEW(_id KEPT)` returns the identical
`counts=0,0,0,1,2`). Declining for shadow-key element types would decline the overwhelmingly common
case, and the failure mode if the key is *not* emitted is a per-row
`InvalidOperationException: Document element is missing for required non-nullable property 'Id'` at
materialization — a hard failure, not silent wrong data, which is the one mercy here.

**One caveat the design should record:** the owner key that must be emitted is the *root* document's
`_id`, and Q1a shows the shaper reads it through `_ownerMappings` keyed on
`Navigation.DeclaringEntityType`. For an array reached through one or more `OwnsOne` hops
(`b.Home.Notes`, §1.1's fifth row) the declaring entity type is the *owned reference* type, whose own
key is again ultimately rooted at `_id` — so `_id` alone is expected to suffice, but that composition
was **NOT MEASURED** (the dotted-leaf probe ran against a base collection with `_id` present, not
against an `_id`-less view). Worth one probe during implementation.

---

## Q2 — what the binder receives

Instrumented `NativeProjectionBinder.TryPopulateNativeProjection` (selector body) and
`TryTranslateLeaf` (each leaf). Verbatim, one block per spelling:

```
Q2 --- anonymous: new { b.Title, b.Posts } ---
  [INSTR binder] selector.Body kind=NewExpression nodeType=New body=new <>f__AnonymousType135`2(Title = b.Title, Posts = [Microsoft.EntityFrameworkCore.Query.MaterializeCollectionNavigationExpression])
  [INSTR leaf] kind=PropertyExpression nodeType=MemberAccess type=System.String text=b.Title
  [INSTR leaf] kind=MaterializeCollectionNavigationExpression nodeType=Extension type=System.Collections.Generic.List`1[...+Post] text=[Microsoft.EntityFrameworkCore.Query.MaterializeCollectionNavigationExpression]

Q2 --- EF.Property spelling ---
  [INSTR binder] selector.Body kind=NewExpression nodeType=New body=new <>f__AnonymousType136`2(Title = b.Title, P = [Microsoft.EntityFrameworkCore.Query.MaterializeCollectionNavigationExpression])
  [INSTR leaf] kind=PropertyExpression nodeType=MemberAccess type=System.String text=b.Title
  [INSTR leaf] kind=MaterializeCollectionNavigationExpression nodeType=Extension type=System.Collections.Generic.List`1[...+Post] ...

Q2 --- MemberInit DTO spelling ---
  [INSTR binder] selector.Body kind=MemberInitExpression nodeType=MemberInit body=new TitlePosts() {Title = b.Title, Posts = [Microsoft.EntityFrameworkCore.Query.MaterializeCollectionNavigationExpression]}
  [INSTR leaf] kind=PropertyExpression nodeType=MemberAccess type=System.String text=b.Title
  [INSTR leaf] kind=MaterializeCollectionNavigationExpression nodeType=Extension type=System.Collections.Generic.List`1[...+Post] ...

Q2 --- two array leaves: new { b.Posts, b.Drafts } ---
  [INSTR leaf] kind=MaterializeCollectionNavigationExpression ... (once per leaf)

Q2 --- primitive collection leaf: new { b.Title, b.Tags } ---
  [INSTR leaf] kind=PropertyExpression nodeType=MemberAccess type=System.Collections.Generic.List`1[System.String] text=b.Tags
   tags=|||t1|t1,t2

Q2 --- baseline for contrast: bare Select(b => b.Posts) ---
  [INSTR binder] selector.Body kind=MaterializeCollectionNavigationExpression nodeType=Extension ...
```

**Verdict: CONFIRMED, and unusually cleanly.** The array leaf is a
`MaterializeCollectionNavigationExpression` — **not** a bare `MemberExpression` — and it is *the same
node kind, with the same `List<T>` static type, for all three spellings*
(anonymous / `EF.Property` / `MemberInit`). Consequences for §3.1:

- `TryTranslateLeaf`'s new branch must **unwrap `MaterializeCollectionNavigationExpression` first**;
  the leaf will never satisfy the existing `leafExpression is MemberExpression` test, so the new branch
  is structurally disjoint from every existing one and is not ordering-sensitive relative to them
  (§5.2's mutation-check should still be run, but this is the mechanical reason to expect it to pass).
- `MaterializeCollectionNavigationExpression.Navigation` gives the `INavigation` directly, so
  §3.2's `ArrayAliasProjectionExpression.Navigation` needs no separate resolution step. Note the design
  plans to route recognition through `TryResolveOwnedCollectionPath` for the *path*; that resolver
  wants a rooted member-access chain, so the unwrap has to hand it
  `MaterializeCollectionNavigationExpression.Subquery`'s underlying member access (or the equivalent),
  not the wrapper.
- **`EF.Property<List<Post>>(b, "Posts")` normalizes to the identical tree** — the design's Q2 ask about
  that spelling has no separate handling requirement.
- A **primitive** collection leaf (`b.Tags`) arrives as a plain `PropertyExpression` typed
  `List<string>` and is **already accepted** by the existing plain-field branch — the projection goes
  native today and returns correct values (`|||t1|t1,t2` for missing/null/empty/single/multi). §1.2's
  "primitive-element collections … flows through a property serializer" is confirmed, and importantly
  a primitive collection leaf is **not** something the new branch has to decline: it never reaches it.

---

## Q3 — the mixed path with an entity-collection leaf

Seed = the five-state matrix. `Select(b => new { b.Title, b.Posts })`:

```
   a_missing=0|b_null=0|c_empty=0|d_single=1|e_multi=2
Q3 Native anon-with-entity-collection: OK
   a_missing=0|b_null=0|c_empty=0|d_single=1|e_multi=2
Q3 DriverLinq anon-with-entity-collection: OK
Q3 NativeOnly anon-with-entity-collection: NativeTranslationNotSupportedException: Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
```

**Verdict: the mixed shaper materializes the collection CORRECTLY.** For every array state, in both
modes, with the EF-358 empty-collection normalization applied to the missing and explicit-null rows.

The design's note that "`MongoMixedProjectionBindingRemovingExpressionVisitor` overrides only the
`ProjectionBindingExpression` case, so its collection case is the base's hard cast at
`MongoProjectionBindingRemovingExpressionVisitor.cs:141`" is **half right, and the half that is wrong
matters.** Instrumentation:

```
  [INSTR shaper:141] shaperType=MongoMixedProjectionBindingRemovingExpressionVisitor CollectionShaper.Projection kind=ObjectArrayProjectionExpression
```

The mixed shaper *is* the one running (confirmed), but `collectionShaperExpression.Projection` is an
**`ObjectArrayProjectionExpression` directly**, so it takes the switch's **second** arm
(`case ObjectArrayProjectionExpression`), never the `ProjectionBindingExpression` arm that performs the
hard cast at `:141`. The hard cast is therefore **not** on the mixed path's current route at all. (The
`ProjectionBindingExpression` arm *is* reached on the whole-entity `Include` route —
`Q4 Native Include` logged `shaperType=MongoProjectionBindingRemovingExpressionVisitor ... kind=ObjectArrayProjectionExpression`,
also the second arm; no probe in this spike reached the first arm, so **which shape reaches `:141`'s
`GetProjection` arm was NOT MEASURED**.) §3.2 plans to turn `:141` into a three-way switch; that is
still the right site for the new `ArrayAliasProjectionExpression` arm, but the *reason* stated —
"its collection case is the base's hard cast at `:141`" as an account of why the mixed path fails — is
not the mechanism, because the mixed path does not fail.

Additional Q3 measurements:

```
Q3 Native HashSet nav new { b.Title, b.Posts }: OK          s_missing=0|s_multi=2
Q3 Native HashSet nav bare Select(b=>b.Posts): OK           HashSet`1:0|HashSet`1:2
Q3 Native anon whole-entity+scalar new { b, b.Title }: OK    (correct, both modes; NativeOnly declines)
```

A `HashSet<Post>` navigation works through the existing `IClrCollectionAccessor` path (§3.2's
"correct for free" claim is confirmed for the *current* path; whether the new arm preserves it is a
property of the implementation, not measured here).

**Reference (cross-collection) collection leaf** — `Select(c => new { c.Name, c.Orders })` over a
two-DbSet model:

```
Q3 Native      REFERENCE-collection leaf new { c.Name, c.Orders }: System.InvalidOperationException
Q3 DriverLinq  REFERENCE-collection leaf new { c.Name, c.Orders }: System.InvalidOperationException
Q3 NativeOnly  REFERENCE-collection leaf new { c.Name, c.Orders }: System.InvalidOperationException
   MESSAGE (all three, identical): The LINQ expression 'DbSet<Order>()' could not be translated. Either rewrite the query in a form that can be translated, or switch to client evaluation explicitly by inserting a call to 'AsEnumerable', 'AsAsyncEnumerable', 'ToList', or 'ToListAsync'. ...
```

### What Q3 changes about the design

**§3.3's decision is settled, and the answer is the first branch — but the branch has almost nothing
left to do.** The mixed shaper materializes an owned entity-collection leaf correctly, so an
out-of-scope owned array leaf in the same projection declines *gracefully* with correct results, and
it does so **already, with no shaper fix needed** — §3.3's first bullet's proviso ("that requires
fixing the shaper-side type mismatch that currently throws") does not apply, because nothing throws.

The three out-of-scope leaf kinds §3.3 lists behave differently from one another, and none of them
matches the design's account:

| Out-of-scope leaf in the same projection | Measured current behaviour |
|---|---|
| primitive collection (`b.Tags`) | **already native**, correct values (Q2) |
| reference collection (`c.Orders`) | hard-fails in **all three modes**, `InvalidOperationException` "could not be translated" — EF Core's own translation-failure path, reached before the provider's binder |
| element-has-a-navigation (nested collection **or** nested reference) | hard-fails in **all three modes**, `ArgumentException` at `MongoProjectionBindingExpressionVisitor.cs:661` (Q5b) |

So §3.3 as written ("this slice also closes it for an out-of-scope array leaf … so those decline
instead of crashing") is aimed at three distinct dispositions, one of which is already native, one of
which is EF Core's own decline and not the provider's to change, and one of which is the actual EF-360
defect the slice puts out of scope. **The honest closure has to be decided per-kind, not as one
branch.**

One concrete hazard the reference-collection row creates: if the slice changes that leaf's failure to a
provider-authored `NotSupportedException` (§3.3's second branch), it will break spec tests — see Q6.

---

## Q4 — converter / `BsonRepresentation` round-trip

Model `ConvBlog { List<ConvPost> Posts }`,
`OwnsMany(Posts, p => { p.Property(x => x.Ref).HasBsonRepresentation(BsonType.String);
p.Property(x => x.Code).HasConversion(v => v.ToString(), v => (Grade)Enum.Parse(...)); })` —
i.e. a `Guid` with a non-default `BsonRepresentation` **and** a value-converted enum on the **element**.
Seeded raw so the stored forms are genuinely strings:

```
Q4 expected: p1 -> Ref=ed7b55f7-8b10-4e46-8851-5f21c57c7d77 Code=High ; p2 -> Ref=f4b8f234-8f0f-44eb-b253-a6fe32e7bd32 Code=Low
Q4 raw stored = { "_id" : {...}, "Title" : "c_multi", "Posts" : [
    { "Heading" : "p1", "Ref" : "ed7b55f7-8b10-4e46-8851-5f21c57c7d77", "Code" : "High" },
    { "Heading" : "p2", "Ref" : "f4b8f234-8f0f-44eb-b253-a6fe32e7bd32", "Code" : "Low" }] }
```

Results (`Heading/Ref/Code` per element; `c_missing` has no `Posts` field at all):

```
   c_missing:
   c_multi: p1/ed7b55f7-8b10-4e46-8851-5f21c57c7d77/High,p2/f4b8f234-8f0f-44eb-b253-a6fe32e7bd32/Low
Q4 Native whole-entity: OK
   p1/ed7b55f7-8b10-4e46-8851-5f21c57c7d77/High,p2/f4b8f234-8f0f-44eb-b253-a6fe32e7bd32/Low
Q4 Native bare array projection Select(b=>b.Posts): OK
Q4 Native Include: OK            (same values)
Q4 DriverLinq whole-entity: OK   (same values)
Q4 DriverLinq bare array projection Select(b=>b.Posts): OK   (same values)
Q4 DriverLinq Include: OK        (same values)
```

**Verdict: SUPPORTED, not proven — and the gap must be stated precisely.**

What is proven: on **every path that exists today** — whole-entity materialization, `Include`, and the
bare array projection through the mixed shaper, in both `Native` and `DriverLinq` — the ordinary
element shaper applies the element's own value converter and non-default `BsonRepresentation`
correctly. This is the mechanism §3.1 relies on ("each element is read back as a `BsonDocument` and
materialized by the ordinary entity shaper, which applies each property's own converter/`BsonRepresentation`
correctly"), and it is real.

What is **NOT** proven: that the same holds when the array is read **from a projection alias**. There
is no way to read from an alias on unmodified `src/`, so the alias path is **NOT MEASURED**. The
argument that it will hold is: the new arm changes only *where the `BsonArray` comes from*
(`BsonBinding.CreateGetBsonArray(DocParameter, alias)` instead of the parent-document path) and leaves
the per-element `innerShaper` untouched, so the element shaper's converter handling is not on the
diff. That argument is sound but it is an **inference**, and the thing that would falsify it is a
change to the element shaper's construction rather than its input — which is exactly the sort of thing
review pressure produces. **Recommendation: keep §3.1's "no converter guard on the array itself"
decision, but make the implementation's converter test read from the alias** (i.e. it must be one of
the slice's own new tests, over the `ConvPost`-shaped fixture above, asserting the exact Guid and enum
values), not a reuse of the existing-path coverage this spike measured.

Separately: the `_id`-less-view leg of Q4 fails with Q1a's owner-key error, not a converter error, so
the two axes are independent and the converter question is not entangled with Q1's fix.

---

## Q6 — spec-suite baseline, both axes

Whole EF10 `SpecificationTests` suite, on this branch, `src/` unmodified.

```
# default Native
Passed!  - Failed:     0, Passed:  4589, Skipped:    19, Total:  4608, Duration: 42 s

# MONGODB_EF_NATIVE_ONLY=1
Failed!  - Failed:  2395, Passed:  2194, Skipped:    19, Total:  4608, Duration: 29 s
```

| Axis | Pass | Fail | Skip | Total |
|---|---|---|---|---|
| `Native` (default) | **4589** | **0** | 19 | 4608 |
| `NativeOnly` | **2194** | **2395** | 19 | 4608 |

Identical to the baselines the `All`-slice and owned-sub-property notes in `Query/AGENTS.md` record
(4589/0/19 and 2194/2395/19), so nothing on this branch has moved the suite.

### Both-axes cross-tab (the lesson the `All` slice paid for)

Cross-tabbing the two `.trx` files per test:

```
NativeOnly-Failed but Native-Passed (the 'invisible on the pass-set axis' population): 2395
```

That is **all 2395** `NativeOnly` failures. Every single one is a `Native` pass with a live `Native`-mode
baseline. So for this suite the "`NativeOnly`-failing tests are invisible if you only inventory the pass
set" hazard is not a narrow corner — it covers the entire fail set, and a pass-set-only inventory would
be blind to 52% of the suite.

### Tests this slice could plausibly rebaseline

Candidate shapes are the spec's array-valued projections. Every one of them is a **reference**
(cross-collection) collection leaf — Northwind has no owned collections at all, which is why the
in-scope owned shapes have zero spec coverage. Both axes, verbatim from the cross-tab:

| Spec test (×2, `async: False`/`True`) | `Native` | `NativeOnly` |
|---|---|---|
| `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_simple` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_simple_followed_by_ordering_by_scalar` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_multi_part` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_multi_part2` | Passed | Passed |
| `NorthwindFunctionsQueryMongoTest.Order_by_length_twice_followed_by_projection_of_naked_collection_navigation` | Passed | Passed |
| `NorthwindSelectQueryMongoTest.SelectMany_without_result_selector_collection_navigation_composed` | Passed | Passed |
| `NorthwindSelectQueryMongoTest.SelectMany_without_result_selector_naked_collection_navigation` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_FirstOrDefault_project_entity` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_FirstOrDefault_project_anonymous_type` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_FirstOrDefault_project_anonymous_type_client_eval` | Passed | Passed |
| `NorthwindNavigationsQueryMongoTest.Select_collection_navigation_simple2` | Passed | Passed |
| `NorthwindAggregateOperatorsQueryMongoTest.Multiple_collection_navigation_with_FirstOrDefault_chained` | Passed | Passed |

**The exposure is concentrated in the first five.** Those five (10 test cases) are overridden as
`await AssertTranslationFailed(() => base.<Test>(async)); AssertMql();` — an **empty** MQL baseline plus
an assertion that translation failed. `NorthwindNavigationsQueryMongoTest` and
`NorthwindFunctionsQueryMongoTest` do **not** declare their own `AssertTranslationFailed`
(grep-verified: the local overrides live only in `NorthwindAggregateOperatorsQueryMongoTest`,
`NorthwindAsTrackingQueryMongoTest`, `NorthwindChangeTrackingQueryMongoTest` and
`BuiltInDataTypesMongoTest`), so they use EF Core's upstream helper, which requires an
`InvalidOperationException` carrying the "could not be translated" text. That is **exactly** the
exception Q3 measured for a reference-collection leaf. Therefore:

> **If the EF-360 closure replaces the reference-collection leaf's `InvalidOperationException`
> ("could not be translated") with a provider-authored `NotSupportedException` — §3.3's second
> branch — these tests flip Passed → Failed on BOTH axes.** They are `AssertTranslationFailed`
> assertions, not MQL-string assertions, so they would fail on the exception *type*, not on a
> re-baselineable string, and `EF_TEST_REWRITE_BASELINES` would not fix them.

(`Select_collection_navigation_simple2` is a projected `.Count`, already native
(`$lookup` + `$project` with `$size`) and unaffected. The `Select_collection_FirstOrDefault_*` and
`Multiple_collection_navigation_with_FirstOrDefault_chained` tests reduce the collection with
`FirstOrDefault` rather than projecting the array, so no array leaf reaches the binder.)

**Predicted delta from the in-scope work alone: zero.** Northwind has no owned collections, so no spec
test exercises an owned array-valued projection at all — the routing flip has nothing to move. The
delta risk is entirely in the EF-360 closure, on the exception-type axis, in the ten test cases above.
Following the `All` slice's lesson, that prediction should be **re-measured on both axes after
implementation**, not trusted.

---

## Loose ends deliberately not closed

- **§5.3's `Union` dedup probe** over projected documents containing arrays: **NOT MEASURED.** Out of
  the six questions; still worth the one probe §5.3 asks for.
- **Which shape reaches `MongoProjectionBindingRemovingExpressionVisitor.cs:141`'s
  `ProjectionBindingExpression`/`GetProjection` arm: NOT MEASURED.** Every collection shaper this spike
  observed (mixed projection path and whole-entity `Include` path alike) took the
  `case ObjectArrayProjectionExpression` arm instead. Relevant because §3.2 plans to convert that switch
  to three arms and to add a `default: throw TranslationFailed`; the existing first arm's coverage should
  be established before assuming a mutation there would be caught.
- **Owner-key emission for an array reached through `OwnsOne` hops (`b.Home.Notes`): NOT MEASURED**
  against an `_id`-less document (see Q1's caveat). The dotted leaf itself was measured and works today:
  `Q5b Native DOTTED leaf new { b.Title, b.Home.Notes }: OK   n_missing=0|n_multi=1`, `DriverLinq` the
  same, `NativeOnly` declining cleanly.
- **Sibling-leaf composition** was measured and is fine today:
  `Q5c Native new { b.Title, N = b.Posts.Count, b.Posts }: OK  a_missing=N0/0|b_null=N0/0|c_empty=N0/0|d_single=N1/1|e_multi=N2/2`
  and `Q5c Native new { b.Title, b.Posts, b.Drafts }: OK  a_missing=0/0|b_null=0/0|c_empty=0/0|d_single=1/0|e_multi=2/1`.
  Both currently fall back as a whole (the count leaf does **not** go native when a sibling leaf declines,
  as expected from the binder's all-or-nothing contract).

## Discarded spike code

`tests/.../Query/SpikeArrayProjectionTests.cs` was deleted and the temporary instrumentation in
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`,
`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
and `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` was
reverted (`git checkout -- src/`); the solution rebuilds clean. `git status --short` confirms this
document is the only change.
