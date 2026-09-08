# EF-449: native translation for a reference-collection-nav reducer (First/FirstOrDefault) projected inline

## Problem

A reference-collection navigation (cross-collection, FK-correlated, not owned/embedded) reduced via
`.First()`/`.FirstOrDefault()` — optionally with a predicate and/or a preceding `OrderBy`/
`OrderByDescending` — then a scalar member access, inside a projection leaf, is currently rejected
in **every** `MongoQueryMode`:

```csharp
var query = from animal in context.Set<Animal>()
            select new { animal.Id, animal.IdentificationMethods.FirstOrDefault().Method };
```

`IdentificationMethods` lives in a separate MongoDB collection (a genuine one-to-many relationship, not
an embedded array). The rejection happens inside the driver-LINQ fallback bridge itself
(`MongoEFToLinqTranslatingExpressionVisitor.cs:575`, `"Unsupported cross-DbSet query between..."`), so
this shape has **no working driver-LINQ oracle today** — the same family as reference `SelectMany`/
`Intersect`/`Except` (see Query `AGENTS.md`'s durable-invariants section). This ticket makes the
recognized shape matrix natively representable; anything outside it keeps failing exactly as today, in
every mode — no new decline/fallback plumbing, no regression risk.

Split off EF-216 (the general "cross-document navigation comparisons don't translate" umbrella), which
this fixes one concrete spec-test case of: `BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default`.

## A key finding from investigation: the join/lookup primitive already exists but is fallback-only

The natural building block for this feature is **not** `NativeSelectManyBinder` (which handles a
top-level, user-authored `.SelectMany()` call — a different tree shape than a nav access buried inside
a projection lambda). It is the `$lookup`(+correlated sub-pipeline)+`$unwind` machinery already built
for reference `Include`/`Join` (`LookupExpression`, `MongoSelectLowerer.AppendLookupStages`,
`MongoPipelineFactory.RenderLookup`).

`LookupExpression` already has a `PipelineStages`/`HasPipeline` mechanism (`LookupExpression.cs:147-155`)
documented as existing for "filtered Includes (e.g., OrderBy, Skip, Take on the included collection)" —
exactly the shape this ticket needs. **However, this mechanism is currently fallback/mixed-visitor-only:**

- `MongoSelectLowerer.AppendLookupStages` (`MongoSelectLowerer.cs:356`) requires `!lookup.HasPipeline`
  for its native reference-lookup branch, and none of its other branches admit `HasPipeline` either — so
  a `HasPipeline` lookup falls through to the final `else` and throws
  `NativeTranslationNotSupportedException` today.
- `MongoPipelineFactory.RenderLookup` (`MongoPipelineFactory.cs:417-424`) renders **only**
  `from`/`localField`/`foreignField`/`as` — it never emits `PipelineStages` as a `pipeline` field at all.
  This is dead code on the native path today, precisely because `AppendLookupStages` never lets a
  `HasPipeline` lookup reach it.
- The only consumers of `HasPipeline`/`PipelineStages` today are
  `MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs:1397` (driver-LINQ fallback) and
  `MongoProjectionBindingExpressionVisitor.Lookup.cs` (the mixed/fallback read visitor) — used for TPH
  discriminator narrowing (`LookupExpression`'s constructor, `LookupExpression.cs:64-73`) and nested
  Include lookups. Both of those remain fallback-only and **out of scope** for this ticket; see the
  scope-boundary note below.

So two small, additive changes to genuinely shared code are needed (not just building on top of
existing native pipeline support), plus a **new, narrow, sealed-off pipeline kind** so we don't
accidentally widen native eligibility for the *existing* `HasPipeline` uses (TPH narrowing, nested
Include) that haven't been vetted for the native/streaming path.

## Scope

**In scope (v1 shape matrix, per design discussion):**
- `nav.FirstOrDefault().Member` — bare.
- `nav.FirstOrDefault(predicate).Member` — predicate becomes a `$match` inside the lookup sub-pipeline.
- `nav.OrderBy(...)/.OrderByDescending(...).FirstOrDefault([predicate]).Member` — one ordering key
  (mirrors the existing single-sort-key scope of `NativeSelectManyBinder`/reducer machinery elsewhere;
  no multi-key `ThenBy` in v1).
- `nav.First(...)` — same as `FirstOrDefault` but throws on empty (`MongoCardinality.EmptyBehavior`,
  reused exactly as `NativeCardinalityBinder.BuildEmptyBehavior` already does for top-level reducers).
- Admitted **standalone or mixed** with other already-native scalar/computed projection leaf siblings
  (`new { animal.Id, animal.IdentificationMethods.FirstOrDefault().Method }`) — since this family has no
  driver-LINQ oracle, there is no late-fallback leg to protect, so none of EF-441/444/447's
  alias-agreement/sibling-readability machinery is needed here.

**Out of scope (declines exactly as today — same exception, no behavior change):**
- Two or more navigation hops (`a.Nav1.Nav2.FirstOrDefault()...`).
- The reduced element being a further navigation/entity (only a **scalar** member read off the reduced
  element is supported — reading a whole entity or nested owned member off it is a separate, larger
  feature).
- `Single`/`SingleOrDefault`/`Last`/`LastOrDefault` reducers in this position.
- `Where(...)` (a filter, not a reducer) or any other LINQ operator between the nav access and the
  reducer, beyond one `OrderBy`/`OrderByDescending`.
- This shape nested inside another correlated construct (owned quantifier, `SelectMany`, another
  instance of this same feature) — single-level correlation only, matching this file's other
  correlation features' stated boundary.
- TPH-derived-type navigations (would need the pre-existing discriminator-narrowing `$match`
  *and* our new `$sort`/`$match(predicate)`/`$limit` stages combined — deferred; declines to the
  existing exception exactly as today).
- Multi-EF-version: no `#if` expected; confirm during implementation.

## Design

### 1. A new, narrow pipeline kind on `LookupExpression`

Add a `LookupPipelineKind` (or a single bool, since there is exactly one native-eligible kind for
now — but per this codebase's own stated invariant ("a node kind that needs different handling at 3+
existing call sites must be a sealed sibling type, not a bool flag"), and because `HasPipeline` already
means three different things across its current call sites, a bool sitting *next to* `HasPipeline`
would be the wrong pattern) — represent it as an explicit discriminator:

```csharp
internal enum LookupPipelineKind { None, FallbackOnly, CorrelatedReducer }
public LookupPipelineKind PipelineKind { get; private init; } = LookupPipelineKind.None;
```

- TPH discriminator narrowing (existing constructor code, `LookupExpression.cs:64-73`) sets
  `FallbackOnly` when it adds its `$match` — preserves today's behavior (still declines native) with an
  explicit, self-documenting reason instead of relying on `HasPipeline`'s ambiguity.
- Nested Include lookups (`MongoProjectionBindingExpressionVisitor.Lookup.cs`) likewise mark
  `FallbackOnly` wherever they currently push onto `PipelineStages`.
- The new recognizer (§2) constructs its `LookupExpression` with `PipelineKind = CorrelatedReducer`.

`HasPipeline` (`PipelineStages.Count > 0`) is unchanged and still gates `IsStreamableReference`/
`IsNativeCollectionLookup` exactly as today — those two remain correctly conservative for *any*
pipeline-carrying lookup, `CorrelatedReducer` included (this new lookup kind is neither a reference
Include nor a collection Include; it goes through its own new lowerer branch, not those two).

### 2. Recognizer: `NativeProjectionBinder.TryGetCorrelatedReducerLeaf`

New arm alongside `TryGetOwnedReferenceNavigationLeaf`/`TryGetDocumentConstructionLeaf`
(`NativeProjectionBinder.cs`, called from `TryTranslateLeaf`, `NativeProjectionBinder.cs:531`).
Recognizes, for a leaf expression rooted at the selector's outer parameter:

```
MemberExpression(
  MethodCallExpression First|FirstOrDefault [+ Expression<Func<TElement,bool>> predicate],
    receiver: MethodCallExpression OrderBy|OrderByDescending [optional],
      receiver: MemberExpression <reference-collection navigation off outer param>)
```

Walks inward from the outer `MemberExpression` (the final scalar member, e.g. `.Method`), matching each
layer optionally, down to the navigation access. Declines (returns `false`, leaf not recognized) unless:
- The navigation resolves via `INavigation` with `IsCollection == true` and is **not** owned/embedded
  (mirrors the existing owned/reference navigation distinction `TryGetOwnedReferenceNavigationLeaf`
  already makes, inverted).
- The navigation's root parameter is the leaf's own scope's outer parameter, checked by
  `ReferenceEquals` (never by name — the codebase's standing invariant).
- The reduced element's final member is a plain scalar property (no further nav/collection) — same
  minimal-widening discipline `TryGetDocumentConstructionLeaf` uses for its own members.
- Target entity type is not TPH-derived (`FindDiscriminatorProperty()` present and
  `TargetEntityType != GetRootType()`) — declines to keep this out of scope (see above).
- Nothing about the surrounding query already declined for another reason (mirrors the "nothing may
  have already declined" join-registration guard, `MongoJoinScope` family, so this recognizer doesn't
  fight the join-registration/set-op post-terminal-gating invariants already documented for other
  lookup-registering features).

On a match, builds:
- A `LookupExpression` (via a shared FK-correlation-isolation helper — extracted from the existing
  reference-Include/Join lookup-building code in `Visitors/MongoProjectionBindingExpressionVisitor.Lookup.cs`,
  **not** `NativeSelectManyBinder`, correcting the initial framing from the design discussion) with
  `PipelineKind = CorrelatedReducer`.
- If an `OrderBy`/`OrderByDescending` was present: appends a `$sort` `BsonDocument` to `PipelineStages`
  (translated via the ordinary single-scope `MongoExpressionTranslator` over the element type — the sort
  key must be a plain member access, matching the existing reducer sort-key scope elsewhere in this
  file).
- If a predicate was present: appends a `$match` `BsonDocument` (translated via
  `MongoExpressionTranslator`/`MongoQueryLanguageRenderer` over the element type, same as any other
  reference-collection element predicate elsewhere in this file).
- Always appends `{$limit: 1}`.
- Registers the lookup on `mongoQ.Lookups` (same registration path other lookup-producing features use)
  and records `MongoCardinality.EmptyBehavior` for `First` vs `FirstOrDefault`
  (`NativeCardinalityBinder.BuildEmptyBehavior`, reused as-is) against **this leaf**, not the whole
  select — cardinality-per-leaf is new; existing `MongoSelectDefinition.Cardinality` is a whole-select
  concept for a top-level reducer, so this needs its own, leaf-scoped carrier (see §5).
- Returns a `MongoElementRefExpression` over `"<lookup.As>.<Member>"` as the leaf's translated value —
  the same read mechanism a reference Include's projected member already uses once unwound.

### 3. Lowerer: new branch in `AppendLookupStages`

`MongoSelectLowerer.AppendLookupStages` (`MongoSelectLowerer.cs:342-404`) gains a new branch, ordered
before the final `else`:

```csharp
else if (lookup.PipelineKind == LookupPipelineKind.CorrelatedReducer)
{
    stages.Add(new MongoLookupStage(lookup));
    stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: true));
}
```

Always left-outer (`preserveNullAndEmptyArrays: true`) regardless of `First` vs `FirstOrDefault` — the
empty-vs-throw distinction is a **read-side** concern (§5), not a join-shape concern; the `$lookup`'s
sub-pipeline already narrowed to 0-or-1 matched documents via its own `$limit: 1`, so `$unwind` here is
just "flatten the 0-or-1-element array to null-or-object," identical to a left-outer reference Include's
unwind.

### 4. `MongoPipelineFactory.RenderLookup`: emit `pipeline` when present

```csharp
private static BsonDocument RenderLookup(LookupExpression lookup)
{
    var doc = new BsonDocument
    {
        { "from", lookup.From },
        { "localField", lookup.LocalField },
        { "foreignField", lookup.ForeignField },
    };
    if (lookup.HasPipeline)
    {
        doc.Add("pipeline", new BsonArray(lookup.PipelineStages));
    }
    doc.Add("as", lookup.As);
    return new BsonDocument("$lookup", doc);
}
```

MongoDB's `$lookup` supports `localField`/`foreignField` combined with an additional `pipeline` (the
pipeline runs over the already-equi-joined subset) — this is the standard, supported combination, not a
workaround. Because `AppendLookupStages` still throws for every *other* `HasPipeline` lookup
(TPH-narrowed, nested Include — both now explicitly `PipelineKind.FallbackOnly`, per §1), this change is
inert for every existing case and only activates for the new `CorrelatedReducer` kind.

### 5. Leaf-scoped cardinality / empty behavior

Unlike a top-level reducer (`MongoSelectDefinition.Cardinality`, one per select), this reducer lives
*inside* a projection leaf — the select can have an ordinary whole-select `Route` (e.g. `Projection`)
independent of this leaf's own empty-behavior. Add a small leaf-scoped carrier:

```csharp
internal sealed record MongoCorrelatedReducerLeaf(LookupExpression Lookup, string Member, MongoEmptyAggregateBehavior EmptyBehavior);
```

held in a new `List<MongoCorrelatedReducerLeaf>` on `MongoSelectDefinition` (populated by §2's recognizer,
alongside the existing `Lookups` registration), keyed positionally to the projection alias it belongs to
— mirroring how `Projection` itself is an ordered alias→field-ref list. `EmptyBehavior` is `First` (throw
if the unwound field is absent) vs `FirstOrDefault` (null is already the correct read for
`preserveNullAndEmptyArrays: true`, so no extra work needed there). The **read side**
(`MongoProjectionBindingRemovingExpressionVisitor`) consults this list when reading a leaf whose alias
matches an entry: reading `"<lookup.As>.<Member>"` off a document where `<lookup.As>` is absent
(unwound-to-null case) throws `InvalidOperationException` (matching `Enumerable.First`'s contract)
instead of silently returning a default, for the `First` (non-`OrDefault`) variant only.

## Boundaries / invariants this design must preserve

- **`AppendLookupStages`'s existing branches must not widen.** The new branch is additive and keyed on
  `PipelineKind.CorrelatedReducer` specifically — not on `HasPipeline` generally — so TPH-narrowed and
  nested-Include lookups continue to decline to fallback exactly as today. This is the direct
  application of the codebase's "a gate wider than what the rewrite handles is a silent-wrong-data trap"
  invariant: `RenderLookup`'s new `pipeline` emission and `AppendLookupStages`'s new branch must be
  reached **only** by lookups this feature itself constructs.
- **Scope by identity, never by name**, for resolving the navigation's root parameter (§2) — same
  standing invariant as every other correlation feature in this file.
- **No driver-LINQ oracle** — an out-of-scope variant must return `null` (EF Core's own
  translation-failure path), not call `MarkNotNativelyRepresentable()`, per the existing
  `SelectMany`/reference-collection/`Intersect`/`Except` invariant this shape joins.
- **`$limit: 1` inside a `$lookup` sub-pipeline is per-outer-document**, not a global limit — verify this
  during implementation with a fixture where two different outer documents each get their own correctly
  independent "first" element (this is standard, well-understood `$lookup`+pipeline behavior, but the
  differential-correctness test below should still assert it directly rather than assume it).

## Testing

- New `NativeCorrelatedReducerProjectionTests` (unit, `NativeTranslation/`) covering the recognizer's
  accept/decline matrix: bare, +predicate, +order, +sibling scalar leaf, two-hop decline, non-scalar
  reduced-member decline, TPH-derived-target decline, nested-inside-another-correlated-shape decline.
- Functional differential-correctness `[Theory]` (per this file's established pattern for
  result-changing native shapes) against an in-memory LINQ oracle evaluated over the same `Expression`,
  across a fixture with: multiple candidate related rows (order matters), zero related rows (`First`
  throws, `FirstOrDefault` returns default), a predicate matching zero/one/many rows, and two outer
  documents to confirm per-document correlation (not a global first).
- `NativeOnly`-mode assertion that the recognized shapes succeed (proving genuinely native — MQL shape
  alone can't prove this, per this file's own stated pitfall).
- Flip `BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default` from
  `AssertTranslationFailed` to a real pass on at least EF9/EF10; check EF8 separately — it may need the
  same `#if EF9`/gate treatment its sibling `Can_read_back_bool_mapped_as_int_through_navigation` test
  has, or it may just work identically (confirm empirically, don't assume symmetry).
- Regression: confirm TPH-narrowed collection Include and nested Include lookups are byte-for-byte
  unaffected (still fallback, same MQL) — these are the two existing `HasPipeline` consumers this design
  must not disturb.

## As-built deviations

This document is committed as its PRE-implementation self. The following points differ in what actually
shipped; the code and its own remarks are authoritative where they disagree with the sections above.

- **§2's recognizer shape is wrong.** It assumed a trailing `MemberExpression` to peel off the reducer call.
  The REAL nav-expanded tree has NO navigation name surviving anywhere and no trailing member access at all —
  a third shape, matching neither of the two the preflight ruling considered. EF's nav-expansion erases the
  navigation member access, hoists the reduced member into a mandatory inner `Select`, and rewrites a reducer
  predicate into its own `Where` layer. The shipped recognizer matches that observed shape; see
  `NativeProjectionBinder.TryGetCorrelatedReducerLeaf`'s remarks for the measured tree.
- **§5's leaf record shipped as `MongoCorrelatedReducerLeaf(Alias, Lookup, Member, ThrowOnEmpty)`**, not
  `(Lookup, Member, EmptyBehavior)`. The alias is carried explicitly (the read side resolves the leaf by its
  `$project` alias, not positionally), and the empty behaviour collapsed to a single `ThrowOnEmpty` bool
  rather than reusing `MongoEmptyAggregateBehavior`. The list also lives on `MongoQueryExpression`, not on
  `MongoSelectDefinition`.
- **Task 4's `ResolveCollectionNavigation` reshape was never called by this feature** (Ruling 1). The
  navigation is resolved from the FK-correlation predicate via `NativeCorrelationMatcher
  .TryMatchCorrelatedCollection` instead, because the actual observed shape carries no navigation name for a
  name-based lookup to work from.
- **The nullable-widening normalization is absent from this spec entirely.** It was discovered and added
  later, and it is what makes the feature's own motivating spec test
  (`BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default`) actually pass: for
  a `FirstOrDefault()` reduced to a non-nullable value-type member, EF widens the member to `Nullable<T>`
  inside the inner `Select` and narrows the reducer result back to `T`, so the leaf arrives as a
  `UnaryExpression`. Both `Convert`s are peeled as one normalization, and both halves must fire together — a
  user-written narrowing cast over an already-nullable member has the identical outer shape and must decline.
  The read side needed real new work for it too (`IsDefaultOnEmptyCorrelatedReducerLeaf`).
- **§79's "out of scope" bullet is wrong about `Where(...)`.** A `Where(predicate)` layer between the nav
  access and the reducer IS admitted in the shipped code — constant-valued predicates only (a parameterized
  one declines, since `PipelineStages` has no placeholder-substitution mechanism). This is not a scope
  widening so much as a consequence of the corrected shape above: nav-expansion hoists
  `FirstOrDefault(predicate)` — which §62 does list as in scope — into exactly such a `Where` layer, and the
  recognizer accepts that layer regardless of whether the user wrote it or EF synthesized it.
- **Two gates the spec does not mention were added at final review:** the SORT KEY is gated on
  `NativeGroupByBinder.HasDefaultKeySerialization` (the same conjunct the reduced member carries — `$sort`
  orders by the STORED representation, so a value-converted / non-default-represented key could pick a
  different row), and the TPH-derived-target exclusion is enforced structurally as well as by metadata (a
  fail-closed check that `LookupExpression`'s constructor left `PipelineStages` empty, since §1's
  `private init` did not survive — `PipelineKind` ships as `internal set`, and an object initializer runs
  after the constructor).
- **§4's `RenderLookup` change is NOT inert for every existing case, contrary to the last sentence of that
  section.** `AppendLookupStages`'s reference-collection-`SelectMany`-flatten branch does not exclude
  `HasPipeline`, so a TPH-derived-target reference-collection `SelectMany` previously dropped its
  discriminator `$match` and now correctly emits it — a latent-bug fix, not a regression.
