# Native translation for a nested anonymous/DTO projection member sourced from a join scope

## Problem

`Include_with_complex_projection` (EF Core's Northwind specification suite) currently falls back to
driver-LINQ in every `MongoQueryMode`:

```csharp
from o in ss.Set<Order>().Include(o => o.Customer)
select new { CustomerId = new { Id = o.Customer!.CustomerID } }
```

Confirmed empirically with `MONGODB_EF_NATIVE_ONLY=1`: it throws
`NativeTranslationNotSupportedException: Query projects a non-entity result` — `MongoSelectDefinition
.Select.Route` never reaches `NativeRoute.Projection`, so no native binder accepted this shape.

`o.Customer` is a cross-collection (FK-correlated, non-owned) reference navigation, so EF's
nav-expansion rewrites the query into a `Join`/`LeftJoin` before the selector is bound. That routes
selector binding through `NativeJoinScopeProjectionBinder.TryBindProjection`
(`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeJoinScopeProjectionBinder.cs:143`), not
the plain `NativeProjectionBinder`. That binder's per-member loop (`TryBindProjection`, the `foreach`
starting at line 182) accepts exactly two leaf shapes for each **top-level** selector member: a
whole-entity Outer/Inner scope reference (`MongoTransparentScopeResolver.TryResolveScopeDepth`), or a
scalar/computed value via `NativeJoinScopeTranslator.TryTranslateValue`. Neither matches here: the member
`CustomerId`'s value is itself a **nested `NewExpression`** (`new { Id = o.Customer!.CustomerID }`), and
the loop never recurses into a nested wrapped body — it declines the whole projection, which is why
`Select.Route` stays `Fallback`.

`NorthwindIncludeQueryMongoTest.Include_with_complex_projection`
(`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs:379`) is
the motivating spec test; it currently carries `// Failed: Throws ExpressionNotSupportedException (query
not translated)` — stale wording (it throws under `NativeOnly`, and silently falls back under
`Native`/`DriverLinq`) but the right test.

## A key finding: the building block already exists, just not for a join-scope-sourced value

There is already a narrow precedent for exactly this kind of nesting:
`NativeProjectionBinder.TryGetDocumentConstructionLeaf` (EF-447,
`NativeProjectionBinder.cs:524`) recognizes a nested `new {...}`/`MemberInit` leaf whose own members are
**plain top-level scalar fields off the query root**, and turns it into a
`MongoDocumentConstructionExpression` (`Query/Expressions/MongoDocumentConstructionExpression.cs`) — a
literal nested sub-document the `$project` stage emits (`{Book: {Id: "$Id", ...}}`). Both the render side
(`MongoAggregationExpressionRenderer.cs:99-104`) and the ordinary (non-mixed) read side
(`MongoProjectionBindingRemovingExpressionVisitor.BuildDocumentConstructionExpression`/
`ReadDocumentConstructionMember`, `MongoProjectionBindingRemovingExpressionVisitor.cs:873-938`) are
**already fully generic** — they never assume the member's source field lives on the query root:

- The renderer (`MongoAggregationExpressionRenderer.cs:99-104`) just recurses: `Render(m.Value, ...)` for
  each member, for **any** `MongoExpression` the renderer already supports — including a `MongoFieldExpression`
  with a **dotted** `ElementName`, which renders as `"$_lookup_Customer.CustomerID"` exactly like any
  other computed leaf value.
- The ordinary (native) read side's base `ReadDocumentConstructionMember`
  (`MongoProjectionBindingRemovingExpressionVisitor.cs:935-938`) reads
  `BsonBinding.CreateGetPropertyValueAtPath(DocParameter, [alias, memberName], field.Property, memberType)`
  — it reads by **`[alias, memberName]`**, the leaf's own `$project` output path, and uses `field.Property`
  only for value-conversion/type-mapping. It never inspects `field.ElementName` at all, so it is already
  correct for a member whose *source* was a dotted, join-derived field — the dotted-ness only matters at
  `$project`-render time, not at read time.

`TryGetDocumentConstructionLeaf` itself is deliberately narrow (only `MemberExpression`/`EF.Property`
leaves via `TryTranslateField`, and it explicitly declines a dotted field — see its own remarks at
`NativeProjectionBinder.cs:509-515`) because it is scoped to the **plain-root** case, where a dotted path
means "owned single-reference hop," which has its own non-default-serialization hazard and needs the
late-fallback read to work off a whole ROOT document unmodified. That reasoning does not apply to a
join-scope-sourced value: there the "late fallback" is a **different** documented mechanism (see below),
and the dotted path is the *expected*, normal shape for an Inner-side field, not a hazard signal.

**Confirmed empirically (static analysis of `NativeJoinScopeTranslator.TryTranslateCore`,
`NativeJoinScopeTranslator.cs:197-271`):** a scalar Inner-side leaf (`x.Inner.Foo`) is translated via a
two-scope `MongoExpressionTranslator` constructed with a non-null `innerPrefix`
(`scope.Levels[0].InnerPrefix`), and produces an ordinary **`MongoFieldExpression`** whose `ElementName`
is dotted (`"<InnerPrefix>.<Foo's element name>"`), not some other node kind. This is the exact
already-shipped shape a plain (non-nested) join-scope leaf already stages today (the ordinary-leaf arm,
`NativeJoinScopeProjectionBinder.cs:293-303`, stages this `MongoFieldExpression` directly as a top-level
projection value with no further gate) — so reusing `MongoDocumentConstructionExpression`'s existing
`(MongoFieldExpression)value` cast (`MongoProjectionBindingRemovingExpressionVisitor.cs:913`) is safe: the
node kind doesn't change, only its `ElementName`'s dottedness does.

> **CORRECTION (final review, Critical 1).** That last sentence is true **only for the Inner-sourced leaf it
> analyses**, and the arm as first implemented was not confined to it — it accepted *anything*
> `TryTranslateValue` returned for a nested member. Two other node kinds really do arise and really do reach
> that cast: a `MongoBinaryExpression` for a **computed** member (`Combo = o.OrderNo + o.Customer!.Rank`), and
> a `MongoOuterFieldExpression` (a sealed **sibling** of `MongoFieldExpression`, not a subtype) for an
> **Outer-sourced** member. Both produced `InvalidCastException` at query-*compile* time in the **default**
> `Native` mode, outside any `TryBuildNativeFactory` decline path — a hard crash for a query that works under
> `MongoQueryMode.DriverLinq`. The arm now gates on `nestedLeaf is MongoFieldExpression` and declines
> otherwise, so the accept set is exactly the Inner-sourced plain field members this section actually analysed.

**The one real gap** is the fallback/mixed leg.
`MongoShapedQueryCompilingExpressionVisitor.HasDocumentConstructionProjectionLeaf`
(`MongoShapedQueryCompilingExpressionVisitor.cs:589-590`) already triggers
`MongoMixedProjectionBindingRemovingExpressionVisitor` on a mid-compile `TryBuildNativeFactory` decline —
no change needed there. But that visitor's own `ReadDocumentConstructionMember` override
(`MongoMixedProjectionBindingRemovingExpressionVisitor.cs:330-341`) unconditionally reads `field.Property`
off the **outer** (root/un-projected) document:

```csharp
protected override Expression ReadDocumentConstructionMember(
    MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
    Type memberType)
{
    var docExpr = (Expression)_docParameter;
    if (_queryExpression.UsesDriverJoinFields)
    {
        docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
    }

    return CreateGetValueExpression(docExpr, field.Property, memberType);
}
```

For a member sourced from the join's Inner side, `field.Property` belongs to the **Customer** entity type
(e.g. `CustomerID`), but `docExpr` is the **Order** document. Reading a Customer property off an Order
document is wrong — either it resolves to an unrelated field of the same element name, or (more likely
for a differently-named key) throws or reads nothing. This must be fixed for the mixed leg to produce
correct data on a mid-compile decline.

**The fix reuses an existing helper, not a new mechanism.** `field.ElementName` for an Inner-side leaf is
already the correct dotted path relative to the OUTER document (e.g. `"_lookup_Customer.CustomerID"`).
`BsonBinding.CreateGetPropertyValueAtPath` (`Storage/BsonBinding.cs:255-263`) already walks an arbitrary
multi-segment path and — notably — already treats an **absent intermediate segment** (the ordinary shape
of an unmatched left-outer join row) as a structurally different, correctly-handled case from "the leaf
itself is missing" (see its own remarks and `GetPropertyValueAtPath`'s intermediate-segment branch,
`BsonBinding.cs:269-280`). This is exactly the semantics needed for a left-outer reference Include's
unmatched row. So: split `field.ElementName` on `.` and use `CreateGetPropertyValueAtPath(docExpr,
segments, field.Property, memberType)` whenever the ElementName is dotted; keep today's single-property
`CreateGetValueExpression` read for the undotted (EF-447 root-relative) case, unchanged.

## Scope

**In scope (v1):**

- A single level of nesting: `new { A = new { B = <leaf>, C = <leaf> } }` — the OUTER member's value is a
  wrapped `NewExpression`/`MemberInitExpression`, and each of ITS members is a plain scalar/computed leaf
  translatable via `NativeJoinScopeTranslator.TryTranslateValue`.
- Depth-1 join scope only (`scope.Levels.Count == 1`) — matches `NativeJoinScopeTranslator
  .TryTranslateValue`'s own existing depth-1-only restriction and the ordinary-leaf arm's existing guard
  (`NativeJoinScopeProjectionBinder.cs:288-291`).
- The nested leaf's members may reference the Outer side, the Inner side, or a computed combination of
  both — anything `NativeJoinScopeTranslator.TryTranslateValue` already accepts for a top-level leaf.
- Standalone (the whole outer body is just the one nested member) or mixed with sibling top-level
  **scalar/computed** members (`new { OrderId = o.OrderID, CustomerId = new { Id = o.Customer!.CustomerID } }`).
- The mixed (fallback) read leg, fixed to resolve a dotted `MongoDocumentConstructionExpression` member
  correctly (see Design §3).

**Out of scope (declines the whole outer leaf, falls back exactly as today, no behavior change):**

- Double nesting (`new { A = new { B = new { C = ... } } }`) — declines outright; not needed by the
  motivating test.
- A nested member that is itself a whole-entity Outer/Inner scope leaf, an array/collection leaf, or
  another nested wrapped object — declines the WHOLE outer leaf (no partial commit), matching
  `TryGetDocumentConstructionLeaf`'s existing "a computed/nested/navigation member declines the whole
  leaf" discipline.
- A chained join scope (`scope.Levels.Count > 1`) — declines, matching the existing ordinary-leaf arm.
- Mixing a nested-document leaf alongside a **whole-entity** sibling leaf (`new { o, CustomerId = new {
  Id = o.Customer!.CustomerID } }`). The existing whole-entity-forces-sibling-readability check
  (`NativeJoinScopeProjectionBinder.cs:332-336`) only allow-lists `MongoElementRefExpression`/
  `MongoFieldExpression`/`MongoOuterFieldExpression` as document-readable siblings.
  `MongoDocumentConstructionExpression` is deliberately **not** added to that allow-list in this ticket —
  even though Design §3 makes the mixed leg *capable* of reading such a leaf correctly, verifying the
  interaction between the whole-entity strip-and-reshape path and a nested join-scope leaf is unnecessary
  work the motivating test doesn't need. Left as a documented follow-up, not a silent gap: the nested-leaf
  recognizer must decline (not partially stage) whenever a whole-entity sibling is already staged.
- Multi-EF-version `#if` gating. **CORRECTED AFTER IMPLEMENTATION — the original expectation ("no `#if` is
  expected; nothing here differs across EF8/EF9/EF10's join scope machinery") was wrong.** The `src/` change
  itself is genuinely version-neutral, but the *reachability* of the whole binder is not: on **EF8/EF9 this
  path is never reached at all** for the motivating shape, and the feature is EF10-only in practice. An
  OPTIONAL reference navigation — what a reference `Include` produces — is lowered by EF's nav-expansion onto
  EF's own **internal `LeftJoin` shim**, and `NativeSlotPopulator.PopulateNativeSlots`' candidate-join arm
  matches only `QueryableMethods.{Join,GroupJoin}` plus, under `#if !EF8 && !EF9`, `QueryableMethods.LeftJoin`
  — which does not exist before EF10. The shim therefore falls through to that method's catch-all and calls
  `MarkNotNativelyRepresentable()` *before any Select-side binder runs*, so
  `NativeJoinScopeProjectionBinder.TryBindProjection` is never invoked (measured: `HasUnsupportedOperator` is
  already `true` when `TranslateSelect`'s wrapped arm consults `IsSingleEligibleNativeJoinScope` on EF9,
  `false` on EF10). This is **family-wide, not nesting-specific**: on EF8/EF9 *no* wrapped projection over an
  optional-reference join goes native, including the flat `new { o.OrderNo, o.Customer.Name }` shape that long
  predates this ticket. A **required** reference navigation lowers to `QueryableMethods.Join` instead and does
  go native on all three EF versions, this arm included. Consequences that actually landed: an
  `#if EF8 || EF9` split of the `NativeOnly` expectation in the functional test, and an `#if EF8 || EF9` MQL
  baseline split across **4 spec test files** (`NorthwindInclude*`/`NorthwindEFPropertyInclude*`/
  `NorthwindStringInclude*`/`NorthwindSelect*`).

## Design

### 1. Recognizer: extend `NativeJoinScopeProjectionBinder.TryBindProjection`'s ordinary-leaf arm

In the per-member loop (`NativeJoinScopeProjectionBinder.cs:182-304`), after the existing whole-entity
scope-depth check declines (i.e., `MongoTransparentScopeResolver.TryResolveScopeDepth` returns `false`
for `leafBody`) and before the existing "ORDINARY (scalar/computed) leaf" arm's `TryTranslateValue` call,
insert a new arm:

```csharp
// A NESTED wrapped leaf (`CustomerId = new { Id = o.Customer!.CustomerID }`) — one level of nesting only.
// Declines the WHOLE outer leaf (not just this member) on any inner shape this doesn't recognize, exactly
// as NativeProjectionBinder.TryGetDocumentConstructionLeaf does for its own (plain-root) nested leaves.
if (scope.Levels.Count == 1
    && leafBody.TryGetProjectionMembers(out var nestedMembers))
{
    var translatedNestedMembers = new List<(string, MongoExpression)>();
    var seenNestedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var declined = false;

    foreach (var (nestedMemberName, nestedValue) in nestedMembers)
    {
        // A doubly-nested member, a whole-entity scope leaf, or anything else TryTranslateValue can't
        // translate declines the WHOLE outer leaf — no partial commit.
        //
        // CORRECTED AFTER IMPLEMENTATION (final review, Critical 1): the `nestedLeaf is not
        // MongoFieldExpression` conjunct below was MISSING from this design and had to be added. The
        // shared read side (MongoProjectionBindingRemovingExpressionVisitor
        // .ReadDocumentConstructionMemberTyped) hard-casts each staged member value to
        // MongoFieldExpression, so accepting "anything TryTranslateValue returns" crashed with
        // InvalidCastException at query-COMPILE time, in the DEFAULT Native mode, for two shapes this
        // design believed were in scope: a COMPUTED member (-> MongoBinaryExpression) and an
        // OUTER-sourced member (-> MongoOuterFieldExpression, a sealed SIBLING of MongoFieldExpression).
        // Both now decline. The real accept set of this arm is therefore narrower than this design
        // assumed: INNER-sourced plain field members only. A value-converted member never reaches here
        // either — the join-scope value translator already declines those upstream, for a flat leaf as
        // well as a nested one.
        if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, nestedValue, out var nestedLeaf)
            || nestedLeaf is not MongoFieldExpression
            || !seenNestedMembers.Add(nestedMemberName))
        {
            declined = true;
            break;
        }

        translatedNestedMembers.Add((nestedMemberName, nestedLeaf));
    }

    if (!declined)
    {
        if (!seenAliases.Add(alias))
        {
            return false;
        }

        staged.Add(new MongoProjection(
            alias, new MongoDocumentConstructionExpression(leafBody, translatedNestedMembers)));
        continue;
    }

    return false; // matches the file's existing "one untranslatable leaf declines the whole projection" rule
}
```

Placement: this arm must run **before** the existing "ORDINARY (scalar/computed) leaf" arm
(`NativeJoinScopeProjectionBinder.cs:274-304`), because that arm's own `TryTranslateValue(scope, rootParam,
leafBody, ...)` call would otherwise be tried first against the nested `NewExpression` and simply fail
(harmless, since it already returns `false` → decline — but the new arm must own the recognition, not
share it), so ordering is for clarity/ownership, not correctness. It must run **after** the whole-entity
scope-depth check, since that check is unconditional and tried first for every member today, and a nested
`NewExpression` can never match it anyway (`TryResolveScopeDepth` inspects a member-access hop chain, not
a `New`/`MemberInit` node) — no interaction, but preserve the existing check order.

`TryGetProjectionMembers` is the same extension method `TryGetDocumentConstructionLeaf` and the top-level
`TryBindProjection` itself already use — no new expression-shape-parsing code is needed for the "is this a
wrapped body" question, only for what to do with each extracted member.

### 2. No renderer or native-read-side change needed

Confirmed in the Problem section: `MongoAggregationExpressionRenderer`'s existing
`MongoDocumentConstructionExpression` case and `MongoProjectionBindingRemovingExpressionVisitor`'s
existing `BuildDocumentConstructionExpression`/base `ReadDocumentConstructionMember` are both already
generic over the member `Value`'s node shape (`MongoFieldExpression`, dotted or not) — they were built for
EF-447 without hard-coding "root-relative," they merely never received a dotted value from EF-447's own
narrower recognizer.

`MongoShapedQueryCompilingExpressionVisitor.HasDocumentConstructionProjectionLeaf`
(`MongoShapedQueryCompilingExpressionVisitor.cs:589-590`, `p.Expression is MongoDocumentConstructionExpression`)
also needs no change: it already matches this new leaf by node type alone, so the existing mid-compile
`TryBuildNativeFactory`-decline path already routes to the mixed visitor for this new leaf shape, for
free.

### 3. Fix `MongoMixedProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember` for a dotted source field

`MongoMixedProjectionBindingRemovingExpressionVisitor.cs:330-341`, current body:

```csharp
protected override Expression ReadDocumentConstructionMember(
    MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
    Type memberType)
{
    var docExpr = (Expression)_docParameter;
    if (_queryExpression.UsesDriverJoinFields)
    {
        docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
    }

    return CreateGetValueExpression(docExpr, field.Property, memberType);
}
```

New body — branch on whether `field.ElementName` is dotted (a join-scope-sourced member) vs. plain (the
original EF-447 root-relative member), using the existing multi-segment path reader instead of the
single-property reader for the dotted case:

```csharp
protected override Expression ReadDocumentConstructionMember(
    MongoDocumentConstructionExpression construction, string alias, string memberName, MongoFieldExpression field,
    Type memberType)
{
    var docExpr = (Expression)_docParameter;
    if (_queryExpression.UsesDriverJoinFields)
    {
        docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
    }

    // A join-scope-sourced member (native-join-scope-nested-projection ticket): field.ElementName is a
    // dotted path relative to docExpr (e.g. "_lookup_Customer.CustomerID"), not a root property of the
    // OUTER entity — CreateGetValueExpression(docExpr, field.Property, memberType) would incorrectly look
    // up field.Property's OWN element name directly on docExpr. Walk the dotted path instead, via the same
    // helper the ordinary (native) read side uses for its own [alias, memberName] path
    // (MongoProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember) — it already treats
    // an absent INTERMEDIATE segment (an unmatched left-outer join row) as null rather than a missing-leaf
    // error, which is exactly the semantics an Inner-side member needs here.
    if (field.ElementName.Contains('.'))
    {
        return BsonBinding.CreateGetPropertyValueAtPath(docExpr, field.ElementName.Split('.'), field.Property, memberType);
    }

    return CreateGetValueExpression(docExpr, field.Property, memberType);
}
```

This is additive: the undotted branch is byte-for-byte the pre-existing behavior, so EF-447's own
motivating shape (`new { Book = new Book { Id = e.Id, ... } }`, root-relative, undotted) is unaffected.

## Boundaries / invariants this design must preserve

- **`TryGetDocumentConstructionLeaf`'s own dotted-field decline (`NativeProjectionBinder.cs:541`,
  `!field.ElementName.Contains('.')`) must NOT be loosened.** That decline exists for a different reason
  (owned single-reference hop, non-default-serialization hazard against a whole-ROOT-document fallback
  read) and stays exactly as strict as today. This ticket adds a **second, separate** recognizer
  (`NativeJoinScopeProjectionBinder`'s new arm) that constructs the *same* `MongoDocumentConstructionExpression`
  node type through a different, join-scope-aware path — it does not touch or relax
  `TryGetDocumentConstructionLeaf` itself.
- **One untranslatable nested member declines the WHOLE outer leaf, never a partial commit** — matches
  this file's own stated invariant for both the whole-entity scope arm and the ordinary scalar arm just
  below it (`NativeJoinScopeProjectionBinder.cs:295`, "one untranslatable leaf declines the whole
  projection").
- **`seenAliases`/case-insensitive alias collision handling is reused unchanged** — the new arm calls the
  existing `seenAliases.Add(alias)` exactly where the other two arms do, so a user member colliding with
  an internal alias (e.g. the `InnerPrefix`-named seed) still declines correctly.
- **`ConfirmEntireChain` / lookup registration is unaffected** — the new arm stages into the same `staged`
  list and falls through to the same commit block (`NativeJoinScopeProjectionBinder.cs:338-344`); no new
  lookup is created (the nested leaf's fields reuse the join's own existing `joinInfo.Lookup`).
- **Scope by parameter IDENTITY, never by name** — the new arm's `NativeJoinScopeTranslator.TryTranslateValue`
  call reuses the exact same `scope`/`rootParam` the ordinary-leaf arm already uses; no new identity
  resolution is introduced.
- **The EF-447 undotted read path must remain byte-for-byte unchanged** (Design §3) — verify with a
  regression test, not just by inspection.

## Testing

- **Unit — recognizer accept/decline matrix** (new test class, `tests/MongoDB.EntityFrameworkCore.UnitTests
  /Query/NativeTranslation/NativeJoinScopeNestedProjectionTests.cs`):
  - Accepts: `new { CustomerId = new { Id = <Inner-side member> } }` (the motivating shape).
  - Accepts: a nested member sourced from the Outer side, and one combining Outer+Inner (a computed
    expression) inside the nested body.
  - Accepts: standalone nested leaf, and nested leaf alongside a sibling ordinary scalar leaf.
  - Declines (whole leaf): double nesting.
  - Declines (whole leaf): a nested member that is itself a whole-entity scope leaf.
  - Declines (whole leaf): a nested member that is an array/collection leaf.
  - Declines (whole leaf): `scope.Levels.Count > 1` (chained join).
  - Declines: nested leaf alongside a whole-entity sibling leaf (confirms the out-of-scope boundary holds,
    not just "happens to not be tested").
  - Declines: two nested members with case-insensitively colliding names.
- **Unit — mixed-visitor dotted read** (extend or add alongside existing
  `MongoMixedProjectionBindingRemovingExpressionVisitor` coverage, or a focused new test if none exists
  yet exercising `ReadDocumentConstructionMember`): construct a `MongoDocumentConstructionExpression` with
  a dotted-`ElementName` member and assert the generated read expression walks the path via
  `CreateGetPropertyValueAtPath` rather than `CreateGetValueExpression(docExpr, field.Property, ...)`, and
  that the pre-existing undotted case still takes the old path (regression guard for EF-447).
- **`NativeOnly`-mode assertion** that `Include_with_complex_projection`'s shape (and the sibling shapes
  from the recognizer matrix, expressed as ad hoc queries against the unit-test/functional model) succeed
  — MQL shape alone cannot prove native, per `Query/AGENTS.md`'s stated pitfall.
- **Flip the spec test**: `NorthwindIncludeQueryMongoTest.Include_with_complex_projection`
  (`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeQueryMongoTest.cs:379`) —
  remove the `// Failed:` comment, run with `EF_TEST_REWRITE_BASELINES=1` scoped to this test to
  regenerate its MQL baseline (the pipeline shape may differ from today's fallback pipeline — a
  `$project` for the nested document is expected to appear where the fallback's `$map`/`$unwind`
  reshaping currently does), then rebuild and re-run without the var to confirm it's genuinely green.
  Check EF8/EF9/EF10 individually via `/test-all`. **As implemented this DID need an `#if EF8 || EF9`
  baseline split** (EF8/EF9 keep the fallback pipeline, EF10 gets the native `$project`) — see the corrected
  "Multi-EF-version `#if` gating" bullet in Scope above for the mechanism.
- **Differential correctness**: a `[Theory]` functional test (real DB) asserting the native result equals
  an in-memory LINQ oracle over the same expression, across a fixture with a matched customer, and — since
  this is a reference Include — an **unmatched** FK (dangling `CustomerID` with no matching `Customer`
  row) to exercise the left-outer/absent-intermediate-segment path in both the native and (if reachable)
  mixed read legs.
- **Regression**: run the full `NativeTranslation`/`Query` unit and functional suites plus the full
  `NorthwindIncludeQueryMongoTest`/`NorthwindMiscellaneousQueryMongoTest` spec classes to confirm no
  existing EF-441/444/447 leaf combination regresses (per this repo's branch-review-coverage lesson: green
  tests alone don't prove a mutation-discriminating gate — spot-check at least one EF-447 test still fails
  correctly if the new arm's ordering were swapped ahead of the whole-entity check, to prove the two arms
  don't shadow each other).
