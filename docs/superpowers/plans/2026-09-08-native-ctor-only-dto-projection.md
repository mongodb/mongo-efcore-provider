# Native Ctor-Only DTO Projection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a DTO constructor that takes a whole entity, a scalar, or an owned nav-entity argument (with no compiler-synthesized `NewExpression.Members`) go native instead of falling back to driver-LINQ, for ordinary `Select` (single argument) and for `GroupBy`/`SelectMany` result selectors (any arity).

**Architecture:** Two independent, non-unified mechanisms. (A) A new recognizer arm in `NativeProjectionBinder.TryPopulateNativeProjection`, capped at exactly one constructor argument, reusing the existing bare-body leaf-translation/alias-derivation machinery — because the read side (`MongoProjectionBindingExpressionVisitor.VisitNew`) resolves it through EF Core's `ProjectionMember`/`MemberInfo`-keyed dictionary, which has no safe way to distinguish multiple unnamed arguments. (B) A new opt-in parameter on the shared `ExpressionExtensionMethods.TryGetProjectionMembers` helper, used only by the `GroupBy`/`SelectMany` result-selector family, which addresses projected values by array index and so has no arity ceiling.

**Tech Stack:** C#, EF Core 8/9/10 (build configuration `Debug EF10` used for iteration), xUnit with plain `Assert.*`, MongoDB C# driver, `mongodb/mongodb-atlas-local` test containers.

**Spec:** `docs/superpowers/specs/2026-09-08-native-ctor-only-dto-projection-design.md`

## Global Constraints

- **Family A is capped at exactly one constructor argument.** Never widen it to more — `ProjectionMember.Append` only accepts a real `MemberInfo`; there is no safe way to key a second unnamed argument.
- **`NativeProjectionBinder`'s existing WRAPPED (named-`Members`) arm (`TryPopulateNativeProjection`'s switch, the case with `when selector.Body.TryGetProjectionMembers(...)`), its document-construction leaf (`TryGetDocumentConstructionLeaf`), and `NativeJoinScopeProjectionBinder.TryBindProjection` must NOT pass `allowPositionalConstructorArguments: true`.** They feed the same `ProjectionMember`-keyed read mechanism as family A; widening them reintroduces the exact alias-mismatch bug this plan's spec found (a synthetic-name-keyed override the ambient/null-keyed read can never find).
- **`NativeJoinScopeProjectionBinder` gets no equivalent recognizer in this plan** — a ctor-only DTO immediately after a `Join` continues to fall back to driver-LINQ, unchanged from today. Out of scope (see spec).
- **`MemberInitExpression` with a non-empty-argument constructor stays out of scope everywhere** — `TryGetProjectionMembers`'s `MemberInit` branch keeps its existing `NewExpression.Arguments.Count: 0` requirement, untouched.
- Every new/changed method needs its existing XML-doc style maintained (this codebase documents *why*, not *what* — see any touched file's existing comments for the register to match).
- Multi-EF-version: build/test against `Debug EF10` while iterating; a full `/test-all`-equivalent (or at least a spot-check against EF8/EF9) belongs in the final task.

---

### Task 1: Widen `TryGetProjectionMembers` with an opt-in positional-constructor path

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/ExpressionExtensionMethods.cs:130-172` (`TryGetProjectionMembers`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/ExpressionExtensionMethodsTests.cs` (new file)

**Interfaces:**
- Produces: `ExpressionExtensionMethods.TryGetProjectionMembers(this Expression body, out IReadOnlyList<(string MemberName, Expression Value)> members, bool allowPositionalConstructorArguments = false)` — new optional parameter, default `false` (today's behavior, byte-for-byte unchanged for every existing call site). When `true`, additionally admits a `NewExpression` with `Members == null` and `Arguments.Count > 0`, yielding `(PositionalConstructorArgumentAlias(i), Arguments[i])` pairs. Also produces `ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix` (`internal const string`, value `"_ctorArg"`).

- [ ] **Step 1: Write the failing unit test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/ExpressionExtensionMethodsTests.cs`:

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

using System.Linq.Expressions;
using System.Reflection;
using MongoDB.EntityFrameworkCore;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

public class ExpressionExtensionMethodsTests
{
    private class OneArgDto
    {
        public string Value { get; }
        public OneArgDto(string value) => Value = value;
    }

    private class TwoArgDto
    {
        public string A { get; }
        public int B { get; }
        public TwoArgDto(string a, int b) { A = a; B = b; }
    }

    private static ConstructorInfo Ctor<T>(int argCount)
        => typeof(T).GetConstructors().Single(c => c.GetParameters().Length == argCount);

    [Fact]
    public void Ctor_only_new_expression_declines_by_default()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members);

        Assert.False(result);
        Assert.Empty(members);
    }

    [Fact]
    public void Ctor_only_new_expression_declines_when_flag_explicitly_false()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: false);

        Assert.False(result);
        Assert.Empty(members);
    }

    [Fact]
    public void Ctor_only_new_expression_admits_one_positional_argument_when_flag_true()
    {
        var arg = Expression.Constant("hello");
        var newExpr = Expression.New(Ctor<OneArgDto>(1), arg);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        var member = Assert.Single(members);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "0", member.MemberName);
        Assert.Same(arg, member.Value);
    }

    [Fact]
    public void Ctor_only_new_expression_admits_multiple_positional_arguments_in_order_when_flag_true()
    {
        var argA = Expression.Constant("hello");
        var argB = Expression.Constant(42);
        var newExpr = Expression.New(Ctor<TwoArgDto>(2), argA, argB);

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        Assert.Equal(2, members.Count);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "0", members[0].MemberName);
        Assert.Same(argA, members[0].Value);
        Assert.Equal(ExpressionExtensionMethods.PositionalConstructorArgumentAliasPrefix + "1", members[1].MemberName);
        Assert.Same(argB, members[1].Value);
    }

    [Fact]
    public void Named_members_new_expression_is_unaffected_by_the_new_flag()
    {
        // An anonymous type: NewExpression.Members IS populated by the compiler. Passing
        // allowPositionalConstructorArguments: true must not change this arm's behavior at all.
        Expression<System.Func<string, int, object>> lambda = (a, b) => new { A = a, B = b };
        var newExpr = (NewExpression)lambda.Body;

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.True(result);
        Assert.Equal(2, members.Count);
        Assert.Equal("A", members[0].MemberName);
        Assert.Equal("B", members[1].MemberName);
    }

    [Fact]
    public void Ctor_only_new_expression_with_zero_arguments_declines_even_when_flag_true()
    {
        var newExpr = Expression.New(typeof(object));

        var result = newExpr.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);

        Assert.False(result);
        Assert.Empty(members);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~ExpressionExtensionMethodsTests"`
Expected: build error (`TryGetProjectionMembers` has no such overload/parameter) or, if it happens to compile against the single-parameter form, `Ctor_only_new_expression_admits_one_positional_argument_when_flag_true` and its multi-arg sibling FAIL (`Assert.True(result)` fails — `result` is `false`).

- [ ] **Step 3: Implement the widened helper**

In `src/MongoDB.EntityFrameworkCore/ExpressionExtensionMethods.cs`, replace the `TryGetProjectionMembers` method (currently lines 130-172) with:

```csharp
    /// <summary>
    /// Reads a WRAPPED projection body — an anonymous type / DTO construction — into its
    /// (member name, value expression) pairs, in both spellings the C# compiler produces: a
    /// <see cref="NewExpression"/> carrying <see cref="NewExpression.Members"/> (anonymous types and
    /// positional constructors) or a <see cref="MemberInitExpression"/> over a parameterless constructor
    /// (object-initializer syntax).
    /// </summary>
    /// <param name="allowPositionalConstructorArguments">
    /// When <see langword="true"/>, additionally admits a <see cref="NewExpression"/> whose
    /// <see cref="NewExpression.Members"/> is <see langword="null"/> (a constructor-only DTO — the compiler
    /// only populates <c>Members</c> when every constructor parameter maps 1:1 by name to a same-named
    /// property, which a DTO computing its own properties in its body does not do), yielding synthetic
    /// positional pseudo-names (<see cref="PositionalConstructorArgumentAliasPrefix"/> + index) instead of
    /// real member names. Defaults to <see langword="false"/> — every pre-existing call site keeps its exact
    /// prior behavior unless it opts in. ONLY the <c>GroupBy</c>/<c>SelectMany</c> result-selector family
    /// (which addresses each bound value by array index, not by EF Core's <c>ProjectionMember</c>/
    /// <c>MemberInfo</c>-keyed dictionary) may pass <see langword="true"/> — see Query's own
    /// <c>NativeProjectionBinder</c>/<c>NativeJoinScopeProjectionBinder</c>, which must NOT, because their read
    /// side resolves a wrapped member's alias through that dictionary, keyed by a REAL <c>MemberInfo</c>; a
    /// synthetic name registered there is never found by that read.
    /// </param>
    /// <returns>
    /// <see langword="false"/> — leaving <paramref name="members"/> empty — for a body that is not a wrapped
    /// construction, has no members, or uses a member binding this cannot express (a nested or list binding,
    /// or a <see cref="MemberInitExpression"/> whose constructor itself takes arguments). Callers treat that as
    /// "not a wrapped projection" and fall through to their bare-body handling.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Eight sites read this same shape — two arms inside <c>NativeProjectionBinder</c>'s own switch, plus
    /// <c>NativeProjectionBinder</c>'s document-construction leaf, <c>NativeJoinScopeProjectionBinder</c>,
    /// <c>NativeSelectManyBinder</c>, <c>NativeGroupByBinder</c>, and the QMTEV's two shaper builders
    /// (<c>TryBuildGroupResultShaper</c>, <c>BuildSelectManyResultShaper</c>, which additionally REBUILD the
    /// construction — see <see cref="RebuildProjectionMembers"/>). They had **already drifted**: the
    /// <c>GroupBy</c> copy omitted both the <c>Members.Count == Arguments.Count</c> pairing check and the
    /// non-empty checks its five siblings carry, so it accepted a degenerate empty construction. This is the
    /// strict form; a degenerate body now declines and falls back, which is the safe direction.
    /// </para>
    /// </remarks>
    internal static bool TryGetProjectionMembers(
        this Expression body, out IReadOnlyList<(string MemberName, Expression Value)> members,
        bool allowPositionalConstructorArguments = false)
    {
        switch (body)
        {
            case NewExpression
            {
                Members: { } newMembers, Arguments: { Count: > 0 } arguments
            } when newMembers.Count == arguments.Count:
            {
                var pairs = new List<(string, Expression)>(arguments.Count);
                for (var i = 0; i < arguments.Count; i++)
                {
                    pairs.Add((newMembers[i].Name, arguments[i]));
                }

                members = pairs;
                return true;
            }

            // A CTOR-ONLY DTO — Members is null because no constructor parameter maps 1:1 to a same-named
            // property (the compiler only synthesizes Members for that exact match). Admitted only when the
            // caller opts in; see the allowPositionalConstructorArguments parameter doc for who may.
            case NewExpression
            {
                Members: null, Arguments: { Count: > 0 } positionalArguments
            } when allowPositionalConstructorArguments:
            {
                var pairs = new List<(string, Expression)>(positionalArguments.Count);
                for (var i = 0; i < positionalArguments.Count; i++)
                {
                    pairs.Add((PositionalConstructorArgumentAlias(i), positionalArguments[i]));
                }

                members = pairs;
                return true;
            }

            case MemberInitExpression { NewExpression.Arguments.Count: 0, Bindings.Count: > 0 } memberInit:
            {
                var pairs = new List<(string, Expression)>(memberInit.Bindings.Count);
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        members = [];
                        return false;
                    }

                    pairs.Add((assignment.Member.Name, assignment.Expression));
                }

                members = pairs;
                return true;
            }

            default:
                members = [];
                return false;
        }
    }

    /// <summary>
    /// The reserved pseudo-member-name prefix for a ctor-only DTO's positional constructor arguments (see
    /// <see cref="TryGetProjectionMembers"/>'s <c>allowPositionalConstructorArguments</c> parameter) — argument
    /// index <c>i</c> becomes <c>"_ctorArg" + i</c>. Underscore-prefixed and self-documenting, consistent with
    /// this codebase's other synthetic-alias conventions (<c>NativeProjectionBinder.SyntheticBareProjectionAlias</c>
    /// = <c>"_v"</c>, <c>MongoSelectDefinition.BareProjectionMemberKey</c>).
    /// </summary>
    internal const string PositionalConstructorArgumentAliasPrefix = "_ctorArg";

    private static string PositionalConstructorArgumentAlias(int index)
        => PositionalConstructorArgumentAliasPrefix + index;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~ExpressionExtensionMethodsTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full UnitTests suite to confirm no regression from the default-parameter change**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build`
Expected: PASS, no new failures (every existing `TryGetProjectionMembers` call site omits the new parameter, so it defaults to `false` — behavior unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/ExpressionExtensionMethods.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/ExpressionExtensionMethodsTests.cs
git commit -m "EF-TBD: widen TryGetProjectionMembers with an opt-in positional-ctor path"
```

---

### Task 2: Wire the positional-ctor path into `GroupBy` result-selector shaping

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs:136` (`TryBindGroupProjection`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:701` (`TryBuildGroupResultShaper`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs` (new file)

**Interfaces:**
- Consumes: `ExpressionExtensionMethods.TryGetProjectionMembers(..., bool allowPositionalConstructorArguments = false)` from Task 1.
- Produces: nothing new — this task only changes two call sites' arguments.

- [ ] **Step 1: Write the failing functional test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs`:

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

using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeGroupByCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string CustomerId { get; set; } = "";
        public decimal Total { get; set; }
    }

    // A ctor-only DTO — no member named "key"/"count" matches a constructor parameter of the same name by
    // the compiler's rules, so NewExpression.Members is null for `new CustomerOrderSummary(g.Key, g.Count())`.
    private class CustomerOrderSummary
    {
        public string CustomerId { get; }
        public int OrderCount { get; }

        public CustomerOrderSummary(string customerId, int orderCount)
        {
            CustomerId = customerId;
            OrderCount = orderCount;
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void GroupBy_select_with_two_argument_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_two_argument_ctor_only_dto_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the ctor-only DTO result selector went native.
        var results = db.Entities
            .GroupBy(o => o.CustomerId)
            .Select(g => new CustomerOrderSummary(g.Key, g.Count()))
            .OrderBy(r => r.CustomerId)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].CustomerId);
        Assert.Equal(2, results[0].OrderCount);
        Assert.Equal("B", results[1].CustomerId);
        Assert.Equal(1, results[1].OrderCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByCtorProjectionTests"`
Expected: FAIL — `NativeTranslationNotSupportedException` (the shape currently falls back).

- [ ] **Step 3: Wire the two call sites**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, change line 136:

```csharp
        if (!resultSelector.Body.TryGetProjectionMembers(out var bindings))
            return false;
```

to:

```csharp
        if (!resultSelector.Body.TryGetProjectionMembers(out var bindings, allowPositionalConstructorArguments: true))
            return false;
```

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, change line 701:

```csharp
        if (!selector.Body.TryGetProjectionMembers(out var members))
            return null;
```

to:

```csharp
        if (!selector.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true))
            return null;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByCtorProjectionTests"`
Expected: PASS.

- [ ] **Step 5: Regression-check existing GroupBy tests**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy"` and `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy"`
Expected: PASS, identical to before this task (the widened parameter only admits a shape that previously declined at the very first line — `Members == null` bodies never reached anything past that check, so no previously-admitted shape's behavior can change).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByCtorProjectionTests.cs
git commit -m "EF-TBD: GroupBy result selector admits a ctor-only DTO natively"
```

---

### Task 3: Wire the positional-ctor path into `SelectMany` result-selector shaping

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs:90` (`TryBind`) and `:528` (`TryBindTransparentIdentifierProjection`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:2936` and `:2947` (`BuildSelectManyResultShaper`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyCtorProjectionTests.cs` (new file)

**Interfaces:**
- Consumes: `ExpressionExtensionMethods.TryGetProjectionMembers(..., bool allowPositionalConstructorArguments = false)` from Task 1.
- Produces: nothing new — this task only changes four call-site arguments (two in `NativeSelectManyBinder`, two in `BuildSelectManyResultShaper`).

- [ ] **Step 1: Write the failing functional test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyCtorProjectionTests.cs`:

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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeSelectManyCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
    }

    // A ctor-only DTO over the owned-collection SelectMany's (outer, inner) pair — no member named
    // "blogTitle"/"postHeading" matches a constructor parameter by the compiler's naming rule, so
    // NewExpression.Members is null.
    private class BlogPostSummary
    {
        public string BlogTitle { get; }
        public string PostHeading { get; }

        public BlogPostSummary(string blogTitle, string postHeading)
        {
            BlogTitle = blogTitle;
            PostHeading = postHeading;
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void SelectMany_owned_collection_with_two_argument_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(SelectMany_owned_collection_with_two_argument_ctor_only_dto_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Posts", new BsonArray { new BsonDocument("Heading", "First"), new BsonDocument("Heading", "Second") } }
        });
        var collection = database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<Blog>().OwnsMany(b => b.Posts),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the ctor-only DTO result selector went native.
        var results = db.Entities
            .SelectMany(b => b.Posts, (b, p) => new BlogPostSummary(b.Title, p.Heading))
            .OrderBy(r => r.PostHeading)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].BlogTitle);
        Assert.Equal("First", results[0].PostHeading);
        Assert.Equal("Alpha", results[1].BlogTitle);
        Assert.Equal("Second", results[1].PostHeading);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyCtorProjectionTests"`
Expected: FAIL — `NativeTranslationNotSupportedException`.

- [ ] **Step 3: Wire the four call sites**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, change line 90:

```csharp
        if (!innerLambda.Body.TryGetProjectionMembers(out var members))
            return false;
```

to:

```csharp
        if (!innerLambda.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true))
            return false;
```

And change line 528:

```csharp
        var isBareBody = !selector.Body.TryGetProjectionMembers(out var members);
```

to:

```csharp
        var isBareBody = !selector.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);
```

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, change line 2936:

```csharp
        if (!projectionBody.TryGetProjectionMembers(out var members))
        {
```

to:

```csharp
        if (!projectionBody.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true))
        {
```

And change line 2947:

```csharp
        if (foldedBody is not null && foldedBody.TryGetProjectionMembers(out var readFolded))
```

to:

```csharp
        if (foldedBody is not null && foldedBody.TryGetProjectionMembers(out var readFolded, allowPositionalConstructorArguments: true))
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyCtorProjectionTests"`
Expected: PASS.

- [ ] **Step 5: Regression-check existing SelectMany tests**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~SelectMany"` and `dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~SelectMany"`
Expected: PASS, identical to before this task.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyCtorProjectionTests.cs
git commit -m "EF-TBD: SelectMany result selector admits a ctor-only DTO natively"
```

---

### Task 4: Extract the bare-body arm's leaf-translation/alias-derivation into a reusable local function (pure refactor)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs:193-258` (`TryPopulateNativeProjection`'s `default:` arm)

**Interfaces:**
- Consumes: nothing new.
- Produces: a local function `bool TryBindAsBareProjection(Expression bareLikeExpr, string provisionalAlias, bool allowWholeRootEntityLeafForThis)`, declared inside `TryPopulateNativeProjection`, closing over that method's existing locals (`mongoQ`, `translator`, `selector`, `pendingLookups`, `pendingReducerLeaves`, `seenAliases`, `projections`, `leafIsArray`, `hasArrayLeaf` (ref via closure), `leafIsOwnedNavEntity`, `hasOwnedNavEntityLeaf` (ref via closure), `bareProjectionAlias` (ref via closure), `bareProjectionTier` (ref via closure)). Used by Task 5's new switch arm. Returns `true`/mutates the closed-over locals exactly as the pre-existing inline code did; returns `false` (no mutation beyond what already happened before the decline point — matching the pre-existing code's behavior) otherwise.

This task must not change any observable behavior — it is a pure code-motion refactor, verified by running the existing test suite unchanged before writing Task 5's new arm.

- [ ] **Step 1: Confirm the current behavior with a regression baseline**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Native"`
Expected: PASS. Record the pass count — Step 4 must reproduce it exactly.

- [ ] **Step 2: Extract the local function**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`, replace the `default:` arm (currently lines 193-258) with:

```csharp
            default:
            {
                // A bare body appended onto an ALREADY-POPULATED projection is declined outright. Reaching here
                // with Projection.Count > 0 means a prior Select on this same select definition already pushed
                // a $project down: the emitted $project would then carry both projections' entries while the
                // single bare-body ProjectionMember can name only one alias, and the alias-override table can
                // hold only one bare entry. Declining keeps the bare override provably write-once, which
                // AddProjectionAliasOverride relies on (it uses Dictionary.Add, so a second write throws).
                if (mongoQ.Select.Projection.Count > 0)
                {
                    return false;
                }

                // A provisional alias, needed only because TryTranslateLeaf's owned-array branch takes the
                // alias as an input (IsNativeArrayProjectionLeaf's alias-agreement conjunct). For a bare array
                // body that conjunct is vacuous, since we choose the alias it demands; what actually admits the
                // leaf is its own root-path check. Every other leaf kind ignores this alias, so the placeholder
                // is never observable.
                var provisionalAlias = selector.Body is MaterializeCollectionNavigationExpression materializeBare
                    ? (materializeBare.Navigation as INavigation)?.TargetEntityType.GetContainingElementName()
                      ?? BareLeafProvisionalAlias
                    : BareLeafProvisionalAlias;

                // allowWholeRootEntityLeaf is false here, so the owned-nav-entity leaf arm (gated on that same
                // flag, for the same reason as the whole-root-entity leaf) never fires for a TRUE bare body —
                // a bare `b => b.Address` keeps declining exactly as before this ticket. The ctor-wrap arm
                // below (added by the native-ctor-only-dto-projection ticket) passes true instead, deliberately
                // — see its own remarks.
                if (!TryBindAsBareProjection(selector.Body, provisionalAlias, allowWholeRootEntityLeafForThis: false))
                {
                    return false;
                }

                break;
            }
```

Immediately above this `switch` statement's closing (i.e. still inside `TryPopulateNativeProjection`, after the `switch` block, or as a local function declared anywhere in the method body — C# hoists local function declarations), add:

```csharp
        // Extracted from the bare-body arm above so the native-ctor-only-dto-projection ticket's new switch
        // arm (a single-argument ctor-only DTO's sole constructor argument, treated the same way a true bare
        // selector body is) can reuse the identical leaf-translation/alias-derivation/registration logic
        // without duplicating it. Closes over this method's own locals rather than taking them as parameters —
        // they are mutated here exactly as the original inline code mutated them.
        //
        // Derive the FINAL alias from the translated leaf rather than from the syntax.
        //
        // Tier 1 is tried first, and the ordering is load-bearing: a leaf with a root-relative document path
        // must take it, since that's what makes the alias-addressed read and the document-path read the same
        // read, letting the late-fallback strip work for it. Tier 2 answers only for a leaf tier 1 cannot — a
        // computed leaf backed by no document element — by choosing the alias the driver would emit for a bare
        // body, so leaving the driver's push-down in place is the correct fallback (hence Synthetic, and hence
        // the strip not firing).
        bool TryBindAsBareProjection(Expression bareLikeExpr, string provisionalAlias, bool allowWholeRootEntityLeafForThis)
        {
            if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], bareLikeExpr, provisionalAlias,
                    pendingLookups, pendingReducerLeaves, out var bareLeaf, out var bareIsArrayLeaf, out _,
                    allowWholeRootEntityLeafForThis))
            {
                return false;
            }

            string derivedAlias;
            if (TryDeriveDocumentPathAlias(bareLeaf, out var documentPathAlias))
            {
                derivedAlias = documentPathAlias;
                bareProjectionTier = ProjectionAliasTier.DocumentPath;
            }
            else if (TryDeriveSyntheticAlias(bareLeaf, selector, pendingLookups, out var syntheticAlias))
            {
                derivedAlias = syntheticAlias;
                bareProjectionTier = ProjectionAliasTier.Synthetic;
            }
            else
            {
                return false;
            }

            bareProjectionAlias = derivedAlias;
            seenAliases.Add(derivedAlias);
            projections.Add(new MongoProjection(derivedAlias, bareLeaf));
            leafIsArray.Add(bareIsArrayLeaf);
            hasArrayLeaf |= bareIsArrayLeaf;
            // A bare body never admits the owned-nav-entity leaf when allowWholeRootEntityLeafForThis is false
            // (see the comment at the true-bare-body call site); the ctor-wrap arm passes true and CAN admit
            // one, but that leaf is never THIS one — the owned-nav-entity leaf's own isOwnedNavEntityLeaf out
            // parameter is discarded here (`out _`) because it can only ever be produced through the WRAPPED
            // arm's own alias-must-equal-member-name path (see TryTranslateLeaf's remarks on that leaf kind),
            // never through this bare/positional path, so it is always false for any leaf this function admits.
            leafIsOwnedNavEntity.Add(false);
            return true;
        }
```

- [ ] **Step 3: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean (no unused-variable/unreachable-code warnings — `TryBindAsBareProjection` is now called from exactly one place, matching Step 1's behavior).

- [ ] **Step 4: Re-run the regression baseline and confirm it is unchanged**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Native"`
Expected: PASS, identical pass count to Step 1 — this is a pure refactor, so nothing may change.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs
git commit -m "EF-TBD: extract bare-body leaf-translation into a reusable local function (no behavior change)"
```

---

### Task 5: New recognizer arm for a single-argument ctor-only DTO in ordinary `Select`/`Join`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (new `case` in `TryPopulateNativeProjection`'s `switch (selector.Body)`, added by Task 4's refactor)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCtorOnlyProjectionTests.cs` (new file)

**Interfaces:**
- Consumes: `TryBindAsBareProjection` (from Task 4), `IsSelectorParameter` (existing, `NativeProjectionBinder.cs:884`), `BareLeafProvisionalAlias` (existing constant).
- Produces: nothing new — this is the terminal recognizer arm; nothing downstream depends on it beyond `TryPopulateNativeProjection`'s own return value.

- [ ] **Step 1: Write the failing functional tests**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCtorOnlyProjectionTests.cs`:

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

using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeCtorOnlyProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, System.Action<ModelBuilder>? modelBuilderAction = null)
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

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 1: whole-entity ctor argument — bypasses projection, lands on NativeRoute.WholeEntity
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string CustomerID { get; set; } = "";
    }

    private class CustomerDtoWithEntityInCtor
    {
        public string Id { get; }
        public CustomerDtoWithEntityInCtor(Customer customer) => Id = customer.CustomerID;
    }

    private IMongoCollection<Customer> SeedCustomers(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ALFKI" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ANATR" } },
        ]);
        return database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Select_with_whole_entity_ctor_only_dto_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_whole_entity_ctor_only_dto_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the whole-entity ctor-only DTO went native (via NativeRoute.WholeEntity — no $project).
        var results = db.Entities.Select(x => new CustomerDtoWithEntityInCtor(x)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "ALFKI");
        Assert.Contains(results, r => r.Id == "ANATR");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 2a: scalar ctor argument
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class IdWrapper
    {
        public string Value { get; }
        public IdWrapper(string value) => Value = value;
    }

    [Fact]
    public void Select_with_scalar_ctor_only_dto_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_scalar_ctor_only_dto_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.Select(x => new IdWrapper(x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Value == "ALFKI");
        Assert.Contains(results, r => r.Value == "ANATR");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 2b: owned single-reference nav-entity ctor argument
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private class AddressDto
    {
        public string City { get; }
        public AddressDto(Address address) => City = address.City;
    }

    private static readonly System.Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsOne(b => b.Address);

    [Fact]
    public void Select_with_owned_nav_entity_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Select_with_owned_nav_entity_ctor_only_dto_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Address", new BsonDocument { { "City", "NYC" } } }
        });
        var collection = database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var results = db.Entities.Select(b => new AddressDto(b.Address)).ToList();

        var dto = Assert.Single(results);
        Assert.Equal("NYC", dto.City);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Guard: two-argument ctor-only DTO still declines (falls back / throws under NativeOnly)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class TwoArgDto
    {
        public string A { get; }
        public string B { get; }
        public TwoArgDto(string a, string b) { A = a; B = b; }
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_still_declines_under_native_only()
    {
        var collection = SeedCustomers(nameof(Select_with_two_argument_ctor_only_dto_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<Query.NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList());
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_still_works_under_default_native_mode_via_fallback()
    {
        var collection = SeedCustomers(nameof(Select_with_two_argument_ctor_only_dto_still_works_under_default_native_mode_via_fallback));
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var results = db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
    }
}
```

*(If `NativeTranslationNotSupportedException`'s namespace differs from `MongoDB.EntityFrameworkCore.Query`, adjust the `using`/qualification to match — confirm against an existing `NativeOnly`-mode decline test, e.g. one in `NativeOwnedReferenceWholeEntityTests.cs`'s sibling "declines" section, before finalizing.)*

- [ ] **Step 2: Run tests to verify the three positive ones fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeCtorOnlyProjectionTests"`
Expected: `Select_with_whole_entity_ctor_only_dto_goes_native`, `Select_with_scalar_ctor_only_dto_goes_native`, and `Select_with_owned_nav_entity_ctor_only_dto_goes_native` FAIL with `NativeTranslationNotSupportedException`. `Select_with_two_argument_ctor_only_dto_still_declines_under_native_only` and its default-mode sibling already PASS (today's fallback behavior).

- [ ] **Step 3: Add the new switch arm**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`, inside `TryPopulateNativeProjection`'s `switch (selector.Body)` (the one Task 4 left with the WRAPPED case, then `default:`), add a new `case` between them:

```csharp
            // A CTOR-ONLY DTO — `x => new CustomerDtoWithEntityInCtor(x)` — as opposed to the WRAPPED case
            // above (which requires TryGetProjectionMembers to succeed, i.e. NewExpression.Members non-null).
            // Members is null here because the compiler only synthesizes it when every constructor parameter
            // maps 1:1 by name to a same-named property, which a DTO computing its own properties in its body
            // does not do. Capped at exactly one constructor argument — see this ticket's design doc for why:
            // the read side (MongoProjectionBindingExpressionVisitor.VisitNew) resolves a Members-null body's
            // arguments through EF Core's ProjectionMember/MemberInfo-keyed dictionary with no Enter/Exit at
            // all, so a SECOND unnamed argument would collide under the same ambient key with no safe way to
            // distinguish them (ProjectionMember.Append accepts only a real MemberInfo — no synthetic key).
            case NewExpression { Members: null, Arguments: { Count: 1 } ctorArguments }:
            {
                var ctorArgument = ctorArguments[0];

                // Sub-case 1: the sole argument literally IS the selector's own root parameter (possibly
                // wrapped in EF auto-include layers, per IsSelectorParameter) — the whole entity, unchanged.
                // No server-side reshaping is needed at all: leaving Select.Projection untouched lets
                // MongoSelectDefinition.Route resolve to the pre-existing NativeRoute.WholeEntity, exactly as
                // it would for a plain Set<Customer>() with no Select — every existing entity-materialization
                // concern (discriminator narrowing, key handling, Include fix-up) applies unchanged.
                //
                // This must be checked BEFORE falling through to sub-case 2's TryBindAsBareProjection: were
                // this leaf run through TryTranslateLeaf's whole-root-entity-leaf arm instead, it would
                // translate to a MongoElementRefExpression whose Path is the "$ROOT" sentinel
                // (MongoElementRefExpression.WholeRootDocumentPath) — and TryDeriveDocumentPathAlias's
                // MongoElementRefExpression case (it matches any UNDOTTED path, "$ROOT" included) would then
                // hand that back as the $project output field's ALIAS, which is not a valid emitted field name
                // and has no matching read-side wiring for a bare projection (the existing whole-root-entity
                // read machinery, MongoProjectionBindingRemovingExpressionVisitor's IsWholeRootEntityAlias, is
                // reachable only via the WRAPPED path). Confirmed empirically against the live code during
                // this ticket's design — do not remove this branch or reorder it after sub-case 2.
                if (IsSelectorParameter(ctorArgument, selector.Parameters[0])
                    && ctorArgument.Type == mongoQ.CollectionExpression.EntityType.ClrType)
                {
                    break;
                }

                // Sub-case 2: any other single-argument shape TryTranslateLeaf recognizes as a bare-admissible
                // leaf — a scalar/computed field, or an owned single-reference navigation entity
                // (allowWholeRootEntityLeafForThis: true admits the latter here; a TRUE bare body still
                // declines it — see TryBindAsBareProjection's own remarks). Reuses the exact same tier-1/
                // tier-2 alias derivation and bareProjectionAlias/bareProjectionTier registration the bare-body
                // arm uses, so the read side (VisitNew's ambient/root-member, null-keyed lookup) resolves it
                // identically — VisitNew already visits a Members-null NewExpression's sole argument under
                // whatever ProjectionMember is ambient, unconditionally, today.
                if (!TryBindAsBareProjection(ctorArgument, BareLeafProvisionalAlias, allowWholeRootEntityLeafForThis: true))
                {
                    return false;
                }

                break;
            }
```

Place this `case` immediately after the existing WRAPPED case (the one ending `break;` before Task 4's `default:` arm) and before `default:`.

- [ ] **Step 4: Build and run the new tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeCtorOnlyProjectionTests"`
Expected: all 5 tests PASS.

- [ ] **Step 5: Full regression run**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"` and `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build`
Expected: PASS, no new failures — the new arm only admits a shape (`NewExpression` with `Members: null`) that every pre-existing case already declined, so nothing previously admitted can regress.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCtorOnlyProjectionTests.cs
git commit -m "EF-TBD: single-argument ctor-only DTO goes native in ordinary Select"
```

---

### Task 6: Flip the upstream spec test and run the full regression suite

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs` (both `Entity_passed_to_DTO_constructor_works` overrides — the earlier research found one near line 121-130 and a duplicate near line 2080-2088, for two different fixture parameterizations)

**Interfaces:**
- Consumes: Task 5's new native path.
- Produces: nothing — this task only proves the fix end-to-end and checks for regressions.

- [ ] **Step 1: Locate both overrides and read their current bodies**

Run: `grep -n "Entity_passed_to_DTO_constructor_works" tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs`

Read each match's surrounding ~15 lines to see the current `AssertMql(...)` baseline (expected to be the bare `Customers.` scan, per this ticket's design doc investigation) and whether either override currently carries a `// Fails:` marker or a `Skip`.

- [ ] **Step 2: Run each override under `MONGODB_EF_NATIVE_ONLY=1` to confirm it now passes natively**

Run (for each fixture class the two overrides belong to):
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSelectQueryMongoTest.Entity_passed_to_DTO_constructor_works"
```
Expected: PASS for both (previously these either fell back silently — passing today only via driver-LINQ — or, if any variant was previously marked `// Fails:`/declining under `NativeOnly`, this run now shows it passing).

- [ ] **Step 3: If either override was marked `// Fails:` (declining), remove that marker and its doc entry**

If Step 1/2 found a `// Fails:` marker on either override, remove the comment and update whatever tracking doc lists current `// Fails:` entries (per the SpecificationTests `AGENTS.md`, `// Fails:` is the durable known-gap marker — removing it here means the gap is closed). Search for a companion doc (e.g. a native-query status doc under `docs/`) that enumerates `// Fails:` entries and update the count if one exists and references this test by name.

- [ ] **Step 4: Regenerate the MQL baseline if it changed**

Run:
```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSelectQueryMongoTest.Entity_passed_to_DTO_constructor_works"
```
Then `git diff` the test file. Per this ticket's design-doc investigation, the baseline is expected to be UNCHANGED (still the bare `Customers.` scan — sub-case 1 of Task 5 adds no `$project`, so the emitted pipeline is identical; only the translation PATH changed from fallback to native). If the diff shows any change, inspect it carefully before accepting — an unexpected MQL change here would mean the "no server-side reshaping" claim in Task 5 was wrong.

- [ ] **Step 5: Rebuild and re-run without the rewrite flag to confirm genuinely green**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~Entity_passed_to_DTO_constructor_works"`
Expected: PASS (both variants), in both default (`Native`) mode and (separately) with `MONGODB_EF_NATIVE_ONLY=1` set.

- [ ] **Step 6: Full spec-suite regression, both modes**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build
```
Expected: zero `Passed → Failed` regressions in either run compared to this branch's pre-existing baseline; `Entity_passed_to_DTO_constructor_works` (both variants) is the only expected `Failed → Passed` flip under the `NativeOnly` run (plus, incidentally, whatever other currently-declining spec test happens to share this exact single-argument-ctor-only shape — check the diff for any surprise flips and investigate rather than assume they're fine).

- [ ] **Step 7: Confirm `MongoQueryMode.DriverLinq` is unaffected**

Run: a quick manual check or existing test sweep confirming an explicit `UseQueryMode(MongoQueryMode.DriverLinq)` context still executes `Entity_passed_to_DTO_constructor_works`'s shape correctly (this ticket must not change the fallback path's own behavior, only add a native path alongside it). If no existing test exercises this explicitly, add one alongside Task 5's test file asserting the same query under `MongoQueryMode.DriverLinq` returns identical results.

- [ ] **Step 8: Spot-check EF8/EF9**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" && dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF8" --no-build --filter "FullyQualifiedName~Entity_passed_to_DTO_constructor_works"` and the same for `EF9`.
Expected: PASS on both — no `#if EF8`/`#if EF9` guard is expected anywhere in this plan's changes (none of `NativeProjectionBinder.cs`, `NativeGroupByBinder.cs`, `NativeSelectManyBinder.cs`, `MongoQueryableMethodTranslatingExpressionVisitor.cs`, or `ExpressionExtensionMethods.cs` were touched with version-conditional code in this plan), so this should need no changes — confirm rather than assume.

- [ ] **Step 9: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs
git commit -m "EF-TBD: Entity_passed_to_DTO_constructor_works now goes native"
```

(If Step 3/4 touched no files — the baseline and markers were already exactly as expected — this task still ends with an empty/no-op commit step; note that in the final report rather than fabricating a change.)

---

## Self-Review Notes

- **Spec coverage:** Family A (single-argument ctor-only DTO in ordinary `Select`) is Tasks 4-5-6. Family B (`GroupBy`/`SelectMany` result-selector, any arity) is Tasks 1-2-3. The spec's explicit out-of-scope items (`NativeJoinScopeProjectionBinder`, `MemberInitExpression`-with-ctor-args, 2+-argument ordinary-`Select` ctors, nested ctor-only DTOs) are all left untouched by construction — no task modifies `NativeJoinScopeProjectionBinder.cs` or `TryGetProjectionMembers`'s `MemberInitExpression` branch.
- **Placeholder scan:** every step has concrete code, exact file/line anchors as read directly from the live source during planning, and named test methods with real assertions — no "TBD"/"similar to Task N"/handwaved error handling.
- **Type consistency:** `TryGetProjectionMembers(this Expression body, out IReadOnlyList<(string MemberName, Expression Value)> members, bool allowPositionalConstructorArguments = false)` is the exact signature introduced in Task 1 and referenced identically in Tasks 2 and 3. `TryBindAsBareProjection(Expression bareLikeExpr, string provisionalAlias, bool allowWholeRootEntityLeafForThis)` is introduced in Task 4 and called with that exact signature in Task 5.
