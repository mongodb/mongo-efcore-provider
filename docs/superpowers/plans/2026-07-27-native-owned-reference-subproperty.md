# Native owned single-reference sub-property predicates & projections — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make predicates and projections over owned single-reference (OwnsOne) sub-properties — `Where(p => p.Home.City == "NYC")`, `Select(p => new { p.Home.City })`, at arbitrary owned-reference nesting depth — go native instead of falling back to driver-LINQ.

**Architecture:** Extend the single shared member-resolution gate `MongoExpressionTranslator.TryResolveMember`. When a member access is rooted on another member (not the bare query parameter), a new focused helper `TryResolveOwnedFieldPath` walks the owned single-reference chain and builds the dotted BSON element path via the existing `IReadOnlyEntityType.GetDocumentPath()` + `IReadOnlyProperty.GetElementName()` helpers. Because `TryResolveMember` also backs sort keys, `Contains`/regex operands, bare-bool access, field-to-field operands, and (via `TryTranslateField`) projection leaves, predicates and projections light up from this one change. Single-scope only; converter/representation correctness resolved by a de-risking spike.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals (EF1001), MongoDB C# driver LINQ v3, xUnit + FluentAssertions.

## Global Constraints

- Multi-EF targeting via build configurations `Debug EF8` / `Debug EF9` / `Debug EF10`; keep code `#if`-clean across all three (no new `#if` unless a genuine API difference forces it).
- All new types/members are `internal` (or `private`); `<Nullable>enable</Nullable>` on `src/` — annotate accordingly.
- Preserve file BOMs on any edited file.
- Not a breaking change: fallback→native with unchanged results; emitted MQL is not contract (per `AGENTS.md` versioning rubric). Disposition is graceful decline — an out-of-scope shape returns `false`/falls back exactly as today (throws only under `NativeOnly`).
- Tests run serially; each functional test uses a uniquely-named database/collection.
- Run the full three-version suite via the `/test-all` skill (foreground, per-version isolated testcontainers) before declaring done — a filter scoped to one class misses cross-class flips.
- Branch `EF-322-owned-ref-subproperty-native`, stacked on native tip `275c90e`; ships as one squashed commit at the end (stacked-PR convention). Keep a `-presquash` safety branch.

---

### Task 1: De-risking spike (throwaway)

Per the project's spike-first practice for silent-wrong-data risk (and matching the two predecessor owned slices, each of which produced a spike doc), settle the converter/shaper unknowns on a throwaway branch before touching production code.

**Files:**
- Create (throwaway): `.superpowers/sdd/EF-322-owned-ref-subproperty-spike.md` (findings doc; the code changes are discarded).

**Interfaces:**
- Consumes: nothing (investigation).
- Produces: three recorded decisions consumed by Tasks 2–3 — (D1) predicate converter behavior, (D2) projection converter behavior + whether a guard is needed and where, (D3) DOM-shaper dotted-alias read-back confirmed incl. absent-owned-ref.

- [ ] **Step 1: Create a throwaway spike branch**

```bash
git checkout -b EF-322-owned-ref-subproperty-spike
```

- [ ] **Step 2: Temporarily wire the owned path (throwaway)**

Apply a rough version of the Task-2 change to `MongoExpressionTranslator.TryResolveMember` (walk a member-on-member chain of embedded single-refs, build the path via `GetDocumentPath()` + `GetElementName()`). This is throwaway — correctness/naming don't matter, only that owned dotted paths resolve so the questions below can be exercised.

- [ ] **Step 3: Characterize the three unknowns with quick functional probes**

Write throwaway probe tests (a `Person` OwnsOne `Location` model; add one property with a value converter, e.g. `Location.City` with `HasConversion(v => v + "!", v => v.TrimEnd('!'))`, and one nested owned `Location` OwnsOne `Geo`):

- **D1 — predicate converter:** `Where(p => p.Home.City == "NYC")` under `Native` vs `DriverLinq`. Expected: equal result sets (the predicate serializes the RHS through the property serializer). Record actual.
- **D2 — projection converter:** first establish the *top-level* baseline — `Select(p => new { p.Name })` where `Name` has a converter, `Native` vs `DriverLinq` — to learn whether the existing native `$project` + DOM shaper already applies value converters on read-back. Then the owned case `Select(p => new { p.Home.City })`. Record whether either diverges and therefore whether a guard is required, and (if required) whether it must be owned-scoped or applies to all leaves.
- **D3 — dotted alias read-back + absent owned:** `Select(p => new { p.Home.City })` and nested `Select(p => new { p.Home.Geo.Country })` under `NativeOnly`, including a seeded document with **no** `Home` element. Confirm the DOM shaper reads `"$Home.City"` / `"$Home.Geo.Country"` correctly and yields `null` for absent owned.

- [ ] **Step 4: Write the findings doc and record decisions D1–D3**

Write `.superpowers/sdd/EF-322-owned-ref-subproperty-spike.md` capturing D1/D2/D3 with the observed evidence and the concrete guard decision for Task 3.

**Gate:** if D2 shows a divergence that cannot be closed by the `HasDefaultKeySerialization` guard (§4 of the spec), narrow the slice (predicates only, or default-serialized leaves only) and revise Tasks 2–3 before proceeding.

- [ ] **Step 5: Discard the spike code, keep only the doc**

```bash
git checkout EF-322-owned-collection-whole-entity-native
git checkout -b EF-322-owned-ref-subproperty-native
git checkout EF-322-owned-ref-subproperty-spike -- .superpowers/sdd/EF-322-owned-ref-subproperty-spike.md
git add .superpowers/sdd/EF-322-owned-ref-subproperty-spike.md
git commit -m "EF-322: owned-ref sub-property spike findings"
git branch -D EF-322-owned-ref-subproperty-spike
```

---

### Task 2: Owned dotted-path resolution for predicates & sort keys

Add `TryResolveOwnedFieldPath` and route member-on-member accesses to it from `TryResolveMember`. This lights up all predicate shapes (equality, null-checks, `Contains`, regex, bare-bool, field-to-field) and `OrderBy` keys over owned single-ref sub-properties.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (add `using Microsoft.EntityFrameworkCore.Infrastructure;` and `using MongoDB.EntityFrameworkCore.Extensions;`; restructure `TryResolveMember` at `:515`; add `TryResolveOwnedFieldPath` + `TryGetMemberOrEFProperty`).
- Test (unit): `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`.
- Test (functional): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedSubPropertyTests.cs` (new).

**Interfaces:**
- Consumes: existing `MongoExpressionTranslator` fields `_entityType`, `_outerParam`, `_innerPrefix`; `IReadOnlyEntityType.GetDocumentPath()` (namespace `MongoDB.EntityFrameworkCore.Extensions` — needs the new `using`), `IReadOnlyProperty.GetElementName()` (namespace `Microsoft.EntityFrameworkCore`, already imported), `IReadOnlyNavigation.IsEmbedded()` (namespace `MongoDB.EntityFrameworkCore.Extensions` — needs the new `using`), `MethodInfo.IsEFPropertyMethod()` (namespace `Microsoft.EntityFrameworkCore.Infrastructure` — needs the new `using`).
- Produces: `private bool TryResolveOwnedFieldPath(Expression node, out IProperty? property, out string? fieldPath)` and `private static bool TryGetMemberOrEFProperty(Expression, out Expression inner, out string name)`; `TryResolveMember` now resolves owned single-ref dotted paths (member-or-`EF.Property` hops) in single-scope mode. `MongoFieldExpression.ElementName` becomes a dotted string like `"Home.City"` for these.

- [ ] **Step 1: Write failing unit tests for owned path resolution**

Add to `MongoExpressionTranslatorTests.cs`. First add the owned model classes and a builder near the other fixtures:

```csharp
    private class OwnedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public OwnedAddress Address { get; set; } = null!;
    }

    private class OwnedAddress
    {
        public string City { get; set; } = "";
        public bool IsPrimary { get; set; }
        public OwnedGeo Geo { get; set; } = null!;
    }

    private class OwnedGeo
    {
        public string Country { get; set; } = "";
    }

    private static IEntityType GetOwnedBlogEntityType()
    {
        using var db = SingleEntityDbContext.Create<OwnedBlog>(mb =>
            mb.Entity<OwnedBlog>().OwnsOne(b => b.Address, a => a.OwnsOne(x => x.Geo)));
        return db.Model.FindEntityType(typeof(OwnedBlog))!;
    }

    // b => b.Address.City style selectors (value-type members get a Convert-to-object wrapper; strip it).
    private static Expression FieldBody<T>(Expression<Func<T, object?>> selector)
        => selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : selector.Body;
```

Then the tests:

```csharp
    [Fact]
    public void Owned_single_ref_subproperty_resolves_to_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var ok = translator.TryTranslateField(FieldBody<OwnedBlog>(b => b.Address.City), out var field);

        Assert.True(ok);
        Assert.Equal("Address.City", field!.ElementName);
    }

    [Fact]
    public void Nested_owned_single_ref_subproperty_resolves_to_deep_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var ok = translator.TryTranslateField(FieldBody<OwnedBlog>(b => b.Address.Geo.Country), out var field);

        Assert.True(ok);
        Assert.Equal("Address.Geo.Country", field!.ElementName);
    }

    [Fact]
    public void Owned_subproperty_comparison_translates_to_dotted_field_op()
    {
        var entityType = GetOwnedBlogEntityType();
        var body = PredicateBody<OwnedBlog>(b => b.Address.City == "NYC");
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        var field = Assert.IsType<MongoFieldExpression>(bin.Left);
        Assert.Equal("Address.City", field.ElementName);
    }

    [Fact]
    public void Owned_bare_bool_subproperty_translates_to_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var body = PredicateBody<OwnedBlog>(b => b.Address.IsPrimary);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Address.IsPrimary", field.ElementName);
    }

    [Fact]
    public void Two_scope_owned_subproperty_is_declined()
    {
        // A two-scope (SelectMany-unwind) translator must NOT engage the owned dotted-path walk.
        var outerType = GetOwnedBlogEntityType();
        var innerType = GetEntityType<Customer>();
        var outerParam = Expression.Parameter(typeof(OwnedBlog), "o");
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_X");
        var body = Expression.Property(Expression.Property(outerParam, nameof(OwnedBlog.Address)), nameof(OwnedAddress.City));

        Assert.False(translator.TryTranslateField(body, out _));
    }

    [Fact]
    public void Owned_subproperty_via_EFProperty_shape_resolves_to_dotted_field()
    {
        // Real EF-translated queries rewrite owned-nav hops to EF.Property(root, "Nav") calls (NOT plain
        // member access). Build that shape by hand to lock the EF.Property branch of the walk:
        //   EF.Property<OwnedAddress>(b, "Address").City   -> "Address.City"
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(OwnedBlog), "b");
        var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(OwnedAddress));
        var addressCall = Expression.Call(efProperty, param, Expression.Constant("Address"));
        var body = Expression.Property(addressCall, nameof(OwnedAddress.City));

        Assert.True(translator.TryTranslateField(body, out var field));
        Assert.Equal("Address.City", field!.ElementName);
    }
```

- [ ] **Step 2: Run the unit tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```
Expected: the five new positive tests FAIL (owned dotted path returns `false` today — the four compiled-lambda tests and `Owned_subproperty_via_EFProperty_shape_resolves_to_dotted_field`); `Two_scope_owned_subproperty_is_declined` already PASSES (nothing resolves it yet).

- [ ] **Step 3: Add the `using`s and the resolver, and restructure `TryResolveMember`**

> **Spike correction (Task 1 finding — load-bearing):** EF's nav-expansion does **not** hand the translator a plain `MemberExpression`-on-`MemberExpression` chain for `p.Home.City`. It rewrites each owned-navigation hop into an `EF.Property(root, "Nav")` **`MethodCallExpression`** (shadow-nav-safe), leaving scalar hops as `MemberExpression`s — so the outer node may itself be an `EF.Property(...)` call, and each hop is *either* shape. The walk below therefore accepts both at every hop (mirroring `NativeSelectManyBinder.TryGetMemberAccess`), and `TryResolveMember` delegates the whole node (not just a `MemberExpression`) to the resolver. Without this, real EF-translated queries silently decline every owned sub-property access (the spike verified this by tracing `NativeSlotPopulator`'s `Where` branch). Compiled C# lambdas in unit tests produce `MemberExpression` chains; real queries produce `EF.Property` chains — both branches must work, so Step 1 covers both.

Add to the usings block of `MongoExpressionTranslator.cs`:
```csharp
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using MongoDB.EntityFrameworkCore.Extensions;        // GetDocumentPath(), IsEmbedded()
```

Replace the top of `TryResolveMember` (`:515`) — the current early bail:
```csharp
        if (node is not MemberExpression { Expression: ParameterExpression param } me)
            return false;
```
with:
```csharp
        // Fast path: a bare top-level member on the query parameter (p.Foo). Everything else — a member
        // rooted on another hop, or an EF.Property(...) call produced by owned-nav expansion — is delegated
        // to the owned single-reference dotted-path resolver (single-scope only), which declines cleanly
        // (returns false) for any shape that is not a valid owned chain.
        if (node is not MemberExpression { Expression: ParameterExpression param } me)
            return TryResolveOwnedFieldPath(node, out property, out fieldPath);
```
(The existing bare-parameter body below — scope resolution, composite-PK guard, `GetElementName()`, inner-prefix — is unchanged.)

Add the two new helpers immediately after `TryResolveMember`:
```csharp
    /// <summary>
    /// Resolves a nested member/navigation access chain into an owned single-reference (OwnsOne) dotted
    /// document path, e.g. <c>p.Address.City</c> → element path <c>"Address.City"</c> and the <c>City</c>
    /// property. Each hop may be a <see cref="MemberExpression"/> (scalar access) or an
    /// <c>EF.Property(root, "Nav")</c> call (the shadow-nav-safe form EF's nav-expansion rewrites owned-nav
    /// access into); every non-leaf hop must resolve to an embedded single-reference navigation, and the chain
    /// must be rooted at the query parameter with a mapped scalar leaf. Returns <see langword="false"/> (caller
    /// falls back to driver-LINQ) for any other shape. Engaged only in single-scope mode — a two-scope
    /// SelectMany-unwind translator declines, because <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>
    /// yields a root-relative path that would not compose with the unwind-scope prefixing.
    /// </summary>
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: owned dotted paths are out of scope (declined, falls back)

        // Collect hop names from the outer (leaf) hop inward; the root must be the query parameter.
        var names = new List<string>();
        var current = node;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression)
            return false;

        // A single top-level member is handled by TryResolveMember's fast path, never here.
        if (names.Count < 2)
            return false;

        names.Reverse(); // now root-first: [firstNav, ..., leaf]

        var scopeType = _entityType;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded() || navigation.IsCollection)
                return false; // cross-collection or owned-collection intermediate → fall back
            scopeType = navigation.TargetEntityType;
        }

        var leaf = scopeType.FindProperty(names[^1]);
        if (leaf is null)
            return false;

        // Composite-PK components are stored under "_id" and are not addressable by their top-level element
        // name (mirrors the single-member guard in TryResolveMember).
        if (leaf.IsPrimaryKey() && leaf.FindContainingPrimaryKey()!.Properties.Count > 1)
            return false;

        property = leaf;
        // GetDocumentPath() gives the ordered containing element names from the document root down to the leaf's
        // declaring owned entity type; append the leaf's own element name. This is the exact dotted path the
        // shapers and pipeline use, so the emitted $match/$project/$sort addresses the stored field correctly.
        fieldPath = string.Join(".", leaf.DeclaringEntityType.GetDocumentPath().Append(leaf.GetElementName()));
        return true;
    }

    // A single access hop in either shape EF produces: a plain MemberExpression (scalar access) or an
    // EF.Property(root, "Name") call (owned-nav expansion). Mirrors NativeSelectManyBinder.TryGetMemberAccess.
    private static bool TryGetMemberOrEFProperty(Expression expression, out Expression inner, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } e } member:
                inner = e;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var root, ConstantExpression { Value: string propName }]:
                inner = root;
                name = propName;
                return true;

            default:
                inner = null!;
                name = null!;
                return false;
        }
    }
```

- [ ] **Step 4: Run the unit tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```
Expected: all pass, including the four new positive tests.

- [ ] **Step 5: Write functional predicate parity tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedSubPropertyTests.cs`. Follow the `NativeOwnedReferenceWholeEntityTests` pattern (BOM-preserving copy of the license header, `SingleEntityDbContext`, `TemporaryDatabaseFixture`):

```csharp
[XUnitCollection("QueryTests")]
public class NativeOwnedSubPropertyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Person
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Location Home { get; set; } = null!;
    }

    private class Location
    {
        public string City { get; set; } = "";
        public bool IsPrimary { get; set; }
        public Geo Geo { get; set; } = null!;
    }

    private class Geo
    {
        public string Country { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> PersonModel =
        mb => mb.Entity<Person>().OwnsOne(p => p.Home, h => h.OwnsOne(x => x.Geo));

    // Seeds 3 people: NYC/US/primary, LA/US/non-primary, and one with NO Home element (absent owned ref).
    private IMongoCollection<Person> SeedPeople(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Ann" },
                { "Home", new BsonDocument
                    { { "City", "NYC" }, { "IsPrimary", true }, { "Geo", new BsonDocument { { "Country", "US" } } } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" },
                { "Home", new BsonDocument
                    { { "City", "LA" }, { "IsPrimary", false }, { "Geo", new BsonDocument { { "Country", "US" } } } } }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Cid" } },
        ]);
        return database.MongoDatabase.GetCollection<Person>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Owned_subproperty_equality_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_equality_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var names = db.Entities.AsNoTracking().Where(p => p.Home.City == "NYC").Select(p => p.Name).ToList();

        Assert.Equal(["Ann"], names);
    }

    [Fact]
    public void Nested_owned_subproperty_equality_goes_native()
    {
        var collection = SeedPeople(nameof(Nested_owned_subproperty_equality_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var names = db.Entities.AsNoTracking()
            .Where(p => p.Home.Geo.Country == "US").OrderBy(p => p.Name).Select(p => p.Name).ToList();

        Assert.Equal(["Ann", "Bob"], names);
    }

    [Fact]
    public void Owned_bare_bool_subproperty_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_bare_bool_subproperty_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var names = db.Entities.AsNoTracking().Where(p => p.Home.IsPrimary).Select(p => p.Name).ToList();

        Assert.Equal(["Ann"], names);
    }

    [Fact]
    public void Owned_subproperty_orderby_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_orderby_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        // Absent-Home doc (Cid) sorts first (missing field), then LA, then NYC.
        var names = db.Entities.AsNoTracking()
            .Where(p => p.Name != "Cid").OrderBy(p => p.Home.City).Select(p => p.Name).ToList();

        Assert.Equal(["Bob", "Ann"], names);
    }

    [Fact]
    public void Owned_subproperty_predicate_matches_driver_linq_including_absent_owned()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_predicate_matches_driver_linq_including_absent_owned));

        using var native = CreateContext(collection, MongoQueryMode.Native, PersonModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, PersonModel);

        // Includes the absent-Home doc in the candidate set; both paths must agree it does not match.
        var nativeNames = native.Entities.AsNoTracking()
            .Where(p => p.Home.City == "NYC").Select(p => p.Name).OrderBy(n => n).ToList();
        var driverNames = driver.Entities.AsNoTracking()
            .Where(p => p.Home.City == "NYC").Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
        Assert.Equal(["Ann"], nativeNames);
    }
}
```

- [ ] **Step 6: Run the functional predicate tests**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedSubPropertyTests"
```
Expected: all pass. (`.Select(p => p.Name)` is a bare-scalar projection that itself falls back — but under `NativeOnly` the whole query would throw if the *predicate* didn't go native; the predicate is what we're proving. If a bare-scalar trailing `Select` interferes under `NativeOnly`, replace with `.ToList()` then project in memory — see note below.)

> **Note on `NativeOnly` + bare-scalar `Select`:** a bare-scalar projection is not native (pre-existing limitation, spec §7). If the trailing `.Select(p => p.Name)` causes the `NativeOnly` query to throw for projection reasons rather than predicate reasons, drop it: materialize whole `Person` entities with `.ToList()` under `NativeOnly` (whole-entity owned is already native per `275c90e`) and assert on `.Name` in memory. Adjust each `NativeOnly` predicate test accordingly during implementation; keep the DTO-projection coverage for Task 3.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedSubPropertyTests.cs
git commit -m "EF-322: owned single-ref sub-property predicates go native"
```

---

### Task 3: Owned dotted-path projections

DTO/anonymous projections of an owned dotted leaf (`Select(p => new { p.Home.City })`) light up automatically through `NativeProjectionBinder.TryTranslateLeaf` → `TryTranslateField` → the Task-2 resolver. This task proves that end-to-end and applies the converter guard.

> **Spike decision (Task 1 D2 — CONFIRMED):** a **value-converted / non-default-`BsonRepresentation` owned leaf DOES diverge** on the projection path: the DOM shaper's `MongoProjectionBindingRemovingExpressionVisitor.TryResolveFieldAccessSource` is single-hop and cannot resolve a nested owned chain, so it falls through to a raw, converter-free read — returning the stored value (`"NYC!"`) instead of the converted CLR value (`"NYC"`). The **top-level** converted case is unaffected (single-hop resolves) and already ships correct, so the guard MUST be **owned-scoped** (dotted path only) — guarding all leaves would regress the shipped top-level shape. The guard below is therefore **required**, not conditional. (The higher-value alternative — teaching `TryResolveFieldAccessSource` to walk nested chains so converted owned leaves go native *correctly* — is deferred: it touches the shared shaper visitor used by every native projection and is out of this slice's scope.)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`.
- Test (functional): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedSubPropertyTests.cs`.

**Interfaces:**
- Consumes: `NativeProjectionBinder.TryTranslateLeaf` (`:99`), `MongoExpressionTranslator.TryTranslateField`, `NativeGroupByBinder.HasDefaultKeySerialization(IProperty)` (`:101`).
- Produces: DTO projections of owned single-ref sub-properties emit `$project: { alias: "$Home.City" }` and materialize via the DOM shaper.

- [ ] **Step 1: Write failing functional projection tests**

Add to `NativeOwnedSubPropertyTests.cs`:

```csharp
    [Fact]
    public void Owned_subproperty_dto_projection_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_dto_projection_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var rows = db.Entities.AsNoTracking()
            .Where(p => p.Name != "Cid")
            .OrderBy(p => p.Name)
            .Select(p => new { p.Name, p.Home.City })
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Ann", "NYC"), (rows[0].Name, rows[0].City));
        Assert.Equal(("Bob", "LA"), (rows[1].Name, rows[1].City));
    }

    [Fact]
    public void Nested_owned_subproperty_dto_projection_goes_native()
    {
        var collection = SeedPeople(nameof(Nested_owned_subproperty_dto_projection_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var rows = db.Entities.AsNoTracking()
            .Where(p => p.Name == "Ann")
            .Select(p => new { p.Name, Country = p.Home.Geo.Country })
            .ToList();

        var row = Assert.Single(rows);
        Assert.Equal(("Ann", "US"), (row.Name, row.Country));
    }

    [Fact]
    public void Owned_subproperty_projection_matches_driver_linq_including_absent_owned()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_projection_matches_driver_linq_including_absent_owned));
        using var native = CreateContext(collection, MongoQueryMode.Native, PersonModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, PersonModel);

        // Cid has no Home → City projects as null; both paths must agree.
        var nativeRows = native.Entities.AsNoTracking()
            .OrderBy(p => p.Name).Select(p => new { p.Name, p.Home.City }).ToList();
        var driverRows = driver.Entities.AsNoTracking()
            .OrderBy(p => p.Name).Select(p => new { p.Name, p.Home.City }).ToList();

        Assert.Equal(
            driverRows.Select(r => (r.Name, r.City)),
            nativeRows.Select(r => (r.Name, r.City)));
        Assert.Null(nativeRows.Single(r => r.Name == "Cid").City);
    }
```

- [ ] **Step 2: Run the projection tests**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedSubPropertyTests"
```
Expected: the three new tests PASS immediately (the functional `Person` model has NON-converted leaves, so they light up via the shared gate from Task 2 and confirm spike decision D3 in a real test). The converter divergence is exercised separately in Step 3.

- [ ] **Step 3: Add the failing converted-leaf guard test, then the owned-scoped guard**

First add a test that a converted owned leaf diverges TODAY (owned-scoped model with a value converter). It must fail before the guard (Native returns the raw stored value) and pass after (falls back → matches DriverLinq):

```csharp
    private class Coded
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public CodedLoc Home { get; set; } = null!;
    }

    private class CodedLoc
    {
        public string Code { get; set; } = "";
    }

    // Home.Code carries a value converter, so the stored form ("NYC!") differs from the CLR form ("NYC").
    private static readonly Action<ModelBuilder> CodedModel = mb =>
        mb.Entity<Coded>().OwnsOne(c => c.Home, h => h.Property(x => x.Code).HasConversion(v => v + "!", v => v.TrimEnd('!')));

    private IMongoCollection<Coded> SeedCoded(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Ann" },
                { "Home", new BsonDocument { { "Code", "NYC!" } } } // stored WITH the converter suffix
            },
        ]);
        return database.MongoDatabase.GetCollection<Coded>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Converted_owned_subproperty_projection_matches_driver_linq()
    {
        var collection = SeedCoded(nameof(Converted_owned_subproperty_projection_matches_driver_linq));
        using var native = CreateContext(collection, MongoQueryMode.Native, CodedModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, CodedModel);

        var nativeCode = native.Entities.AsNoTracking().Select(c => new { c.Home.Code }).Single().Code;
        var driverCode = driver.Entities.AsNoTracking().Select(c => new { c.Home.Code }).Single().Code;

        // Both must return the converted CLR value "NYC" (not the raw stored "NYC!"). Before the guard,
        // Native leaks "NYC!" and this fails; the guard makes Native fall back to the correct driver path.
        Assert.Equal("NYC", driverCode);
        Assert.Equal(driverCode, nativeCode);
    }
```

Run it and confirm it FAILS (Native returns `"NYC!"`):
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedSubPropertyTests.Converted_owned_subproperty_projection_matches_driver_linq"
```

Then add the owned-scoped guard in `NativeProjectionBinder.TryTranslateLeaf` (`:107`) — the dotted `ElementName` (produced only by the owned walk) is the owned-scope signal, so top-level converted projections are untouched:

```csharp
        if (leafExpression is MemberExpression && translator.TryTranslateField(leafExpression, out var field))
        {
            // A dotted (owned single-ref) leaf is read back RAW by the DOM shaper (the shaper's field-access
            // resolver is single-hop and cannot apply the converter for a nested owned chain), so a
            // value-converted or non-default-BsonRepresentation owned leaf would diverge from the CLR value.
            // Decline it → the projection falls back to driver-LINQ (which resolves it correctly). Top-level
            // leaves have no dot and are unaffected (they already round-trip converters correctly).
            if (field.ElementName.Contains('.')
                && !NativeGroupByBinder.HasDefaultKeySerialization(field.Property))
            {
                result = null!;
                return false;
            }
            result = field;
            return true;
        }
```

(`NativeProjectionBinder` already has `using` for `NativeGroupByBinder`'s namespace — both are in `MongoDB.EntityFrameworkCore.Query.NativeTranslation`.)

- [ ] **Step 4: Re-run the projection tests to verify pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedSubPropertyTests"
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedSubPropertyTests.cs
git commit -m "EF-322: owned single-ref sub-property projections go native"
```

---

### Task 4: Handle eligibility flips, re-baseline, and document

New shapes going native flips any spec/functional test that asserted these owned sub-property shapes fall back, and shifts the `NativeOnly` spec pass-set. Handle deliberately and update the area doc.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (add a note describing the new owned dotted-path support).
- Modify: any spec/functional test that previously asserted an owned sub-property predicate/projection falls back (found in Step 1).
- Modify (measurement only): none permanent.

**Interfaces:**
- Consumes: the two-sweep `NativeOnly` measurement from the status doc (`docs/native-query-status-EF-322.md` §7.4).
- Produces: updated AGENTS.md note; updated flipped tests asserting native with verified-correct data.

- [ ] **Step 1: Find tests that asserted these shapes fall back**

Run:
```bash
grep -rn "Home.City\|Address.City\|\.City ==\|owned.*sub-?propert" tests/ --include=*.cs -i | grep -i "fallback\|falls_back\|NotSupported\|DriverLinq\|non-?representable"
```
Also skim `NativeOwnedReferenceWholeEntityTests` / `NativeOwnedCollectionWholeEntityTests` for any assertion that a sub-property predicate/projection declines. For each hit, update it to assert the new native behavior **and** verify correct data (use the `Native == DriverLinq` oracle). If none exist (these shapes may simply have been untested rather than pinned), record that explicitly.

- [ ] **Step 2: Re-baseline the `NativeOnly` spec sweep**

Build and run the two-sweep measurement per `docs/native-query-status-EF-322.md` §7.4:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"
dotnet test  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory /private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/eaa6cbd4-0042-4302-89cb-e384709a1f54/scratchpad/sweep
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory /private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/eaa6cbd4-0042-4302-89cb-e384709a1f54/scratchpad/sweep
```
Expected: `Native` still fails 0; the `NativeOnly` pass count rises (fallback count drops) by exactly the owned sub-property predicate/projection shapes. Confirm the delta is only those shapes — investigate any unexpected flip before proceeding.

- [ ] **Step 3: Update the Query AGENTS.md note**

Add a concise note under the projection/predicate section of `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` describing: owned single-reference sub-property dotted-path predicates/sorts/projections now go native via `TryResolveOwnedFieldPath` (single-scope only; embedded single-ref chain; `GetDocumentPath()`-built path); the composite-PK and (if added) converter guards; and what remains deferred (owned-collection sub-properties `Any`/`All`/array-projection; two-scope owned access; bare-scalar projection). Match the existing note style.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md tests/
git commit -m "EF-322: document owned sub-property native support; handle eligibility flips"
```

---

### Task 5: Full three-version verification and squash

**Files:** none (verification + history hygiene).

**Interfaces:** consumes all prior tasks.

- [ ] **Step 1: Safety branch before squashing**

```bash
git branch EF-322-owned-ref-subproperty-native-presquash
```

- [ ] **Step 2: Run the full suite on all three EF versions**

Invoke the `/test-all` skill (builds + tests `Debug EF8`, `Debug EF9`, `Debug EF10` in parallel, foreground, per-version isolated testcontainers). Expected: all three green, no regressions. Fix any failure before continuing — a per-class filter would have missed cross-class flips, so this full run is the gate.

- [ ] **Step 3: Squash to a single commit (stacked-PR convention)**

```bash
git reset --soft 275c90e
git commit -m "EF-322: owned single-ref sub-property predicates & projections go native

A predicate, sort key, or DTO/anonymous projection over an owned
single-reference (OwnsOne) sub-property — Where(p => p.Home.City == x),
OrderBy(p => p.Home.City), Select(p => new { p.Home.City }), at arbitrary
owned-reference nesting depth — now goes native; previously it always fell
back to driver-LINQ.

Root cause was the single shared gate MongoExpressionTranslator.TryResolveMember,
which accepted only a member rooted on the bare query parameter. A new
TryResolveOwnedFieldPath walks an owned single-reference chain (every
non-leaf member IsEmbedded() && !IsCollection, rooted at the parameter) and
builds the dotted BSON element path via GetDocumentPath() + GetElementName().
Because TryResolveMember also backs sort keys, Contains/regex operands,
bare-bool access, field-to-field operands, and (via TryTranslateField)
projection leaves, predicates and projections light up together.

Single-scope only (two-scope SelectMany-unwind declines); composite-PK leaves
declined; converted/non-default-BsonRepresentation owned projection leaves declined (owned-scoped guard). Owned-collection sub-properties,
two-scope owned access, and bare-scalar projection remain deferred. Not a
break (fallback->native, results unchanged; MQL is not contract). Includes the
spike findings, design spec, and implementation plan under docs/superpowers/
and .superpowers/sdd/."
```

- [ ] **Step 4: Confirm the squash is byte-identical to the presquash tip**

```bash
git diff EF-322-owned-ref-subproperty-native-presquash --stat
```
Expected: empty (no content difference; only history collapsed).

- [ ] **Step 5: Hand off for review**

Stop for review per the subagent-driven-development rhythm. Do not push or open a PR until the user directs.

---

## Self-Review

**Spec coverage:**
- §1 problem / single gate → Task 2 (TryResolveMember restructure). ✓
- §2 in-scope predicates (equality, null, Contains, regex, bare-bool, field-to-field) → Task 2 unit + functional tests + the shared-gate mechanism. ✓
- §2 in-scope DTO projections + nested depth → Task 3. ✓
- §3 approach B (extracted resolver) → Task 2 Step 3. ✓
- §4 guards: single-scope → Task 2 resolver + unit test; composite-PK → Task 2 resolver; converter asymmetry → Task 1 spike + Task 3 Step 3. ✓
- §5 spike → Task 1. ✓
- §6 eligibility flips + re-baseline → Task 4. ✓
- §7 non-goals (owned-collection, bare-scalar, two-scope) → declined by construction (Task 2 resolver) + documented (Task 4 Step 3). ✓
- §8 testing (parity, NativeOnly routing, unit, /test-all) → Tasks 2/3/5. ✓

**Placeholder scan:** the only deferred decision is the converter guard, which is explicitly spike-resolved (Task 1 D2) with concrete guard code provided in Task 3 Step 3 for the divergence branch and a concrete test for the no-divergence branch — not a placeholder. No TBD/TODO.

**Type consistency:** `TryResolveOwnedFieldPath(Expression, out IProperty?, out string?)` + `TryGetMemberOrEFProperty(Expression, out Expression, out string)` consistent across Tasks 2–5; `HasDefaultKeySerialization(IProperty)` matches `NativeGroupByBinder:101`; `GetDocumentPath()`/`GetElementName()`/`IsEmbedded()`/`FindNavigation`/`TargetEntityType` are the real EF/provider APIs verified during planning; `MongoFieldExpression.ElementName` is the real accessor.
