# Native cross-collection reference SelectMany — projected (EF-347 slice 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the projected form of cross-collection **reference** SelectMany (`q.SelectMany(c => c.Orders, (c,o) => new {c.Name, o.Total})` / query syntax) translate natively to `$lookup` + `$unwind` + `$project`, reusing the owned-SelectMany binder/shaper machinery; every other shape keeps hard-failing.

**Architecture:** The reference-nav `collectionSelector` is a correlated `Where`-over-`EntityQueryRootExpression` (spike-confirmed). A new binder recognizes it (reusing `NativeProjectionBinder`'s Count-binder correlation helpers, extracted to a shared `NativeCorrelationMatcher`), resolves the reference nav, and registers a `ForceUnwind` `LookupExpression`. `MongoUnwindSource` is generalized (`Owned`/`Reference` kind, `InnerScopePath`); for `Reference` the inner scope is `_lookup_<Nav>`. The lowerer emits `$lookup` + inner-join `$unwind` (preserve:false); slice-4's two-scope binder + `TranslateSelect` pending branch + `TransparentIdentifier` shaper + DOM projection shaper are reused unchanged.

**Tech Stack:** C# / EF Core provider, xUnit (plain `Assert.*`). Multi-EF via `Debug EF8|EF9|EF10`.

## Global Constraints

- **Purely additive; no regression.** Reference SelectMany hard-fails all modes today; unsupported shapes keep returning `null` → EF base `NotTranslated`. No parity baseline — verify vs expected in-memory results.
- **Behavior-preserving extraction:** moving the correlation helpers to `NativeCorrelationMatcher` must not change the projected-`Count` binder's behavior.
- **Do not regress** Include (reference-streaming `$lookup+$unwind`; array-collection `$lookup`), owned SelectMany (slices 3–4), SP3/Distinct/GroupBy, set-ops.
- **Multi-version EF8/EF9/EF10** — slice 4 hit an EF8-only CS9174 (newer `ReplacingExpressionVisitor.Replace` overload); avoid newer-only overloads / collection-expression-to-non-constructible.
- Never materialize a bare owned/reference entity (projection required; bare-entity `ti => ti.Inner` hard-declines cleanly via slice-4's guard, unchanged).
- `<Nullable>enable</Nullable>`; no-BOM; new members `internal`/`private`. Configs contain spaces: `-c "Debug EF10"`.
- Design: `docs/superpowers/specs/2026-07-16-native-selectmany-reference-projected-design.md`.

## Scope

**In:** SelectMany over a cross-collection reference (non-embedded) single-level collection nav, **projected form** (`(c,o) => new {…}` explicit result selector + query syntax), trailing projection = terminal member-access `new {…}`/DTO of `ti.Outer`/`ti.Inner`. **Out → hard-fail (unchanged):** bare-entity result (`select o`/`select i` — clean decline via slice-4 guard), user-filtered/correlated inner (`c.Orders.Where(userPred)`), computed leaves, nested/multi-level.

---

### Task 1: Extract `NativeCorrelationMatcher` from `NativeProjectionBinder` (behavior-preserving refactor)

Move the correlated-`Where`-over-`EntityQueryRoot` recognition + FK-nav resolution out of `NativeProjectionBinder` into a shared helper, so both the projected-`Count` binder and the new reference-SelectMany binder consume it.

**Files:**
- Create: `src/…/Query/NativeTranslation/NativeCorrelationMatcher.cs`
- Modify: `src/…/Query/NativeTranslation/NativeProjectionBinder.cs`
- Test: `tests/…/UnitTests/Query/NativeTranslation/NativeCorrelationMatcherTests.cs` (create) + existing `NativeProjectionBinder`/projected-`Count` tests must stay green.

**Interfaces produced:**
- `internal static class NativeCorrelationMatcher` with:
  - `bool TryMatchCorrelatedCollection(Expression whereBody, IEntityType outerEntityType, ParameterExpression outerParameter, IEntityType targetEntityType, bool requireEmbedded, out INavigation navigation)` — recognizes the FK correlation predicate (via the moved `TryGetCorrelationEqualitySides`/`TryExtractEqualitySides`/`IsNullGuard`/`GetRootParameter`) and resolves the single matching collection nav (the moved candidate loop), filtered by `IsEmbedded() == requireEmbedded`. Returns false on no/ambiguous match or an extra predicate conjunct.

- [ ] **Step 1: Write failing matcher tests**
Create `NativeCorrelationMatcherTests.cs`. Build a model with a reference collection nav (principal `Customer` + separate `Order` with FK; `HasMany().WithOne().HasForeignKey()`) and, separately, an owned collection nav. Construct the correlation predicate `o => c.pk == o.fk` (both the bare-equality and null-guarded-AndAlso forms EF emits) and assert:
- `TryMatchCorrelatedCollection(pred.Body, customerType, cParam, orderType, requireEmbedded:false, out nav)` → true, `nav` = the Orders reference nav.
- `requireEmbedded:true` over the same reference nav → false (and vice-versa for the owned nav).
- an extra conjunct (`… && o.Amount > 5`) → false.
- ambiguous (two navs to the same target with the same FK) → false.

Run `--filter "FullyQualifiedName~NativeCorrelationMatcherTests"` — Expected: compile failure.

- [ ] **Step 2: Create `NativeCorrelationMatcher` by MOVING the helpers**
Move verbatim from `NativeProjectionBinder` (lines ~242-316): `TryGetCorrelationEqualitySides`, `TryExtractEqualitySides`, `IsNullGuard`, `IsNullConstant`, `GetRootParameter`. Wrap the candidate-resolution loop (currently `NativeProjectionBinder.cs` ~203-219 — `outerEntityType.GetNavigations().Where(n => n.IsCollection && !n.IsEmbedded() && n.TargetEntityType == targetEntityType && n.ForeignKey.Properties.Count == 1 && n.ForeignKey.Properties[0].Name == dependentPropertyName)`) into `TryMatchCorrelatedCollection`, parameterizing `!n.IsEmbedded()` as `n.IsEmbedded() == requireEmbedded`. Keep the exact null-guard/equality/dependent-property-name logic. Make the moved members `internal static` on `NativeCorrelationMatcher`.

- [ ] **Step 3: Rewrite `NativeProjectionBinder.TryTranslateProjectedCollectionCount` to consume it**
Replace its inline correlation extraction + candidate loop with a `NativeCorrelationMatcher.TryMatchCorrelatedCollection(predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false, out navigation)` call (the Count binder wants a reference nav — `!IsEmbedded`, i.e. `requireEmbedded: false`), preserving the surrounding `MongoSizeExpression`/`LookupExpression{InjectAfterRoot=true}` logic. Behavior must be identical.

- [ ] **Step 4: Run tests + commit**
`dotnet build … -c "Debug EF10" -v quiet` then `dotnet test …UnitTests… -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeCorrelationMatcher|FullyQualifiedName~NativeProjectionBinder|FullyQualifiedName~ProjectedCollectionCount|FullyQualifiedName~Count"` (whatever covers the projected-`Count` binder). Expected: PASS — new matcher tests + all pre-existing `Count`-projection tests green (behavior-preserving). Commit `EF-347: Extract NativeCorrelationMatcher from NativeProjectionBinder (shared correlated-collection recognition)`.

---

### Task 2: Generalize `MongoUnwindSource` + `TryBindReferenceNavUnwind` (the recognition crux)

**Files:**
- Modify: `src/…/Query/Expressions/MongoUnwindSource.cs`
- Modify: `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Modify (rename `ElementPath` → `InnerScopePath` at readers): `MongoSelectLowerer.cs`, `NativeSelectManyBinder.cs`, and the slice-3/4 test files that set `MongoUnwindSource`.
- Test: `tests/…/UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` (add reference cases)

**Interfaces produced:**
- `internal enum MongoUnwindSourceKind { Owned, Reference }`
- `MongoUnwindSource` — `Kind`, `InnerScopePath` (renamed from `ElementPath`), `InnerEntityType`, and `LookupExpression? Lookup` (set for `Reference`, null for `Owned`). Two ctors or a kind arg (owned: `(Owned, elementName, innerType, null)`; reference: `(Reference, "_lookup_<Nav>", navTargetType, lookup)`).
- `internal static bool NativeSelectManyBinder.TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)`.

- [ ] **Step 1: Write failing tests**
Add to `NativeSelectManyBinderTests.cs`. Construct `collectionSelector = (Customer c) => DbSet<Order>().Where(o => c.Id == o.CustomerId).AsQueryable()`-equivalent tree (match the spike's shape — `Queryable.Where(EntityQueryRootExpression<Order>, o => c.pk == o.fk)`, possibly wrapped in `AsQueryable`). Assert:
- `TryBindReferenceNavUnwind` returns true; `Select.UnwindSource.Kind == Reference`, `.InnerScopePath == "_lookup_Orders"`, `.InnerEntityType == orderType`, `.Lookup` is the `ForceUnwind` lookup; and `mongoQ.Lookups` contains that lookup.
- returns false for: an owned collection nav (embedded — belongs to the owned binder); a filtered inner (extra `Where` conjunct); a non-`Where`-over-`EntityQueryRoot` body.
- **Two-scope binding over the reference source:** with `UnwindSource` set to `Reference("_lookup_Orders", orderType)`, `TryBindTransparentIdentifierProjection` (slice 4, unchanged) binds `ti.Inner.Total` → field ref elementName `"_lookup_Orders.Total"` and `ti.Outer.Name` → `"Name"`. (This proves the generalized `InnerScopePath` flows through the unchanged binder.)

Run the focused filter — Expected: compile failure.

- [ ] **Step 2: Generalize `MongoUnwindSource`**
Add `MongoUnwindSourceKind`; rename `ElementPath` → `InnerScopePath`; add `Kind` + `LookupExpression? Lookup`. Provide `MongoUnwindSource.Owned(string elementPath, IEntityType inner)` and `MongoUnwindSource.Reference(string scopePath, IEntityType inner, LookupExpression lookup)` factory statics (or a kind-taking ctor). Update slice-3/4 owned call sites (`NativeSelectManyBinder.TryBind`/`TryBindBareNavUnwind`) to `MongoUnwindSource.Owned(...)`, and the two-scope binder + lowerer reads from `InnerScopePath`. Grep for `ElementPath` / `new MongoUnwindSource(` and update all (incl. test files — the slice-3/4 tests set it).

- [ ] **Step 3: Implement `TryBindReferenceNavUnwind`**
```csharp
// EF-347 slice 5: a cross-collection reference SelectMany's collectionSelector is a correlated subquery,
// Queryable.Where(EntityQueryRootExpression<Target>, o => outer.pk == o.fk) (spike-confirmed), NOT a bare nav.
// Recognize it via NativeCorrelationMatcher, resolve the reference (!IsEmbedded) nav, register a ForceUnwind
// $lookup, and set a Reference-kind UnwindSource whose inner scope is _lookup_<Nav>.
internal static bool TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
{
    var body = UnwrapAsQueryable(collectionSelector.Body);   // reuse slice-3 UnwrapAsQueryable
    if (body is not MethodCallExpression
        {
            Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var d },
            Arguments: [EntityQueryRootExpression root, var predicateArg]
        }
        || d != typeof(System.Linq.Queryable))
        return false;

    var predicate = predicateArg.UnwrapLambdaFromQuote();
    var outerEntityType = mongoQ.CollectionExpression.EntityType;
    if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            predicate.Body, outerEntityType, collectionSelector.Parameters[0], root.EntityType,
            requireEmbedded: false, out var navigation))
        return false;

    var lookup = new LookupExpression(navigation, forceUnwind: true);
    mongoQ.AddLookup(lookup);
    mongoQ.Select.UnwindSource = MongoUnwindSource.Reference(
        LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup);
    return true;
}
```
> Note: the correlation predicate's parameter identity — confirm `TryMatchCorrelatedCollection` binds the outer side to `collectionSelector.Parameters[0]` and the dependent side to the `Where` predicate's own parameter (mirror how the Count binder passes `outerParameter`). Adapt the matcher signature if the Count binder threaded these differently.

- [ ] **Step 4: Run tests + commit**
Focused filter — Expected: PASS (reference binding + the two-scope `_lookup_Orders` prefix + rejections; slice-3/4 owned tests still green after the `InnerScopePath` rename). Commit `EF-347: MongoUnwindSource Owned/Reference kind + TryBindReferenceNavUnwind (correlated reference-nav recognition)`.

---

### Task 3: Lowerer — ForceUnwind-collection `$lookup` + inner-join `$unwind`

**Files:**
- Modify: `src/…/Query/NativeTranslation/MongoSelectLowerer.cs`
- Modify: `src/…/Query/NativeTranslation/MongoPipelineFactory.cs` (the `$unwind` render for the SelectMany flatten — `preserve: false`)
- Possibly: `src/…/Query/NativeTranslation/Stages/MongoUnwindStage.cs` (a flag to distinguish preserve vs flatten), or a new render path.
- Test: `MongoSelectLowererTests.cs`, `MongoPipelineFactoryTests.cs`

- [ ] **Step 1: Write failing lowerer/factory tests**
Construct a `MongoQueryExpression` with `Select.UnwindSource = MongoUnwindSource.Reference("_lookup_Orders", orderType, lookup)`, `mongoQ.AddLookup(lookup)` (a `ForceUnwind` collection lookup), and a two-entry `Projection`. Assert `Lower` produces `[…, MongoLookupStage, MongoUnwindStage, MongoProjectStage]` in that order (no `MongoUnwindFieldStage` — the owned-only stage). Assert (factory) the ForceUnwind-collection `$unwind` renders with **`preserveNullAndEmptyArrays: false`** (or omitted — the flatten/inner-join default), distinct from the reference-Include `$unwind` (`preserve: true`). Confirm an OWNED `UnwindSource` still lowers to `MongoUnwindFieldStage` + `$project`.

Run the focused filters — Expected: fail (AppendLookupStages throws for a ForceUnwind collection lookup today; or the assertions fail).

- [ ] **Step 2: `AppendLookupStages` — ForceUnwind-collection branch**
Add, between the `IsStreamableReference` and `IsNativeCollectionLookup` arms (or after), a branch:
```csharp
else if (lookup.Navigation.IsCollection && lookup.ForceUnwind)
{
    // EF-347 slice 5: a reference-collection SelectMany flatten — $lookup the referenced collection then
    // $unwind to a single element per row (inner-join semantics; a principal with no children drops out).
    stages.Add(new MongoLookupStage(lookup));
    stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: false));
}
```
(Include's `IsStreamableReference`/`IsNativeCollectionLookup` arms unchanged; this keys on `IsCollection && ForceUnwind`, which neither produces.)

- [ ] **Step 3: `$unwind` preserve flag**
`MongoUnwindStage` today renders with `preserveNullAndEmptyArrays: true` (Include LeftJoin). Thread a `bool PreserveNullAndEmptyArrays` (default `true` for existing callers) so the SelectMany flatten emits `false`. Update `MongoPipelineFactory.RenderUnwind` to read it. Existing reference-Include streaming keeps `true`.

- [ ] **Step 4: Lowerer `UnwindSource` block — branch on Kind**
In `MongoSelectLowerer.Lower`, change the slice-3 `UnwindSource` block so it only emits `MongoUnwindFieldStage` for `Owned` (Reference's `$lookup`+`$unwind` are already emitted by `AppendLookupStages`, which runs earlier):
```csharp
if (select.UnwindSource is { } unwind)
{
    if (unwind.Kind == MongoUnwindSourceKind.Owned)
        stages.Add(new MongoUnwindFieldStage(unwind.InnerScopePath));
    // Reference: $lookup + $unwind already appended by AppendLookupStages above.
    if (select.Projection.Count > 0)
        stages.Add(new MongoProjectStage(select.Projection));
    return stages;
}
```
(Confirm `AppendLookupStages` runs before this block — it does, at stage 5.)

- [ ] **Step 5: Run tests + commit**
Focused unit filters — Expected: PASS. Commit `EF-347: Lowerer ForceUnwind-collection $lookup + inner-join $unwind for reference SelectMany`.

---

### Task 4: QMTEV wiring — `TranslateSelectMany` reference branch; functional tests

**Files:**
- Modify: `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` (add reference cases; reuse the reference-collection fixture from the EF-339 collection-Include tests or add one)

- [ ] **Step 1: Write failing functional tests**
Reference fixture (principal + separate child collection with FK). Cover:
- `q.SelectMany(c => c.Orders, (c,o) => new { c.Name, o.Total })` and query syntax — succeed under `NativeOnly`, correct results (== expected in-memory join), MQL (`$lookup` from/localField/foreignField/as `_lookup_Orders` → `$unwind "$_lookup_Orders"` → `$project` `$Name`/`$_lookup_Orders.Total`).
- a principal with ZERO children → contributes NO rows (inner-join; proves `preserve: false`).
- shared outer/inner member name → correct.
- hard-fail (assert still throws): bare-entity `SelectMany(c => c.Orders)` (clean `NotSupportedException` via slice-4 guard); filtered inner `SelectMany(c => c.Orders.Where(o => o.Total > k), (c,o) => …)`; computed leaf.

Run after build — Expected: native-success cases fail (reference SelectMany hard-fails today).

- [ ] **Step 2: `TranslateSelectMany` reference branch**
After the owned bare-nav / inner-`Select` binders, add the reference branch (mirror the slice-4 bare-nav accept + wrapped-shaper build):
```csharp
if (NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQueryExpression, collectionSelector))
{
    // Build the TransparentIdentifier(Outer=source shaper, Inner=<reference target entity shaper>) shape
    // EF's trailing Select expects — identical to the owned bare-nav path (slice 4).
    return BuildSelectManyWrappedShaper-equivalent(source, mongoQueryExpression, resultSelector,
        innerEntityType: mongoQueryExpression.Select.UnwindSource!.InnerEntityType);
}
```
Reuse slice 4's wrapped-shaper construction (`TransparentIdentifier(Outer, Inner)` via nested single-arg `Replace`, item shaper = a `StructuralTypeShaperExpression` for the reference target, type-check-only). The trailing `TranslateSelect` pending-SelectMany branch (slice 4) is **unchanged** — it fires on `UnwindSource != null && Projection empty` and binds via `TryBindTransparentIdentifierProjection` (inner prefix = `_lookup_<Nav>` from the generalized `InnerScopePath`).

- [ ] **Step 3: Run tests + regression + commit**
Build EF10, `--filter "FullyQualifiedName~NativeSelectMany|FullyQualifiedName~QueryModeGate|FullyQualifiedName~Include"` (include an Include filter to catch a `$lookup` regression) — Expected: PASS. Commit `EF-347: Native cross-collection reference SelectMany (projected) — $lookup + $unwind + $project`.

---

### Task 5: Composition-seam + AGENTS.md + full verification

- [ ] **Step 1: Seam/coverage tests** — operators after the reference SelectMany (Where/OrderBy/Skip/Take/Count/GroupBy/second SelectMany): assert ACTUAL behavior (per the owned slices, some graceful-correct-fallback, some hard-fail — run + observe + assert real). Parametrized-outer-predicate end-to-end. Run — Expected: PASS.
- [ ] **Step 2: AGENTS.md** — reference SelectMany (projected) now native: correlated-`Where` recognition (`NativeCorrelationMatcher`), `ForceUnwind` `$lookup`+inner-join-`$unwind`, generalized `MongoUnwindSource` (Owned/Reference), reuse of slice-4 two-scope binding (`_lookup_<Nav>` inner scope). Note the inner-join `$unwind` (`preserve:false`) vs Include's `preserve:true`. Remove reference SelectMany from deferred; keep bare-entity/filtered-inner/nested deferred. Commit.
- [ ] **Step 3: Full verification (controller-run; no commit)** — full 3-version `/test-all` green; `MONGODB_EF_NATIVE_ONLY=1` spec sweep vs `d2cacc5` (this may move some Northwind SelectMany tests native — they are cross-collection reference; confirm the delta + zero regressions). Report counts.

---

## Self-Review

**1. Spec coverage:** correlation-matcher extraction (Task 1); IR generalize + reference binder recognition (Task 2); lowerer ForceUnwind `$lookup`+inner-join-`$unwind` (Task 3); QMTEV wiring + functional end-to-end (Task 4); seam/docs/verification (Task 5). Reuse of slice-4 two-scope binder + shaper is explicit; bare-entity/filtered-inner deferred (hard-fail via slice-4 guard / matcher rejection). Hazards: Count-binder-not-regressed (Task 1 tests), Include-not-regressed (Task 3 branch keys on `IsCollection && ForceUnwind` + Task 4 Include filter), inner-join `$unwind` preserve:false (Task 3 flag + Task 4 empty-children test), two-scope inner prefix (Task 2 test), multi-version (Task 3/4 no newer-only APIs). ✓

**2. Placeholder scan:** the `>` notes (matcher parameter-threading; `BuildSelectManyWrappedShaper-equivalent`) point at concrete slice-4 code to mirror — not vague. Production code steps show complete code or the exact existing helper to reuse. ✓

**3. Type consistency:** `NativeCorrelationMatcher.TryMatchCorrelatedCollection(Expression, IEntityType, ParameterExpression, IEntityType, bool, out INavigation)`; `MongoUnwindSourceKind`, `MongoUnwindSource.Owned/.Reference`, `InnerScopePath`, `Lookup`; `TryBindReferenceNavUnwind(MongoQueryExpression, LambdaExpression)`; `MongoUnwindStage(lookup, preserveNullAndEmptyArrays)`. Reuse of `UnwrapAsQueryable`/`UnwrapLambdaFromQuote`/`TryBindTransparentIdentifierProjection`/slice-4 wrapped-shaper. ✓
