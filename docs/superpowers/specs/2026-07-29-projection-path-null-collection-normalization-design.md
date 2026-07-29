# EF-358 — the projection path normalizes a missing/null embedded array to an empty collection

**Ticket:** EF-358 (Bug). **Closes as a side effect:** EF-357 (its documented residual).
**Branch:** `EF-358`, stacked on the unmerged EF-322 native stack (`origin/NativeQueryOngoing` = `cfe873e`).
**Date:** 2026-07-29.

---

## 1. The defect

**Correction (made after Task 2's implementation, not before it — see the "Corrected, post-implementation"
note under §1.1 for the full mechanism).** This section originally framed the defect as "whole-entity
materialization normalizes; the projection path doesn't." That framing is **false** — it was believed true
from the owned-data slice 7 measurement (which really did observe whole-entity queries returning an empty
collection), but a later, targeted measurement (task-2-report.md's addendum, and independent verification in
source) showed the provider never distinguished whole-entity from projection at the point that matters: it
computed `null` for a missing/explicit-null stored array on **every** path, projection and whole-entity alike,
and never created a collection for that row. What looked like "whole-entity is fine" was an accident of which
CLR field-initializer style the entity class happened to use — see §1.1's correction for the actual mechanism.
The paragraph below is kept as *originally written*, because it states the **observed symptom** correctly (the
`Select(b => b.Posts.Count)` throw and the `Select(b => b.Posts)` null are real, and were genuinely fixed); only
the **causal story** — why whole-entity queries looked unaffected — was wrong, and is corrected in §1.1.

Whole-entity materialization **normalizes** a missing or explicitly-BSON-null embedded array to an empty
collection. The projection path **does not** — it materializes `null`. Measured side by side on the same three
documents (empty array / missing field / explicit `null`) during owned-data slice 7.

Consequences, all already recorded in `docs/native-query-status-EF-322.md` §4:

- `Select(b => b.Posts.Count)` throws `ArgumentNullException` (`Enumerable.Count(null)`) instead of returning 0
  for those rows. This is the residual that keeps **EF-357** only partially resolved.
- `Select(b => b.Posts)` returns `null` for those rows, where the same entity materialized whole yields an
  empty collection.

The **native** path does not share the problem: `$ifNull` maps both states to `[]`, so a native count leaf
answers 0. This is a defect of the DOM/fallback shaper, not of native translation — so, unlike the seven
owned-data slices below it in this stack, **this is not a fallback→native widening**. It is a plain bug fix in
the shaper that both `Native`-DOM and driver-LINQ share.

### 1.1 Root cause — one site

`Query/Visitors/BsonDocumentInjectingExpressionVisitor.cs`, the `CollectionShaperExpression` case. It assigns
the array via `TypeAs` and then guards on null, collapsing the **entire collection shaper** to a null constant:

```csharp
Expression.Assign(
    arrayVariable,
    Expression.TypeAs(collectionShaperExpression.Projection, typeof(BsonArray))),
Expression.Condition(
    Expression.Equal(arrayVariable, Expression.Constant(null, arrayVariable.Type)),
    Expression.Constant(null, collectionShaperExpression.Type),   // <- the defect
    collectionShaperExpression)
```

**Corrected, post-implementation — this paragraph was measured false.** It originally read: "The whole-entity
path never reaches this. It fills collections through `IClrCollectionAccessor` on an already-created
collection (`MongoProjectionBindingRemovingExpressionVisitor`, the `PopulateCollection` helper and
`CollectionAccessorAddMethodInfo`), so an absent array simply contributes no elements and the collection stays
empty. That asymmetry *is* EF-358."

That is false: the whole-entity path reaches the **exact same** `Expression.Condition` above, for the exact
same reason a projection does — `BsonDocumentInjectingExpressionVisitor` visits every `CollectionShaperExpression`
in the shaper tree, nested ones (inside a whole entity) included, with no path-dependent branch. Verified two
ways: (1) directly in source, tracing the injector's unconditional `ExpressionVisitor.Visit` walk and (2) by
measurement — a throwaway probe (EF-358 task 2's coordinator-requested addendum) held one model and one
document fixed and varied only `.ToList()` vs `.First()`, `Native` vs `DriverLinq`, tracked vs `AsNoTracking`,
and streaming-eligible vs not; every cell gave the *same* answer for both cardinalities, ruling out a
whole-entity-vs-projection or DOM-vs-streaming split entirely.

**What actually produced the pre-fix "whole-entity looks fine" observation** is one layer up, in
`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection` — the fixup EF Core's own auto-included
`IncludeExpression` runs for *every* owned collection navigation (synthesized whether or not the query writes
an explicit `.Include()`). That method only calls `navigation.GetCollectionAccessor()!.GetOrCreate(entity,
forMaterialization: true)` inside `if (relatedEntities != null)`. Pre-fix, `relatedEntities` — the value the
`Expression.Condition` above computed — was `null` for a missing/explicit-null array, so the fixup was skipped
**entirely**, and the navigation property was left exactly as the entity class's own field initializer set it:
`null` for a plain `List<T> Posts { get; set; }`, but `[]` for `List<T> Posts { get; set; } = [];` — a
pre-existing value nobody actively created for that row, simply never overwritten. Two entity classes reading
the identical broken (`null`) computation therefore reported two different answers, depending on an unrelated
authoring detail that has nothing to do with the query, the data, or the whole-entity/projection distinction.
That per-class-initializer-dependent split — not a whole-entity/projection split — *is* what EF-358 actually
removes: post-fix, `PopulateCollection` (in the same visitor) always returns a real, possibly-empty collection,
so `relatedEntities != null` is now always true, the fixup always runs, and every class gets the same answer
regardless of how it declared the property.

### 1.2 It is released behaviour, in every query mode

The conditional above is **byte-identical to `v10.0.2`** (the latest released v10; the only diff in that file
since the tag is an unrelated `AllVariables` addition). `BsonDocumentInjectingExpressionVisitor` is applied
unconditionally to the shaper body in `MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery`, before the
streaming rewrite and before the native-vs-fallback split has any bearing — so this null reaches users on the
native DOM path and the driver-LINQ path alike.

So `Select(b => b.Posts)` returning `null` for a ragged row is shipped behaviour that this change alters.

### 1.3 Disposition: a bug fix, WITH a BREAKING-CHANGES.md entry (ruling REVERSED)

> **REVERSED by the branch owner at the final whole-branch review. An entry IS added.** This section originally
> recorded the opposite ruling ("a bug fix, with no BREAKING-CHANGES.md entry"), and the original rationale is
> kept below rather than deleted, because most of it still stands — only its conclusion changed.
>
> **What carried the reversal**, raised by `api-stability-reviewer` and not considered when the original ruling
> was made: it defeats the second rationale bullet below ("the old value was **not usable**") *for the shipped
> case specifically*. On the PROJECTION path the shaper returned the null constant **directly**, with no POCO
> field initializer involved anywhere — so `Select(b => b.Posts)` yielded `null` deterministically, and
> `posts is null` was a working, usable discriminator between "the stored field was absent or explicitly null"
> and "the stored field was present but empty". In a schemaless store that is a meaningful distinction, and it
> is now unobservable through a materialized collection navigation. The "not usable" bullet is true of a
> downstream LINQ operator over the null (which throws) but false of the null itself as a *test*, which is a use
> that worked and no longer exists.
>
> The narrow rubric argument below (a read path changes no persisted document shape, so it is not one of the
> rubric's enumerated breaks) is **not** what was wrong. It remains correct as far as it goes; it simply is not
> the whole test — an observable behavior change on a supported, shipped API warrants an entry whether or not it
> matches an enumerated category.
>
> The entry landed under `BREAKING-CHANGES.md`'s **`## Breaking changes in 8.5.0 / 9.2.0 / 10.1.0`** heading —
> the file's existing top heading, and the current unreleased one (published maxima at the time of writing are
> `v8.4.2` / `v9.1.2` / `v10.0.2`), so no new heading was invented. It documents: the new empty-collection
> behavior on every read path; that the old behavior was partly field-initializer-dependent, so some users
> already saw empty; that absent-vs-present-but-empty is no longer observable through a collection navigation;
> and that a caller needing the distinction must query the stored field's existence through the driver.
>
> The **timing** argument at the end of this section is unaffected and still applies asymmetrically: the shipped
> `Select(b => b.Posts)` null is what the entry covers, while the count-projection residual is new on this
> unmerged stack and needs no entry of its own.

**Original ruling, superseded above — decided by the branch owner, recorded so it is settled rather than
re-raised in review.** The rationale was:

- The projection path **contradicts whole-entity materialization of the same bytes**. One of the two is wrong,
  and it is not the one that agrees with EF Core's own contract that a collection navigation materializes as an
  empty collection rather than `null`. **Caveat added post-implementation (§1.1):** the two paths did not
  reliably "contradict" each other pre-fix — both computed the identical `null`, and whether that null leaked
  to the caller depended on an unrelated class-authoring detail (whether the POCO's collection-navigation
  property declared a `= []` field initializer), not on projection-vs-whole-entity. The decision below is
  unaffected: EF Core's contract still calls for an empty collection, never `null`, regardless of which path
  or which class happened to mask the defect.
- The old value was **not usable**. A `null` collection navigation throws on any downstream LINQ operator —
  which is exactly how EF-358 was discovered (`Enumerable.Count(null)`). There was no working behaviour to
  preserve, only a latent throw moved earlier.
- The native path already answers the corrected way, so this **converges** the two execution paths rather than
  making them diverge.

On the versioning rubric specifically: its enumerated break list covers public API signatures/defaults/
visibility, annotation keys, the three named interfaces, and **behavior changes affecting persisted document
shape**. This is a **read** path — no stored document changes — so it is not one of the enumerated breaks. That
was the narrow reason given for adding no entry, distinct from the three substantive reasons above. (Still true
as stated, but not sufficient on its own — see the reversal at the top of this section.)

Timing is the other half of the argument, and it applies asymmetrically. The `Select(b => b.Posts)` null **is**
shipped (§1.2). The count-projection residual is **not** — it is new on this unmerged stack. Ship the residual
and the `ArgumentNullException` becomes de-facto contract, so fixing it later would cost a break entry on top of
the same work. Doing it now costs neither.

---

## 2. Scope

**In:** collection **navigations** materialized through `CollectionShaperExpression` — this covers projection
*and* whole-entity/`Include` reads alike, since (per §1.1's correction) both go through the identical
computation; there is no whole-entity-vs-projection branch to scope around. A missing or explicitly-null stored
array normalizes to an **empty collection** uniformly. Both states are treated alike, and — after this change —
every entity class is treated alike regardless of whether its own collection-navigation property declares a
`= []` field initializer or not (§1.1). ("Matching whole-entity materialization," this section's original
wording, is retained below only where it describes the *target* behavior EF Core's own contract calls for — a
collection navigation should materialize empty, never null — not as a claim that whole-entity queries already
did this reliably pre-fix; they did not, uniformly, for the reason in §1.1.)

**Out, and deliberately so:**

- **Primitive collection properties.** `Query/PrimitiveCollectionTests` pins a *nullable* primitive list to
  `null` for both missing (`Nullable_primitive_list_is_null_when_bson_missing`) and explicit-null
  (`Nullable_primitive_list_is_null_when_bson_null`) BSON, and a *non-nullable* one to a nullability-aware
  `InvalidOperationException`. That is a mapped property going through a property serializer, a different
  mechanism from `CollectionShaperExpression`, and its null is deliberate and nullability-aware. **This change
  must not touch it**, and those tests staying green is a named acceptance criterion, not a side effect of "the
  suite passed".
- **Making array-valued projections native.** `Select(b => b.Posts)` still routes through the fallback/DOM
  path. Going native needs a new alias-driven array read-back branch in the DOM shaper —
  `MongoProjectionBindingRemovingExpressionVisitor`'s collection case hard-casts the bound projection to
  `ObjectArrayProjectionExpression`, which is navigation-driven and entity-only. That is the next slice; EF-358
  is its precondition, because array projections are not observable-correct until ragged rows normalize.
- **EF-359** (filtered `Count(pred)` in a projection). Independent translation-time crash, unrelated mechanism.

---

## 3. Mechanism

**Two edits, in two files.** The normalization cannot live at the injection site — see §3.1 for the hard
constraint that rules that out.

**Edit 1 — `BsonDocumentInjectingExpressionVisitor`, `CollectionShaperExpression` case: delete the conditional,
leave the assignment byte-identical.**

```csharp
var expressions = new List<Expression>
{
    Expression.Assign(
        arrayVariable,
        Expression.TypeAs(
            collectionShaperExpression.Projection,
            typeof(BsonArray))),          // UNCHANGED — see §3.1, this shape is a cross-visitor contract
    collectionShaperExpression            // was: Expression.Condition(… null …, collectionShaperExpression)
};
```

**Edit 2 — `MongoProjectionBindingRemovingExpressionVisitor`, `CollectionShaperExpression` case: coalesce at the
point of use**, wrapping `bsonArrayExpression` before it is `Cast<BsonDocument>()`-ed:

```csharp
bsonArrayExpression = Expression.Coalesce(bsonArrayExpression, Expression.New(typeof(BsonArray)));
```

Coalescing at the *point of use* rather than at either assignment site is what makes one line cover **both**
array sources in that case: the bound `_projectionBindings` variable, and the nested
collection-in-collection branch that reads from the parent document via `BsonBinding.CreateGetBsonArray`.

The shaper then enumerates an empty `BsonArray`, and `PopulateCollection` builds the collection through the
navigation's own `IClrCollectionAccessor`, so the CLR collection type is correct **for free** — `List<T>`,
`HashSet<T>`, or a custom collection — and no second place in the codebase learns how to construct an empty
collection. All collection-construction knowledge stays in the removing visitor, where it already lives.

### 3.1 The constraint that rules out fixing it at the injection site

`MongoProjectionBindingRemovingExpressionVisitor.VisitBinary` **hard-casts** the assignment's right-hand side:

```csharp
var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
```

reached for any `Assign` whose left side is a `ParameterExpression` of type `BsonDocument` **or `BsonArray`**.
The injector's `TypeAs` is a `UnaryExpression`, which is why that cast works today — an undocumented contract
between the two visitors.

So the tempting one-line fix at the injection site — `Assign(arrayVariable, Coalesce(TypeAs(…), New(BsonArray)))`
— makes `Right` a `BinaryExpression` and throws `InvalidCastException` for **every collection shaper, in every
query mode**, not just ragged rows. An earlier draft of this design specified exactly that and was wrong; it is
corrected here rather than repeated. A *second* normalizing assignment to the same variable fails the same way
(`New(BsonArray)` is a `NewExpression`, also not a `UnaryExpression`).

**Consequence for anyone editing either visitor:** the injector's collection assignment must keep a
`UnaryExpression` right-hand side, or `VisitBinary`'s cast has to be widened in the same change. Edit 1 keeps it
a pure deletion for exactly this reason.

### 3.2 Why not the remaining alternative

**Keep the conditional and construct an empty collection in its null branch.** Workable —
`collectionShaperExpression.Navigation` is in hand, so `IClrCollectionAccessor.Create()` could be called there —
but it duplicates collection-construction knowledge into a visitor that is purely *structural* today, and
creates a second empty-collection path that has to stay correct against non-`List` collection types forever.
The chosen mechanism has one construction site, the existing one.

### 3.3 The streaming interaction, which is structural and load-bearing

`MongoStreamingEntityMaterializerRewriter.FindCollectionShaper` unwraps
`ConditionalExpression.IfFalse` **first**, then `IfTrue` — that clause exists precisely to see through the null
guard being removed here. After the change the block is `[Assign(arrayVar, Coalesce(...)), collectionShaper]`,
and `FindCollectionShaper`'s `BlockExpression` walk (which iterates expressions **backwards**) finds the shaper
at the last position. So the walk survives.

Behaviourally streaming was never affected: the streaming materializer reads collections off the cursor's own
reader and already yields empty for an absent array. Only the structural walk matters — and the spike verifies
it rather than assuming it.

### 3.4 One conflation, recorded rather than left to be found in review

`TypeAs` yields `null` both when the element is **absent** and when it is **present but not an array**.
Coalescing treats the two alike. Both produce `null` today, so this is not a regression — but it means a
document storing a scalar where an array belongs now materializes as an empty collection instead of `null`,
rather than surfacing as a type error. Noted; not addressed here.

### 3.5 Consequence for EF-357

`Enumerable.Count(null)` becomes `Count(empty)` → 0. EF-357 closes fully: the translation-time
`ArgumentException` was fixed in owned-data slice 7, and this removes the materialization-time
`ArgumentNullException` that kept it partial.

---

## 4. Verification

The entire risk is that this changes results for shapes that work today, so the plan measures the blast radius
before touching anything and pins the change so reverting it goes red.

### 4.1 Spike — enumerate what actually reaches the null-collapse

Measured, not read. For each shape, record behaviour across `Native` / `DriverLinq` / `NativeOnly` × stored
array state {populated, empty, missing, explicit BSON `null`}:

| Shape | Why |
|---|---|
| `Select(b => b.Posts)` | the array projection — the headline changed shape |
| `Select(b => new { b.Title, b.Posts })` | array leaf alongside a scalar |
| `Select(b => b.Posts.Count)` | EF-357's residual |
| `Include(b => b.Posts)` | ~~control — a `$lookup` always writes an array, so predicted unaffected~~ **WRONG, corrected post-implementation: `Blog.Posts` here is the same OWNED/embedded navigation used throughout this doc — no `$lookup` is involved.** That reasoning describes the *reference-collection* row below, not this one. `Include` on an owned collection reaches the identical `CollectionShaperExpression`/`IncludeCollection` computation as a bare projection (§1.1), so it was never a control. Task 1's own spike probe reported `0, 0, 0, 2` for this row (looking unaffected) only because its `Blog` POCO happened to declare `Posts = []` — the exact masking mechanism §1.1 describes, not evidence of a genuine control. |
| nested collection-in-collection, ragged **inner** array | the `CreateGetBsonArray` parent-document path |
| projected reference collection (`Select(c => c.Orders)`) | control — the second `CollectionShaperExpression` construction site; `$lookup`-backed, so predicted unaffected. (This one IS a genuine control — a cross-collection reference navigation is `$lookup`-backed and never reaches the owned-collection `CollectionShaperExpression` site at all, unlike the `Include` row above.) |
| whole-entity | ~~control — must not move~~ **WRONG, corrected post-implementation.** This was the pre-implementation expectation, carried over uncritically from owned-data slice 7's observation. Measured false (EF-358 task-2-report.md's addendum): whole-entity reaches the identical null-collapse computation as a projection, on every path, always. What looked like "must not move" was the same class-field-initializer masking as the `Include` row above (Task 1's own probe class declared `Blog.Posts = []`, hence its `CONTROL whole-entity => 0, 0, 0, 2` reading). The branch owner's ruling accepted that whole-entity's missing/null-array result changes too — "accept the wider normalization," recorded in the EF-358 task-2 report — rather than treating this row's original prediction as a requirement to preserve. |

Two questions the spike answers rather than assumes:

1. Does `Select(b => b.Posts)` under `DriverLinq` reach this site at all, or does the driver render its own
   projection? (Determines whether the `DriverLinq` leg is even in the flip surface.)
2. Does `FindCollectionShaper` still locate the shaper after the conditional is removed? (§3.3.)

GO/NO-GO on the result.

**Correction, made after running the spike (Task 1) — the `Select(b => new { b.Title, b.Posts })` row above does
NOT exercise the null-collapse at all.** It was expected to show the same missing/null flip as the bare array
projection, alongside an unaffected scalar sibling. Measured instead: it throws `ArgumentException` ("Argument
type '…IEnumerable`1[…Post]' does not match the corresponding member type '…List`1[…Post]'") in **every**
`MongoQueryMode` and for **every** row, including the well-formed `two` row — i.e. before any row's array state
is even consulted. This is a pre-existing, structural mismatch between the anonymous-type constructor's
`List<T>`-typed member and the shaper's `IEnumerable<T>`-typed argument, unrelated to EF-358's null/missing
collapse, and this change does not touch it. (The implementation plan already anticipated this possibility —
see `docs/superpowers/plans/2026-07-29-projection-path-null-collection-normalization.md` §Task 2, the paragraph
beginning "If that same test fails because…" — so this is a confirmation of a flagged risk, not a surprise.)
**Consequence: this row is excluded from the flip surface.** See the spike findings doc,
`docs/superpowers/specs/2026-07-29-projection-path-null-collection-normalization-spike-findings.md`, for the
full verbatim measurement and the resulting (narrower-than-predicted) flip surface.

### 4.2 The fix, with teeth

The coalesce, plus a test that goes **red when the coalesce is reverted**. It must assert returned **data**, not
an exception type — an assertion pinning only an exception type usually cannot prove which guard fired, which
made several teeth-checks vacuous in earlier slices in this stack.

Plus **collection-type breadth**: a non-`List` collection navigation (`HashSet<T>` or a custom collection). This
is the single thing most likely to be silently wrong under the rejected alternative in §3.2, and the cheapest
proof that the accessor path is what constructs the empty collection.

### 4.3 Differential oracle

Expected values come from materializing **whole entities** under `Native` and compiling the same selector
client-side — the pattern owned-data slice 7 established for
`Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state`. **Do not** simplify this into a
projection query: the whole-entity leg is precisely what supplies the empty collections, and a projection query
would be asserting the change against itself.

**Caveat added post-implementation, per the §1.1/§4.1 correction:** "the whole-entity leg supplies the empty
collections" was true of slice 7's actual test fixture, but only because that fixture's owned-collection POCO
happened to declare a `= []` field initializer — not because whole-entity materialization was a principled,
initializer-independent oracle. Pre-EF-358, a whole-entity query over a POCO *without* that initializer would
have supplied `null`, not empty, for the same rows (§1.1). The oracle pattern above still works as a technique
— compare against `Native` whole-entity materialization — but it inherited its reliability from happening to
use a defensively-initialized POCO, not from whole-entity being unconditionally correct. Anyone reusing this
pattern for a *new* fixture should not assume the whole-entity leg is safe by construction; it is safe here
specifically because EF-358 now makes it uniformly correct going forward.

Matrix: array state × shape (§4.1) × query mode.

### 4.4 Non-regression proofs, named individually

Not "the suite passed" — each of these is a stated criterion:

- **`PrimitiveCollectionTests` green.** The deliberate nullability-aware null precedent (§2). The single most
  important thing this change must not touch.
- Whole-entity materialization's *result* for a well-formed/empty array unchanged (§1.1: it reaches the same
  site as a projection always did; a missing/explicit-null array's result was accepted to change too — see
  the "accept the wider normalization" ruling recorded in the task-2 report, not "must still not" as this
  bullet originally claimed).
- Collection `Include` unchanged for a well-formed/empty array, by the same correction.
- Tracked and no-tracking paths both.

### 4.5 Expected flips

`NativeOwnedCollectionCountTests.Bare_embedded_collection_Count_projection_still_throws_for_a_missing_or_null_array`
inverts: `Throws<ArgumentNullException>` becomes `0`. Rewritten and renamed, closing EF-357.

Grep for any other test encoding the null rather than assuming that is the only one.

### 4.6 Sweeps

- Three-version `/test-all`, **zero failures on all three**. Record what was run and the outcome; a bare pass
  count in isolation has produced three irreconcilable totals within one branch's life before.
- EF10 spec sweep on **both axes** — `NativeOnly` pass/fail **and** `Native`-mode MQL baselines. The `All` slice
  was caught out by inventorying only the pass set and missed a test that was `NativeOnly`-failing but whose
  `Native` baseline had moved.

---

## 5. Documentation — the four-surface sweep

A correction is not done until it lands on **all four** surfaces. The status doc kept a refuted rationale alive
through nine correction rounds because nobody diffed it, and it is the file read first on resume.

1. **`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`** — the note beginning "A GENERAL property of the
   projection path, not a count detail" becomes **false** and needs rewriting, not patching. Its
   "`Native == DriverLinq` parity fails by design" claim needs care: parity is **restored** for the bare count
   (both answer 0), but the **wrapped** count still aborts under `DriverLinq` via the driver's bare server-side
   `$size` with no `$ifNull` — unchanged by this slice and unrelated to it.
2. **`docs/native-query-status-EF-322.md`** — §4's projection-path paragraph, and §6's EF-357 and EF-358 rows.
3. **Owned-data slice 7's design and plan docs** — prose **and verbatim code blocks**. Amending prose alone has
   already carried a stale claim into a test comment two tasks later; grep both.
4. **Source comments** — `NativeOwnedCollectionCountTests` (several cite the residual), and the
   count-projection notes that describe it.

Cross-references **quote-anchored, not line-numbered**: three precision line references broke within a single
slice last time, one of them twice mid-fix.

---

## 6. Multi-version and API surface

No `#if` expected — the touched type is `internal` and the behaviour is identical across EF8/EF9/EF10. No public
API change, no annotation-key change, no change to persisted document shape (this is a read path only).
