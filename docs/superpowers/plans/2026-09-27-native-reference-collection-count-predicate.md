# Native reference-collection-nav Count in predicates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Teach the native query translator to recognize an unfiltered reference-collection-navigation
`Count`/`LongCount` inside a `Where` predicate comparison (`Where(c => c.Orders.Count > 2)`, and the equivalent
shape EF Core substitutes into a `Where` composed after a projected `Select`), so such queries go native
instead of falling back to the driver-LINQ provider.

**Architecture:** Extract the lookup-resolution logic already proven correct in
`NativeProjectionBinder.TryTranslateProjectedCollectionCount` (the Select-projection-leaf case) into two small
shared statics on `NativeCorrelationMatcher`, widen `MongoExpressionTranslator.TryMatchCountExpression`'s
visibility to `internal` so it's reusable, and add one new binder
(`NativeReferenceCollectionCountPredicateBinder`) that recognizes the comparison shape and is wired into
`NativeSlotPopulator`'s existing `Where` arm as one more `else if`. No renderer changes: the
`MongoBinaryExpression`-over-`MongoSizeExpression` node this produces already renders correctly in a `$match`
context today (proven by the existing owned-collection-count-in-predicate case).

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 build configurations), MongoDB C# driver, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md`

## Global Constraints

- Build and pass on all three EF configurations: `Debug EF8`, `Debug EF9`, `Debug EF10`.
- Task 1 is a pure refactor — zero behavior change. Verify via the existing test suite before writing any new
  code, per the spec's "no changes to observable behavior" boundary for that step.
- Do not widen `NativeCorrelationMatcher.TryMatchCorrelatedCollection`'s own matching power (still FK-correlation
  only, no genuinely-filtered `.Where(userPred).Count()` support) — reuse it as-is from a new call site only.
- Preserve file BOMs (per repo `AGENTS.md`) when editing existing files.
- `src/` is nullable-enabled — annotate new members accordingly.
- Every new/modified public-facing behavior needs an `AssertMql`/`NativeOnly` proof — MQL shape alone never
  proves nativeness (`Query/AGENTS.md`).

## Review Focus

- **Ambiguous navigation** — two collection navigations off the same entity with the same target type and FK
  shape. Already unit-pinned at the matcher level by the existing
  `NativeCorrelationMatcherTests.Ambiguous_two_navigations_same_target_and_fk_name_returns_false`, which this
  work does not touch or bypass (`TryMatchReferenceCollectionCountNavigation` calls the identical,
  unmodified `TryMatchCorrelatedCollection`). The new call site's OWN job on a `false` return — decline
  cleanly rather than mishandle it — is the same propagation path Task 3's
  `Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly` already exercises end-to-end, so no
  separate ambiguous-navigation test is added; if that reasoning turns out wrong (a future change makes the
  two decline paths diverge), add one then.
- **A genuinely user-filtered `.Where(pred).Count()`** over a reference collection (e.g.
  `c.Orders.Where(o => o.Total > 5).Count() > 2`) — must still decline to fallback, not silently ignore the
  user's filter and count everything. (Task 3)
- **`LongCount` alongside `Count`** — same code path, but the result type differs (`long` vs `int`); a person
  reading "Count support" would expect `LongCount` covered too, not silently left behind. (Task 2)
- **The count operand on either side of the comparison** (`c.Orders.Count > 2` vs `2 < c.Orders.Count`) — both
  must produce the same, correctly-ordered comparison, not silently swap the operator's meaning. (Task 2)
- **Zero related rows** (an owner with no orders at all) — the `$lookup` produces an empty array, not a missing
  field; `$size` over it must read `0` cleanly, not throw or read `null`. (Task 3, differential fixture)

---

## Task 1: Extract shared reference-collection-count lookup resolution (pure refactor)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs:150`
  (widen `TryMatchCountExpression` from `private` to `internal`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCorrelationMatcher.cs` (add two new
  `internal static` methods)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs:2281-2360`
  (`TryTranslateProjectedCollectionCount` — refactor to call the new shared methods)
- No test file changes in this task — the existing suite is the regression pin.

**Interfaces:**
- Consumes: nothing new (this task only moves existing, already-correct code).
- Produces (for Task 2 to consume):
  - `internal static bool MongoExpressionTranslator.TryMatchCountExpression(Expression node, [NotNullWhen(true)] out Expression? source, out LambdaExpression? predicate)`
    (visibility widened only; behavior unchanged)
  - `internal static bool NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation(MongoQueryExpression mongoQ, ParameterExpression outerParameter, Expression whereArg, [NotNullWhen(true)] out INavigation? navigation)`
  - `internal static bool NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup(MongoQueryExpression mongoQ, INavigation navigation, List<LookupExpression> pendingLookups, Type resultType, [NotNullWhen(true)] out MongoSizeExpression? result)`

- [ ] **Step 1: Establish the regression baseline before touching anything**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: all pass, 0 failures. Note the pass count — Step 4 must match it exactly.

- [ ] **Step 2: Widen `TryMatchCountExpression`'s visibility**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`, change:

```csharp
    private static bool TryMatchCountExpression(
```
to:
```csharp
    internal static bool TryMatchCountExpression(
```

No other change to that method's body.

- [ ] **Step 3: Add the two shared statics to `NativeCorrelationMatcher.cs`**

Add near the end of the class (after `TryMatchCorrelatedCollection` and its private helpers), using the file's
existing `using`s (it already has `System.Linq.Expressions`, `Microsoft.EntityFrameworkCore.Metadata`; add
`System.Collections.Generic` and `System.Diagnostics.CodeAnalysis` for `NotNullWhen`, and
`MongoDB.EntityFrameworkCore.Query.Expressions` for `LookupExpression`/`MongoSizeExpression`/
`MongoQueryExpression`):

```csharp
    /// <summary>
    /// Recognizes the <c>Queryable.Where(root, correlationPredicate)</c> shape EF Core's nav-expansion always
    /// wraps a reference-collection-navigation <c>Count</c>/<c>LongCount</c> in (see
    /// <see cref="MongoExpressionTranslator.TryMatchCountExpression"/>'s own remarks — the <c>Where</c> here is
    /// EF's own FK-correlation plumbing, present for both a bare <c>c.Orders.Count</c> and a user-filtered
    /// <c>c.Orders.Where(pred).Count()</c> alike), then resolves the single matching collection navigation via
    /// <see cref="TryMatchCorrelatedCollection"/>. Shared by <see cref="NativeProjectionBinder"/>'s
    /// projected-<c>Count</c> leaf and <see cref="NativeReferenceCollectionCountPredicateBinder"/>'s predicate
    /// comparison — both need the identical "which navigation does this whereArg correlate to" answer.
    /// </summary>
    internal static bool TryMatchReferenceCollectionCountNavigation(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression whereArg,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;

        if (whereArg is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        return TryMatchCorrelatedCollection(
            predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false, out navigation!);
    }

    /// <summary>
    /// Builds (or reuses, via the same cross-leaf alias-collision dedupe rule
    /// <see cref="NativeProjectionBinder.TryTranslateProjectedCollectionCount"/> has always applied) the
    /// <c>$lookup</c> a reference-collection <c>Count</c> needs, and stages it into <paramref name="pendingLookups"/>
    /// for the caller to commit. Two leaves in one query can both want the same <c>_lookup_&lt;Nav&gt;</c> alias;
    /// that is only safe when they are INTERCHANGEABLE — a same-kind lookup (another count over the same nav, or
    /// an already-pending collection-Include lookup for it) is reused, but colliding with a
    /// <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup (which unwinds to a single document, not an
    /// array) is not, and declines instead of risking a <c>$size</c> over the wrong shape.
    /// </summary>
    internal static bool TryBuildReferenceCollectionCountLookup(
        MongoQueryExpression mongoQ,
        INavigation navigation,
        List<LookupExpression> pendingLookups,
        Type resultType,
        [NotNullWhen(true)] out MongoSizeExpression? result)
    {
        result = null;

        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        if (!lookup.IsNativeCollectionLookup)
            return false;

        var collidingLookup = pendingLookups.FirstOrDefault(l => l.As == lookup.As)
            ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }

        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), resultType);
        return true;
    }
```

- [ ] **Step 4: Refactor `TryTranslateProjectedCollectionCount` to call the two new shared methods**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`, the method currently
reads (only the first guard block, establishing `whereArg` from `leafExpression`, is UNCHANGED and not shown
below):

```csharp
        if (whereArg is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        // The Count binder wants a reference (non-embedded) collection navigation.
        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false, out var navigation))
        {
            return false;
        }

        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        if (!lookup.IsNativeCollectionLookup)
            return false;

        // CROSS-LEAF ALIAS COLLISION. [... existing multi-paragraph comment ...]
        var collidingLookup = pendingLookups.FirstOrDefault(l => l.As == lookup.As)
            ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }

        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), leafExpression.Type);
        return true;
    }
```

Replace this **entire block** (everything from `if (whereArg is not MethodCallExpression` through the closing
`}` of the method — i.e. everything AFTER the first guard block that established `whereArg`, including the
now-redundant `CROSS-LEAF ALIAS COLLISION` comment, which moved verbatim to
`TryBuildReferenceCollectionCountLookup` in Step 3) with:

```csharp
        if (!NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation(
                mongoQ, outerParameter, whereArg, out var navigation))
        {
            return false;
        }

        if (!NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup(
                mongoQ, navigation, pendingLookups, leafExpression.Type, out var sizeExpression))
        {
            return false;
        }

        result = sizeExpression;
        return true;
    }
```

This is safe precisely because `TryMatchReferenceCollectionCountNavigation` (Step 3) re-implements the exact
`whereArg is not MethodCallExpression { ... Where ... }` / `predicate` / `outerEntityType`/`targetEntityType` /
`TryMatchCorrelatedCollection` sequence shown above, byte-for-byte, taking the same raw `whereArg` this method
already has in scope. The method's signature and its first guard block (establishing `whereArg` from
`leafExpression`) are unchanged.

- [ ] **Step 5: Verify zero behavior change**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: identical pass count to Step 1. If anything now fails, the refactor introduced a behavior change —
stop and find the diff before proceeding; do not paper over it.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCorrelationMatcher.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs
git commit -m "EF-322: extract shared reference-collection Count/lookup resolution (pure refactor)"
```

---

## Task 2: Recognize the Count comparison in `Where` predicates

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeReferenceCollectionCountPredicateBinder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:347-348` (add one
  `else if` arm before the final `else mongoQ.Select.MarkNotNativelyRepresentable();`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceCollectionCountPredicateTests.cs`

**Interfaces:**
- Consumes:
  - `MongoExpressionTranslator.TryMatchCountExpression` (Task 1, now `internal`)
  - `NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation` / `TryBuildReferenceCollectionCountLookup` (Task 1)
  - `MongoExpressionTranslator.TryTranslateValue(Expression valueBody, out MongoExpression? result)` (pre-existing, public)
  - `MongoQueryExpression.AddLookup(LookupExpression lookup)` (pre-existing)
  - `MongoSelectDefinition.AddPredicateConjunct(MongoExpression conjunct)` (pre-existing, via `mongoQ.Select`)
- Produces (for Task 3 to consume): `NativeReferenceCollectionCountPredicateBinder.TryTranslate(MongoQueryExpression mongoQ, ParameterExpression outerParameter, Expression predicateBody, out MongoExpression? result)` — no new public surface beyond this; Task 3 only exercises it through end-to-end queries, not by calling it directly.

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceCollectionCountPredicateTests.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322: an unfiltered reference-collection-navigation Count/LongCount, compared against a value inside a
/// Where predicate, translates natively via a $lookup + $size — the same machinery
/// NativeProjectionBinder.TryTranslateProjectedCollectionCount already proved correct for the Select-leaf case,
/// reused here from the Where-predicate call site. See
/// docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceCollectionCountPredicateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Bare_Count_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Bare_Count_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.Count > 1).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    [Fact]
    public void LongCount_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(LongCount_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.LongCount() > 1).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    [Fact]
    public void Count_on_the_right_hand_side_of_the_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_on_the_right_hand_side_of_the_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => 1 < o.Orders.Count).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    private sealed record Seed(Owner[] Owners, Order[] Orders);

    private static Seed SeedOwnersAndOrders()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob" };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m },
        };

        return new Seed([ownerA, ownerB], orders);
    }

    private CountPredicateTestDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Owners" + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Orders" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);

        return new CountPredicateTestDbContext(database, ownersName, ordersName, mode);
    }

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
    }

    private sealed class CountPredicateTestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _ordersCollection;

        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        public CountPredicateTestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _ownersCollection = ownersCollection;
            _ordersCollection = ordersCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<CountPredicateTestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Orders).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Order>(b => b.ToCollection(_ordersCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
```

Add `using System.Collections.Generic;` at the top alongside the others (needed for `List<Order>`).

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeReferenceCollectionCountPredicateTests"
```
Expected: all three FAIL with `NativeTranslationNotSupportedException: Query is not natively representable and
MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.`

- [ ] **Step 3: Implement the binder**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeReferenceCollectionCountPredicateBinder.cs`:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes a relational/equality comparison whose one operand is an UNFILTERED reference-collection-nav
/// <c>Count</c>/<c>LongCount</c> (<c>c.Orders.Count &gt; 2</c>) and the other a translatable value, and
/// translates it into a native <c>$lookup</c> + <c>$size</c> comparison. Called from
/// <see cref="NativeSlotPopulator"/>'s <c>Where</c> arm, after the general
/// <see cref="MongoExpressionTranslator.TryTranslate"/> attempt has already declined (which it always will for
/// this shape — the general translator's owned-collection-count arm requires an EMBEDDED collection).
/// See docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md.
/// </summary>
internal static class NativeReferenceCollectionCountPredicateBinder
{
    internal static bool TryTranslate(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression predicateBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (predicateBody is not BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                    or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } binary)
        {
            return false;
        }

        Expression countSide;
        Expression otherSide;
        if (MongoExpressionTranslator.TryMatchCountExpression(binary.Left, out var leftWhereArg, out var leftPredicate)
            && leftPredicate is null)
        {
            countSide = binary.Left;
            otherSide = binary.Right;
        }
        else if (MongoExpressionTranslator.TryMatchCountExpression(binary.Right, out var rightWhereArg, out var rightPredicate)
                 && rightPredicate is null)
        {
            countSide = binary.Right;
            otherSide = binary.Left;
        }
        else
        {
            return false;
        }

        MongoExpressionTranslator.TryMatchCountExpression(countSide, out var whereArg, out _);

        if (!NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation(
                mongoQ, outerParameter, whereArg!, out var navigation))
        {
            return false;
        }

        var pendingLookups = new List<LookupExpression>();
        if (!NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup(
                mongoQ, navigation, pendingLookups, countSide.Type, out var sizeExpression))
        {
            return false;
        }

        var valueTranslator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        if (!valueTranslator.TryTranslateValue(otherSide, out var otherNode))
            return false;

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);

        var leftNode = ReferenceEquals(countSide, binary.Left) ? (MongoExpression)sizeExpression : otherNode;
        var rightNode = ReferenceEquals(countSide, binary.Left) ? otherNode : (MongoExpression)sizeExpression;
        result = new MongoBinaryExpression(MapOperator(binary.NodeType), leftNode, rightNode);
        return true;
    }

    private static MongoBinaryOperator MapOperator(ExpressionType nodeType)
        => nodeType switch
        {
            ExpressionType.Equal => MongoBinaryOperator.Equal,
            ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
            ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => MongoBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
            _ => throw new System.NotSupportedException($"Unexpected comparison operator '{nodeType}'.")
        };
}
```

- [ ] **Step 4: Wire the binder into `NativeSlotPopulator`'s `Where` arm**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`, find the end of the `Where`
arm — the final two lines are:

```csharp
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
```

Change to:

```csharp
            // EF-322: an unfiltered reference-collection-nav Count/LongCount compared against a value —
            // `c.Orders.Count > 2`, or the identical shape EF Core substitutes into a Where composed after a
            // projected Select. The general translator.TryTranslate attempt above always declines this shape
            // (its owned-collection-count arm requires an embedded collection), so this is tried only once that
            // has already failed. See docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md.
            else if (NativeReferenceCollectionCountPredicateBinder.TryTranslate(
                         mongoQ, predicate.Parameters[0], predicate.Body, out var countPredicateNode))
            {
                mongoQ.Select.AddPredicateConjunct(countPredicateNode);
            }
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeReferenceCollectionCountPredicateTests"
```
Expected: all three PASS.

- [ ] **Step 6: Run the full EF10 suite to check for regressions**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: 0 failures (some spec-test baseline mismatches are EXPECTED here if any spec test's shape newly goes
native — Task 4 handles those; if this task's own new tests are the only difference, there should be none yet).

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeReferenceCollectionCountPredicateBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceCollectionCountPredicateTests.cs
git commit -m "EF-322: native reference-collection-nav Count in Where predicates"
```

---

## Task 3: Decline-boundary and differential-correctness coverage

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceCollectionCountPredicateTests.cs`

**Interfaces:**
- Consumes: the same `CountPredicateTestDbContext`/`Owner`/`Order`/`Seed` fixtures Task 2 already defined in
  this file — no new production interfaces.

- [ ] **Step 1: Write the failing (or vacuously-passing, for the decline cases) tests**

Add to `NativeReferenceCollectionCountPredicateTests`:

```csharp
    [Fact]
    public void Zero_related_rows_reads_as_zero_under_NativeOnly()
    {
        var ownerWithNoOrders = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };
        var seed = new Seed([ownerWithNoOrders], []);
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Zero_related_rows_reads_as_zero_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.Count == 0).ToList();

        Assert.Single(result);
        Assert.Equal("Carol", result[0].Name);
    }

    [Fact]
    public void Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly));

        Assert.Throws<Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => db.Owners.Where(o => o.Orders.Where(ord => ord.Total > 15m).Count() > 0).ToList());
    }

    [Fact]
    public void Differential_native_result_matches_in_memory_oracle_across_related_row_counts()
    {
        var ownerWithNoOrders = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };
        var seed = SeedOwnersAndOrders();
        var allOwners = seed.Owners.Append(ownerWithNoOrders).ToArray();
        var combinedSeed = new Seed(allOwners, seed.Orders);

        using var native = CreateContext(combinedSeed, MongoQueryMode.NativeOnly,
            nameof(Differential_native_result_matches_in_memory_oracle_across_related_row_counts) + "Native");
        using var driverLinq = CreateContext(combinedSeed, MongoQueryMode.DriverLinq,
            nameof(Differential_native_result_matches_in_memory_oracle_across_related_row_counts) + "DriverLinq");

        var nativeNames = native.Owners.Where(o => o.Orders.Count >= 1).Select(o => o.Name).OrderBy(n => n).ToList();
        var oracleNames = driverLinq.Owners.Where(o => o.Orders.Count >= 1).Select(o => o.Name).OrderBy(n => n).ToList();

        Assert.Equal(oracleNames, nativeNames);
    }
```

- [ ] **Step 2: Run to verify the decline test fails first (proves it was actually exercising the gap), then verify all pass after Task 2's implementation is in place**

Since Task 2 is already implemented at this point in the plan, run directly:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeReferenceCollectionCountPredicateTests"
```
Expected: all PASS, including `Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly` (which
passes by the exception being thrown, proving the decline boundary holds).

To confirm `Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly` is actually pinning
something (not vacuously passing because of an unrelated failure), temporarily comment out this task's
`NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation`'s body to always `return false` — that
must make `Bare_Count_comparison_goes_native_under_NativeOnly` (Task 2) fail while
`Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly` still passes for a DIFFERENT reason
(the filtered shape was never going to match `TryMatchReferenceCollectionCountNavigation` in the first place —
it declines earlier, at the `predicate.Parameters.Count != 1`/`TryGetCorrelationEqualitySides` level inside
`TryMatchCorrelatedCollection` itself, because the filtered predicate has an extra conjunct beyond the bare FK
correlation). Revert the temporary change immediately after confirming this.

- [ ] **Step 3: Run EF8 and EF9 too**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF8" --no-build --filter "FullyQualifiedName~NativeReferenceCollectionCountPredicateTests"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF9" --no-build --filter "FullyQualifiedName~NativeReferenceCollectionCountPredicateTests"
```
Expected: all pass on both.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeReferenceCollectionCountPredicateTests.cs
git commit -m "EF-322: decline-boundary and differential coverage for reference-collection Count predicates"
```

---

## Task 4: Fix the motivating spec test(s) and verify the full suite

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindIncludeNoTrackingQueryMongoTest.cs`
  (`Include_with_complex_projection_does_not_change_ordering_of_projection`)
- Modify (if the same shape appears there — check in Step 1): `NorthwindIncludeQueryMongoTest.cs`,
  `NorthwindStringIncludeQueryMongoTest.cs`
- Modify (if the full-suite run in Step 4 surfaces any other newly-passing baseline, per this branch's
  established regeneration procedure): whichever spec test file(s) those belong to.

**Interfaces:** none — this task only regenerates test baselines and runs verification, no production code
changes.

- [ ] **Step 1: Find every spec test with this exact shape**

```bash
grep -rln "Include_with_complex_projection_does_not_change_ordering_of_projection" \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
```

For each match, confirm (by reading the override) it currently asserts a fallback-shaped MQL (a `$project`/
`$$ROOT` join-scope pattern, or an `AssertTranslationFailed` wrapper) — those are this task's targets.

- [ ] **Step 2: Regenerate the baseline for each, confirming data-assertion-only failure first**

For each target test and each of the three EF configurations:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Include_with_complex_projection_does_not_change_ordering_of_projection"
```

Confirm the failure is `Assert.Equal() Failure: Strings differ` (baseline-only — the data assertion inside
`base.Include_with_complex_projection_does_not_change_ordering_of_projection` already passed) rather than a
data/behavior assertion failure. If it's a data failure instead, STOP — that means the native translation
produces wrong rows, which is a bug in Task 2's implementation, not a baseline to regenerate.

`git diff` the resulting change to the test file and read it before trusting it (per
`SpecificationTests/AGENTS.md`'s own warning that the rewriter "can corrupt files or mis-place output"). Repeat
for EF8 and EF9 (`-c "Debug EF8"` / `"Debug EF9"`).

- [ ] **Step 3: Verify each fixed test passes clean (no rewrite flag)**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Include_with_complex_projection_does_not_change_ordering_of_projection"
```
Repeat for EF8 and EF9. Expected: all pass.

- [ ] **Step 4: Run the full three-EF-version suite**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```

If any OTHER spec test now fails with the same `Assert.Equal() Failure: Strings differ` pattern (its query
happened to contain the same reference-collection-count-in-predicate shape and newly goes native), repeat Step
2's regeneration procedure for it too — confirm baseline-only, regenerate, diff, verify. Do not regenerate a
baseline for a test failing any other way; investigate that as a genuine regression instead.

Expected final state: 0 failures across all three configurations, all three test projects (unit, functional,
specification).

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
git commit -m "EF-322: regenerate spec baselines now going native via reference-collection Count predicates"
```
