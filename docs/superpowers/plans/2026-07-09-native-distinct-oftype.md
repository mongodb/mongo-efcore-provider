# Native Distinct + OfType Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate `OfType<Derived>()` and anonymous/DTO-projected `Distinct` (`Select(new{...}).Distinct()`) onto the native aggregation-pipeline path instead of the driver-LINQ fallback.

**Architecture:** OfType emits a discriminator `$match` conjunct (`$eq`/`$in`) from EF metadata — the native DOM shaper already materializes TPH derived types, so it's purely additive. Distinct is modeled as a degenerate GroupBy: convert the already-bound native `Projection` into a key-only `MongoGrouping` (zero accumulators), emit `$group(_id:<key>) → $project(flatten)`, reusing the EF-344 grouping machinery and inheriting its post-group guards.

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 via build configs), MongoDB C# driver, xUnit + FluentAssertions. Primary loop `Debug EF10`.

## Global Constraints

- Multi-EF via build **configurations** not TFMs: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (also validate `Debug EF8`, `Debug EF9`). Version-conditional code uses `EF8`/`EF9`/`EF10`.
- `src/` is `<Nullable>enable</Nullable>`. Preserve file encodings (these files have **no BOM** — match siblings).
- MethodInfo matching uses canonical `Microsoft.EntityFrameworkCore.Query.QueryableMethods` constants (compare `GetGenericMethodDefinition()`).
- New types `internal`; **no public-API / annotation-key change**. Native-default + emitted MQL are not breaking changes (per the versioning rubric). **Results under `MongoQueryMode.Native` MUST equal `MongoQueryMode.DriverLinq`** — a shape that goes native with wrong results is a Critical defect; the correct behavior for an unsupported shape is to fall back / mark non-native.
- **Scope refinement discovered in exploration:** native Distinct requires the preceding `Select` to already be natively bound, which `NativeProjectionBinder` only does for **anonymous/DTO member-access** projections. So `Select(x => new { x.City }).Distinct()` is native; **bare-scalar `Select(x => x.City).Distinct()` falls back** (bare-scalar `Select` is not native — SP3 scope). This narrows the spec's "scalar/anonymous/DTO" to "anonymous/DTO"; scalar-via-anonymous covers the scalar case.
- Zero regressions: full EF8/EF9/EF10 suites green; `MONGODB_EF_NATIVE_ONLY=1` spec sweep +passing, zero regressions.
- Ships as one squashed commit on `EF-347` (stacked on the design commit `a568381`, itself on `7a9a702`). Keep an `EF-347-presquash` backup until merge. Frequent commits during dev; squash at the end.
- Container-backed tests: run with **both `MONGODB_URI` and `ATLAS_URI` unset** (atlas-local container). If Docker is unavailable, STOP and report — don't fabricate passes.

## File Structure

**Modify (src):**
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateOfType` (emit discriminator `$match`), `TranslateDistinct` (new impl), the `VisitMethodCall` switch (add a `Distinct` arm).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` — whitelist `Distinct` in `IsNativeRepresentableSlotOperator`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` — a distinct entry point / relaxed accumulator rule (`TryBindDistinctFromProjection`).
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — a `ClearProjections()` (or `ReplaceProjections`) internal method (Distinct needs to swap field-ref projections for flatten projections).
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — as-built note (Task 4).

**Create (test):**
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOfTypeTests.cs`
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeDistinctTests.cs`

**Reuse unchanged:** `MongoGrouping`/`MongoGroupingKeyPart`/`MongoGroupAccumulator`, `MongoGroupStage`, `MongoPipelineFactory.RenderKeyedGroup`/`RenderProject`, `MongoSelectLowerer` group branch, the grouped-native execution routing (`NativeRoute.GroupBy`), `MongoInExpression`/`MongoConstantExpression`/`MongoFieldExpression`, `MongoSelectDefinition.AddPredicateConjunct`, the post-group guards.

---

### Task 1: OfType → discriminator `$match`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateOfType`, lines ~634-659)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOfTypeTests.cs` (create)

**Interfaces:**
- Consumes: `MongoSelectDefinition.AddPredicateConjunct(MongoExpression)`; `MongoFieldExpression(IProperty, string elementName)`; `MongoInExpression(MongoFieldExpression, MongoExpression values, bool negated)`; `MongoConstantExpression(object?, IProperty? forSerialization)`; `MongoBinaryExpression`/`MongoBinaryOperator.Equal` (single-value case); EF metadata `entityType.FindDiscriminatorProperty()`, `GetElementName()`, `entityType.GetDerivedTypes()`, `GetDiscriminatorValue()`.
- Produces: `TranslateOfType` returns the re-typed shaped query, native when the discriminator predicate is built, else marked non-native.

Background (verified): the native DOM shaper already materializes TPH derived types polymorphically (TPH entities are streaming-ineligible → DOM shaper → EF's discriminator-based `MaterializationCondition`; `DiscriminatorTests` prove `OfType<Customer>()` returns the correct 4 rows under `Native`). So OfType native = add the `$match`, keep the existing shaper re-typing, drop `MarkNotNativelyRepresentable()`.

- [ ] **Step 1: Write the failing test**

Create `NativeOfTypeTests.cs` modeled on `DiscriminatorTests` (reuse its `BaseEntity`→`Customer`→`SubCustomer` / `Supplier` TPH hierarchy and `SingleEntityDbContext.Create(collection, mapping)`), but set `UseQueryMode` like `NativeGroupByTests.Make`. Add:

```csharp
[Fact]
public void OfType_intermediate_type_goes_native_and_returns_subtree()
{
    // Customer is intermediate (SubCustomer derives from it) → $in over {Customer, SubCustomer} discriminators.
    using var db = /* CreateContext(seed, MongoQueryMode.NativeOnly, ...) over the TPH hierarchy */;
    var result = db.Entities.OfType<Customer>().ToList();
    Assert.Equal(expectedCustomerPlusSubCustomerCount, result.Count); // succeeds under NativeOnly ⇒ went native
    Assert.All(result, e => Assert.IsAssignableFrom<Customer>(e));
}

[Fact]
public void OfType_leaf_type_goes_native()
{
    using var db = /* NativeOnly */;
    var result = db.Entities.OfType<SubCustomer>().ToList(); // leaf → $eq
    Assert.All(result, e => Assert.IsType<SubCustomer>(e));
}

[Fact]
public void OfType_matches_driver_linq()
{
    // parity: same query under Native vs DriverLinq
    using var nativeDb = /* Native */; using var driverDb = /* DriverLinq */;
    Assert.Equal(driverDb.Entities.OfType<Customer>().OrderBy(e => e.Id).Select(e => e.Id).ToList(),
                 nativeDb.Entities.OfType<Customer>().OrderBy(e => e.Id).Select(e => e.Id).ToList());
}
```

> Implementer note: mirror `DiscriminatorTests` seeding (`SetupTestData`) and the three `MappingMode`s if practical; at minimum test `MappingMode.RealProperty` (string discriminator) + a shadow-default mode. Use the file's `database.CreateCollection<BaseEntity>(...)` + `SingleEntityDbContext.Create(collection, mapping)` and add `UseQueryMode(mode)`.

- [ ] **Step 2: Run to verify it fails**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~NativeOfTypeTests"`
Expected: FAIL — under `NativeOnly` the current `MarkNotNativelyRepresentable()` makes OfType throw `NativeTranslationNotSupportedException`.

- [ ] **Step 3: Implement the discriminator `$match` in `TranslateOfType`**

Replace the body's `MarkNotNativelyRepresentable()` block. Build the predicate from `resultEntityType`:

```csharp
var resultEntityType = entityType.Model.FindEntityType(resultType);
if (resultEntityType != null)
{
    var mongoQ = (MongoQueryExpression)source.QueryExpression;
    if (TryBuildDiscriminatorPredicate(resultEntityType, out var predicate))
    {
        mongoQ.Select.AddPredicateConjunct(predicate);
    }
    else
    {
        mongoQ.Select.MarkNotNativelyRepresentable();
    }

    return source.UpdateShaperExpression(entityShaperExpression.WithType(resultEntityType));
}
```

Add the helper (this file, private static):

```csharp
private static bool TryBuildDiscriminatorPredicate(IEntityType targetType, out MongoExpression predicate)
{
    predicate = null!;
    var discProp = targetType.FindDiscriminatorProperty();
    if (discProp is null)
        return false; // non-TPH / no discriminator → fall back

    var elementName = discProp.GetElementName();
    var values = targetType.GetDerivedTypes().Prepend(targetType)
        .Select(t => t.GetDiscriminatorValue())
        .ToArray();
    if (values.Length == 0)
        return false;

    var field = new MongoFieldExpression(discProp, elementName);
    predicate = values.Length == 1
        ? new MongoBinaryExpression(MongoBinaryOperator.Equal, field,
              new MongoConstantExpression(values[0], forSerialization: discProp))
        : new MongoInExpression(field,
              new MongoConstantExpression(values, forSerialization: discProp), negated: false);
    return true;
}
```

> Implementer note (the one rendering risk, from the design's §9): the `$in`/`$eq` renderer serializes each discriminator value via `MongoConstantExpression.ForSerialization` (the `IProperty`). Confirm the discriminator property's type mapping serializes the raw `GetDiscriminatorValue()` values (string or int) to the same BSON the stored `_t` uses — this is what the driver-LINQ path produces via `MongoEFDiscriminator`/`BsonValue.Create`. **Verify by running the parity test in Step 4.** If the property-serializer path produces a wrong BSON value (e.g. a shadow discriminator whose mapping doesn't round-trip the raw value), fall back for that case and record it as a follow-up rather than emitting a wrong `$match`. Keep the `entityType.ClrType == resultType` no-op short-circuit and the complex-type `NotSupportedException` unchanged.

- [ ] **Step 4: Run to verify pass (incl. parity)**

Run the Step-2 command. Expected: PASS — leaf (`$eq`), intermediate (`$in` subtree), and Native==DriverLinq parity all green. If parity fails on a mapping mode, apply the fallback in the Step-3 note.

- [ ] **Step 5: Confirm no regression to existing OfType behavior + build EF8**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~DiscriminatorTests"` (still green — those run default `Native` and now exercise the native `$match`). Then `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"`.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOfTypeTests.cs
git commit -m "EF-347: native OfType via discriminator \$match"
```

---

### Task 2: Distinct → degenerate GroupBy

**Files:**
- Modify: `MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateDistinct` + `VisitMethodCall` switch)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (whitelist)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` (distinct entry point)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (`ClearProjections`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeDistinctTests.cs` (create)

**Interfaces:**
- Consumes: `MongoSelectDefinition.Projection` (`IReadOnlyList<MongoProjection>`, each `MongoProjection(string Alias, MongoExpression Expression)` where a native Select's Expression is a `MongoFieldExpression`); `MongoGrouping`/`MongoGroupingKeyPart`; `MongoElementRefExpression(string path, Type)`; `HasDefaultKeySerialization(IProperty)`; `MongoSelectDefinition.AddProjection`, new `ClearProjections`, `Grouping`/`IsGroupBy` setters.
- Produces: `TranslateDistinct` sets `Select.Grouping` (key-only) + replaces `Projection` with flatten projections when native; else marks non-native. `NativeGroupByBinder.TryBindDistinctFromProjection(MongoQueryExpression) -> bool`.

Mechanism: after `Select(new {..})`, `Select.Projection` holds `[(alias, MongoFieldExpression)]`. `TranslateDistinct` converts each into a composite key part `MongoGroupingKeyPart(alias, fieldExpr)` and a flatten `MongoProjection(alias, MongoElementRefExpression("_id." + alias, fieldExpr.Type))`, sets `Select.Grouping = new MongoGrouping(keyParts, [] /*no accumulators*/)`, clears the old projections and adds the flatten ones. `Route` → `GroupBy`; lowerer emits `$group(_id:{alias:field,...}) → $project(flatten)`; the existing projection shaper (already built by the Select, reading top-level aliases) materializes the result unchanged. Setting `Grouping`/`IsGroupBy` means the EF-344 post-group guards force fallback for any operator after `Distinct`.

**As-built note (2026-07-10):** the plan's last sentence above is imprecise — as built, `Distinct` does NOT set `IsGroupBy`; it sets a separate `Select.IsDistinct` provenance flag instead (see `NativeGroupByBinder.TryBindDistinctFromProjection`). This is deliberate: `TranslateJoinCore`'s `GroupBy`+`Join` decline is a hard failure (a real `GroupBy` joined via driver-LINQ returns silently-wrong empty joins), whereas `Distinct`+`Join` must fall back gracefully (driver-LINQ joins a flat Distinct row set correctly) — reusing `IsGroupBy` would have hard-thrown `Distinct().Join(...)` under `Native`, a correct-results→throw regression. The post-terminal guards described here are keyed on `IsGroupBy || IsDistinct` (centralized as `MongoSelectDefinition.HasTerminalGrouping`), so `IsDistinct` gets the same fallback coverage `IsGroupBy` does.

- [ ] **Step 1: Write the failing tests**

Create `NativeDistinctTests.cs` modeled on `NativeGroupByTests` (`SingleEntityDbContext<Order>`, `CreateContext(seed, mode, name)`, `db.Entities`). Cover:

```csharp
[Fact]
public void Distinct_anonymous_projection_goes_native_and_dedups()
{
    using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(Distinct_anonymous_projection_goes_native_and_dedups));
    var result = db.Entities.Select(o => new { o.Country }).Distinct()
        .AsEnumerable().OrderBy(r => r.Country).ToList();
    Assert.Equal(new[] { "FR", "UK", "US" }, result.Select(r => r.Country).ToArray()); // deduped, went native
}

[Fact]
public void Distinct_composite_projection_matches_driver_linq()
{
    using var nativeDb = CreateContext(SeedOrders(), MongoQueryMode.Native, nameof(Distinct_composite_projection_matches_driver_linq) + "N");
    using var driverDb = CreateContext(SeedOrders(), MongoQueryMode.DriverLinq, nameof(Distinct_composite_projection_matches_driver_linq) + "D");
    Func<SingleEntityDbContext<Order>, object[]> run = db => db.Entities.Select(o => new { o.Country, o.Year }).Distinct()
        .AsEnumerable().OrderBy(r => r.Country).ThenBy(r => r.Year).Select(r => (object)(r.Country, r.Year)).ToArray();
    Assert.Equal(run(driverDb), run(nativeDb));
}

[Fact]
public void Bare_scalar_Distinct_falls_back_under_native_only()
{
    using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(Bare_scalar_Distinct_falls_back_under_native_only));
    Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Select(o => o.Country).Distinct().ToList());
}

[Fact]
public void Whole_entity_Distinct_falls_back_under_native_only()
{
    using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(Whole_entity_Distinct_falls_back_under_native_only));
    Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Distinct().ToList());
}

[Fact]
public void Operator_after_Distinct_falls_back_under_native_only()
{
    using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(Operator_after_Distinct_falls_back_under_native_only));
    Assert.Throws<NativeTranslationNotSupportedException>(() =>
        db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country == "US").ToList());
}
```

Plus a parity test that a **represented/converted projection key** falls back (mirror `NativeGroupByTests.GroupBy_bson_represented_key_falls_back`) if the fixture can express one; otherwise note it's covered by the shared `HasDefaultKeySerialization` reuse.

- [ ] **Step 2: Run to verify fail**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~NativeDistinctTests"`
Expected: FAIL — Distinct not native yet (the "goes native"/parity cases fail; the fall-back cases may already pass).

- [ ] **Step 3: Add `ClearProjections` to `MongoSelectDefinition`**

Beside `AddProjection`:

```csharp
    internal void ClearProjections() => _projections.Clear();
```

- [ ] **Step 4: Add the distinct entry point to `NativeGroupByBinder`**

```csharp
// Distinct(projection): the terminal Select already populated Select.Projection with
// (alias -> field-ref) pairs. Convert them into a key-only grouping (group by the projected
// value, zero accumulators) and replace the projections with a flatten that reads the value
// back out of _id. Returns false (→ fall back) if there is no native projection, or any key
// is not a default-serialized field ref (generic _id readback would diverge from DriverLinq).
internal static bool TryBindDistinctFromProjection(MongoQueryExpression mongoQ)
{
    var select = mongoQ.Select;
    if (select.Projection.Count == 0 || select.Grouping != null || select.Cardinality != null || select.HasPaging)
        return false;

    var keyParts = new List<MongoGroupingKeyPart>();
    var flatten = new List<MongoProjection>();
    foreach (var projection in select.Projection)
    {
        if (projection.Expression is not MongoFieldExpression field || !HasDefaultKeySerialization(field.Property))
            return false;
        keyParts.Add(new MongoGroupingKeyPart(projection.Alias, field));
        flatten.Add(new MongoProjection(projection.Alias,
            new MongoElementRefExpression("_id." + projection.Alias, field.Type)));
    }

    select.ClearProjections();
    select.Grouping = new MongoGrouping(keyParts, []);
    foreach (var f in flatten)
        select.AddProjection(f);
    return true;
}
```

> Note: this relaxes the "≥1 accumulator" constraint by being a **separate** entry point — `TryBindGroupProjection` (the GroupBy user path) still requires an accumulator; only Distinct produces a zero-accumulator grouping. Confirm `RenderKeyedGroup` renders `{ $group: { _id: {...} } }` with no accumulator entries (verified: the accumulator `foreach` simply doesn't execute) and the lowerer appends the flatten `$project`.

- [ ] **Step 5: Implement `TranslateDistinct` + switch arm + whitelist**

`TranslateDistinct` (replace `=> null`):

```csharp
protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source)
{
    var mongoQ = (MongoQueryExpression)source.QueryExpression;
    if (!NativeGroupByBinder.TryBindDistinctFromProjection(mongoQ))
        mongoQ.Select.MarkNotNativelyRepresentable();
    return source; // shaper unchanged: the Select's projection shaper reads the flatten aliases
}
```

Add to `VisitMethodCall`'s switch (so the override actually runs — Distinct isn't dispatched otherwise), beside the other bubble-through arms:

```csharp
case nameof(Queryable.Distinct) when methodDefinition == QueryableMethods.Distinct:
```
(route it the same way the arm group does — through `base.VisitMethodCall` so `TranslateDistinct` is invoked; follow the exact pattern the `GroupBy`/`Contains` arm uses).

Whitelist in `NativeSlotPopulator.IsNativeRepresentableSlotOperator`:
```csharp
       || methodDefinition == QueryableMethods.Distinct
```

- [ ] **Step 6: Run to verify pass**

Run the Step-2 command. Expected: PASS — anonymous/composite Distinct native + parity; bare-scalar / whole-entity / post-Distinct fall back (throw under NativeOnly).

- [ ] **Step 7: Confirm GroupBy still green + EF8 build**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByTests"` and unit `--filter "FullyQualifiedName~NativeGroupBy"` (the shared binder/definition changes must not regress GroupBy). Then `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"`.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeDistinctTests.cs
git commit -m "EF-347: native projected Distinct via degenerate \$group"
```

---

### Task 3: Regression sweep, baselines, EF8/EF9

**Files:** possibly spec MQL baselines under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/`.

- [ ] **Step 1: EF10 unit + functional suites**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10"` then FunctionalTests. Expected: green.

- [ ] **Step 2: Native-only spec sweep (zero-regression gate)**

Run SpecificationTests with `MONGODB_EF_NATIVE_ONLY=1` on EF10, and normally. Expected: **+passing** (Distinct/OfType spec tests now native) with **zero regressions**. For any test failing only on `AssertTranslationFailed` exception-type/baseline drift because a Distinct/OfType shape now goes native or falls back differently, apply the same lenient-assert / baseline-regen bookkeeping used in EF-344 (regenerate with `EF_TEST_REWRITE_BASELINES=1`, diff, confirm data-clean). If a test genuinely regressed (wrong data / new throw on a supported shape), STOP and report.

- [ ] **Step 3: EF8 + EF9 build + affected tests**

Run: `dotnet build … -c "Debug EF8"` and `-c "Debug EF9"`; then the Query-filtered UnitTests + FunctionalTests + the OfType/Distinct/Discriminator/GroupBy spec classes for each. Expected: green. `#if`-guard only if a base API differs (none expected — `QueryableMethods.Distinct`/`OfType`, `FindDiscriminatorProperty`, `GetDiscriminatorValue` are version-stable).

- [ ] **Step 4: Commit** any regenerated baselines (`git add -A && git commit -m "EF-347: regenerate Distinct/OfType spec baselines; EF8/EF9 validation"`); if none, say so (no empty commit).

---

### Task 4: Docs + squash

- [ ] **Step 1: Update `Query/AGENTS.md`**

Add a concise EF-347 as-built note: native now covers `OfType<Derived>()` (discriminator `$match`, `$eq` leaf / `$in` subtree; DOM shaper already materializes derived types) and anonymous/DTO-projected `Distinct` (degenerate key-only `$group`); bare-scalar Distinct, whole-entity `.Distinct()`, and any operator after `Distinct` fall back (the last via the inherited `IsGroupBy` post-group guard). Keep claims accurate; don't overstate. Update the line that lists `Distinct`/`OfType` as fallback-only.

- [ ] **Step 2: Backup + squash to one commit**

```bash
git branch -f EF-347-presquash HEAD
git reset --soft a568381   # the EF-347 design commit (base of the impl work)
git commit -F <PR-style message>
git diff --quiet EF-347-presquash HEAD && echo "content-identical OK"
```
PR-style message: what went native (OfType `$match`; anonymous/DTO Distinct as degenerate `$group`), the reuse of the EF-344 grouping machinery + inherited post-group guards, the fall-back list, not-a-breaking-change, follow-ups. Exclude `nuget.config`/scratch (soft reset leaves them out).

- [ ] **Step 3: Final EF10 suites post-squash**; confirm green. Do NOT push (user drives the force-push to `origin/NativeQueryOngoing`).

---

## Self-Review notes

- **Spec coverage:** OfType full-hierarchy `$eq`/`$in` (Task 1); anonymous/DTO projected Distinct via degenerate GroupBy (Task 2); post-Distinct + whole-entity + bare-scalar + represented-key fall back (Task 2 tests, via `IsGroupBy` guard + `HasDefaultKeySerialization` + the no-native-projection precondition); OfType materialization confirmed (exploration §8, not a separate task); zero-regression sweep + EF8/9 (Task 3); docs (Task 4). Spec's "scalar projection" narrowed to "scalar-via-anonymous" (bare-scalar falls back) — recorded in Global Constraints.
- **Risk / split point:** Task 1's discriminator-`$in` **serialization** (does `ForSerialization`=discriminator-property render the raw `GetDiscriminatorValue()` correctly across the mapping modes?) is the one uncertainty — Step 3's note handles it with a per-case fallback and the parity test is the check. If it proves broadly unreliable, OfType may need a small `MongoValueRenderer`/discriminator-constant tweak; keep that contained to Task 1.
- **Type consistency:** `TryBindDistinctFromProjection` (Task 2) / `TryBuildDiscriminatorPredicate` (Task 1) names used consistently; `MongoProjection(Alias, Expression)`, `MongoGroupingKeyPart(name, field)`, `MongoGrouping(keyParts, accumulators)`, `MongoElementRefExpression(path, type)` match the exploration's verbatim signatures.
