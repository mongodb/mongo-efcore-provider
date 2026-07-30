# Native array-valued projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an owned entity-collection leaf inside a terminal anonymous-type / DTO projection go native, so `Select(b => new { b.Title, b.Posts })` emits a server-side `$project` instead of fetching whole documents with `aggregate([])`.

**Architecture:** A new marker expression node (`ArrayAliasProjectionExpression`) and a shared interface (`IArrayProjectionExpression`) let the DOM shaper's existing `CollectionShaperExpression` machinery source its `BsonArray` from a `$project` output alias instead of from a navigation's document path. The emit side adds one leaf branch to `NativeProjectionBinder`. **The alias itself is never carried on the node** — it flows from the `ProjectionExpression` the post-processor builds from the `ProjectionMember`, exactly as every scalar leaf's alias does, which makes the two alias spaces agree by construction.

**Tech Stack:** C#, EF Core 8/9/10 (build configurations `Debug EF8`/`EF9`/`EF10`), MongoDB C# driver, xUnit. No new dependencies.

## Global Constraints

- **Design spec:** `docs/superpowers/specs/2026-07-29-native-array-valued-projections-design.md`. **Spike findings:** `docs/superpowers/specs/2026-07-29-native-array-valued-projections-spike-findings.md`. Read both before Task 1.
- **Branch:** `EF-360`, currently two docs-only commits on `7c199e4`. Commit per task; the whole branch is squashed to one commit at the end (Task 10).
- **Preserve file BOMs** on every existing file you edit.
- `src/` is `<Nullable>enable</Nullable>` — annotate all new types.
- **No `#if`** is expected; every new type is `internal`. If you find yourself reaching for `EF8`/`EF9`/`EF10` guards, stop and flag it.
- **Build:** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. **Test:** `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "..."`.
- **Leave `MONGODB_URI` and `ATLAS_URI` unset** so TestContainers boots an isolated `mongodb/mongodb-atlas-local` container per test process. Prefix test commands with `env -u MONGODB_URI -u ATLAS_URI` if your shell exports them.
- **Routing is proven ONLY by `MongoQueryMode.NativeOnly`,** never by MQL shape. `NativeOnly` succeeding = native; `NativeTranslationNotSupportedException` = fell back.
- **Test fixtures must be un-masked:** never write `= []` on a collection navigation under test, and seed missing/null array states as **raw BSON** so the field is genuinely absent or genuinely BSON null. The previous slice's blocking bug survived nine review rounds because every fixture was masked; the *original framing of this slice* was wrong for the same reason.
- **Plain xUnit `Assert.*` only.** FluentAssertions is not referenced by the test projects.
- **Do not change the exception type or message for a reference-collection array projection leaf** (`Select(c => new { c.Name, c.Orders })`). It raises EF Core's own `InvalidOperationException` "could not be translated", and five spec tests (10 cases) assert exactly that via EF Core's upstream `AssertTranslationFailed`. Changing it breaks them on both axes with no re-baselining possible.
- **Do not attempt to fix EF-360** (an element type carrying its own navigation). Task 4 declines it structurally so it stays byte-identical for a following slice.
- Baseline to preserve, measured at `21b2e61`: EF10 SpecificationTests **`Native` 4589 pass / 0 fail / 19 skip**, **`NativeOnly` 2194 pass / 2395 fail / 19 skip**.

---

## Mechanism: what the spike and a code read established

The design doc's §3.2 describes adding a new array-*source* branch to the `CollectionShaperExpression` case that calls `BsonBinding.CreateGetBsonArray(DocParameter, alias)` directly. **That is not necessary, and the plan below deliberately does not do it.** Reading the shaper end-to-end shows the existing path already does exactly this once `VisitBinary` is taught the new node:

1. `BsonDocumentInjectingExpressionVisitor` (`BsonDocumentInjectingExpressionVisitor.cs:71-114`) creates a `bsonArrayN` variable per collection shaper and assigns it `Expression.TypeAs(collectionShaperExpression.Projection, typeof(BsonArray))`.
2. `MongoProjectionBindingRemovingExpressionVisitor.VisitBinary` (`:264-361`) intercepts that assignment. At `:282-287`, because our `Projection` is a `ProjectionBindingExpression`, it already resolves `projectionExpression = projection.Expression` **and sets `fieldName = projection.Alias`**.
3. At `:295-300` it needs an arm for our node — registering `_projectionBindings[node] = parameterExpression` and setting `innerAccessExpression = node.AccessExpression`. Without one it falls to the `else` at `:301` which hard-casts to `EntityProjectionExpression` and throws `InvalidCastException`.
4. Its tail at `:357` calls `CreateGetValueExpression(innerAccessExpression, fieldName, fieldRequired, typeof(BsonArray))`, which resolves a `RootReferenceExpression` to `DocParameter` (`:630`) and then dispatches on `mappedType == typeof(BsonArray)` to `BsonBinding.CreateGetBsonArray(doc, alias)` (`BsonBinding.cs:59-61` and `:115-117`, both overloads).
5. Back in the `CollectionShaperExpression` case, `_projectionBindings.TryGetValue(node, ...)` now succeeds, so the array comes from the injector variable via the **first** branch at `:152-156` — and flows into the untouched EF-358 `Coalesce` at `:183`.

So the read-back needs exactly two edits: a `VisitBinary` arm, and widening the `:136-148` switch's hard cast to admit both node kinds. `fieldRequired` is irrelevant on this path (the `BsonArray` branch ignores it), and `CreateGetBsonArray` returns `null` for both a missing field and an explicit BSON null — which is precisely why the EF-358 coalesce is load-bearing here rather than defensive.

**Consequence for the node's shape:** since the alias arrives from `ProjectionExpression.Alias` at step 2, `ArrayAliasProjectionExpression` does **not** carry an alias. It is a marker that means "this array is read at a `$project` output alias, not at a navigation's document path". Alias agreement between the emit side and the shaper is therefore automatic — both derive from the same `ProjectionMember` — which removes the alias-divergence hazard `Query/AGENTS.md` warns about for hand-passed aliases.

---

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/IArrayProjectionExpression.cs` | The two-line contract the DOM shaper's collection case needs from an array projection node: navigation, owner access expression, inner entity projection, and the document-path field name (null when the array is alias-addressed). |
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/ArrayAliasProjectionExpression.cs` | The alias-addressed sibling of `ObjectArrayProjectionExpression`. |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/ArrayAliasProjectionExpressionTests.cs` | Node-level unit tests (type, equality, update, print). |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs` | The slice's whole functional surface. |

**Modify:**

| File | Change |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/ObjectArrayProjectionExpression.cs:15` | Implement `IArrayProjectionExpression` (add `ArrayFieldName => Name`). |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:136-165` | Widen the switch to `IArrayProjectionExpression`; rename the local. |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:295-300` | Widen the `VisitBinary` arm to `IArrayProjectionExpression`. |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs:764-778` | On `Route == Projection`, register the array as one projection member and wrap an `ArrayAliasProjectionExpression`. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs:48-91, 99-...` | Array-leaf branch in `TryTranslateLeaf`; owner-`_id` emission in `TryPopulateNativeProjection`. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` | New `TryTranslateOwnedCollectionArray` entry point wrapping the existing `TryResolveOwnedCollectionPath`. |
| `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | New as-built note; correct the "Array-valued projections are still NOT native" note. |
| `docs/native-query-status-EF-322.md` | Correct bullet 2 of the owned-collection follow-ons; re-characterise EF-360. |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs:339-341` | Correct the comment that claims the anonymous shape throws in every mode. |

---

### Task 1: The node and the shared interface

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/IArrayProjectionExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/ArrayAliasProjectionExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/ObjectArrayProjectionExpression.cs:15`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/ArrayAliasProjectionExpressionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal interface IArrayProjectionExpression { INavigation Navigation { get; } Expression AccessExpression { get; } EntityProjectionExpression InnerProjection { get; } string? ArrayFieldName { get; } }` and `internal sealed class ArrayAliasProjectionExpression : Expression, IPrintableExpression, IArrayProjectionExpression` with constructor `(INavigation navigation, Expression accessExpression, EntityProjectionExpression? innerProjection = null)` and method `ArrayAliasProjectionExpression Update(Expression accessExpression, EntityProjectionExpression innerProjection)`. Tasks 2 and 3 depend on both.

This task is inert — nothing constructs the new node yet, so no behaviour changes.

- [ ] **Step 1: Write the failing unit test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/ArrayAliasProjectionExpressionTests.cs`. Match the surrounding unit-test conventions (look at a sibling file under `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` first for the exact `using` set and how a test model is built).

```csharp
public class ArrayAliasProjectionExpressionTests
{
    // A minimal model with one owned collection, built through a real ModelBuilder so the
    // INavigation and IEntityType are genuine rather than mocked.
    private static INavigation GetPostsNavigation()
    {
        var builder = MongoTestHelpers.Instance.CreateConventionBuilder();
        builder.Entity<Blog>().OwnsMany(b => b.Posts);
        var model = builder.FinalizeModel();
        return model.FindEntityType(typeof(Blog))!.FindNavigation(nameof(Blog.Posts))!;
    }

    [Fact]
    public void Type_is_the_enumerable_of_the_element_clr_type()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var sut = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Equal(typeof(IEnumerable<Post>), sut.Type);
    }

    // The node is alias-addressed, so it deliberately has NO document-path field name. This is what
    // tells MongoProjectionBindingRemovingExpressionVisitor.VisitBinary to keep the alias it already
    // resolved from the ProjectionExpression rather than substituting a navigation element name.
    [Fact]
    public void ArrayFieldName_is_null_because_the_array_is_addressed_by_projection_alias()
    {
        var navigation = GetPostsNavigation();
        var sut = new ArrayAliasProjectionExpression(navigation, new RootReferenceExpression(navigation.DeclaringEntityType));

        Assert.Null(((IArrayProjectionExpression)sut).ArrayFieldName);
    }

    [Fact]
    public void Inner_projection_defaults_to_an_entity_projection_over_the_element_type()
    {
        var navigation = GetPostsNavigation();
        var sut = new ArrayAliasProjectionExpression(navigation, new RootReferenceExpression(navigation.DeclaringEntityType));

        Assert.Same(navigation.TargetEntityType, sut.InnerProjection.EntityType);
    }

    [Fact]
    public void Equal_nodes_are_equal_and_hash_alike()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var a = new ArrayAliasProjectionExpression(navigation, access);
        var b = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // Equality must NOT collapse with the navigation-driven sibling: the two nodes address the same
    // array by different mechanisms, and _projectionBindings is keyed on the node, so conflating them
    // would let a document-path read satisfy an alias lookup.
    [Fact]
    public void Is_not_equal_to_an_object_array_projection_for_the_same_navigation()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var alias = new ArrayAliasProjectionExpression(navigation, access);
        var byPath = new ObjectArrayProjectionExpression(navigation, access);

        Assert.NotEqual<object>(alias, byPath);
    }

    [Fact]
    public void Update_returns_the_same_instance_when_nothing_changed()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);
        var sut = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Same(sut, sut.Update(access, sut.InnerProjection));
    }

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }

    private class Post
    {
        public string? Heading { get; set; }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~ArrayAliasProjectionExpressionTests"
```

Expected: compile failure — `ArrayAliasProjectionExpression` and `IArrayProjectionExpression` do not exist.

- [ ] **Step 3: Create the interface**

`src/MongoDB.EntityFrameworkCore/Query/Expressions/IArrayProjectionExpression.cs`:

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// What <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s
/// <c>CollectionShaperExpression</c> case needs from a node describing an array of entities, regardless of
/// how that array is addressed in the document it is read from. Two implementations:
/// <see cref="ObjectArrayProjectionExpression"/> reads the array at the navigation's own document path;
/// <see cref="ArrayAliasProjectionExpression"/> reads it at a <c>$project</c> output alias.
/// </summary>
internal interface IArrayProjectionExpression
{
    /// <summary>The collection navigation this array materializes into.</summary>
    INavigation Navigation { get; }

    /// <summary>Access to the document that OWNS the array — used for the owned element's owner-key read.</summary>
    Expression AccessExpression { get; }

    /// <summary>The per-element entity projection.</summary>
    EntityProjectionExpression InnerProjection { get; }

    /// <summary>
    /// The BSON element name the array sits at, or <see langword="null"/> when the array is addressed by a
    /// projection alias instead. A <see langword="null"/> here means the caller must already have resolved an
    /// alias from the owning <see cref="ProjectionExpression"/>; see the <c>??=</c> in
    /// <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s <c>VisitBinary</c>.
    /// </summary>
    string? ArrayFieldName { get; }
}
```

- [ ] **Step 4: Create the node**

`src/MongoDB.EntityFrameworkCore/Query/Expressions/ArrayAliasProjectionExpression.cs`:

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An array of owned entities read back from a native <c>$project</c> OUTPUT ALIAS rather than from the
/// navigation's own document path — the alias-addressed sibling of
/// <see cref="ObjectArrayProjectionExpression"/> (EF-322 owned-data slice 8).
/// </summary>
/// <remarks>
/// <para>
/// This node deliberately carries NO alias. The alias is resolved by
/// <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s <c>VisitBinary</c> from the
/// <see cref="ProjectionExpression"/> that the post-processor built from this projection's
/// <c>ProjectionMember</c> — the identical mechanism every scalar projection leaf uses. Carrying an alias
/// here as well would create a second, independently-derived copy of the same name and reintroduce exactly
/// the emit-side/shaper-side alias divergence documented in <c>Query/AGENTS.md</c>.
/// </para>
/// <para>
/// It is a separate type from <see cref="ObjectArrayProjectionExpression"/>, not a flag on it, because that
/// node's contract is "read this navigation at its containing element name": its <c>Name</c> is derived from
/// <c>GetContainingElementName()</c> and participates in its equality. Two nodes that address the same array
/// by different mechanisms must not compare equal — <c>_projectionBindings</c> is keyed on the node itself.
/// </para>
/// </remarks>
internal sealed class ArrayAliasProjectionExpression : Expression, IPrintableExpression, IArrayProjectionExpression
{
    public ArrayAliasProjectionExpression(
        INavigation navigation,
        Expression accessExpression,
        EntityProjectionExpression? innerProjection = null)
    {
        var targetType = navigation.TargetEntityType;
        Type = typeof(IEnumerable<>).MakeGenericType(targetType.ClrType);
        Navigation = navigation;
        AccessExpression = accessExpression;
        InnerProjection = innerProjection
                          ?? new EntityProjectionExpression(targetType, new RootReferenceExpression(targetType));
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type { get; }

    public INavigation Navigation { get; }

    public Expression AccessExpression { get; }

    public EntityProjectionExpression InnerProjection { get; }

    /// <inheritdoc />
    /// <remarks>Always <see langword="null"/>: this array is addressed by projection alias.</remarks>
    public string? ArrayFieldName => null;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression), (EntityProjectionExpression)visitor.Visit(InnerProjection));

    public ArrayAliasProjectionExpression Update(
        Expression accessExpression,
        EntityProjectionExpression innerProjection)
        => accessExpression != AccessExpression || innerProjection != InnerProjection
            ? new ArrayAliasProjectionExpression(Navigation, accessExpression, innerProjection)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    public override string ToString()
        => $"{AccessExpression}[<alias>] /* {Navigation.Name} */";

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ArrayAliasProjectionExpression other && Equals(other));

    private bool Equals(ArrayAliasProjectionExpression other)
        => Navigation == other.Navigation
           && AccessExpression.Equals(other.AccessExpression)
           && InnerProjection.Equals(other.InnerProjection);

    public override int GetHashCode()
        => HashCode.Combine(Navigation, AccessExpression, InnerProjection);
}
```

- [ ] **Step 5: Make the existing node implement the interface**

In `ObjectArrayProjectionExpression.cs`, change the declaration at line 15 and add the one member. Nothing else in that file changes.

```csharp
internal sealed class ObjectArrayProjectionExpression
    : Expression, IPrintableExpression, IAccessExpression, IArrayProjectionExpression
```

Add next to the existing `Name` property:

```csharp
    /// <inheritdoc />
    /// <remarks>This node addresses its array by document path, so the field name is its <see cref="Name"/>.</remarks>
    public string? ArrayFieldName => Name;
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~ArrayAliasProjectionExpressionTests"
```

Expected: all PASS. If `MongoTestHelpers.Instance.CreateConventionBuilder()` is not the right entry point in this repo, find the one the sibling unit tests use and adjust — do not mock `INavigation`.

- [ ] **Step 7: Confirm nothing else moved**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build
```

Expected: 0 failures. This task is inert, so any failure here is a real regression from the interface addition.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/ tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/
git commit -m "EF-360: add IArrayProjectionExpression + ArrayAliasProjectionExpression"
```

---

### Task 2: Read-back side — teach the DOM shaper the alias-addressed node

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:136-165` and `:295-300`
- Test: none new (this task is inert on its own; Task 3 makes it live). Regression-tested by the existing suite.

**Interfaces:**
- Consumes: `IArrayProjectionExpression` (Task 1).
- Produces: a shaper that materializes a `CollectionShaperExpression` whose `Projection` is a `ProjectionBindingExpression` resolving to **either** array-projection node kind.

Read the "Mechanism" section at the top of this plan before starting — it explains why no new array-*source* branch is needed.

- [ ] **Step 1: Widen the `CollectionShaperExpression` switch**

Replace lines 136-148 with the following. The local's type changes from the concrete class to the interface; the hard cast becomes a type test that fails into the same `TranslationFailed` the surrounding switch already throws.

```csharp
                    IArrayProjectionExpression arrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            // Both array-projection node kinds are admissible: a navigation-driven
                            // ObjectArrayProjectionExpression (a projected reference collection) and an
                            // ArrayAliasProjectionExpression (a native $project alias, EF-322 slice 8).
                            // This USED TO BE an unchecked cast to ObjectArrayProjectionExpression, which
                            // would throw InvalidCastException rather than declining. Anything that is not an
                            // array projection is a translation failure.
                            if (projection.Expression is not IArrayProjectionExpression fromProjection)
                            {
                                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                            }

                            arrayProjection = fromProjection;
                            break;
                        case IArrayProjectionExpression inlineArrayProjection:
                            arrayProjection = inlineArrayProjection;
                            break;
                        default:
                            throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                    }
```

- [ ] **Step 2: Rename the local's uses in the rest of the case**

In the same case body (lines ~150-208 before the edit), rename every `objectArrayProjection` to `arrayProjection`, and change the one document-path read at the old line 163-164 to use the interface member:

```csharp
                        var parentAccess = arrayProjection.AccessExpression;
                        var parentDoc = _projectionBindings[parentAccess];
                        bsonArrayExpression = BsonBinding.CreateGetBsonArray(parentDoc, arrayProjection.ArrayFieldName!);
                        arrayName = arrayProjection.ArrayFieldName!;
```

Leave the EF-358 coalesce, the `_projectionBindings`/`_ownerMappings`/`_ordinalMappings` writes, `AddIncludes`, and the `PopulateCollection` tail **exactly** as they are. The `arrayProjection.Navigation.DeclaringEntityType` and `arrayProjection.AccessExpression` reads all resolve through the interface unchanged.

- [ ] **Step 3: Add the `VisitBinary` arm**

Replace lines 295-300 with:

```csharp
                    Expression innerAccessExpression;
                    if (projectionExpression is IArrayProjectionExpression arrayProjectionExpression)
                    {
                        innerAccessExpression = arrayProjectionExpression.AccessExpression;
                        _projectionBindings[projectionExpression] = parameterExpression;

                        // ArrayFieldName is null for an alias-addressed array, in which case fieldName was
                        // already set from projection.Alias above (the ProjectionBindingExpression branch) —
                        // that IS the alias, and it is the same name the emit side derived from the same
                        // ProjectionMember. A null here with no alias resolved would mean the node reached this
                        // point some other way, which is a bug rather than a shape to tolerate.
                        fieldName ??= arrayProjectionExpression.ArrayFieldName
                                      ?? throw new InvalidOperationException(
                                          CoreStrings.TranslationFailed(binaryExpression.Print()));
                    }
```

This is a strict generalization: for an `ObjectArrayProjectionExpression` the behaviour is byte-identical (`ArrayFieldName` returns `Name`, and the `_projectionBindings` key is the same node object).

- [ ] **Step 4: Build and run the whole functional Query suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```

Expected: 0 failures. This task changes no behaviour — it only widens two type tests — so a failure means the rename or the arm is wrong.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs
git commit -m "EF-360: DOM shaper accepts an alias-addressed array projection"
```

---

### Task 3: Walking skeleton — one owned array leaf goes native end-to-end

This is the risky integration task. It deliberately uses the **easiest** model — an element with an explicit `HasKey` and no navigations — so the owner-key question (Task 4) is out of the way. Get it green before adding breadth.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs:764-778`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs` (create)

**Interfaces:**
- Consumes: `ArrayAliasProjectionExpression`, `IArrayProjectionExpression` (Task 1); the widened shaper (Task 2).
- Produces:
  - `MongoExpressionTranslator.TryTranslateOwnedCollectionArray(Expression expression, [NotNullWhen(true)] out MongoElementRefExpression? result)` — `internal`, returns `true` for a rooted member-access chain whose final hop is an embedded collection navigation.
  - `NativeProjectionBinder` accepting an array leaf; Task 4 adds a guard inside it, Task 5 relies on it for breadth.

- [ ] **Step 1: Read the three sites you are about to change**

Read, and do not skip this — the plan's code below assumes these exact shapes:
- `NativeProjectionBinder.cs:34-91` (`TryPopulateNativeProjection`) and `:99-193` (`TryTranslateLeaf`).
- `MongoProjectionBindingExpressionVisitor.cs:400-406` — the count-leaf registration. **This is the precedent your new registration must mirror**: `GetCurrentProjectionMember()`, `_projectionMapping[member] = ...`, return a `ProjectionBindingExpression`.
- `MongoProjectionBindingExpressionVisitor.cs:756-782` — the `navigationProjection` switch you are adding to.
- `MongoExpressionTranslator`'s `TryResolveOwnedCollectionPath` — note its exact signature and what it returns (the scope-relative dotted array path), and its two guards (`_outerParam`/`_innerPrefix` two-scope decline; final hop must be an embedded collection navigation).

- [ ] **Step 2: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs`. Copy the class scaffolding conventions from `NativeOwnedCollectionCountTests.cs` (its `[XUnitCollection("QueryTests")]` attribute, primary constructor, `CreateContext(MongoQueryMode)` factory, `SpyLoggerProvider` MQL capture helper, and unique collection naming) — read that file's first ~90 lines and mirror them.

```csharp
[XUnitCollection("QueryTests")]
public class NativeArrayProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // NOTE the deliberate absence of `= []` on Posts. A field initializer masks exactly the class of bug
    // this slice touches; see the design doc's verification section.
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }

    // No navigations of its own, and an EXPLICIT key: this is the walking-skeleton model. An element
    // carrying a navigation is EF-360 (declined in Task 4); a shadow-key element needs the owner _id
    // emitted (Task 4).
    public class Post
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> KeyedModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    [Fact]
    public void Owned_array_leaf_in_an_anonymous_projection_goes_native()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_in_an_anonymous_projection_goes_native));

        using var db = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);

        var results = db.Set<Blog>().AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => new { b.Title, b.Posts })
            .ToList();

        Assert.Equal(new[] { "a_empty", "b_one", "c_two" }, results.Select(r => r.Title));
        Assert.Equal(new[] { 0, 1, 2 }, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] { "h1" }, results[1].Posts.Select(p => p.Heading));
        Assert.Equal(new[] { "h2", "h3" }, results[2].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Owned_array_leaf_projection_emits_a_project_stage_and_no_longer_fetches_whole_documents()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_projection_emits_a_project_stage_and_no_longer_fetches_whole_documents));
        var spy = new SpyLoggerProvider();

        using var db = CreateContext(collection, KeyedModel, MongoQueryMode.Native, spy);
        _ = db.Set<Blog>().AsNoTracking().Select(b => new { b.Title, b.Posts }).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("$project", mql);
        Assert.Contains("Posts", mql);
        Assert.DoesNotContain("aggregate([])", mql);
    }

    [Fact]
    public void Owned_array_leaf_projection_matches_driver_linq()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_projection_matches_driver_linq));

        static List<(string Title, int Count, string?[] Headings)> Run(DbContext db)
            => db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { b.Title, b.Posts })
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, r.Posts.Select(p => p.Heading).ToArray()))
                .ToList();

        using var native = CreateContext(collection, KeyedModel, MongoQueryMode.Native);
        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);

        Assert.Equal(Run(driver), Run(native));
    }
}
```

Write `SeedKeyed` to insert three raw BSON documents — `a_empty` with `Posts: []`, `b_one` with one element, `c_two` with two — following how `NativeOwnedCollectionCountTests` seeds raw BSON. Ragged states (missing/null) come in Task 6.

- [ ] **Step 3: Run the test to verify it fails**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeArrayProjectionTests"
```

Expected: the `NativeOnly` test FAILS with `NativeTranslationNotSupportedException` ("Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback") because the binder still declines the array leaf. The MQL test FAILS on `aggregate([])`. The parity test PASSES already (both modes fall back today) — that is expected and it is the regression guard for this task.

- [ ] **Step 4: Add the translator entry point**

In `MongoExpressionTranslator`, add next to the other `TryTranslate*` entry points:

```csharp
    /// <summary>
    /// Resolves a rooted member-access chain whose final hop is an embedded COLLECTION navigation into a raw
    /// element reference for the array itself (e.g. <c>b.Posts</c> → <c>Posts</c>,
    /// <c>b.Home.Notes</c> → <c>Home.Notes</c>), for use as a native projection leaf.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="TryResolveOwnedCollectionPath"/>, so it inherits that resolver's guards: the
    /// chain must be rooted at the query parameter, every non-final hop must be an embedded single-reference
    /// navigation, and the FINAL hop must be an embedded collection navigation. That last requirement is the
    /// structural protection against a mapped scalar property that happens to share a navigation's name — a
    /// scalar's receiver is an entity, never a collection.
    /// </remarks>
    internal bool TryTranslateOwnedCollectionArray(
        Expression expression,
        [NotNullWhen(true)] out MongoElementRefExpression? result)
    {
        if (TryResolveOwnedCollectionPath(expression, out var arrayPath))
        {
            result = new MongoElementRefExpression(arrayPath);
            return true;
        }

        result = null;
        return false;
    }
```

Adjust to `TryResolveOwnedCollectionPath`'s real signature as read in Step 1 (it may take/return different shapes — the *contract* above is what matters, not the exact plumbing).

- [ ] **Step 5: Add the emit-side leaf branch**

In `NativeProjectionBinder.TryTranslateLeaf`, add a branch **after** the existing plain-field branch and before the count branch. The leaf arrives as a `MaterializeCollectionNavigationExpression`, never a `MemberExpression` (spike Q2), so this branch is structurally disjoint from every other one.

```csharp
        // An owned entity-collection leaf (EF-322 slice 8): `new { b.Title, b.Posts }`. EF's nav-expansion
        // always wraps the navigation in a MaterializeCollectionNavigationExpression — measured for the
        // anonymous, EF.Property and MemberInit spellings alike — so this never collides with the
        // MemberExpression branch above. A PRIMITIVE collection (`b.Tags`) is a mapped property, arrives as a
        // plain member access, and is already accepted by that branch; it never reaches here.
        if (leafExpression is MaterializeCollectionNavigationExpression materializeCollection
            && materializeCollection.Navigation is INavigation collectionNavigation
            && collectionNavigation.IsEmbedded()
            && collectionNavigation.IsCollection
            && translator.TryTranslateOwnedCollectionArray(materializeCollection.Subquery, out var arrayRef))
        {
            result = arrayRef;
            return true;
        }
```

If `materializeCollection.Subquery` is not the member-access chain the resolver wants, unwrap it to the underlying access first — Step 1's read tells you the shape. Do **not** pass the wrapper.

- [ ] **Step 6: Register the array as one projection member in the shaper builder**

In `MongoProjectionBindingExpressionVisitor.cs`, in the `navigationProjection` switch, add a new case **before** the existing `case ObjectArrayProjectionExpression` (lines 764-778):

```csharp
            // EF-322 slice 8: on the fully-native projection route, the array is read back from the $project
            // OUTPUT ALIAS, not from the navigation's document path — so register it as ONE projection member
            // (exactly as the count leaf does at the top of VisitMethodCall) and hand the shaper an
            // ArrayAliasProjectionExpression. The alias itself is derived by the post-processor from this
            // ProjectionMember, which is the same name NativeProjectionBinder used on the emit side, so the
            // two alias spaces agree by construction.
            //
            // The Route == Projection guard is load-bearing for the same reason it is on the count and
            // arithmetic cases: NativeProjectionBinder sets Route = Projection only when EVERY leaf is
            // natively representable, so a mixed or fallback shape must fall through to the arm below and be
            // shaped client-side by the mixed shaper exactly as before this slice.
            case ObjectArrayProjectionExpression when _queryExpression.Select.Route == NativeRoute.Projection:
                {
                    var arrayProjectionMember = GetCurrentProjectionMember();
                    var aliasedArray = new ArrayAliasProjectionExpression(
                        navigation,
                        innerEntityProjection.ParentAccessExpression);

                    _projectionMapping[arrayProjectionMember] = aliasedArray;

                    var aliasedInnerShaper = new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(
                            Expression.Convert(aliasedArray.InnerProjection, typeof(object)),
                            typeof(ValueBuffer)),
                        nullable: true);

                    return new CollectionShaperExpression(
                        new ProjectionBindingExpression(_queryExpression, arrayProjectionMember, aliasedArray.Type),
                        aliasedInnerShaper,
                        navigation,
                        aliasedInnerShaper.StructuralType.ClrType);
                }
```

- [ ] **Step 7: Run the test to verify it passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeArrayProjectionTests"
```

Expected: all three PASS.

**If the `NativeOnly` test throws `InvalidOperationException: Document element is missing for required non-nullable property`,** you have hit the owner-key problem early — the model in this task uses an explicit `HasKey` specifically to avoid it, so re-check that the `HasKey` is actually applied. Task 4 handles the shadow-key case.

**If it throws `InvalidCastException`,** Task 2's `VisitBinary` arm is not matching — confirm `projectionExpression` is your node at that point, not a `ProjectionBindingExpression`.

- [ ] **Step 8: Run the whole functional Query suite**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```

Expected: 0 failures. Pay attention to `ProjectedCollectionNormalizationTests` and `NativeOwnedCollectionCountTests` — their `Post` types carry a nested `Comments` collection, so they exercise Task 4's territory and must be unaffected until then.

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/ tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs
git commit -m "EF-360: an owned array leaf in an anonymous projection goes native"
```

---

### Task 4: Owner-key emission and the element-navigation decline

Two guards that both fall out of what the spike measured. Grouped because they are the two admissibility questions on the same branch, and a reviewer would judge them together.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs`

**Interfaces:**
- Consumes: the array-leaf branch (Task 3).
- Produces: no new public surface. After this task, a shadow-key element works and an element with a navigation declines.

- [ ] **Step 1: Write the failing shadow-key test**

Add to `NativeArrayProjectionTests`. This is the model shape almost every real user has — no explicit key on the owned element, so EF creates a shadow FK to the owner and the element shaper reads the owner's key through `_ownerMappings`.

```csharp
    private static readonly Action<ModelBuilder> ShadowKeyModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts);

    // Measured (spike Q1a): with a shadow-key owned collection the element shaper reads the OWNER's key out
    // of the document it is given. A $project that emits only { Title, Posts } has no _id, and materialization
    // then fails PER ROW with InvalidOperationException "Document element is missing for required
    // non-nullable property 'Id'" at Storage/BsonBinding.cs:229. So the array-leaf branch must emit the
    // owner key alongside. An explicit-HasKey element never emits that read at all (Q1b), which is why the
    // walking-skeleton test in Task 3 passed without this.
    [Fact]
    public void Owned_array_leaf_with_a_shadow_key_element_goes_native()
    {
        var collection = SeedShadow(nameof(Owned_array_leaf_with_a_shadow_key_element_goes_native));

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);

        var results = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new { b.Title, b.Posts })
            .ToList();

        Assert.Equal(new[] { 0, 1, 2 }, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] { "h2", "h3" }, results[2].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Owned_array_leaf_projection_emits_the_owner_key()
    {
        var collection = SeedShadow(nameof(Owned_array_leaf_projection_emits_the_owner_key));
        var spy = new SpyLoggerProvider();

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.Native);
        _ = db.Set<Blog>().AsNoTracking().Select(b => new { b.Title, b.Posts }).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("_id", mql);
        // RenderProject emits an explicit `_id : 0` suppression UNLESS the projection itself emits _id.
        Assert.DoesNotContain("\"_id\" : 0", mql);
    }
```

- [ ] **Step 2: Run to verify the shadow-key test fails**

Expected: `InvalidOperationException: Document element is missing for required non-nullable property 'Id'`, thrown from `BsonBinding.GetPropertyValue` via `PopulateCollection`. If it instead passes, re-verify the model has no explicit key — a passing test here means you are not exercising the shadow-key path.

- [ ] **Step 3: Emit the owner key**

In `NativeProjectionBinder.TryPopulateNativeProjection`, track whether any leaf was an array leaf and, if so, add the owner-key projection before the projections are applied. Add a `bool` alongside the existing `projections`/`pendingLookups` locals, set it in the array-leaf branch, then just before the `foreach (var projection in projections)` loop:

```csharp
        // EF-322 slice 8: an owned element with a SHADOW key reads its owner's key out of the document the
        // shaper is handed, so a $project that omits _id makes materialization throw PER ROW
        // ("Document element is missing for required non-nullable property"). Emit the root key alongside the
        // requested aliases. This is inert for the result shape — the shaper reads by alias, never
        // positionally — and it correctly suppresses RenderProject's `_id : 0`, which is only emitted when the
        // projection does not itself emit _id. An explicit-HasKey element never emits the owner-key read at
        // all, so emitting _id is harmless there too; keying this on the leaf kind rather than on the element's
        // key kind keeps one code path instead of two.
        if (hasArrayLeaf && seenAliases.Add("_id"))
        {
            projections.Add(new MongoProjection("_id", new MongoElementRefExpression("_id")));
        }
```

Note the `seenAliases.Add("_id")` guard: a user projection with its own member literally named `_id` would already have taken that alias, and the binder's case-insensitive collision rule must keep governing.

- [ ] **Step 4: Run to verify the shadow-key tests pass**

Both new tests PASS. Then re-run the walking-skeleton tests from Task 3 — they must still pass with the extra `_id` in the pipeline.

- [ ] **Step 5: Write the failing element-navigation decline test**

```csharp
    public class NestedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<NestedPost> Posts { get; set; } = null!;
    }

    public class NestedPost
    {
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = null!;
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> NestedModel = mb =>
        mb.Entity<NestedBlog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    // EF-360, NOT this slice's to fix. An element type carrying ANY navigation of its own — a nested owned
    // collection or a nested owned single reference, failing identically — makes EF's nav-expansion emit the
    // auto-include as an inner Queryable.Select, which MongoProjectionBindingExpressionVisitor rebuilds as an
    // enumerable and Expression.New's member-type validation then rejects at
    // MongoProjectionBindingExpressionVisitor.cs:661. That is an ArgumentException in EVERY mode, before
    // MongoQueryMode is read. This slice's array-leaf branch declines such an element structurally, so the
    // shape stays BYTE-IDENTICAL rather than being perturbed into a different failure.
    [Fact]
    public void Element_with_its_own_navigation_is_declined_and_still_fails_identically_in_every_mode()
    {
        var collection = SeedNested(nameof(Element_with_its_own_navigation_is_declined_and_still_fails_identically_in_every_mode));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, NestedModel, mode);

            var ex = Assert.Throws<ArgumentException>(
                () => db.Set<NestedBlog>().AsNoTracking().Select(b => new { b.Title, b.Posts }).ToList());

            Assert.Contains("does not match the corresponding member type", ex.Message);
        }
    }

    // The bare spelling on the SAME nested model works and must keep working — it takes the mixed path and
    // never reaches the native projection binder at all.
    [Fact]
    public void Bare_array_projection_of_an_element_with_its_own_navigation_still_works()
    {
        var collection = SeedNested(nameof(Bare_array_projection_of_an_element_with_its_own_navigation_still_works));

        using var db = CreateContext(collection, NestedModel, MongoQueryMode.Native);

        var results = db.Set<NestedBlog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => b.Posts)
            .ToList();

        Assert.Equal(new[] { 0, 2 }, results.Select(r => r.Count));
    }
```

- [ ] **Step 6: Run to see how it currently behaves**

Run the two tests. Record what actually happens — the decline test may already pass (if the element navigation happens to make `TryResolveOwnedCollectionPath` decline) or may fail with a *different* exception (if the branch accepts it and the crash moves). **Write down which**, because it decides whether Step 7 is a real change or a no-op guard.

- [ ] **Step 7: Add the representability guard**

In the array-leaf branch, before calling the translator:

```csharp
            // Decline an element type that carries ANY navigation of its own (EF-360). Mirrors
            // IsWholeElementRepresentable's owned-navigation guard. This is NOT an optimization gap: such a
            // projection throws ArgumentException at shaper-build time in every mode, before MongoQueryMode is
            // read, so admitting it here could only move or mask that crash rather than fix it. Declining
            // leaves the shape byte-identical for the follow-on slice that fixes the underlying MatchTypes gap.
            && !collectionNavigation.TargetEntityType.GetNavigations().Any()
```

- [ ] **Step 8: Run both tests plus the full functional Query suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```

Expected: 0 failures, `ProjectedCollectionNormalizationTests` and `NativeOwnedCollectionCountTests` included.

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs
git commit -m "EF-360: emit the owner key; decline an element carrying its own navigation"
```

---

### Task 5: Breadth — every in-scope spelling and shape

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs`
- Modify (only if a test fails): the Task 3 sites.

**Interfaces:**
- Consumes: everything from Tasks 3-4. Produces nothing new — this task is coverage, plus whatever fixes it forces.

Write these as separate `[Fact]`s, run each, and fix the implementation where one fails. Every one asserts under `NativeOnly` (routing) **and** compares against `DriverLinq` (values), except where noted.

- [ ] **Step 1: Add the spelling and shape tests**

One test each, all against the shadow-key model unless stated:

1. `Named_dto_projection_goes_native` — `Select(b => new TitlePosts { Title = b.Title, Posts = b.Posts })` with a nested `public class TitlePosts { public string Title { get; set; } = ""; public List<Post> Posts { get; set; } = null!; }`. Exercises `NativeProjectionBinder`'s `MemberInitExpression` branch rather than `NewExpression`.
2. `EF_Property_spelling_goes_native` — `Select(b => new { b.Title, P = EF.Property<List<Post>>(b, "Posts") })`. Spike Q2 measured this normalizes to the identical tree, so it should pass with no code change; the test pins that.
3. `Two_array_leaves_go_native` — `Select(b => new { b.Posts, b.Drafts })` with a second `OwnsMany(b => b.Drafts)`. Assert both arrays' contents, not just counts, so a swapped-alias bug is caught.
4. `Array_leaf_alongside_a_count_leaf_goes_native` — `Select(b => new { b.Title, N = b.Posts.Count, b.Posts })`. Assert `N` equals `Posts.Count` per row and that the MQL contains both `$size` and the array field.
5. `Array_leaf_alongside_an_arithmetic_leaf_goes_native` — add an `int Rank` to `Blog`, project `new { X = b.Rank * 2, b.Posts }`.
6. `Array_leaf_with_an_alias_that_differs_from_the_navigation_name_goes_native` — `Select(b => new { b.Title, Mine = b.Posts })`. **This is the alias-agreement test**: the emit alias is `Mine` while the navigation's element name is `Posts`. If the emit side and the shaper ever derive the alias independently, this is the test that fails.
7. `Array_reached_through_an_owned_reference_hop_goes_native` — `Blog { Home { List<Note> Notes } }` via `OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes))`, projected as `Select(b => new { b.Title, b.Home.Notes })`. Assert the MQL contains the dotted path `Home.Notes`. **This also covers residual spike item 1** — the `OwnsOne`-hop owner-key composition that was NOT MEASURED against an owner-key-less document. Use a shadow-key `Note` so the owner-key read is genuinely exercised; if it throws the missing-`Id` error, the `_id` emission from Task 4 is insufficient for a nested owner and you must record what the nested owner key actually needs before widening the fix.
8. `HashSet_navigation_goes_native` — a `Blog` variant whose navigation is `HashSet<Post>`. Assert the runtime type is a `HashSet<Post>`, proving the `IClrCollectionAccessor` path rather than a hand-made `List<T>`.
9. `Tracking_query_with_an_array_leaf_throws_EF_Cores_owned_tracking_guard` — no `AsNoTracking()`. Under `Native` and `DriverLinq` expect `InvalidOperationException` containing "owned entities cannot be tracked without their owner"; under `NativeOnly` expect `NativeTranslationNotSupportedException` instead, because the provider's decline fires first (spike Q5, measured — the exception type genuinely differs by mode here).
10. `Primitive_collection_leaf_alongside_an_owned_array_leaf_goes_native` — `Select(b => new { b.Tags, b.Posts })`. Spike Q2 measured a primitive collection already goes native via the plain-field branch; this pins that the two leaf kinds compose.

- [ ] **Step 2: Run them and fix what breaks**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeArrayProjectionTests"
```

For each failure: read the exception, decide whether the shape should be native or declined, and either fix the branch or convert the test into a documented decline (asserting `NativeTranslationNotSupportedException` under `NativeOnly` **and** correct values under `Native`). Do not silently drop a shape from the list — if you decline one, say so in the test name and comment.

- [ ] **Step 3: Run the full functional suite**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build
```

Expected: 0 failures.

- [ ] **Step 4: Commit**

```bash
git add -A tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs src/
git commit -m "EF-360: cover every in-scope array-projection spelling and shape"
```

---

### Task 6: Ragged arrays, the differential oracle, and converters through the alias

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs`

**Interfaces:** consumes Tasks 3-5; produces nothing new.

- [ ] **Step 1: Add the five-state matrix test**

Seed **raw BSON** for five documents: `a_missing` (no `Posts` field at all), `b_null` (`Posts: null`), `c_empty` (`Posts: []`), `d_single`, `e_multi`.

```csharp
    // EF-358's contract, on the new native route: a MISSING or explicitly-BSON-null stored array materializes
    // as an EMPTY collection, never null, uniformly with every other path. The coalesce that guarantees this
    // lives at the point of use in MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression
    // case, and it is load-bearing on this route specifically because BsonBinding.CreateGetBsonArray returns
    // null for BOTH of those states.
    [Fact]
    public void Native_array_projection_normalizes_missing_and_null_arrays_to_empty()
    {
        var collection = SeedFiveStates(nameof(Native_array_projection_normalizes_missing_and_null_arrays_to_empty));

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);

        var results = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new { b.Title, b.Posts })
            .ToList();

        Assert.All(results, r => Assert.NotNull(r.Posts));
        Assert.Equal(new[] { 0, 0, 0, 1, 2 }, results.Select(r => r.Posts.Count));
    }
```

- [ ] **Step 2: Add the differential oracle**

The oracle materializes **whole entities** and compiles the *same* selector client-side, so it cannot be satisfied by a bug that is symmetric across both native and driver paths.

```csharp
    [Fact]
    public void Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state()
    {
        var collection = SeedFiveStates(nameof(Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state));

        Expression<Func<Blog, int>> countSelector = b => b.Posts.Count;

        using var oracleDb = CreateContext(collection, ShadowKeyModel, MongoQueryMode.Native);
        var expected = oracleDb.Set<Blog>().AsNoTracking().OrderBy(b => b.Title).ToList()
            .Select(countSelector.Compile()).ToList();

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);
        var actual = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new { b.Title, b.Posts })
            .ToList()
            .Select(r => r.Posts.Count).ToList();

        Assert.Equal(expected, actual);
    }
```

- [ ] **Step 3: Add the converter-through-the-alias test**

Spike Q4 proved element converters work on every path that existed *before* this slice, but could not prove the alias path — this test is the slice's own obligation.

```csharp
    public enum Grade { Low, High }

    public class ConvBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<ConvPost> Posts { get; set; } = null!;
    }

    public class ConvPost
    {
        public string? Heading { get; set; }
        public Guid Ref { get; set; }
        public Grade Code { get; set; }
    }

    private static readonly Action<ModelBuilder> ConverterModel = mb =>
        mb.Entity<ConvBlog>().OwnsMany(b => b.Posts, p =>
        {
            p.Property(x => x.Ref).HasBsonRepresentation(BsonType.String);
            p.Property(x => x.Code).HasConversion(v => v.ToString(), v => (Grade)Enum.Parse(typeof(Grade), v));
        });

    // The element shaper is unchanged by this slice — only WHERE its BsonArray comes from changes — so
    // element-level value converters and non-default BsonRepresentation must round-trip through the alias
    // read exactly as they do through a document-path read. Spike Q4 could only measure the pre-existing
    // paths, so this test is what actually proves it on the native route.
    [Fact]
    public void Element_converters_round_trip_through_the_alias_read()
    {
        var (collection, expectedRef) = SeedConverters(nameof(Element_converters_round_trip_through_the_alias_read));

        using var db = CreateContext(collection, ConverterModel, MongoQueryMode.NativeOnly);

        var posts = db.Set<ConvBlog>().AsNoTracking()
            .Select(b => new { b.Title, b.Posts })
            .Single().Posts.OrderBy(p => p.Heading).ToList();

        Assert.Equal(expectedRef, posts[0].Ref);
        Assert.Equal(Grade.High, posts[0].Code);
        Assert.Equal(Grade.Low, posts[1].Code);
    }
```

Seed the stored `Ref` and `Code` as genuine BSON **strings** so a raw read would visibly differ from a converted one.

- [ ] **Step 4: Run and fix**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeArrayProjectionTests"
```

Expected: PASS. If the converter test fails, **do not** add a converter guard without first checking whether the element shaper is being rebuilt rather than reused — the design's no-guard decision rests on it being reused.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs
git commit -m "EF-360: ragged-array matrix, differential oracle, converter round-trip"
```

---

### Task 7: The set-op operand question

`TryTranslateLeaf` is shared by projected set-op operands and trailing projections, so an array leaf silently becomes admissible there too. Spec §5.3 leaves this to a probe.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeArrayProjectionTests.cs`
- Modify (only if the probe is bad): `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`

- [ ] **Step 1: Probe a projected-operand union carrying an array leaf**

```csharp
    [Fact]
    public void Projected_operand_union_with_an_array_leaf()
    {
        var collection = SeedShadow(nameof(Projected_operand_union_with_an_array_leaf));

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.Native);

        var results = db.Set<Blog>().AsNoTracking().Where(b => b.Title == "b_one")
            .Select(b => new { b.Title, b.Posts })
            .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Title == "c_two")
                .Select(b => new { b.Title, b.Posts }))
            .ToList();

        Assert.Equal(2, results.Count);
    }
```

Run it. `Union` dedups by whole-document value equality over the *projected* documents, which now contain arrays.

- [ ] **Step 2: Decide, based on what you measured**

- **If it returns correct results:** keep the test, rename it `..._goes_native` or `..._falls_back_with_correct_results` to match the measured routing (check under `NativeOnly`), and add a comment recording that array-in-`$$ROOT` dedup was measured sound.
- **If it returns wrong rows, throws, or dedups surprisingly:** decline an array leaf in a set-op-operand position. Gate on the same signal `IsPlainProjectedSelect` uses, add the decline in the array-leaf branch, and convert the test into a decline test asserting graceful fallback with correct results under `Native` (`Union`/`Concat` have a driver oracle) — and note that `Intersect`/`Except` have none, so those hard-fail in every mode.

Either way, **write down in the test comment what you measured**, not what you expected.

- [ ] **Step 3: Commit**

```bash
git add -A tests/ src/
git commit -m "EF-360: settle the set-op-operand disposition for an array leaf"
```

---

### Task 8: Mutation-verify every new gate

A gate no test defends is a gate that will be deleted by a later refactor. This task proves each new decision point is load-bearing. **Every mutation must be reverted before the next one.**

**Files:** no permanent changes expected — this task adds tests where a mutation turns nothing red.

- [ ] **Step 1: Run each mutation and record the result**

For each row: apply the mutation, `dotnet build -c "Debug EF10"`, run the functional Query suite, record which tests fail, then `git checkout -- src/`.

| # | Mutation | Expected to turn red |
|---|---|---|
| 1 | Delete `&& _queryExpression.Select.Route == NativeRoute.Projection` from Task 3's new case in `MongoProjectionBindingExpressionVisitor` | Mixed/fallback array projections — e.g. `ProjectedCollectionNormalizationTests`, and any `new { b, b.Posts }` whole-entity-plus-array shape |
| 2 | Delete the `!collectionNavigation.TargetEntityType.GetNavigations().Any()` guard (Task 4) | `Element_with_its_own_navigation_is_declined_and_still_fails_identically_in_every_mode` |
| 3 | Delete the `hasArrayLeaf` `_id` emission (Task 4) | `Owned_array_leaf_with_a_shadow_key_element_goes_native`, `Owned_array_leaf_projection_emits_the_owner_key` |
| 4 | Change the EF-358 `Coalesce` at `:183` to pass the array through unchanged | `Native_array_projection_normalizes_missing_and_null_arrays_to_empty` **and** the pre-existing EF-358 tests |
| 5 | In `VisitBinary`, key `_projectionBindings` on a fresh object instead of `projectionExpression` | Every array-projection test (the shaper would take the parent-document branch) |
| 6 | Swap the array-leaf branch above the plain-field branch in `TryTranslateLeaf` | **Expected: nothing.** Spike Q2 says the branches are structurally disjoint. If nothing goes red, that is the correct result — record it, and do **not** claim the ordering is load-bearing. |
| 7 | Make `ArrayAliasProjectionExpression.Equals` also return true for an `ObjectArrayProjectionExpression` with the same navigation | Task 1's `Is_not_equal_to_an_object_array_projection_for_the_same_navigation` |

- [ ] **Step 2: Close any gap**

For any row (other than #6) where nothing turned red, write a test that does catch it, then re-run the mutation to confirm the test fails and reverting makes it pass.

- [ ] **Step 3: Establish coverage of the pre-existing `:141` arm — residual spike item 2**

The spike found that **no** probe reached the old `ProjectionBindingExpression`/`GetProjection` arm of the collection switch; every collection shaper it observed took the inline `ObjectArrayProjectionExpression` arm. That arm is now shared with your new node, so its behaviour matters.

Mutate it: make the `ProjectionBindingExpression` case throw unconditionally, rebuild, and run the **whole** functional suite plus the EF10 spec suite. Record which tests fail. If **only** this slice's tests fail, then before this slice that arm was genuinely dead for every covered shape — write that down in the AGENTS.md note in Task 10, because it means a future reader cannot assume the arm is exercised by the projected-reference-collection path.

- [ ] **Step 4: Commit any tests added**

```bash
git status   # MUST show no src/ changes — every mutation reverted
git add -A tests/
git commit -m "EF-360: mutation-verify the new gates"
```

---

### Task 9: Multi-version and both-axes spec sweep

**Files:** none — this task is measurement. It produces the numbers Task 10's documentation must quote.

- [ ] **Step 1: Three-version sweep**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"  && env -u MONGODB_URI -u ATLAS_URI dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8"  --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"  && env -u MONGODB_URI -u ATLAS_URI dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9"  --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && env -u MONGODB_URI -u ATLAS_URI dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

Or invoke the `/test-all` skill, which runs all three in parallel. Expected: **0 failures on all three**, and the pass-count increase should be **uniform** across versions — this slice adds no `#if`, so a non-uniform delta means something is version-sensitive and must be explained before proceeding.

- [ ] **Step 2: Spec suite, both axes**

```bash
env -u MONGODB_URI -u ATLAS_URI dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
env -u MONGODB_URI -u ATLAS_URI MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
```

Baseline at `21b2e61`: `Native` 4589/0/19, `NativeOnly` 2194/2395/19.

**Predicted delta: zero on both axes** — Northwind has no owned collections, and every array-valued projection in the suite is a reference-collection leaf this slice does not touch.

- [ ] **Step 3: If anything moved, diagnose before rebaselining**

Check **both** axes per changed test — `NativeOnly` pass/fail *and* `Native`-mode emitted MQL. A test that is `NativeOnly`-failing but carries a live `Native`-mode MQL baseline is invisible on the pass-set axis alone; that is the mistake the `All` slice made, and all 2395 `NativeOnly` failures are `Native` passes, so the exposure is the whole fail set.

**In particular:** if any of the five `AssertTranslationFailed` reference-collection tests changed, you have violated the global constraint. That is a stop-and-fix, not a rebaseline — they assert an exception *type*, which `EF_TEST_REWRITE_BASELINES` cannot repair.

- [ ] **Step 4: Record the numbers**

Write the six figures (three versions × pass/fail, plus both spec axes) into the commit message. Task 10 quotes them.

```bash
git commit --allow-empty -m "EF-360: three-version + both-axes sweep results

EF8 <p>/<f>, EF9 <p>/<f>, EF10 <p>/<f>.
Spec EF10 Native <p>/<f>/<s>, NativeOnly <p>/<f>/<s>."
```

---

### Task 10: Documentation sweep and squash

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: `docs/native-query-status-EF-322.md`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectedCollectionNormalizationTests.cs:339-341`
- Modify: `docs/superpowers/specs/2026-07-29-native-array-valued-projections-design.md`

Sweep by **behaviour**, not by ticket vocabulary — grepping for "EF-360" or "array projection" will miss statements phrased differently.

- [ ] **Step 1: Correct the three places that state the blocking reason wrongly**

1. `Query/AGENTS.md` — the note beginning "**Array-valued projections are still NOT native.**" Replace it with the as-built note (Step 2), and correct its claim that the blocker is the `:141` hard cast: the mixed path never reached that arm.
2. `docs/native-query-status-EF-322.md:275-278` — bullet 2 of the owned-collection follow-ons. It says array projections are "blocked on the DOM-shaper mechanism alone". Correct it: that was true of the wrapped spelling (now done) and never of the bare spelling, which is additionally blocked by the SP3-wide bare-projection boundary. Move the bare form to its own remaining-work bullet.
3. `ProjectedCollectionNormalizationTests.cs:339-341` — the comment claiming `Select(b => new { b.Title, b.Posts })` "is an ArgumentException in every mode and every array state … confirmed by direct probe, not assumed". It is not: that fixture's `Post` declares `List<Comment> Comments`, and the element navigation was the trigger. Correct it in place and say so — this is a masked-fixture correction, exactly the class of error the file's own EF-358 comments warn about.

- [ ] **Step 2: Add the as-built note to `Query/AGENTS.md`**

Follow the house style of the surrounding notes: what went native, the mechanism, what is declined and why, what was measured versus assumed, and the flips. It must record, at minimum:

- The shape that is now native and the emitted `$project`, including the owner `_id`.
- That the alias is **not** carried on `ArrayAliasProjectionExpression` — it flows from the `ProjectionExpression` built from the `ProjectionMember`, the same mechanism scalar leaves use, which is what makes the two alias spaces agree by construction.
- That the read-back needed **no** new array-source branch: `VisitBinary` + the existing injector variable + `CreateGetValueExpression`'s `BsonArray` dispatch already produce `GetBsonArray(rootDoc, alias)`. State this explicitly — the design doc's first draft asserted a direct `CreateGetBsonArray(DocParameter, alias)` call in the collection case, and a future reader will otherwise look for code that does not exist.
- The `Route == Projection` guard's load-bearing role, and mutation #1's result.
- The owner-`_id` emission, why shadow-key needs it and explicit-`HasKey` does not, and the exact failure if it is dropped.
- The element-navigation decline, that it is EF-360, and that the shape stays byte-identical.
- The reference-collection constraint and the five spec tests, so nobody changes that exception type.
- Task 8 step 3's finding about the `:141` arm's coverage.
- The measured sweep numbers from Task 9, and that this is **not a break** (fallback → native, unchanged results, changed MQL — both carved out; no materialized-value change, so no `BREAKING-CHANGES.md` entry).

- [ ] **Step 3: Reconcile the design doc with what was built**

Add a short "as-built deltas" section to the design spec recording where implementation diverged from §3.2 — principally that no new array-source branch was needed and that the node carries no alias. Do not rewrite the spec's history; append.

- [ ] **Step 4: Final full verification**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
env -u MONGODB_URI -u ATLAS_URI dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

Expected: 0 failures.

- [ ] **Step 5: Whole-branch review**

Invoke this repo's `/review-ef-core-provider` skill — **not** a generic reviewer. It fans out the per-area reviewers (`query-reviewer` owns most of this diff) plus the cross-cutting `api-stability-reviewer`, `ef-conformance-reviewer` and `security-reviewer`. Address findings; re-run until clean.

- [ ] **Step 6: Squash the branch**

Keep a pre-squash safety branch, per the stacked-PR convention.

```bash
git branch EF-360-presquash
git reset --soft 7c199e4
git commit -m "EF-360: owned array-valued projections go native

An owned entity-collection leaf in a terminal anonymous-type or DTO projection
(Select(b => new { b.Title, b.Posts })) now emits a server-side \$project and reads
the array back from the projection alias, instead of falling back to aggregate([])
and folding the projection client-side. Results are unchanged; this is a bandwidth
and allocation win.

<summary of mechanism, guards, and sweep numbers>"
git log --oneline -2   # one commit on top of 7c199e4
```

Do **not** force-push over the shared branch; `EF-360-presquash` stays until the PR merges.

---

## Self-Review

**Spec coverage.** §1.1's seven in-scope rows → Tasks 3 (anonymous), 5.1 (DTO), 5.2 (`EF.Property`), 5.3 (two leaves), 5.4-5.5 (sibling leaves), 5.7 (`OwnsOne` hop), 5.8 (`HashSet`). §1.1's tracking paragraph → 5.9. §1.2's four out-of-scope kinds → 5.10 (primitive, already native), Task 4 (element navigation), Task 9 step 3 (reference collection, guarded by constraint), Task 4 step 5 (bare form still works). §3.1 emit side → Task 3 steps 4-5; representability guard → Task 4 step 7; no-converter-guard decision → Task 6 step 3. §3.2 read-back → Tasks 1-2. §3.3 decoupling → Task 4 + Task 10. §5.1 owner key → Task 4. §5.2 sibling leaves → 5.4-5.5 + mutation #6. §5.3 set-ops → Task 7. §6 verification → Tasks 5-8. §7 multi-version and versioning → Task 9 + Task 10. §4's four residual `NOT MEASURED` items → 5.7 (item 1), Task 8 step 3 (item 2), Task 7 (item 3), Task 6 step 3 (item 4). **No gaps.**

**Placeholders.** The four `<...>` markers are all runtime-measured values (sweep numbers, commit-message summaries) that cannot be known before the run, and each says what to put there. Task 3 step 4 and step 5 say to adjust to the real signature of `TryResolveOwnedCollectionPath` / the real shape of `MaterializeCollectionNavigationExpression.Subquery`, with the contract stated — that is a deliberate instruction to verify, not an unspecified step, and step 1 requires reading those sites first.

**Type consistency.** `IArrayProjectionExpression` (`Navigation`, `AccessExpression`, `InnerProjection`, `ArrayFieldName`) is defined in Task 1 and used in Task 2 with the same four members. `ArrayAliasProjectionExpression`'s constructor is `(INavigation, Expression, EntityProjectionExpression?)` in Task 1 and called with two arguments in Task 3 step 6 (the third defaulted) — consistent. `TryTranslateOwnedCollectionArray(Expression, out MongoElementRefExpression?)` is defined in Task 3 step 4 and called in step 5 — consistent. `hasArrayLeaf` is introduced and used within Task 4 step 3. The local rename `objectArrayProjection` → `arrayProjection` is applied consistently across Task 2 steps 1-2.

---

Plan complete and saved to `docs/superpowers/plans/2026-07-29-native-array-valued-projections.md`.
