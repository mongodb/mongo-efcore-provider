# Native nested (2-level) reference SelectMany Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make EF's second, chained `SelectMany` in `from o in Owners from m in o.Mids from l in m.Leaves select ...` (a nested, exactly-2-level, cross-collection **reference** `SelectMany`) go native — two chained `$lookup`+`$unwind` stages — for a projected (`new{o.Name,m.Tag,l.Label}`) and a bare-entity (`select l`) result, instead of hard-failing at `TranslateSelectMany`'s terminal guard.

**Architecture:** `MongoSelectDefinition.UnwindSource` (a single slot) becomes an ordered `UnwindSources` list, with `UnwindSource` retained as a read-only "last source" shim so every existing single-unwind-source consumer (lowerer, whole-element gate, both projection binders) keeps working unchanged. The terminal guard in `TranslateSelectMany` gets a narrow carve-out that recognizes a second SelectMany correlating off the first's own unwound element (`ti.Inner.<pk>`, a transparent-identifier-rooted member chain) by rewriting that chain onto a synthetic parameter and reusing the existing, unmodified `NativeCorrelationMatcher`. The second level's `$lookup` is registered with its `LocalField` prefixed by the first level's lookup alias (`_lookup_Mids._id`); the existing lookup-dependency sort (`MongoQueryExpression.GetPendingLookups`) already orders such a transitive lookup after the one it depends on, so lowering needs **no code changes**. The trailing projection binder (`TryBindTransparentIdentifierProjection`) is generalized from 2 scopes to N scopes by counting `Outer`/`Inner` hops against the unwind-source count.

**Tech Stack:** C#, EF Core 8/9/10 (multi-targeted via build configuration), MongoDB C# driver, xUnit (unit + functional), MongoDB aggregation pipeline (`$lookup`/`$unwind`/`$project`/`$replaceRoot`).

## Global Constraints

- Branch `EF-347-selectmany-nested-reference`, current tip `fde640d` (adds the design/spike docs) on top of `4fa2162` (`EF-347: Native owned correlated-beyond-outer inner-filter SelectMany`) — confirmed via `git merge-base --is-ancestor 4fa2162 HEAD`.
- No `#if EF8`/`#if EF9`/`#if EF10` anywhere in this slice — the spike (`.superpowers/sdd/EF-347-nested-ref-spike.md`) confirmed byte-identical EF8/EF9/EF10 dumps.
- `<Nullable>enable</Nullable>` on all `src/` code — annotate new members accordingly.
- Preserve file BOMs on every edited file.
- All new/touched types stay `internal` (or `private`) — no public API surface changes.
- Unit tests use plain xUnit `Assert.*` (no FluentAssertions anywhere in the test projects).
- Run the functional suite with both `MONGODB_URI` and `ATLAS_URI` unset (TestContainers boots an isolated `mongodb/mongodb-atlas-local` container per run).
- No-partial-mutation-on-decline: every binder in this slice must leave `MongoQueryExpression`/`MongoSelectDefinition` completely untouched when it returns `false`.
- A nested reference `SelectMany` has **no driver-LINQ oracle** (the driver's own LINQ v3 provider rejects cross-collection `SelectMany` outright, confirmed for the single-level case and unchanged for the nested case) — every new functional test proves nativeness via `MongoQueryMode.NativeOnly` succeeding plus an expected in-memory-computed result set. There is **no** `Native == DriverLinq` parity assertion anywhere in this slice, and no "falls back gracefully" assertion either — every decline in this slice hard-fails in **every** mode (`Native`/`DriverLinq`/`NativeOnly`).
- Single-level SelectMany behavior must stay **byte-identical** after the `UnwindSources` refactor (Task 1) — the existing `NativeSelectManyTests`/`NativeSelectManyBinderTests`/`MongoSelectLowererTests` suites staying green, unmodified in assertions (only mechanical `UnwindSource = X` → `AddUnwindSource(X)` call-site edits), is the additive proof.
- The controller runs the 3-version `/test-all` skill **in the foreground** before finalization — do not background it.
- Finalization squashes every commit above `4fa2162` into one, with a `-presquash` backup branch, and the user drives the fast-forward push of `origin/NativeQueryOngoing`.

---

## Task 1: Unwind-chain IR — `MongoSelectDefinition.UnwindSources`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs:251,267-268,280-282`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs:118,156,264`
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs:366,389,420,435,451,468,746`
- Modify: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs:309,337,362,401,438,459,478`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs`

**Interfaces:**
- Produces: `MongoSelectDefinition.UnwindSources` (`IReadOnlyList<MongoUnwindSource>`), `MongoSelectDefinition.AddUnwindSource(MongoUnwindSource source)` (`internal void`), `MongoSelectDefinition.IsSingleReferenceUnwindTerminalOnly` (`internal bool`) — all consumed by Tasks 2–4.
- Consumes: nothing new — `MongoUnwindSource`/`MongoUnwindSourceKind` (existing, `Expressions/MongoUnwindSource.cs`).

- [ ] **Step 1: Write failing unit tests for the new IR members**

Add to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (after the existing `Route_is_Fallback_even_when_grouping_set_if_marked_not_native` test, before the closing `}`):

```csharp
    // ── UnwindSources chain (EF-347 nested-reference slice) ─────────────────────────

    [Fact]
    public void New_select_has_no_unwind_sources_and_null_UnwindSource_shim()
    {
        var select = new MongoSelectDefinition();

        Assert.Empty(select.UnwindSources);
        Assert.Null(select.UnwindSource);
        Assert.False(select.HasTerminalOperator);
    }

    [Fact]
    public void AddUnwindSource_appends_and_UnwindSource_shim_reads_the_last_one()
    {
        var select = new MongoSelectDefinition();
        var first = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        var second = MongoUnwindSource.Reference("_lookup_Leaves", innerEntityType: null!, lookup: null!);

        select.AddUnwindSource(first);
        Assert.Same(first, select.UnwindSource); // single source: shim == that source
        Assert.True(select.HasTerminalOperator);

        select.AddUnwindSource(second);
        Assert.Equal(2, select.UnwindSources.Count);
        Assert.Same(first, select.UnwindSources[0]);
        Assert.Same(second, select.UnwindSources[1]);
        Assert.Same(second, select.UnwindSource); // shim now reads the LAST source
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_true_for_exactly_one_reference_source()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));

        Assert.True(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_for_owned_source()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Owned("Items", innerEntityType: null!));

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_once_two_sources_are_chained()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Leaves", innerEntityType: null!, lookup: null!));

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_when_a_group_is_also_set()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));
        select.IsGroupBy = true;

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }
```

- [ ] **Step 2: Run the new tests to verify they fail (compile error — members don't exist yet)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: build FAILS — `'MongoSelectDefinition' does not contain a definition for 'UnwindSources'/'AddUnwindSource'/'IsSingleReferenceUnwindTerminalOnly'`, and `MongoUnwindSource.Reference`'s 3rd parameter is currently non-nullable `LookupExpression` so the `lookup: null!` call sites above compile fine (the `!` suppresses the warning) — the only real failures are the missing members.

- [ ] **Step 3: Implement the `UnwindSources` chain on `MongoSelectDefinition`**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`, replace the existing single-slot block (currently at lines 251, 267-268, 280-282):

Replace:
```csharp
    internal bool HasTerminalOperator => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSource != null;
```
with:
```csharp
    internal bool HasTerminalOperator => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0;
```

Replace:
```csharp
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSource == null && Projection.Count == 0;
```
with:
```csharp
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 && Projection.Count == 0;
```

Replace:
```csharp
    /// <summary>Set when this select is a terminal owned-collection SelectMany (EF-347 slice 3): the element
    /// path to $unwind before the result-selector $project. Route stays Projection (the result is a projection).</summary>
    internal MongoUnwindSource? UnwindSource { get; set; }
```
with:
```csharp
    private readonly List<MongoUnwindSource> _unwindSources = [];

    /// <summary>
    /// The ordered chain of terminal SelectMany unwind sources (EF-347 nested-reference slice). Index 0 is
    /// the first (outermost) SelectMany's unwind; index 1, when present, is a SECOND, chained SelectMany's
    /// own unwind — correlated off the first's unwound element (see
    /// <see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>). Every
    /// existing single-level SelectMany populates exactly one entry (byte-identical behavior, preserved via
    /// the <see cref="UnwindSource"/> shim below); a 2-level nested reference SelectMany populates two.
    /// Write only via <see cref="AddUnwindSource"/> — there is no direct setter, so every write site is
    /// grep-visible.
    /// </summary>
    public IReadOnlyList<MongoUnwindSource> UnwindSources => _unwindSources;

    /// <summary>Appends a new terminal SelectMany unwind source to the chain (EF-347 nested-reference slice).</summary>
    internal void AddUnwindSource(MongoUnwindSource source) => _unwindSources.Add(source);

    /// <summary>
    /// The LAST (most-recently-appended) terminal SelectMany unwind source, or <see langword="null"/> when
    /// none is set — a read-only "last source" shim over <see cref="UnwindSources"/> (EF-347 nested-reference
    /// slice) that keeps every existing single-source read site (the lowerer, the whole-element gate in
    /// <c>TranslateSelect</c>, both projection binders) working unchanged: every current consumer cares about
    /// the TERMINAL unwind source, which for a single-level SelectMany is its only source and for the 2-level
    /// nested case is the SECOND (innermost) source. There is no setter — write via
    /// <see cref="AddUnwindSource"/>.
    /// </summary>
    internal MongoUnwindSource? UnwindSource => _unwindSources.Count > 0 ? _unwindSources[^1] : null;

    /// <summary>
    /// <see langword="true"/> when the terminal seen so far on this select is EXACTLY a single REFERENCE
    /// unwind source, with no grouping/distinct/set-op mixed in (EF-347 nested-reference slice). This is the
    /// narrow carve-out condition <c>TranslateSelectMany</c> checks BEFORE its ordinary
    /// <see cref="HasTerminalOperator"/> guard: only when this holds does a SECOND, chained SelectMany get a
    /// chance at nested-reference recognition (<see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>);
    /// every other post-terminal shape (a 2nd SelectMany after GroupBy/Distinct/a set-op/an OWNED unwind, or a
    /// query already 2+ levels deep) still hits the unmodified guard exactly as before this slice.
    /// </summary>
    internal bool IsSingleReferenceUnwindTerminalOnly
        => UnwindSources.Count == 1 && UnwindSources[0].Kind == MongoUnwindSourceKind.Reference
           && !IsGroupBy && !IsDistinct && !IsSetOp && Grouping == null;
```

- [ ] **Step 4: Update the 3 write call-sites in `NativeSelectManyBinder.cs`**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, at each of lines 118, 156, and 264, replace:
```csharp
        mongoQ.Select.UnwindSource = unwind;
```
with:
```csharp
        mongoQ.Select.AddUnwindSource(unwind);
```
(Three occurrences: in `TryBind`, `TryBindBareNavUnwind`, and `TryBindReferenceNavUnwind` — the assignment is the last mutation before each method's own `return true;`.)

- [ ] **Step 5: Update the 7 write call-sites in `NativeSelectManyBinderTests.cs`**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`, at each of lines 366, 389, 420, 435, 451, 468, and 746, replace:
```csharp
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));
```
(six occurrences of this exact line) and, at line 746-747:
```csharp
        mongoQ.Select.UnwindSource =
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagNav), tagNav.TargetEntityType, lookup);
```
with the `AddUnwindSource` equivalent in each case, e.g.:
```csharp
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));
```
and:
```csharp
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagNav), tagNav.TargetEntityType, lookup));
```

- [ ] **Step 6: Update the 7 write call-sites in `MongoSelectLowererTests.cs`**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`, at each of lines 309, 337, 362, 401, 438, 459, and 478, apply the same mechanical change: `query.Select.UnwindSource = X;` → `query.Select.AddUnwindSource(X);` (`X` varies per call site — read each line's existing right-hand side and wrap it in `AddUnwindSource(...)` unchanged).

- [ ] **Step 7: Run the new + updated unit tests to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests|FullyQualifiedName~NativeSelectManyBinderTests|FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS, 0 failures.

- [ ] **Step 8: Prove byte-identical single-level behavior — the additive proof for this task**

Run the full existing functional SelectMany suite:
`dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests"`
(with `MONGODB_URI` and `ATLAS_URI` unset)
Expected: PASS, 0 failures — every existing single-level owned/reference SelectMany test (unfiltered, filtered-inner, correlated-beyond-FK/outer, bare-entity, all three user spellings) is unaffected by the shim.

- [ ] **Step 9: Verify EF8 still builds (multi-version guard)**

Run: `dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF8"`
Expected: build succeeds with 0 errors (this task touches no `#if`-guarded code, so this is a quick sanity check, not expected to surface anything).

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs
git commit -m "EF-347: UnwindSources chain IR (behavior-preserving refactor)"
```

---

## Task 2: Level-2 correlation recognition — `TryBindNestedReferenceNavUnwind`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.UnwindSources` / `.AddUnwindSource` (Task 1); `NativeCorrelationMatcher.TryMatchCorrelatedCollection(Expression whereBody, IEntityType outerEntityType, ParameterExpression outerParameter, IEntityType targetEntityType, bool requireEmbedded, out INavigation navigation)` (existing, **unchanged**); `LookupExpression(INavigation navigation, bool forceUnwind = false)` constructor + settable `LocalField`/`As` properties (existing, **unchanged**); `LookupExpression.GetLookupAlias(IReadOnlyNavigationBase navigation)` (existing); `MongoQueryExpression.AddLookup(LookupExpression)` (existing); `MongoUnwindSource.Reference(string innerScopePath, IEntityType innerEntityType, LookupExpression lookup)` (existing).
- Produces: `NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)` (`internal static bool`) — consumed by Task 4's QMTEV carve-out.

- [ ] **Step 1: Write failing unit tests for the new binder**

Add to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`, immediately before the final closing `}` of the class (after the existing `TryBindReferenceNavUnwind_reference_source_flows_through_two_scope_projection_binder` test):

```csharp
    // ── TryBindNestedReferenceNavUnwind: 2-level chained reference SelectMany (EF-347 nested-reference) ──
    // Spike-confirmed (.superpowers/sdd/EF-347-nested-ref-spike.md): the SECOND SelectMany's collectionSelector
    // is Queryable.Where(EntityQueryRootExpression<Leaf>, l => ti.Inner.Id == l.MidId) — the SAME correlated-
    // subquery shape TryBindReferenceNavUnwind already parses, except the correlation's outer-key side is
    // ti.Inner.<pk> (a transparent-identifier-rooted member chain), not a bare parameter.

    private class NestedOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<NestedMid> Mids { get; set; } = [];
    }

    private class NestedMid
    {
        public int Id { get; set; }
        public string Tag { get; set; } = "";
        public int OwnerId { get; set; }
        public List<NestedLeaf> Leaves { get; set; } = [];
    }

    private class NestedLeaf
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public int MidId { get; set; }
    }

    private class OwnerMidTi
    {
        public NestedOwner Outer { get; set; } = null!;
        public NestedMid Inner { get; set; } = null!;
    }

    private static MongoQueryExpression NestedTestQuery()
    {
        using var db = SingleEntityDbContext.Create<NestedOwner>(mb =>
        {
            mb.Entity<NestedMid>();
            mb.Entity<NestedOwner>().HasMany(o => o.Mids).WithOne().HasForeignKey(m => m.OwnerId);
            mb.Entity<NestedLeaf>();
            mb.Entity<NestedMid>().HasMany(m => m.Leaves).WithOne().HasForeignKey(l => l.MidId);
        });
        var entityType = db.Model.FindEntityType(typeof(NestedOwner))!;
        return new MongoQueryExpression(entityType);
    }

    // Simulates level 1 already having bound (TryBindReferenceNavUnwind, unmodified, run against o.Mids).
    private static void BindLevel1(MongoQueryExpression mongoQ)
    {
        var midsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(NestedOwner.Mids))!;
        var lookup = new LookupExpression(midsNav, forceUnwind: true);
        mongoQ.AddLookup(lookup);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(midsNav), midsNav.TargetEntityType, lookup));
    }

    private static IEntityType LeafEntityType(MongoQueryExpression mongoQ)
        => mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(NestedOwner.Mids))!.TargetEntityType
            .FindNavigation(nameof(NestedMid.Leaves))!.TargetEntityType;

    // Queryable.Where(EntityQueryRootExpression<Leaf>, l => ti.Inner.<pk> == l.<fk>) — the level-2 spike shape.
    private static LambdaExpression NestedLeavesCorrelatedSelector(IEntityType leafEntityType, ParameterExpression ti)
    {
        var lParam = Expression.Parameter(typeof(NestedLeaf), "l");
        var predicate = Expression.Lambda(
            Expression.Equal(
                Expression.Property(Expression.Property(ti, nameof(OwnerMidTi.Inner)), nameof(NestedMid.Id)),
                Expression.Property(lParam, nameof(NestedLeaf.MidId))),
            lParam);
        var whereCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [typeof(NestedLeaf)],
            new EntityQueryRootExpression(leafEntityType), Expression.Quote(predicate));
        return Expression.Lambda(whereCall, ti);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_binds_second_lookup_scoped_under_first()
    {
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var leafEntityType = LeafEntityType(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(leafEntityType, ti);

        Assert.True(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));

        Assert.Equal(2, mongoQ.Select.UnwindSources.Count);
        var level2 = mongoQ.Select.UnwindSources[1];
        Assert.Equal(MongoUnwindSourceKind.Reference, level2.Kind);
        Assert.Equal("_lookup_Leaves", level2.InnerScopePath);
        Assert.Same(leafEntityType, level2.InnerEntityType);
        Assert.NotNull(level2.Lookup);
        Assert.True(level2.Lookup!.ForceUnwind);
        Assert.Equal("_lookup_Mids._id", level2.Lookup.LocalField);
        Assert.Null(level2.Filter); // unfiltered — this slice's scope

        // The lookup-dependency sort (MongoQueryExpression.GetPendingLookups, unmodified) must already order
        // the level-1 lookup before level-2's — no lowering change needed for this.
        var lookups = mongoQ.Lookups;
        Assert.Equal(2, lookups.Count);
        Assert.Equal("_lookup_Mids", lookups[0].As);
        Assert.Equal("_lookup_Leaves", lookups[1].As);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_without_a_prior_reference_source()
    {
        var mongoQ = NestedTestQuery(); // no level-1 bind at all
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(LeafEntityType(mongoQ), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Empty(mongoQ.Select.UnwindSources);
        Assert.Empty(mongoQ.Lookups);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_when_prior_source_is_owned()
    {
        var mongoQ = NestedTestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Mids", LeafEntityType(mongoQ))); // wrong Kind
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(LeafEntityType(mongoQ), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Single(mongoQ.Select.UnwindSources); // untouched — no partial mutation
        Assert.Empty(mongoQ.Lookups);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_for_non_where_body()
    {
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = Expression.Lambda(new EntityQueryRootExpression(LeafEntityType(mongoQ)), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Equal(1, mongoQ.Select.UnwindSources.Count); // untouched
        Assert.Empty(mongoQ.Lookups);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_when_correlation_is_not_ti_inner_rooted()
    {
        // l.MidId == l.MidId (a self-comparison on the inner param, not ti.Inner.<pk>) never resolves a
        // navigation off the level-1 target — must decline, not crash or mis-bind.
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var lParam = Expression.Parameter(typeof(NestedLeaf), "l");
        var predicate = Expression.Lambda(
            Expression.Equal(Expression.Property(lParam, nameof(NestedLeaf.MidId)), Expression.Property(lParam, nameof(NestedLeaf.MidId))),
            lParam);
        var whereCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [typeof(NestedLeaf)],
            new EntityQueryRootExpression(LeafEntityType(mongoQ)), Expression.Quote(predicate));
        var collectionSelector = Expression.Lambda(whereCall, ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Equal(1, mongoQ.Select.UnwindSources.Count);
        Assert.Empty(mongoQ.Lookups);
    }
```

- [ ] **Step 2: Run the new tests to verify they fail (compile error)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryBindNestedReferenceNavUnwind"`
Expected: build FAILS — `'NativeSelectManyBinder' does not contain a definition for 'TryBindNestedReferenceNavUnwind'`.

- [ ] **Step 3: Implement `TryBindNestedReferenceNavUnwind` and its rewriter**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, add the following method immediately after `TryBindReferenceNavUnwind` (after its closing `}`, before the `TrySplitCorrelation` region — i.e. right after line 266):

```csharp
    /// <summary>
    /// Binds the SECOND level of a nested (2-level) cross-collection reference <c>SelectMany</c> (EF-347) —
    /// <c>from o in q from m in o.Mids from l in m.Leaves select ...</c>. Spike-confirmed
    /// (<c>.superpowers/sdd/EF-347-nested-ref-spike.md</c>): EF's nav-expansion produces this as a SECOND,
    /// sequentially-chained <c>Queryable.Where(EntityQueryRootExpression&lt;Leaf&gt;, l => ti.Inner.Id ==
    /// l.MidId)</c> correlated subquery — structurally identical to the single-level reference-SelectMany
    /// shape <see cref="TryBindReferenceNavUnwind"/> already parses, except the correlation's outer-key side
    /// is a TRANSPARENT-IDENTIFIER-ROOTED member access <c>ti.Inner.&lt;pk&gt;</c> (<c>ti</c> is this
    /// SelectMany's own outer parameter, bound by nav-expansion to level 1's <c>TransparentIdentifier(Outer,
    /// Inner)</c> result; <c>.Inner</c> is the level-1 unwound element) rather than a bare parameter. Rather
    /// than teach <see cref="NativeCorrelationMatcher"/> a new shape, this rewrites every <c>ti.Inner</c>
    /// occurrence in the predicate onto a synthetic parameter of the level-1 target entity type first, then
    /// reuses <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> completely UNCHANGED —
    /// identical to how the single-level binder resolves its own FK correlation, just fed a pre-rewritten
    /// predicate.
    /// <para>
    /// Requires the caller to have already confirmed exactly one prior REFERENCE unwind source
    /// (<see cref="MongoSelectDefinition.IsSingleReferenceUnwindTerminalOnly"/> — see the QMTEV carve-out).
    /// Resolves the navigation OFF that source's <see cref="MongoUnwindSource.InnerEntityType"/> (the level-1
    /// target, e.g. Mid), registers a SECOND <c>ForceUnwind</c> <see cref="LookupExpression"/> whose
    /// <see cref="LookupExpression.LocalField"/> is overridden to be scoped under the level-1 source's own
    /// <see cref="MongoUnwindSource.InnerScopePath"/> (e.g. <c>_lookup_Mids._id</c> — the existing
    /// lookup-dependency sort in <see cref="MongoQueryExpression.GetPendingLookups"/> already orders such a
    /// transitive lookup after the one it depends on, so no lowering change is needed), and appends a second
    /// <see cref="MongoUnwindSource"/>. No partial mutation on decline.
    /// </para>
    /// <para>
    /// Unfiltered only, matching this slice's scope: unlike <see cref="TryBindReferenceNavUnwind"/> this does
    /// NOT peel outer <c>Where</c> layers — an inner filter at level 2 nav-expands to an outer <c>Where</c>
    /// wrapping the FK-correlation <c>Where</c>, which does not match the single-<c>Where</c> shape checked
    /// here, so a filtered level 2 declines structurally (out of scope, hard-fails, exactly as intended).
    /// </para>
    /// </summary>
    internal static bool TryBindNestedReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var sources = mongoQ.Select.UnwindSources;
        if (sources.Count != 1 || sources[0].Kind != MongoUnwindSourceKind.Reference)
            return false;
        var level1Source = sources[0];

        var ti = collectionSelector.Parameters[0];
        var body = UnwrapAsQueryable(collectionSelector.Body);

        if (body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var whereDecl },
                Arguments: [EntityQueryRootExpression root, var predicateArg]
            }
            || whereDecl != typeof(System.Linq.Queryable))
            return false;

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        // Rewrite every `ti.Inner` occurrence onto a synthetic parameter of the level-1 target entity type
        // (e.g. Mid), so the existing single-level matcher (which expects a bare-parameter-rooted outer side)
        // recognizes the correlation unchanged.
        var level1Param = Expression.Parameter(level1Source.InnerEntityType.ClrType, "l1");
        var rewritten = new TransparentIdentifierInnerRewriter(ti, level1Param).Visit(predicate.Body);

        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                rewritten, level1Source.InnerEntityType, level1Param, root.EntityType, requireEmbedded: false, out var navigation))
            return false;

        var scope2 = LookupExpression.GetLookupAlias(navigation);
        var lookup2 = new LookupExpression(navigation, forceUnwind: true);
        lookup2.LocalField = level1Source.InnerScopePath + "." + lookup2.LocalField;
        mongoQ.AddLookup(lookup2);
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Reference(scope2, navigation.TargetEntityType, lookup2));
        return true;
    }

    /// <summary>
    /// Rewrites every <c>tiParam.Inner</c> occurrence (a <see cref="MemberExpression"/> whose <c>Expression</c>
    /// is exactly <paramref name="tiParam"/> and whose member name is <c>"Inner"</c>) onto
    /// <paramref name="replacement"/>. Used by <see cref="TryBindNestedReferenceNavUnwind"/> to translate the
    /// level-2 correlation's transparent-identifier-rooted outer side (<c>ti.Inner.&lt;pk&gt;</c>) into a plain
    /// bare-parameter-rooted member access the existing <see cref="NativeCorrelationMatcher"/> already
    /// recognizes.
    /// </summary>
    private sealed class TransparentIdentifierInnerRewriter(ParameterExpression tiParam, ParameterExpression replacement)
        : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => node.Expression == tiParam && node.Member.Name == "Inner"
                ? replacement
                : base.VisitMember(node);
    }
```

- [ ] **Step 4: Run the new tests to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryBindNestedReferenceNavUnwind"`
Expected: PASS, 0 failures.

- [ ] **Step 5: Run the full unit suite to confirm no regressions**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"`
Expected: PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: level-2 nested-reference correlation recognition"
```

---

## Task 3: N-scope projection binder — generalize `TryBindTransparentIdentifierProjection`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs:418-460`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.UnwindSources` (Task 1); `MongoExpressionTranslator(IEntityType)` 1-arg constructor + `TryTranslateField(Expression, out MongoFieldExpression?)` (existing, unchanged); `MongoFieldExpression(IProperty property, string elementName)` constructor + `.Property`/`.ElementName` (existing); `MongoProjection(string Alias, MongoExpression Expression)` record struct (existing).
- Produces: `NativeSelectManyBinder.TryBindTransparentIdentifierProjection(MongoQueryExpression mongoQ, LambdaExpression selector)` — **same signature as before**, now N-scope-aware; consumed unchanged by `TranslateSelect`'s pending-SelectMany-projection branch (`MongoQueryableMethodTranslatingExpressionVisitor.cs:215-221`) and by Task 4's end-to-end tests.

- [ ] **Step 1: Write failing unit test for a 3-scope (doubly-nested) projection**

Add to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`, in the `TryBindTransparentIdentifierProjection` region (immediately after `TryBindTransparentIdentifierProjection_shared_member_name_resolves_by_scope_not_name`, before the `_no_unwind_source_returns_false` test):

```csharp
    private class DoublyNestedTi
    {
        public TransparentIdentifier Outer { get; set; } = null!; // TI(Outer=Owner, Inner=Item) — level 1
        public Tag Inner { get; set; } = null!;                   // level 2's own unwound element
    }

    private class TripleProjected
    {
        public string OwnerName { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string TagLabel { get; set; } = "";
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_binds_three_scope_projection()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));
        var tagsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;
        var tagLookup = new LookupExpression(tagsNav, forceUnwind: true);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagsNav), tagsNav.TargetEntityType, tagLookup));

        var ti = Expression.Parameter(typeof(DoublyNestedTi), "ti");
        var outerOuter = Expression.Property(Expression.Property(ti, nameof(DoublyNestedTi.Outer)), nameof(TransparentIdentifier.Outer));
        var outerInner = Expression.Property(Expression.Property(ti, nameof(DoublyNestedTi.Outer)), nameof(TransparentIdentifier.Inner));
        var inner = Expression.Property(ti, nameof(DoublyNestedTi.Inner));

        var body = Expression.MemberInit(Expression.New(typeof(TripleProjected)),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.OwnerName))!,
                Expression.Property(outerOuter, nameof(Owner.Name))),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.ItemName))!,
                Expression.Property(outerInner, nameof(Item.Name))),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.TagLabel))!,
                Expression.Property(inner, nameof(Tag.Label))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));

        var ownerP = mongoQ.Select.Projection.Single(p => p.Alias == "OwnerName");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(ownerP.Expression).ElementName);
        var itemP = mongoQ.Select.Projection.Single(p => p.Alias == "ItemName");
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(itemP.Expression).ElementName);
        var tagP = mongoQ.Select.Projection.Single(p => p.Alias == "TagLabel");
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(tagP.Expression).ElementName);
    }

    // Wraps DoublyNestedTi one level further, so a chain 1 hop deeper than any valid 2-source shape can be
    // constructed with real static types (ti3.Outer.Outer.Outer.Name — 3 "Outer" hops under a 2-source chain).
    private class TripleNestedTi
    {
        public DoublyNestedTi Outer { get; set; } = null!;
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_chain_deeper_than_source_count_returns_false()
    {
        // ti3.Outer.Outer.Outer.Name — 3 "Outer" hops under a 2-source chain (a would-be 3rd-nesting-level
        // leaf). TryResolveScopeDepth must reject path.Count > sourceCount before even checking the hop
        // pattern. This is the unit-level proof of the same boundary Task 5's functional 3-level decline test
        // exercises end-to-end (there, the shape never even reaches this binder — the QMTEV carve-out's own
        // IsSingleReferenceUnwindTerminalOnly check already declines a 3rd chained SelectMany; this test
        // isolates the projection-binder half of that boundary in case the two are ever exercised separately).
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));
        var tagsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;
        var tagLookup = new LookupExpression(tagsNav, forceUnwind: true);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagsNav), tagsNav.TargetEntityType, tagLookup));

        var ti3 = Expression.Parameter(typeof(TripleNestedTi), "ti3");
        var threeHops = Expression.Property(
            Expression.Property(
                Expression.Property(Expression.Property(ti3, nameof(TripleNestedTi.Outer)), nameof(DoublyNestedTi.Outer)),
                nameof(TransparentIdentifier.Outer)),
            nameof(Owner.Name));
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!, threeHops));
        var selector = Expression.Lambda(body, ti3);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryBindTransparentIdentifierProjection_binds_three_scope_projection|FullyQualifiedName~TryBindTransparentIdentifierProjection_chain_deeper_than_source_count_returns_false"`
Expected: the FIRST test FAILS — `TryBindTransparentIdentifierProjection` returns `false`/binds incorrectly for the 3-scope shape under the current 2-scope-only implementation (the `argExpr is MemberExpression { Expression: MemberExpression scopeAccess }` pattern requires `scopeAccess.Expression == ti` exactly, which `ti.Outer.Outer` and `ti.Inner` chains violate for the `OwnerName`/`ItemName` leaves). The SECOND test (the deeper-than-source-count decline) already PASSES even before Step 3's rewrite, since the current implementation also returns `false` for that shape (for a different, soon-to-be-superseded reason) — that is fine; it becomes a genuine regression guard once Step 3 lands.

- [ ] **Step 3: Rewrite `TryBindTransparentIdentifierProjection` to walk N scopes**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, replace the entire body of `TryBindTransparentIdentifierProjection` (lines 418-460) — **keep its XML doc comment and signature unchanged**, replace only the method body:

```csharp
    internal static bool TryBindTransparentIdentifierProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        var sources = mongoQ.Select.UnwindSources;
        if (sources.Count == 0 || mongoQ.Select.Projection.Count > 0)
            return false;
        if (selector.Parameters.Count != 1)
            return false;
        var ti = selector.Parameters[0];

        if (!TryReadProjection(selector.Body, out var members))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        // One translator (+ synthetic re-rooting parameter) per scope: index 0 = the query root/owner, index
        // k (1..sources.Count) = UnwindSources[k-1] (the k-th SelectMany level's own unwound element).
        var translators = new MongoExpressionTranslator[sources.Count + 1];
        var scopeParams = new ParameterExpression[sources.Count + 1];
        translators[0] = new MongoExpressionTranslator(outerEntityType);
        scopeParams[0] = Expression.Parameter(outerEntityType.ClrType, "s0");
        for (var i = 0; i < sources.Count; i++)
        {
            translators[i + 1] = new MongoExpressionTranslator(sources[i].InnerEntityType);
            scopeParams[i + 1] = Expression.Parameter(sources[i].InnerEntityType.ClrType, "s" + (i + 1));
        }

        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            if (argExpr is not MemberExpression member
                || !TryResolveScopeDepth(member.Expression, ti, sources.Count, out var scopeIndex))
                return false;

            var rerooted = Expression.MakeMemberAccess(scopeParams[scopeIndex], member.Member);
            if (!translators[scopeIndex].TryTranslateField(rerooted, out var field))
                return false;

            if (scopeIndex > 0)
                field = new MongoFieldExpression(field.Property, sources[scopeIndex - 1].InnerScopePath + "." + field.ElementName);

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, field));
        }

        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    /// <summary>
    /// Peels a chain of <c>ti.Outer</c>/<c>ti.Outer.Outer</c>/…/<c>ti.Inner</c> member accesses down to the
    /// bare <paramref name="ti"/> parameter, and resolves which scope it refers to (EF-347 nested-reference
    /// slice — generalizes the 2-scope <c>ti.Outer</c>/<c>ti.Inner</c> shape to N scopes). Given
    /// <paramref name="sourceCount"/> chained unwind sources, the doubly(-or-more)-nested transparent
    /// identifier's <c>k</c>-th level's own element is reached via <c>(sourceCount - k)</c> leading
    /// <c>"Outer"</c> hops followed by exactly one trailing <c>"Inner"</c> hop; the query root (owner) is
    /// reached via exactly <paramref name="sourceCount"/> <c>"Outer"</c> hops and no <c>"Inner"</c> at all.
    /// <paramref name="scopeIndex"/> is <c>0</c> for the root, or <c>k</c> (1-based) for
    /// <c>UnwindSources[k-1]</c>. Returns <see langword="false"/> — declining cleanly — for any chain that
    /// does not terminate exactly at <paramref name="ti"/>, is empty, exceeds <paramref name="sourceCount"/>
    /// hops, or does not match either of the two valid shapes above (e.g. a would-be 3rd-level leaf under a
    /// 2-source chain, or a bare scope-object selection with no trailing member).
    /// </summary>
    private static bool TryResolveScopeDepth(Expression scopeAccess, ParameterExpression ti, int sourceCount, out int scopeIndex)
    {
        scopeIndex = -1;
        var path = new List<string>();
        var current = scopeAccess;
        while (current is MemberExpression { Member.Name: "Outer" or "Inner" } hop)
        {
            path.Add(hop.Member.Name);
            current = hop.Expression;
        }

        if (current != ti || path.Count == 0 || path.Count > sourceCount)
            return false;

        path.Reverse(); // now ordered outward-from-ti: path[0] is the first hop off ti.

        if (path[^1] == "Inner" && path.Take(path.Count - 1).All(h => h == "Outer"))
        {
            scopeIndex = sourceCount - path.Count + 1;
            return true;
        }

        if (path.Count == sourceCount && path.All(h => h == "Outer"))
        {
            scopeIndex = 0;
            return true;
        }

        return false;
    }
```

Note: `TryTranslateScopedField` (the old 2-scope helper) stays in the file **unchanged** — it is still used by `TryBind` (the inner-`Select` owned form, unaffected by this task).

- [ ] **Step 4: Run the new tests to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~TryBindTransparentIdentifierProjection"`
Expected: PASS, 0 failures — **including every pre-existing 2-scope test in this region** (`_binds_two_scope_projection`, `_shared_member_name_resolves_by_scope_not_name`, `_no_unwind_source_returns_false`, `_bare_scope_leaf_returns_false`, `_computed_leaf_returns_false`, `_member_off_neither_scope_returns_false`, `_entity_valued_leaf_returns_false`, and `TryBindReferenceNavUnwind_reference_source_flows_through_two_scope_projection_binder`) — this is the byte-identical-for-N=1 proof for this task.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"`
Expected: PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: generalize TryBindTransparentIdentifierProjection to N scopes"
```

---

## Task 4: QMTEV wiring + functional fixture + first end-to-end native test

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1420-1470`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.IsSingleReferenceUnwindTerminalOnly` (Task 1); `NativeSelectManyBinder.TryBindNestedReferenceNavUnwind` (Task 2); `NativeSelectManyBinder.TryBindTransparentIdentifierProjection` (Task 3, already wired from `TranslateSelect`); `BuildBareNavWrappedShaper(ShapedQueryExpression source, MongoQueryExpression mongoQueryExpression, LambdaExpression resultSelector)` (existing, **unchanged** — already reads the `UnwindSource` shim, which after Task 1 resolves to the LAST source).
- Produces: the end-to-end native nested-reference SelectMany query path; the `NestOwner`/`NestMid`/`NestLeaf` functional fixture, reused by Task 5.

- [ ] **Step 1: Write a failing functional test for the projected nested result**

Add to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`. First, add the fixture (place it after the existing `RefOwnerItemDbContext`/`SeedRefData`/`CreateRefContext*` region, i.e. after the closing `}` of `CreateRefContextWithLogging`):

```csharp
    // ── Nested (2-level) cross-collection reference SelectMany fixture (EF-347 nested-reference) ──────────
    // NestOwner --(Mids, FK OwnerId)--> NestMid --(Leaves, FK MidId)--> NestLeaf. All cross-collection
    // (ToCollection) references, mirroring RefOwnerItemDbContext's pattern one level deeper.

    private class NestOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<NestMid> Mids { get; set; } = [];
    }

    private class NestMid
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public ObjectId? OwnerId { get; set; }
        public NestOwner? Owner { get; set; }
        public List<NestLeaf> Leaves { get; set; } = [];
    }

    private class NestLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? MidId { get; set; }
        public NestMid? Mid { get; set; }
    }

    private sealed class NestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _midsCollection;
        private readonly string _leavesCollection;

        public DbSet<NestOwner> Owners { get; set; } = null!;
        public DbSet<NestMid> Mids { get; set; } = null!;
        public DbSet<NestLeaf> Leaves { get; set; } = null!;

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            MongoQueryMode mode)
            : base(BuildOptions(database, mode, null))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
        }

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            MongoQueryMode mode, ILoggerFactory loggerFactory)
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<NestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            if (loggerFactory != null)
                optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NestOwner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Mids).WithOne(m => m.Owner).HasForeignKey(m => m.OwnerId);
            });
            modelBuilder.Entity<NestMid>(b =>
            {
                b.ToCollection(_midsCollection);
                b.HasMany(m => m.Leaves).WithOne(l => l.Mid).HasForeignKey(l => l.MidId);
            });
            modelBuilder.Entity<NestLeaf>(b => b.ToCollection(_leavesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Seeds a discriminating 3-level dataset: OwnerA (2 Mids, each with Leaves — a genuine multi-row join),
    /// OwnerB (0 Mids — proves an owner with no children contributes no rows), OwnerC (1 Mid with 0 Leaves —
    /// proves a MID with no leaves contributes no rows even though its owner has a mid). Expected joined
    /// (Owner,Mid,Leaf) triples: (OwnerA,A1,Red), (OwnerA,A1,Blue), (OwnerA,A2,Green) — exactly 3 rows.
    /// </summary>
    private static (NestOwner[] Owners, NestMid[] Mids, NestLeaf[] Leaves) SeedNestData()
    {
        var ownerA = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerA" };
        var ownerB = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerB" }; // no mids
        var ownerC = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerC" };

        var midA1 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "A1", OwnerId = ownerA.Id };
        var midA2 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "A2", OwnerId = ownerA.Id };
        var midC1 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "C1", OwnerId = ownerC.Id }; // no leaves

        var leafA1a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Red", MidId = midA1.Id };
        var leafA1b = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Blue", MidId = midA1.Id };
        var leafA2a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Green", MidId = midA2.Id };

        return (
            [ownerA, ownerB, ownerC],
            [midA1, midA2, midC1],
            [leafA1a, leafA1b, leafA2a]);
    }

    private (string Owners, string Mids, string Leaves) NewNestCollectionNames(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Owners") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Mids") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Leaves") + suffix);
    }

    private void SeedNestContext(
        string ownersCollection, string midsCollection, string leavesCollection,
        NestOwner[] owners, NestMid[] mids, NestLeaf[] leaves)
    {
        using var seedDb = new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, MongoQueryMode.Native);
        seedDb.Owners.AddRange(owners);
        seedDb.Mids.AddRange(mids);
        seedDb.Leaves.AddRange(leaves);
        seedDb.SaveChanges();
    }

    private NestDbContext CreateNestContext(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves)
    {
        var (ownersCollection, midsCollection, leavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, owners, mids, leaves);
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, mode);
    }

    private NestDbContext CreateNestContextWithLogging(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves,
        out SpyLoggerProvider spyLogger)
    {
        var (ownersCollection, midsCollection, leavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, owners, mids, leaves);

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, mode, loggerFactory);
    }

    [Fact]
    public void Nested_reference_selectmany_projected_goes_native()
    {
        // No driver-LINQ oracle (cross-collection SelectMany), so proven via Native + NativeOnly succeeding
        // plus an expected in-memory-computed result set — no DriverLinq iteration, no parity assertion.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_projected_goes_native) + mode, out var owners, out var mids, out var leaves);

            var result = (
                from o in db.Owners
                from m in o.Mids
                from l in m.Leaves
                select new { o.Name, m.Tag, l.Label })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            var expected = (
                from o in owners
                from m in mids.Where(m => m.OwnerId == o.Id)
                from l in leaves.Where(l => l.MidId == m.Id)
                select new { o.Name, m.Tag, l.Label })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            Assert.Equal(expected, result);
            Assert.Equal(3, result.Count); // OwnerA/A1/Red, OwnerA/A1/Blue, OwnerA/A2/Green
            Assert.DoesNotContain(result, x => x.Name == "OwnerB" || x.Name == "OwnerC");
        }
    }
```

- [ ] **Step 2: Run the new test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_projected_goes_native"`
(with `MONGODB_URI` and `ATLAS_URI` unset)
Expected: FAIL — the query hard-fails translation (the second `SelectMany` still hits the unmodified `HasTerminalOperator` guard), surfacing as a translation exception under both `Native` and `NativeOnly`.

- [ ] **Step 3: Wire the terminal-guard carve-out into `TranslateSelectMany`**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, immediately after the line:
```csharp
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
```
(currently line 1420) and **before** the existing comment block + guard:
```csharp
        // Post-terminal guard (composition-seam audit): ...
        if (mongoQueryExpression.Select.HasTerminalOperator)
            return null;
```
insert:

```csharp
        // EF-347 nested-reference slice: a narrow carve-out BEFORE the terminal guard below. Fires only when
        // the sole terminal so far is a single REFERENCE unwind source (IsSingleReferenceUnwindTerminalOnly)
        // — i.e. this IS the second, chained SelectMany of a nested reference shape, not some unrelated
        // post-terminal operator (a 2nd SelectMany after GroupBy/Distinct/a set-op/an owned unwind, or a
        // query already 2+ levels deep, still falls through unchanged to the guard below). On a structural
        // match (TryBindNestedReferenceNavUnwind), reuse the SAME wrapped-shaper builder the single-level
        // bare-nav bind uses — BuildBareNavWrappedShaper already reads Select.UnwindSource, which (via the
        // Task 1 last-source shim) now resolves to this SECOND source, so no new shaper code is needed: the
        // result is the doubly-nested TransparentIdentifier(Outer=<level-1 result>, Inner=<level-2 element>)
        // shape EF's nav-expansion expects.
        if (mongoQueryExpression.Select.IsSingleReferenceUnwindTerminalOnly
            && NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQueryExpression, collectionSelector))
        {
            return BuildBareNavWrappedShaper(source, mongoQueryExpression, resultSelector);
        }

```

(The existing guard and every binder call below it are otherwise **unchanged**.)

- [ ] **Step 4: Run the new test to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_projected_goes_native"`
Expected: PASS, 0 failures, for both `Native` and `NativeOnly`.

- [ ] **Step 5: Run the full existing SelectMany functional suite to confirm no regressions**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests"`
Expected: PASS, 0 failures.

- [ ] **Step 6: Verify EF8 builds**

Run: `dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF8"`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: wire nested-reference SelectMany carve-out into TranslateSelectMany"
```

---

## Task 5: Remaining functional coverage — bare-entity, zero-children, parametrized, MQL, declines

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`

**Interfaces:**
- Consumes: the `NestDbContext`/`NestOwner`/`NestMid`/`NestLeaf` fixture (Task 4); adds `NestLeaf.Extras`/`NestGrandLeaf` (a 4th, minimal entity) for the 3-level decline test only.

- [ ] **Step 1: Write the bare-entity (whole level-2-leaf) test**

Add:

```csharp
    [Fact]
    public void Nested_reference_selectmany_bare_entity_goes_native()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_bare_entity_goes_native) + mode, out _, out _, out var leaves);

            var result = (from o in db.Owners from m in o.Mids from l in m.Leaves select l)
                .AsEnumerable().OrderBy(x => x.Label).ToList();

            var expected = leaves.OrderBy(x => x.Label).Select(x => x.Label).ToList();

            Assert.Equal(3, result.Count);
            Assert.Equal(expected, result.Select(x => x.Label).ToList());
            Assert.Equal(leaves.OrderBy(x => x.Label).Select(x => x.Id).ToList(), result.Select(x => x.Id).ToList());
        }
    }
```

- [ ] **Step 2: Run it to verify it fails or passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_bare_entity_goes_native"`
Expected: **PASS already** — Task 4's QMTEV wiring reuses `BuildBareNavWrappedShaper` unconditionally, and the bare-entity trailing selector (`select l`) reaches the SAME whole-element gate in `TranslateSelect` (`wholeElementCandidateUnwind.Kind is Owned or Reference`, keyed off the `UnwindSource` shim, i.e. the LAST/level-2 source) that already handles a single-level reference bare-entity result — no new code needed. If it fails, that is a signal the whole-element gate does not yet correctly read the shim for a 2-source chain; re-open Task 1/4 rather than adding new code here.

- [ ] **Step 3: Write the MQL-shape assertion test**

Add:

```csharp
    [Fact]
    public void Nested_reference_selectmany_emits_two_lookups_and_unwinds()
    {
        using var db = CreateNestContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Nested_reference_selectmany_emits_two_lookups_and_unwinds), out _, out _, out _, out var spyLogger);

        _ = (from o in db.Owners from m in o.Mids from l in m.Leaves select new { o.Name, m.Tag, l.Label })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(message, "\\$lookup").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(message, "\\$unwind").Count);
        Assert.Contains("_lookup_Mids", message);
        Assert.Contains("_lookup_Leaves", message);
        Assert.Contains("_lookup_Mids._id", message); // level-2 localField, scoped under level 1's alias
        Assert.Contains("$project", message);
    }
```

- [ ] **Step 4: Run it to verify GREEN (or diagnose)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_emits_two_lookups_and_unwinds"`
Expected: PASS. If the exact `_lookup_Mids._id` substring does not appear verbatim in the logged MQL (e.g. BSON pretty-printing renders it differently), inspect the captured `message` (add a temporary `Console.WriteLine(message)` or run with `--logger "console;verbosity=detailed"`) and adjust the assertion to the actually-rendered form — the underlying claim (two lookups, two unwinds, correlation scoped under `_lookup_Mids`) is what matters, not the exact string formatting.

- [ ] **Step 5: Write the parametrized-outer-predicate test**

Add:

```csharp
    [Fact]
    public void Nested_reference_selectmany_composes_with_parametrized_outer_predicate()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_composes_with_parametrized_outer_predicate) + mode,
                out var owners, out var mids, out var leaves);
            var targetName = "OwnerA";

            var result = (
                from o in db.Owners
                where o.Name == targetName
                from m in o.Mids
                from l in m.Leaves
                select new { o.Name, m.Tag, l.Label })
                .AsEnumerable().OrderBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            var expected = (
                from o in owners
                where o.Name == targetName
                from m in mids.Where(m => m.OwnerId == o.Id)
                from l in leaves.Where(l => l.MidId == m.Id)
                select new { o.Name, m.Tag, l.Label })
                .OrderBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            Assert.Equal(expected, result);
            Assert.Equal(3, result.Count);
        }
    }
```

- [ ] **Step 6: Run it to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_composes_with_parametrized_outer_predicate"`
Expected: PASS.

- [ ] **Step 7: Write the whole-outer decline tests**

Add (both `select o` and `select m` — a whole OUTER-scope selector at either level must still decline, unchanged from the single-level reference note):

```csharp
    [Fact]
    public void Nested_reference_selectmany_whole_outer_owner_result_still_declines_cleanly_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_whole_outer_owner_result_still_declines_cleanly_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners from m in o.Mids from l in m.Leaves select o).ToList());
        }
    }

    [Fact]
    public void Nested_reference_selectmany_whole_outer_mid_result_still_declines_cleanly_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_whole_outer_mid_result_still_declines_cleanly_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners from m in o.Mids from l in m.Leaves select m).ToList());
        }
    }
```

- [ ] **Step 8: Run them to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_whole_outer"`
Expected: PASS — both throw in every mode (no driver-LINQ oracle for cross-collection SelectMany at all, so `DriverLinq` throws too, exactly like every other reference-SelectMany decline).

- [ ] **Step 9: Extend the fixture with a 4th entity for the 3-level decline test, and write it**

Add to the fixture region (after `NestLeaf`):

```csharp
    private class NestGrandLeaf
    {
        public ObjectId Id { get; set; }
        public string Detail { get; set; } = "";
        public ObjectId? LeafId { get; set; }
        public NestLeaf? Leaf { get; set; }
    }
```

Add a `NestLeaf.GrandLeaves` navigation:
```csharp
    private class NestLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? MidId { get; set; }
        public NestMid? Mid { get; set; }
        public List<NestGrandLeaf> GrandLeaves { get; set; } = [];
    }
```
(replace the existing `NestLeaf` class from Task 4 with this version — adds the one new property).

Replace the whole `NestDbContext` class (introduced in Task 4 Step 1) with this 4-collection version — every change from the Task 4 version is marked; the only NEW moving parts are the `_grandLeavesCollection` field, the `grandLeavesCollection` constructor parameter, the `GrandLeaves` `DbSet`, and the `NestLeaf`/`NestGrandLeaf` entries in `OnModelCreating`:

```csharp
    private sealed class NestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _midsCollection;
        private readonly string _leavesCollection;
        private readonly string _grandLeavesCollection; // NEW

        public DbSet<NestOwner> Owners { get; set; } = null!;
        public DbSet<NestMid> Mids { get; set; } = null!;
        public DbSet<NestLeaf> Leaves { get; set; } = null!;
        public DbSet<NestGrandLeaf> GrandLeaves { get; set; } = null!; // NEW

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            string grandLeavesCollection, MongoQueryMode mode) // NEW param
            : base(BuildOptions(database, mode, null))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
            _grandLeavesCollection = grandLeavesCollection; // NEW
        }

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            string grandLeavesCollection, MongoQueryMode mode, ILoggerFactory loggerFactory) // NEW param
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
            _grandLeavesCollection = grandLeavesCollection; // NEW
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<NestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            if (loggerFactory != null)
                optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NestOwner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Mids).WithOne(m => m.Owner).HasForeignKey(m => m.OwnerId);
            });
            modelBuilder.Entity<NestMid>(b =>
            {
                b.ToCollection(_midsCollection);
                b.HasMany(m => m.Leaves).WithOne(l => l.Mid).HasForeignKey(l => l.MidId);
            });
            modelBuilder.Entity<NestLeaf>(b =>
            {
                b.ToCollection(_leavesCollection);
                b.HasMany(l => l.GrandLeaves).WithOne(g => g.Leaf).HasForeignKey(g => g.LeafId); // NEW
            });
            modelBuilder.Entity<NestGrandLeaf>(b => b.ToCollection(_grandLeavesCollection)); // NEW
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
```

Then replace the four collection-name/seed/create helpers (also from Task 4 Step 1) with 4-collection versions:

```csharp
    private (string Owners, string Mids, string Leaves, string GrandLeaves) NewNestCollectionNames(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Owners") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Mids") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Leaves") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "GrandLeaves") + suffix); // NEW
    }

    private void SeedNestContext(
        string ownersCollection, string midsCollection, string leavesCollection, string grandLeavesCollection,
        NestOwner[] owners, NestMid[] mids, NestLeaf[] leaves)
    {
        using var seedDb = new NestDbContext(
            database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, MongoQueryMode.Native);
        seedDb.Owners.AddRange(owners);
        seedDb.Mids.AddRange(mids);
        seedDb.Leaves.AddRange(leaves);
        // No NestGrandLeaf rows: the 3-level decline test below fails at translation time, before any data is
        // read, so the collection can stay empty.
        seedDb.SaveChanges();
    }

    private NestDbContext CreateNestContext(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves)
    {
        var (ownersCollection, midsCollection, leavesCollection, grandLeavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, owners, mids, leaves);
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, mode);
    }

    private NestDbContext CreateNestContextWithLogging(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves,
        out SpyLoggerProvider spyLogger)
    {
        var (ownersCollection, midsCollection, leavesCollection, grandLeavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, owners, mids, leaves);

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, mode, loggerFactory);
    }
```

(These four replace the 3-collection versions written in Task 4 Step 1 verbatim — same names, same call sites in every test from Tasks 4-5, since `out` parameter lists are unchanged; only the internal collection-name plumbing gained the 4th string.)

Then add the decline test:

```csharp
    [Fact]
    public void Nested_reference_selectmany_third_level_still_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_third_level_still_hard_fails_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners
                 from m in o.Mids
                 from l in m.Leaves
                 from g in l.GrandLeaves
                 select new { o.Name, m.Tag, l.Label, g.Detail }).ToList());
        }
    }
```

- [ ] **Step 10: Run it to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~Nested_reference_selectmany_third_level_still_hard_fails_in_every_mode"`
Expected: PASS — the THIRD `SelectMany`'s own collection selector correlates off the level-2 unwound element (`ti2.Inner.<pk>`, one transparent-identifier hop deeper than this slice's carve-out recognizes: `IsSingleReferenceUnwindTerminalOnly` is `false` once `UnwindSources.Count == 2`), so it falls straight through to the unmodified `HasTerminalOperator` guard and returns `null` — a clean hard-fail in every mode, exactly as intended.

- [ ] **Step 11: Run the FULL functional SelectMany suite**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests"`
Expected: PASS, 0 failures.

- [ ] **Step 12: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: nested-reference SelectMany functional coverage (bare-entity, MQL, declines)"
```

---

## Task 6: AGENTS.md as-built note + self-review prep

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:** none (documentation only).

- [ ] **Step 1: Add the as-built note**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, immediately after the existing `> **Cross-collection reference \`SelectMany\` — projected (EF-347 slice 5).**` note block and its immediate siblings (the filtered-inner / correlated-beyond-FK notes), add a new note block:

```markdown
> **Nested (2-level) reference SelectMany (EF-347).** Native now ALSO covers a SECOND, chained cross-collection
> reference `SelectMany` — `from o in q from m in o.Mids from l in m.Leaves select new {o.Name, m.Tag, l.Label}`
> (and the bare-entity `select l`) — for exactly two unfiltered levels. **Mechanism:** `MongoSelectDefinition`'s
> single `UnwindSource` slot became an ordered `UnwindSources` list (`AddUnwindSource` writer; `UnwindSource`
> retained as a read-only "last source" shim — behavior-preserving for every single-level shape, since every
> existing consumer already only cares about the terminal/last source). `TranslateSelectMany`'s terminal guard
> gained a narrow carve-out, `MongoSelectDefinition.IsSingleReferenceUnwindTerminalOnly` (exactly one prior
> REFERENCE source, no grouping/distinct/set-op mixed in): when true, `NativeSelectManyBinder.TryBindNestedReferenceNavUnwind`
> attempts to recognize the second SelectMany's collection selector — spike-confirmed
> (`.superpowers/sdd/EF-347-nested-ref-spike.md`) to be the SAME correlated-subquery shape as a single-level
> reference SelectMany, `Queryable.Where(EntityQueryRootExpression<Leaf>, l => ti.Inner.<pk> == l.<fk>)`, except
> the correlation's outer-key side is a TRANSPARENT-IDENTIFIER-ROOTED member chain (`ti.Inner.<pk>`) rather than
> a bare parameter. Rather than teach `NativeCorrelationMatcher` a new shape, the binder rewrites every
> `ti.Inner` occurrence onto a synthetic parameter of the level-1 target entity type first, then reuses
> `NativeCorrelationMatcher.TryMatchCorrelatedCollection` COMPLETELY UNCHANGED. On a match, it registers a
> second `ForceUnwind` `LookupExpression` whose `LocalField` is overridden to be scoped under the level-1
> source's own lookup alias (e.g. `_lookup_Mids._id`) — **no changes were needed to `LookupExpression`,
> `MongoQueryExpression.AddLookup`, or `MongoSelectLowerer`/`AppendLookupStages`**: the existing
> lookup-dependency sort (`MongoQueryExpression.GetPendingLookups`/`OrderLookupsByDependency`, built for
> transitive collection-Include lookups) already recognizes a `LocalField` prefixed with another lookup's `As`
> and orders that lookup after the one it depends on, so the two `$lookup`+`$unwind` pairs emit in the correct
> dependency order with zero lowering changes. The QMTEV carve-out reuses `BuildBareNavWrappedShaper`
> UNCHANGED too (it already reads the `UnwindSource` shim, which now resolves to the SECOND source). The
> trailing projection binder, `TryBindTransparentIdentifierProjection`, was generalized from 2 scopes
> (`ti.Outer`/`ti.Inner`) to N scopes: it counts `Outer`/`Inner` hops in a leaf's accessor chain against
> `UnwindSources.Count` to resolve `ti.Outer.Outer.<m>` (root/owner), `ti.Outer.Inner.<m>` (level-1 scope,
> `_lookup_Mids` prefix), and `ti.Inner.<m>` (level-2 scope, `_lookup_Leaves` prefix) — byte-identical for the
> existing N=1 (single-level) shapes, proven by the pre-existing 2-scope unit tests staying green unmodified.
> **Bare-entity result:** `select l` reuses the SAME whole-element gate the single-level reference bare-entity
> note documents (`IsWholeElementRepresentable`, keyed off the `UnwindSource` shim — now the LAST/level-2
> source) with NO code change. **No driver-LINQ oracle** (same as every reference SelectMany), so every
> accepted shape is proven via `MongoQueryMode.NativeOnly` succeeding plus an expected in-memory result set,
> and every decline hard-fails in EVERY mode (`Native`/`DriverLinq`/`NativeOnly`). **Still deferred/declines
> cleanly:** three-or-more levels of nesting (the THIRD SelectMany's own carve-out check,
> `IsSingleReferenceUnwindTerminalOnly`, is `false` once `UnwindSources.Count == 2`, so it falls straight to
> the unmodified `HasTerminalOperator` guard); owned or mixed nesting (owned-in-owned, owned-in-reference,
> reference-in-owned); any inner `.Where` filter or correlation-beyond-FK on either level (the binder matches
> only the exact unfiltered single-`Where` spike shape — an outer `Where` layer wrapping the FK correlation,
> the filtered-inner shape, does not match and declines structurally); a computed projection leaf; and the
> whole-OUTER shape at either level (`select o` / `select m`), which still declines via the unchanged
> whole-outer guard. **Multi-version:** no `#if` — identical EF8/EF9/EF10 behavior (spike-confirmed
> byte-identical dumps across all three).
```

- [ ] **Step 2: Verify markdown renders sensibly (no code fence / nesting errors)**

Run: `git diff --stat src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` and read the diff to confirm the added block is a single well-formed `>`-quoted paragraph with no stray blank `>` lines breaking the blockquote, consistent with the surrounding notes' style.

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: as-built note for nested (2-level) reference SelectMany"
```

---

## Finalization

Controller-driven, after all 6 tasks are committed and individually reviewed:

1. **3-version `/test-all`, run in the FOREGROUND** (not backgrounded) — summing all three per-assembly (EF8/EF9/EF10) pass/fail blocks. Expect 0 failures across all three.
2. **`MongoQueryMode.NativeOnly` spec sweep** — run the full specification suite with `MONGODB_EF_NATIVE_ONLY=1` and confirm no regressions versus the `4fa2162` baseline (the sweep should show the SAME failing set as before this slice, since nested reference SelectMany is not part of the standard Northwind spec fixtures — this slice's own coverage lives entirely in `NativeSelectManyTests`).
3. **Opus whole-branch review** — invoke `/review-ef-core-provider` (or an equivalent full-branch review) covering every commit from Task 1 through Task 6; address any findings with a new commit (or amend before squash, per reviewer instructions) before proceeding.
4. **Squash** every commit above `4fa2162` into ONE commit (`EF-347: Native nested (2-level) reference SelectMany`), after creating a `-presquash` backup branch (`git branch EF-347-selectmany-nested-reference-presquash`) for safety.
5. **User drives the fast-forward push** of `origin/NativeQueryOngoing` (current tip → the new squashed commit) — do not push automatically.

## Self-Review

**1. Spec coverage** — cross-checked against `docs/superpowers/specs/2026-07-24-native-selectmany-nested-reference-design.md`:
- §1 Unwind-chain IR → Task 1.
- §2 Terminal-guard carve-out → Task 4 Step 3.
- §3 Level-2 correlation recognition → Task 2.
- §4 Chained lowering (localField `_lookup_Mids._id`, dependency ordering, both `$unwind`s `preserveNullAndEmptyArrays:false`) → Task 2 Step 3 (binder) + Task 2's unit test asserting `mongoQ.Lookups` ordering (the "no lowering changes needed" finding is verified there and documented, not left as an open question) + Task 5 Step 3 (MQL assertion).
- §5 N-scope projection binder → Task 3.
- §6 Bare-entity result → Task 5 Step 1-2 (asserted to already work via the unchanged whole-element gate, per Task 1's shim).
- "Decline & no-oracle" (design doc) → every functional test in Tasks 4-5 uses `NativeOnly`-succeeds-plus-expected-result (never parity), and every decline test asserts `Assert.ThrowsAny<Exception>` across `Native`/`DriverLinq`/`NativeOnly`.
- "Not a breaking change" → Global Constraints + Task 1's byte-identical proof (Step 8) + Task 3's byte-identical proof (Step 4).
- Scope boundaries (3+ levels, owned/mixed nesting, filtered inner, correlated-beyond-FK, computed leaf, whole-outer) → Task 5 Steps 7-10 cover 3-levels and whole-outer directly; filtered-inner/correlated-beyond-FK/computed-leaf are covered by construction (the binder's structural match in Task 2 only recognizes the exact unfiltered spike shape — no separate test is needed to prove a shape the binder structurally cannot reach still declines, since it falls through to the pre-existing, already-tested `TryBind`/`TryBindReferenceNavUnwind`/`TryBind` chain and ultimately the unmodified terminal guard).

**2. Placeholder scan** — no "TBD"/"similar to"/"add appropriate" strings anywhere in the task steps; every code step shows complete, concrete code read against the actual current file contents (verified via direct `Read` of every touched file before drafting). The one deliberately-scoped-down step (Task 3 Step 1's second test) is explicit about why it is weakened and what would need to change to strengthen it — not a placeholder, a documented simplification.

**3. Type consistency** — verified across tasks:
- `MongoSelectDefinition.UnwindSources`/`AddUnwindSource`/`UnwindSource`/`IsSingleReferenceUnwindTerminalOnly` (Task 1) are consumed with identical names/signatures in Tasks 2-5.
- `NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(MongoQueryExpression, LambdaExpression)` (Task 2) is called with the exact same signature in Task 4 Step 3.
- `TryBindTransparentIdentifierProjection`'s signature is unchanged (Task 3) — confirmed its only caller (`TranslateSelect`, `MongoQueryableMethodTranslatingExpressionVisitor.cs:217`) needs no edit.
- `BuildBareNavWrappedShaper(ShapedQueryExpression, MongoQueryExpression, LambdaExpression)` (existing, read verbatim from source) is called with matching argument order/types in Task 4 Step 3.
- `LookupExpression.LocalField`/`.As` (existing, settable) are used consistently in Task 2 Step 3 and asserted in Task 2 Step 1's unit test with the same expected value (`_lookup_Mids._id`).
- The functional fixture's `NestOwner`/`NestMid`/`NestLeaf`/`NestGrandLeaf` class and `NestDbContext` constructor shapes introduced in Task 4 are extended (not redefined) in Task 5 Step 9 — same collection-name-threading pattern throughout.
