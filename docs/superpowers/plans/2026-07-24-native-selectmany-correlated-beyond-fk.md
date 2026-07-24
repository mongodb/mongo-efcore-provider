# Native correlated-beyond-FK inner-filter reference `SelectMany` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a cross-collection **reference** `SelectMany` whose inner `.Where(pred)` references the **outer** entity beyond the FK correlation (`o.Refs.Where(r => r.Tag == o.Name)`) go **native**, emitting the correlated conjunct as a `$expr` field-to-field comparison in the post-`$unwind` `$match`.

**Architecture:** Extend `MongoExpressionTranslator` with an **optional, strictly-additive** outer scope so a member access rooted on the outer `SelectMany` parameter resolves against the outer entity type at document root, while inner members resolve against the inner entity type with the `_lookup_<Nav>` prefix. Relax the `ReferencesParameter` guard in `NativeSelectManyBinder.TryBindReferenceNavUnwind` from *decline* to *route*: a peeled predicate that references the outer param is translated with the two-scope translator and ANDed onto the existing `MongoUnwindSource.Filter`; the lowerer/renderer are unchanged (`Filter` already emits as a `$match` via the `$expr`-capable `MongoQueryLanguageRenderer`).

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions).

**Design doc:** `docs/superpowers/specs/2026-07-24-native-selectmany-correlated-beyond-fk-design.md`
**Spike:** `.superpowers/sdd/EF-347-correlated-inner-spike.md`

## Global Constraints

- **Branch:** `EF-347-selectmany-correlated-beyond-fk` (already cut off tip `6ae61ac`; design committed `f6019e6`). Do NOT switch branches.
- **No `#if` EF-version guards** — the spike confirmed the tree is byte-identical across EF8/EF9/EF10.
- `<Nullable>enable</Nullable>` on `src/` — annotate new members accordingly (`?` on the optional outer-scope fields/params).
- **Preserve file BOMs.**
- All touched production types stay `internal`; this is a hard-fail → native change, not a break.
- Unit tests use **plain xUnit `Assert.*`** — FluentAssertions is not referenced in the test projects.
- Tests run **serially** (assembly-level parallelization disabled). Run functional/spec tests with **both `MONGODB_URI` and `ATLAS_URI` unset** so an isolated `mongodb/mongodb-atlas-local` container is used.
- **No-partial-mutation-on-decline invariant:** a binder that returns `false` must leave `mongoQueryExpression` untouched (no `UnwindSource`, no registered `Lookup`).
- **No new mismatched-CLR-type guard** (EF-221 known interaction): the correlated field-to-field comparison inherits the provider's existing value-equality semantics.
- Commit after each task. The controller runs the full 3-version `/test-all` foreground and drives squash + push at the end (not part of these tasks).

---

### Task 1: Additive two-scope extension to `MongoExpressionTranslator`

Add an optional outer scope so the translator can resolve a **correlated** predicate: members rooted on the outer param resolve against the outer entity type at root; all other members resolve against the inner entity type and are prefixed with the unwind scope. When constructed single-scope (every existing caller), behavior is byte-identical.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (fields + second constructor at ~line 44-55; `TryResolveMember` at ~line 393-414)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Produces: a second constructor
  `MongoExpressionTranslator(IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string innerPrefix)`.
  In this mode, `TryTranslate`/`TryTranslateField` resolve a `MemberExpression` rooted on `outerParam` against `outerEntityType` (root element name, no prefix); any other member resolves against `innerEntityType` with `innerPrefix + "." + elementName`. The single-arg constructor `MongoExpressionTranslator(IEntityType)` is unchanged.
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Add failing two-scope unit tests**

Add two small entity classes and four tests to `MongoExpressionTranslatorTests.cs`. Put the classes next to the existing `Customer` class (~line 46) and the tests at the end of the class (before the final closing brace).

```csharp
// Two-scope (correlated reference SelectMany) fixtures — InnerRef and OuterRef deliberately share a
// "Name" member to prove identity-based routing never conflates the two scopes by name.
private class InnerRef
{
    public ObjectId Id { get; set; }
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

private class OuterRef
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public int Threshold { get; set; }
}

[Fact]
public void Two_scope_correlated_comparison_routes_inner_prefixed_and_outer_root()
{
    var innerType = GetEntityType<InnerRef>();
    var outerType = GetEntityType<OuterRef>();
    var outerParam = Expression.Parameter(typeof(OuterRef), "o");
    var innerParam = Expression.Parameter(typeof(InnerRef), "r");
    // r.Tag == o.Name
    var body = Expression.Equal(
        Expression.Property(innerParam, nameof(InnerRef.Tag)),
        Expression.Property(outerParam, nameof(OuterRef.Name)));
    var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
    Assert.Equal("_lookup_Refs.Tag", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
}

[Fact]
public void Two_scope_shadowed_member_name_resolves_by_parameter_identity_not_name()
{
    var innerType = GetEntityType<InnerRef>();
    var outerType = GetEntityType<OuterRef>();
    var outerParam = Expression.Parameter(typeof(OuterRef), "o");
    var innerParam = Expression.Parameter(typeof(InnerRef), "r");
    // r.Name == o.Name — same member name on both scopes; must resolve to DISTINCT field refs.
    var body = Expression.Equal(
        Expression.Property(innerParam, nameof(InnerRef.Name)),
        Expression.Property(outerParam, nameof(OuterRef.Name)));
    var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("_lookup_Refs.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
}

[Fact]
public void Two_scope_inner_only_conjunct_still_gets_the_inner_prefix()
{
    var innerType = GetEntityType<InnerRef>();
    var outerType = GetEntityType<OuterRef>();
    var outerParam = Expression.Parameter(typeof(OuterRef), "o");
    var innerParam = Expression.Parameter(typeof(InnerRef), "r");
    // r.Tag == "x" — no outer reference; in two-scope mode the inner field is still prefixed.
    var body = Expression.Equal(
        Expression.Property(innerParam, nameof(InnerRef.Tag)),
        Expression.Constant("x"));
    var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal("_lookup_Refs.Tag", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    Assert.IsType<MongoConstantExpression>(bin.Right);
}

[Fact]
public void Two_scope_numeric_correlated_comparison_translates()
{
    var innerType = GetEntityType<InnerRef>();
    var outerType = GetEntityType<OuterRef>();
    var outerParam = Expression.Parameter(typeof(OuterRef), "o");
    var innerParam = Expression.Parameter(typeof(InnerRef), "r");
    // r.Score >= o.Threshold — proves the full comparison breadth flows through field-to-field.
    var body = Expression.GreaterThanOrEqual(
        Expression.Property(innerParam, nameof(InnerRef.Score)),
        Expression.Property(outerParam, nameof(OuterRef.Threshold)));
    var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

    Assert.True(translator.TryTranslate(body, out var result));
    var bin = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal(MongoBinaryOperator.GreaterThanOrEqual, bin.Operator);
    Assert.Equal("_lookup_Refs.Score", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    Assert.Equal("Threshold", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests.Two_scope"
```
Expected: FAIL — compile error (`MongoExpressionTranslator` has no 4-argument constructor).

- [ ] **Step 3: Add the optional outer scope to `MongoExpressionTranslator`**

In `MongoExpressionTranslator.cs`, replace the single field + constructor (lines ~46-55) with:

```csharp
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    /// <summary>
    /// Creates a single-scope <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    public MongoExpressionTranslator(IEntityType entityType)
    {
        _entityType = entityType;
    }

    /// <summary>
    /// Creates a two-scope translator for a CORRELATED reference-<c>SelectMany</c> inner filter: a member access
    /// rooted on <paramref name="outerParam"/> resolves against <paramref name="outerEntityType"/> at document
    /// root (no prefix); any other member resolves against <paramref name="innerEntityType"/> and is prefixed
    /// with <paramref name="innerPrefix"/> (the <c>_lookup_&lt;Nav&gt;</c> unwind scope). Outer members are
    /// identified by reference identity, never by name, so a member name shared between the two scopes never
    /// conflates them.
    /// </summary>
    public MongoExpressionTranslator(
        IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string innerPrefix)
    {
        _entityType = innerEntityType;
        _outerParam = outerParam;
        _outerEntityType = outerEntityType;
        _innerPrefix = innerPrefix;
    }
```

Then replace the body of `TryResolveMember` (lines ~393-414) with the scope-aware version (DRY — one resolve path, scope selected by identity):

```csharp
    private bool TryResolveMember(Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;

        if (node is not MemberExpression { Expression: ParameterExpression param } me)
            return false;

        // Two-scope mode: a member rooted on the outer param resolves against the outer entity type at document
        // root; every other member is inner-scoped. Identity (ReferenceEquals), never name — so a member name
        // shared between the two scopes cannot be mis-routed.
        var isOuter = _outerParam is not null && ReferenceEquals(param, _outerParam);
        var scopeType = isOuter ? _outerEntityType! : _entityType;

        var resolved = scopeType.FindProperty(me.Member.Name);
        if (resolved is null)
            return false;

        // A component of a composite primary key is stored nested under "_id" (e.g. { _id: { Key1, Key2 } }),
        // so its top-level element name does not address the stored field. The native translator does not resolve
        // the dotted "_id.<name>" path, so refuse it here and let the query fall back rather than emit a $match
        // against a non-existent top-level field (which silently returns nothing).
        if (resolved.IsPrimaryKey() && resolved.FindContainingPrimaryKey()!.Properties.Count > 1)
            return false;

        property = resolved;
        fieldPath = resolved.GetElementName();

        // Inner-scope fields are prefixed with the unwind scope in two-scope mode; outer-scope fields (and every
        // field in single-scope mode, where _innerPrefix is null) stay at their resolved element name.
        if (!isOuter && _innerPrefix is not null)
            fieldPath = _innerPrefix + "." + fieldPath;

        return true;
    }
```

- [ ] **Step 4: Run the new tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests.Two_scope"
```
Expected: PASS (4 tests).

- [ ] **Step 5: Run the FULL translator suite to prove the change is additive**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```
Expected: PASS — every pre-existing single-scope test is green (byte-identical behavior when no outer scope is configured).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs
git commit -m "EF-347: Two-scope (correlated) resolution in MongoExpressionTranslator"
```

---

### Task 2: Route correlated peeled predicates in `TryBindReferenceNavUnwind`

Flip the `ReferencesParameter` guard from *decline* to *route*: a peeled user predicate (or the folded conjunct) that references the outer param is translated with the two-scope translator (Task 1) and ANDed onto `MongoUnwindSource.Filter`; inner-only predicates keep the existing inner-translate + blanket-prefix path. A correlated predicate the two-scope translator cannot handle still declines cleanly with no mutation.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs` (the `foldedUserBody` block ~line 229-239, the `userPredicates` loop ~line 241-252; add a private helper near `ReferencesParameter` ~line 349)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: the Task-1 two-scope constructor `MongoExpressionTranslator(IEntityType, ParameterExpression, IEntityType, string)`.
- Produces: a private static helper
  `bool TryTranslateReferenceFilterLayer(Expression body, MongoExpressionTranslator innerTranslator, IEntityType innerEntityType, string scope, ParameterExpression outerParam, IEntityType outerEntityType, out MongoExpression? conjunct)`.

- [ ] **Step 1: Add failing / converted binder unit tests**

In `NativeSelectManyBinderTests.cs`, **convert** `TryBindReferenceNavUnwind_correlated_beyond_fk_filter_returns_false` (line ~590) into a binds-with-`$expr`-filter test, and **add** shadow + mixed tests. Reuse the existing `ReferenceCollectionSelectorFiltered` / `TagsNavigation` / `TestQuery` helpers (Owner = outer with `Id`/`Name`; Tag = inner with `OwnerId`/`Label`).

Replace the `_returns_false` test with:

```csharp
    [Fact]
    public void TryBindReferenceNavUnwind_correlated_beyond_fk_filter_binds_with_expr_filter()
    {
        // A user filter referencing the OUTER entity beyond the FK (t.Label == o.Name) now goes native: the
        // correlated conjunct is two-scope-translated (inner field prefixed, outer field at root) and stored on
        // the Filter as a field-to-field comparison the renderer emits as $expr.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        // t.Label == o.Name — correlated beyond the FK.
        var user = Expression.Lambda(
            Expression.Equal(Expression.Property(tParam, nameof(Tag.Label)), Expression.Property(outerParam, nameof(Owner.Name))),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource!;
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind.Kind);
        var bin = Assert.IsType<MongoBinaryExpression>(unwind.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        // Inner field prefixed with the lookup scope; outer field at document root — resolved by parameter
        // identity, so the shared-nothing scopes never conflate.
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_mixed_inner_and_correlated_conjunct_binds()
    {
        // One .Where layer whose body ANDs an inner-only conjunct with a correlated one:
        // t.Label != "x" && t.Label == o.Name. The whole layer routes through the two-scope translator.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var innerOnly = Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x"));
        var correlated = Expression.Equal(Expression.Property(tParam, nameof(Tag.Label)), Expression.Property(outerParam, nameof(Owner.Name)));
        var user = Expression.Lambda(Expression.AndAlso(innerOnly, correlated), tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        var and = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        // Both conjuncts' inner fields are prefixed; the correlated conjunct's outer field is at root.
        var left = Assert.IsType<MongoBinaryExpression>(and.Left);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(left.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_unsupported_correlated_operator_returns_false_without_mutation()
    {
        // t.Label.ToUpper() == o.Name — the correlated conjunct uses an operator the translator rejects, so the
        // two-scope translation fails and the bind declines cleanly with no partial mutation.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user = Expression.Lambda(
            Expression.Equal(
                Expression.Call(Expression.Property(tParam, nameof(Tag.Label)), typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!),
                Expression.Property(outerParam, nameof(Owner.Name))),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests.TryBindReferenceNavUnwind_correlated_beyond_fk_filter_binds_with_expr_filter|FullyQualifiedName~NativeSelectManyBinderTests.TryBindReferenceNavUnwind_mixed_inner_and_correlated_conjunct_binds|FullyQualifiedName~NativeSelectManyBinderTests.TryBindReferenceNavUnwind_unsupported_correlated_operator_returns_false_without_mutation"
```
Expected: FAIL — the `binds_with_expr_filter` and `mixed` tests fail (bind currently returns `false` on any outer-param reference); the `unsupported_correlated_operator` test may pass already (still declines) but must stay green after the change.

- [ ] **Step 3: Add the routing helper**

In `NativeSelectManyBinder.cs`, add this private static helper next to `ReferencesParameter` (~line 349), above the `ParameterReferenceVisitor` class:

```csharp
    /// <summary>
    /// Translates one peeled reference-<c>SelectMany</c> inner-filter <c>Where</c> layer into a filter conjunct.
    /// A layer that references the outer <c>SelectMany</c> parameter (correlated beyond the FK) is translated with
    /// the two-scope translator — inner field refs prefixed with <paramref name="scope"/>, outer field refs at
    /// document root, routed by parameter identity so a shared member name never conflates the scopes — and its
    /// field-to-field comparison is later rendered as <c>$expr</c>. A layer that references only the inner element
    /// keeps the single-scope translate + blanket-prefix path unchanged. Returns <see langword="false"/> (with no
    /// mutation of the caller's query) when the layer cannot be translated.
    /// </summary>
    private static bool TryTranslateReferenceFilterLayer(
        Expression body, MongoExpressionTranslator innerTranslator, IEntityType innerEntityType, string scope,
        ParameterExpression outerParam, IEntityType outerEntityType, [NotNullWhen(true)] out MongoExpression? conjunct)
    {
        conjunct = null;

        if (ReferencesParameter(body, outerParam))
        {
            var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, scope);
            if (!twoScope.TryTranslate(body, out var correlated))
                return false;
            conjunct = correlated; // already correctly scoped — do NOT blanket-prefix
            return true;
        }

        if (!innerTranslator.TryTranslate(body, out var innerExpr))
            return false;
        conjunct = MongoFieldPrefixRewriter.Rewrite(innerExpr, scope);
        return true;
    }
```

(Ensure `using System.Diagnostics.CodeAnalysis;` is present for `[NotNullWhen]` — the file already uses it for other members.)

- [ ] **Step 4: Rewire the two decline sites to route through the helper**

In `TryBindReferenceNavUnwind`, replace the `foldedUserBody` block (currently ~lines 229-239):

```csharp
        if (foldedUserBody != null)
        {
            if (!TryTranslateReferenceFilterLayer(
                    foldedUserBody, innerTranslator, navigation.TargetEntityType, scope, outerParam, outerEntityType, out var foldedExpr))
                return false;
            filter = foldedExpr;
        }
```

and replace the `userPredicates` loop (currently ~lines 241-252):

```csharp
        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1
                || !TryTranslateReferenceFilterLayer(
                    userPredicate.Body, innerTranslator, navigation.TargetEntityType, scope, outerParam, outerEntityType, out var userExpr))
                return false;
            filter = filter == null
                ? userExpr
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, userExpr);
        }
```

(`outerEntityType` is already in scope — declared ~line 213 as `mongoQ.CollectionExpression.EntityType`. `innerTranslator` is still declared ~line 226 and reused for the inner-only branch inside the helper.)

- [ ] **Step 5: Run the binder tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"
```
Expected: PASS — the three new/converted tests pass, and every pre-existing binder test (including `_nested_filtered_inner_binds_with_filter`, `_stacked_filters_bind_and_and_together`, `_folded_filtered_inner_binds_with_filter`) stays green (inner-only path byte-unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: Route correlated-beyond-FK reference SelectMany filters to native $expr"
```

---

### Task 3: End-to-end functional tests (real DB)

Convert the functional decline test into a native-success test and add real-DB coverage for the correlated shapes (projected, bare-entity, shadow, mixed, stacked, numeric-comparison, zero-match, parametrized-outer), plus an MQL assertion that the post-`$unwind` `$match` carries `$expr`.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` (the `RefOwner`/`RefItem` entities ~line 696-714; seed helper ~line 800; the decline test ~line 1401-1423)

**Interfaces:**
- Consumes: the native binding from Tasks 1-2 (no code from those tasks is referenced directly — this is black-box end-to-end).

- [ ] **Step 1: Add numeric fields to the reference fixture for a comparison-breadth test**

In `NativeSelectManyTests.cs`, add an `int` to each of `RefOwner` and `RefItem` (additive — defaults 0 for existing seed rows). After `RefOwner.Name` (~line 699) add:

```csharp
        public int Threshold { get; set; }
```

After `RefItem.Name` (~line 711) add:

```csharp
        public int Score { get; set; }
```

Find the reference seed (the `seedDb.Refs.AddRange(items)` region ~line 800) and set `Score`/`Threshold` on the seeded rows so at least one row satisfies `r.Score >= o.Threshold` and at least one does not (mirror whatever seed values the existing reference tests rely on; do not remove existing fields). Keep the existing rows' other values unchanged.

- [ ] **Step 2: Convert the decline test to a native-success test and add coverage**

Replace `Reference_form_correlated_beyond_fk_inner_hard_fails_in_every_mode` (~line 1401-1423) with a native-success test, and add the sibling cases below. Use the existing `CreateRefContext` / seed / `AssertMql` conventions already in this file (look at `Reference_form_filtered_inner_goes_native`-style tests near line 1300-1400 for the exact helper signatures and the seeded data shape).

```csharp
    [Fact]
    public void Reference_form_correlated_beyond_fk_inner_goes_native()
    {
        // r.Tag == o.Name references the outer entity beyond the FK. Now native: a $expr field-to-field
        // comparison in the post-$unwind $match. No driver-LINQ oracle for cross-collection SelectMany, so
        // prove via NativeOnly succeeding + expected in-memory result set, and confirm every mode agrees.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_beyond_fk_inner_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Reference_form_correlated_shadowed_member_name_resolves_by_scope()
    {
        // r.Name == o.Name — RefItem.Name deliberately shadows RefOwner.Name. Native routing by parameter
        // identity must compare the inner element's Name to the outer owner's Name, not the item to itself.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_shadowed_member_name_resolves_by_scope) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Name == o.Name), (o, r) => new { OuterName = o.Name, InnerName = r.Name })
                .AsEnumerable().OrderBy(x => x.OuterName).ThenBy(x => x.InnerName).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Name == o.Name), (o, r) => new { OuterName = o.Name, InnerName = r.Name })
                .OrderBy(x => x.OuterName).ThenBy(x => x.InnerName).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Reference_form_correlated_mixed_conjunct_goes_native()
    {
        // Inner-only conjunct ANDed with a correlated one in one .Where.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_mixed_conjunct_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget" && r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget" && r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Reference_form_correlated_numeric_comparison_goes_native()
    {
        // r.Score >= o.Threshold — proves comparison-operator breadth end-to-end via $expr $gte.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_numeric_comparison_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Score >= o.Threshold), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Score >= o.Threshold), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Reference_form_correlated_bare_entity_result_goes_native()
    {
        // Bare-entity trailing selector composes with the correlated $match (which runs before $replaceRoot).
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_bare_entity_result_goes_native), out var owners, out _);

        var result = (from o in db.Owners from r in o.Refs.Where(r => r.Tag == o.Name) select r)
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

        var expected = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => r)
            .Select(r => r.Tag).OrderBy(t => t).ToList();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_correlated_stacked_where_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_stacked_where_goes_native), out var owners, out _);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name).Where(r => r.Name == o.Name), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name).Where(r => r.Name == o.Name), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_correlated_excluding_all_children_contributes_no_rows()
    {
        // A correlated predicate that matches nothing yields no rows (inner-join semantics preserved).
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_excluding_all_children_contributes_no_rows), out _, out _);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name + "::never"), (o, r) => new { o.Name, r.Tag })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Reference_form_correlated_composes_with_parametrized_outer_predicate()
    {
        // An outer Where parameter still substitutes correctly alongside the correlated inner $match.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_composes_with_parametrized_outer_predicate), out var owners, out _);

        var cutoff = owners[0].Name;
        var result = db.Owners.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_correlated_emits_expr_match_after_unwind()
    {
        // MQL assertion: the correlated conjunct renders as $expr in the post-$unwind $match.
        using var db = CreateRefContext(MongoQueryMode.Native,
            nameof(Reference_form_correlated_emits_expr_match_after_unwind), out _, out _, captureMql: true);

        _ = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .ToList();

        // Assert the pipeline contains a $lookup, an inner-join $unwind, and a $match with $expr comparing the
        // prefixed inner field to the root outer field. Use the file's existing AssertMql helper; if this file
        // uses a captured-log accessor instead, assert the captured MQL string contains "$expr" and
        // "_lookup_Refs.Tag" and "$Name". Match the exact assertion style already used by the neighboring
        // Filtered_*_emits_match_after_unwind / *_emits_lookup_unwind_* tests in this file.
    }
```

> Note for the implementer: `CreateRefContext`'s exact signature (whether it exposes seeded owners, an MQL-capture flag, etc.) is defined earlier in this same file — read it and the neighboring `Reference_form_filtered_inner_*` / `Filtered_reference_*` tests before writing these, and match their helper usage and seed shape exactly rather than inventing new helpers. The `Reference_form_correlated_emits_expr_match_after_unwind` MQL assertion in particular must mirror the existing `*_emits_*` tests' capture mechanism.

- [ ] **Step 3: Run the correlated functional tests (EF10)**

Run (both env vars unset → isolated atlas-local container):
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form_correlated"
```
Expected: PASS — all new correlated tests green.

- [ ] **Step 4: Run the full `NativeSelectManyTests` class (EF10) to catch fixture-change regressions**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"
```
Expected: PASS — adding `Threshold`/`Score` and re-seeding did not disturb the existing reference/owned tests.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: Functional tests for native correlated-beyond-FK reference SelectMany"
```

---

### Task 4: As-built docs

Document the newly-native shape and the guard-turned-router in the Query area's `AGENTS.md`, and update the filtered-inner note's "still deferred: correlated-beyond-FK" wording.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (the "Filtered inner-element reference `SelectMany`" note and the slice-5 note's "Still deferred" lists)

- [ ] **Step 1: Add the as-built note**

In `Query/AGENTS.md`, immediately after the **"Filtered inner-element reference `SelectMany` (EF-347 slice 5 Task 5)"** note, add a new note. Include:
- Scope: a reference `SelectMany` whose inner filter references the outer entity beyond the FK now goes native, for projected + bare-entity results, mixed/stacked filters, full comparison/arithmetic breadth.
- Mechanism: `MongoExpressionTranslator` gained an optional additive outer scope (member rooted on `outerParam` by `ReferenceEquals` → outer entity type at document root; else inner entity type with the `_lookup_<Nav>` prefix). `TryBindReferenceNavUnwind`'s `ReferencesParameter` check is now a **router**, not a decline: a correlated peeled layer is two-scope-translated and stored on `MongoUnwindSource.Filter`; the renderer already mixes dialects (inner-only conjuncts → plain `$match` terms, correlated field-to-field → `$expr`). Lowerer/renderer unchanged.
- Load-bearing point: identity routing (not name) is what disambiguates a shared member name (`RefItem.Name` vs `RefOwner.Name`) — the exact hazard the old guard declined on is now the routing signal.
- Decline: an unsupported correlated operator still declines with no mutation → hard-fails every mode (no cross-collection oracle).
- **Known interaction (EF-221):** the correlated field-to-field comparison inherits the provider's value-equality semantics for mismatched CLR types — no new guard.
- Multi-version: no `#if` (identical EF8/9/10 per the spike).

- [ ] **Step 2: Update the "still deferred" wording**

In the slice-5 note and the filtered-inner note, change the "Still deferred: a **correlated-beyond-FK** inner …" wording so correlated-beyond-FK is **no longer** listed as deferred for the reference form; the remaining deferrals are a computed projection leaf, a nested reference `SelectMany`, and the **owned** correlated-beyond-outer inner filter (the fast-follow).

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: AGENTS.md as-built note for native correlated-beyond-FK reference SelectMany"
```

---

## Finalization (controller-driven, after Task 4 review)

Not plan tasks — the controller runs these:

1. **Full 3-version `/test-all` foreground** (EF8/EF9/EF10), summing all three per-assembly summary blocks — GREEN 0-fail required.
2. **NativeOnly spec sweep** (`MONGODB_EF_NATIVE_ONLY=1`) — no regressions vs the `6ae61ac` baseline.
3. **Opus whole-branch review** (`6ae61ac..HEAD`) — resolve any Critical/Important; fold safe minors before squash.
4. **Squash** the slice to one commit above `6ae61ac`: back up `git branch -f EF-347-selectmany-correlated-beyond-fk-presquash HEAD`, then `git reset --soft 6ae61ac` + one `git commit -F <message>`; verify `git diff --quiet EF-347-selectmany-correlated-beyond-fk-presquash HEAD`. Keep the presquash backup until merge. The design/plan commits (`f6019e6` + design/plan docs) fold into the squash; the gitignored spike note is excluded.
5. **User drives the fast-forward push** of `origin/NativeQueryOngoing` (`6ae61ac` → new tip) — verify FF first (`git merge-base --is-ancestor origin/NativeQueryOngoing HEAD`), then a plain `git push` (no `--force`).

## Self-Review

- **Spec coverage:** Scope (projected + bare-entity, full breadth, mixed/stacked) → Tasks 1-3. Two-scope identity routing → Task 1. Guard-relaxation + no-partial-mutation → Task 2. Decline/no-oracle hard-fail → Task 2 (unit) + Task 3 (functional bare-entity via NativeOnly). EF-221 known interaction → no guard, noted in Task 4. Lowerer/renderer unchanged → verified by grounding (Filter → MongoMatchStage → RenderMatch(MongoQueryLanguageRenderer)); no task needed. Verification (3-version /test-all, NativeOnly sweep, opus, squash, FF push) → Finalization.
- **Placeholder scan:** All code steps show real code. The one soft spot — the `Reference_form_correlated_emits_expr_match_after_unwind` MQL assertion and `CreateRefContext` signature — is explicitly delegated to "read the neighboring `*_emits_*` tests and match their capture mechanism," because the exact MQL-capture helper is local to that test file and must be matched, not invented.
- **Type consistency:** `TryTranslateReferenceFilterLayer` signature (Task 2) consumes the Task-1 4-arg `MongoExpressionTranslator` constructor exactly. `MongoFieldExpression.ElementName`, `MongoBinaryExpression.Operator/Left/Right`, `MongoUnwindSource.Filter`, `MongoUnwindSourceKind.Reference`, `LookupExpression.ForceUnwind` all match the real types read during planning.
