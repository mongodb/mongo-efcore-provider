# Native GroupBy SP1: Key-Selector Generalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generalize `NativeGroupByBinder.TryBindGroupKey` so a `GroupBy` key selector can be an `EF.Property(...)`
call, a computed expression (`.Value.Year`, a cast), or a captured/closure query parameter — not just a bare
member, an all-member composite, a literal constant, or empty `new{}` — moving 11 `NorthwindGroupByQueryMongoTest`
methods off the driver-LINQ fallback and onto the native `$group` pipeline.

**Architecture:** `TryBindGroupKey`'s `switch` on the key selector's body currently dispatches by C# expression
node type (`NewExpression`/`MemberExpression`/`ConstantExpression`) and calls `MongoExpressionTranslator
.TryTranslateField` for the member case. `TryTranslateField` only resolves a bare mapped member. This plan
replaces that per-shape dispatch with `MongoExpressionTranslator.TryTranslateValue` — the SAME general "any
translatable value" method `TryBindAccumulator` already uses two call sites below in this exact file (lines 437
and 561, both with the identical trade-off comment this plan follows) — which internally already resolves a
bare member, `EF.Property`, computed date/arithmetic/conditional/coalesce expressions, literal constants, AND
captured query parameters, all with the same `AllFieldsDefaultSerialized` safety check
(`NativeGroupByBinder.HasDefaultKeySerialization` under the hood) that today's manual per-case checks perform.
This is a narrowing of new code, not new capability invented from scratch — `TryTranslateValue` already handles
every shape this plan needs; the change is *calling* it from more places.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`, no FluentAssertions), MongoDB
aggregation pipeline (`$group`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP1 section)

## Global Constraints

- **`Native == DriverLinq` invariant** — this change must alter only the execution path (fallback → native),
  never query results. Every target test's `AssertQuery`/`AssertMql` must keep passing under the DEFAULT
  (`MongoQueryMode.Native`) mode exactly as it does today, in addition to newly passing under `NativeOnly`.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.
- A GroupBy key part with a value converter or non-default `BsonRepresentation` must still **decline** (fall
  back to driver-LINQ), never silently read back the wrong value — `TryTranslateValue`'s
  `AllFieldsDefaultSerialized` check already enforces this; do not bypass or special-case around it.
- **A recognizer must not mutate then decline** — stage into locals (as today's code already does with the
  `parts` list) and only assign `select.PendingGroupKey` once every part in the key has bound successfully.
- Multi-EF-version targeting (`EF8`/`EF9`/`EF10` build configurations) — this change touches no version-
  conditional code and needs no `#if` guards, but the final verification step still runs all three
  configurations (this repo's `/test-all` skill) before considering the slice done.

## Review Focus

- **A GroupBy key on a value-converted/non-default-represented property must still decline**, not silently
  read back the wrong value once the dispatch broadens from `TryTranslateField` to `TryTranslateValue` — Task 1
  adds `Non_default_serialized_property_key_still_declines`.
- **A composite key mixing an ordinary member with an `EF.Property` part** (`new { A = EF.Property<string>(o,
  "CustomerID"), o.Region }`) must bind both parts correctly, not just a uniform-shape composite — Task 1 adds
  `Composite_key_with_mixed_member_and_EF_Property_parts_binds`.
- **A captured-parameter key combined with a member in the SAME composite key** (`var a = 2; GroupBy(o => new
  { a, o.Region })`) exercises the per-part loop's new three-way fallback with two DIFFERENT shapes in one key,
  not just a single-shape key — Task 1 adds `Composite_key_with_parameter_and_member_parts_binds`.
- **A nested `GroupBy(key1).Select(...).GroupBy(key2)` whose FIRST key is now computed** — the outer
  `GroupBy`'s key resolution (`select.PriorGrouping`) and the eventual flatten/read-back must still work when a
  key part's `FieldRef` is a computed expression type (e.g. `MongoDatePartExpression`) instead of a
  `MongoFieldExpression` — this is exactly `GroupBy_aggregate_followed_another_GroupBy_aggregate`'s shape; Task
  2's spec-suite run is this bucket's actual proof, but Task 1 also adds a binder-level unit test
  (`Computed_key_part_flows_through_PriorGrouping_for_nested_GroupBy`) so a future regression is caught at the
  unit level, not only by the full spec suite.
- **Baselines may coincidentally already match.** The driver's own LINQ v3 provider already emits an
  identical-looking `$group`/`$project` shape for several of these queries today (confirmed by reading the
  existing `AssertMql` baseline for `GroupBy_Property_Select_Key_Count`, already `{"$group": {"_id":
  "$CustomerID", ...`). Do not assume every target method needs a baseline rewrite — check each one
  individually (Task 2) and only regenerate the ones that actually differ.

---

### Task 1: Generalize `TryBindGroupKey`'s key-part translation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs:50-111`
  (`TryBindGroupKey`)
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs:105-124`
  (flip two existing pinned-decline tests) and append new tests after the existing `TryBindGroupKey` section
  (before the `// ── TryBindGroupProjection ──` marker, if present — otherwise directly after
  `Constructor_call_key_with_two_arguments_does_not_bind_as_empty_key`, currently ending at line 166)

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslateValue(Expression, out MongoExpression?)` (existing, public,
  already used by `TryBindAccumulator` in this same file) and `MongoExpressionTranslator.DistinctAliasScope`
  (existing, already set for `PriorGrouping` earlier in this method — unchanged).
- Produces: `TryBindGroupKey`'s existing public signature and `MongoSelectDefinition.PendingGroupKey` shape are
  UNCHANGED — `MongoGroupingKeyPart(string? Name, MongoExpression FieldRef)` — only the set of `Expression`
  shapes it accepts grows. No other file's code needs to change to consume the new key shapes; `TryGetKeyMemberPath`,
  `TryBindGroupProjection`'s flatten loop, and the MQL renderer already treat `FieldRef` as an opaque
  `MongoExpression` and work with any concrete subtype.

- [ ] **Step 1: Flip the two existing pinned-decline tests to their new expected behavior**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`. Replace
the two tests below (currently at lines 105–124):

```csharp
    [Fact]
    public void Computed_key_returns_false_and_leaves_state_unset()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> key = x => x.OrderDate.Year;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Composite_key_with_computed_part_returns_false()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }
```

with:

```csharp
    [Fact]
    public void Computed_key_binds_via_TryTranslateValue()
    {
        // EF-322 SP1: a computed key part (a DateTime.Year extraction here) is no longer a hard decline —
        // TryBindGroupKey now falls through to the same TryTranslateValue every accumulator operand already
        // uses, which resolves this to a MongoDatePartExpression. There is no backing IProperty for a computed
        // key part, so HasDefaultKeySerialization's converter/BsonRepresentation check does not apply here —
        // same reasoning as the pre-existing literal-constant key case.
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> key = x => x.OrderDate.Year;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        Assert.IsType<MongoDatePartExpression>(part.FieldRef);
    }

    [Fact]
    public void Composite_key_with_computed_part_binds_via_TryTranslateValue()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Country", parts[0].Name);
        Assert.IsType<MongoFieldExpression>(parts[0].FieldRef);
        Assert.Equal("Yr", parts[1].Name);
        Assert.IsType<MongoDatePartExpression>(parts[1].FieldRef);
    }
```

Then append these NEW tests immediately after
`Constructor_call_key_with_two_arguments_does_not_bind_as_empty_key` (still `false` — a real constructor call
with `Members == null` is tried whole-body against `TryTranslateValue`, which cannot translate a constructor
call, so this test's assertion is unaffected by this change and needs no edit — only added as context for where
the new tests go):

```csharp
    [Fact]
    public void EF_Property_scalar_key_binds()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => EF.Property<string>(x, nameof(Order.Country));

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        var field = Assert.IsType<MongoFieldExpression>(part.FieldRef);
        Assert.Equal("Country", field.ElementName);
    }

    [Fact]
    public void Captured_parameter_key_binds_as_parameter()
    {
        var mongoQ = TestQuery();
        var a = 2;
        Expression<Func<Order, int>> key = x => a;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        Assert.IsType<MongoParameterExpression>(part.FieldRef);
    }

    [Fact]
    public void Composite_key_with_mixed_member_and_EF_Property_parts_binds()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { A = EF.Property<string>(x, nameof(Order.Country)), x.Region };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("A", parts[0].Name);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(parts[0].FieldRef).ElementName);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Composite_key_with_parameter_and_member_parts_binds()
    {
        var mongoQ = TestQuery();
        var a = 2;
        Expression<Func<Order, object>> key = x => new { A = a, x.Region };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("A", parts[0].Name);
        Assert.IsType<MongoParameterExpression>(parts[0].FieldRef);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Non_default_serialized_property_key_still_declines()
    {
        // A GroupBy key over a property with a non-default BsonRepresentation has no safe generic _id
        // readback (same reasoning HasDefaultKeySerialization's own doc comment gives for the ordinary member
        // case) — TryTranslateValue's AllFieldsDefaultSerialized check must still catch this once the
        // dispatch broadens, not just the narrower TryTranslateField path it replaces.
        using var db = SingleEntityDbContext.Create<OrderWithRepresentedKey>(builder =>
            builder.Entity<OrderWithRepresentedKey>().Property(o => o.Country).HasBsonRepresentation(BsonType.String));
        var entityType = db.Model.FindEntityType(typeof(OrderWithRepresentedKey))!;
        var mongoQ = new MongoQueryExpression(entityType);
        Expression<Func<OrderWithRepresentedKey, string>> key = x => x.Country;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Computed_key_part_flows_through_PriorGrouping_for_nested_GroupBy()
    {
        // Mirrors GroupBy_aggregate_followed_another_GroupBy_aggregate: the FIRST GroupBy's key has a computed
        // part; a second GroupBy nests on the first's flattened projection via PriorGrouping. This only
        // proves the binder-level plumbing accepts a computed first-level key alongside PriorGrouping — the
        // full nested-GroupBy shape is proven end-to-end by the spec suite (Task 2 of the SP1 plan).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> proj = g => new { g.Key };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        mongoQ.Select.SnapshotPriorGroupingForNestedGroupBy();
        Assert.NotNull(mongoQ.Select.PriorGrouping);

        Expression<Func<Order, string>> secondKey = x => x.Country;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, secondKey));
        Assert.NotNull(mongoQ.Select.PendingGroupKey);
    }
```

`OrderWithRepresentedKey` needs a small private entity added near the top of the test class, alongside the
existing `Order`/`OrderGroup`/`OrderKeyDto`/`KeyOnlyDto` private types:

```csharp
    private class OrderWithRepresentedKey
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
    }
```

Add `using MongoDB.Bson;` at the top of the file if not already present (check — `MongoDB.Bson` is already
imported for `ObjectId`, so `BsonType` is available from the same `using`).

- [ ] **Step 2: Run the test file to confirm the new/changed tests fail against today's code**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: `Computed_key_binds_via_TryTranslateValue`,
`Composite_key_with_computed_part_binds_via_TryTranslateValue`, `EF_Property_scalar_key_binds`,
`Captured_parameter_key_binds_as_parameter`, `Composite_key_with_mixed_member_and_EF_Property_parts_binds`,
`Composite_key_with_parameter_and_member_parts_binds`, and
`Computed_key_part_flows_through_PriorGrouping_for_nested_GroupBy` FAIL (the production code hasn't changed
yet). `Non_default_serialized_property_key_still_declines` PASSES already (both old and new code decline a
non-default-represented member) — that's expected; it's here to catch a REGRESSION in Step 4, not to be red now.

- [ ] **Step 3: Implement the production fix**

Replace `TryBindGroupKey`'s `switch` block in
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` (currently lines 66–107) with:

```csharp
        switch (keySelector.Body)
        {
            // A zero-argument new{} key (GroupBy(o => new { })) groups every row into a single group. Matched
            // on Arguments.Count, NOT Members: NewExpression.Members is null for EVERY non-anonymous-type
            // constructor call (e.g. new OrderKey(o.Country, o.Year) also has Members == null), so matching on
            // Members alone would ALSO bind a genuine multi-part constructor-based key as a zero-part one —
            // silently collapsing every row into one group. Arguments.Count == 0 correctly admits only a
            // true zero-member new{} (both presentations the compiler emits have zero constructor arguments)
            // and excludes any real key, which always has one argument per key part.
            case NewExpression { Arguments.Count: 0 }:
                break;

            case NewExpression { Members: { Count: > 0 } members } newExpr:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    // EF-322 SP1: each key part is translated via TryTranslateValue — the SAME general
                    // "any translatable value" method TryBindAccumulator already uses for an accumulator's
                    // operand (see this file's own remarks there) — which accepts a bare member, EF.Property,
                    // a computed expression (date-part, arithmetic, cast), a literal constant, or a captured
                    // query parameter, uniformly. A computed/parameter part has no backing IProperty, so
                    // HasDefaultKeySerialization's converter/BsonRepresentation check does not apply to it —
                    // TryTranslateValue's own AllFieldsDefaultSerialized already only checks the property of a
                    // genuine MongoFieldExpression leaf, exactly like the pre-existing scalar/composite cases
                    // did via HasDefaultKeySerialization, so no unsafe shape is newly admitted.
                    if (!translator.TryTranslateValue(newExpr.Arguments[i], out var partValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(members[i].Name, partValue));
                }

                break;

            // A bare member, EF.Property(...) call, computed expression (date-part/arithmetic/cast), literal
            // constant, or captured query parameter — TryTranslateValue resolves all of these uniformly. This
            // subsumes the old dedicated MemberExpression and ConstantExpression cases (both produce
            // byte-identical output through this same path — TryTranslateValue's own TranslateOperand bottoms
            // out at TryResolveMember for a member and at TranslateValue(node, forSerialization: null) for a
            // constant/parameter, matching what those cases built by hand). A genuine multi-argument
            // constructor call (NewExpression with Members == null, e.g. new OrderKey(o.Country, o.Year))
            // reaches here as the WHOLE NewExpression, which TryTranslateValue cannot translate — declines
            // correctly, unchanged from before.
            default:
                if (!translator.TryTranslateValue(keySelector.Body, out var scalarValue))
                    return false;
                parts.Add(new MongoGroupingKeyPart(null, scalarValue));
                break;
        }
```

Delete the now-unused `HasDefaultKeySerialization` calls from this method (they were only in the removed
`MemberExpression`/composite-loop branches) — but do NOT delete the `HasDefaultKeySerialization` method itself,
which stays in use by `MongoExpressionTranslator.AllFieldsDefaultSerialized` and `TryBindDistinctFromProjection`
elsewhere in this file.

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests in the file PASS, including every pre-existing test (nothing else in this file's behavior
changes) and every new/changed one from Step 1.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: generalize GroupBy key-selector translation via TryTranslateValue"
```

---

### Task 2: Verify the target spec-test methods against a real MongoDB, both modes

**Files:**
- Test (read-only, no changes expected — see Step 3 for the conditional exception):
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs`

**Interfaces:**
- Consumes: Task 1's `NativeGroupByBinder.TryBindGroupKey` change — this task is pure verification, no new
  production code.
- Produces: confirmation the SP1 slice is complete — the spec doc's exit criterion for SP1.

- [ ] **Step 1: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

If neither Docker nor `MONGODB_URI` is available, stop here and ask the user to start Docker or set
`MONGODB_URI` before continuing — the remaining steps need a real MongoDB.

- [ ] **Step 2: Run the 11 target methods under default (`Native`) mode**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_Property_Select_Key_Count|FullyQualifiedName~GroupBy_Property_Select_Key_with_constant|FullyQualifiedName~GroupBy_Property_Select_Sum_Min_Key_Max_Avg|FullyQualifiedName~GroupBy_aggregate_followed_another_GroupBy_aggregate|FullyQualifiedName~GroupBy_anonymous_key_type_mismatch_with_aggregate|FullyQualifiedName~GroupBy_with_grouping_key_DateTime_Day|FullyQualifiedName~GroupBy_param_Select_Sum_Min_Key_Max_Avg|FullyQualifiedName~GroupBy_param_with_element_selector_Select_Sum)" \
  --logger "console;verbosity=normal" > /tmp/sp1-native.log 2>&1
tail -30 /tmp/sp1-native.log
```

Expected: all pass (they already passed before this change, via the fallback — this step proves the DEFAULT
mode still gets the right answer now that it goes native, i.e. `Native == DriverLinq`).

- [ ] **Step 3: Diff each target method's `AssertMql` baseline; regenerate only the ones that changed**

For each of the 11 methods, check whether its current baseline (in
`NorthwindGroupByQueryMongoTest.cs`) still matches what Step 2 just asserted (a passing `AssertMql` means it
already matches — no action needed for that method). For any method whose test body shows a baseline mismatch
in the Step 2 log, regenerate just that one:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm the diff is a plausible MQL shape (a `$group`/`$project` pair mirroring the query), then rebuild and
rerun WITHOUT the env var to confirm green (per this repo's baseline-regeneration caveats — the rewritten test
still reports "failed" on the rewrite run itself; that's the signal a rewrite happened, not a problem).

- [ ] **Step 4: Run the same 11 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_Property_Select_Key_Count|FullyQualifiedName~GroupBy_Property_Select_Key_with_constant|FullyQualifiedName~GroupBy_Property_Select_Sum_Min_Key_Max_Avg|FullyQualifiedName~GroupBy_aggregate_followed_another_GroupBy_aggregate|FullyQualifiedName~GroupBy_anonymous_key_type_mismatch_with_aggregate|FullyQualifiedName~GroupBy_with_grouping_key_DateTime_Day|FullyQualifiedName~GroupBy_param_Select_Sum_Min_Key_Max_Avg|FullyQualifiedName~GroupBy_param_with_element_selector_Select_Sum)" \
  --logger "console;verbosity=normal" > /tmp/sp1-nativeonly.log 2>&1
tail -30 /tmp/sp1-nativeonly.log
```

Expected: all 11 methods (22 with async) now PASS under `NativeOnly` — previously they threw
`NativeTranslationNotSupportedException`. This is the slice's actual proof: these queries now execute via the
native `$group` pipeline, not the driver-LINQ fallback.

- [ ] **Step 5: Re-measure the whole class's `NativeOnly` failure count**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp1-full-nativeonly.log 2>&1
tail -15 /tmp/sp1-full-nativeonly.log
```

Expected: failure count drops from 72 to 50 (72 − 22). If it's not exactly 50, grep the log for `Failed
MongoDB` and diff the method names against the 36-method baseline list in the spec doc — some other method may
have regressed, or an unrelated method may have coincidentally started passing (investigate either case before
proceeding; don't just accept a different number).

```bash
grep -E "^\s*Failed MongoDB" /tmp/sp1-full-nativeonly.log | sed -E 's/^\s*Failed //' | sort -u
```

- [ ] **Step 6: Run `/test-all` (all three EF versions) before calling the slice done**

Invoke the `/test-all` skill (or manually run the build+test commands above for `Debug EF8` and `Debug EF9` too)
to confirm this change is version-agnostic, as expected (it touches no `#if EF8`/`EF9`/`EF10` code).

- [ ] **Step 7: Commit any baseline changes from Step 3** (skip if Step 3 found no diffs)

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
git commit -m "EF-322: regenerate GroupBy AssertMql baselines for SP1 key-selector generalization"
```

---

## After this plan

This plan covers SP1 only (11 of the 30 in-scope tests). SP2–SP9, per the design doc, are separate follow-up
plans written and executed one at a time as each slice starts — the same stacked-PR convention this repo
already uses elsewhere (squash each slice onto `NativeQueryOngoing`, one ticket per slice). Do not start SP2's
plan until SP1 is merged and its `NativeOnly` failure count (50, confirmed in Task 2 Step 5) is the new
baseline for SP2 to measure against.
