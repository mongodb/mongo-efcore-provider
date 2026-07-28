# Native filtered-inner reference `SelectMany` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a filtered cross-collection reference `SelectMany` (`from o in q from r in o.Refs.Where(pred) select …`, inner-element-only `pred`) go native, for both the projected and bare-entity result shapes, by emitting the user filter as a `$match` on the unwound element.

**Architecture:** `NativeSelectManyBinder.TryBindReferenceNavUnwind` peels/splits the inner user predicate off the FK-correlated subquery (leaving the shared `NativeCorrelationMatcher` contract untouched), translates it against the inner target entity type, prefixes its field refs with the `_lookup_<Nav>` scope, and stores it on `MongoUnwindSource.Filter`. `MongoSelectLowerer` emits a `MongoMatchStage` for that filter right after the reference `$unwind` and before the `$replaceRoot`/`$project`.

**Tech Stack:** C#, EF Core provider internals, xUnit (plain `Assert.*`, no FluentAssertions), MongoDB aggregation pipeline.

## Global Constraints

- `<Nullable>enable</Nullable>` on `src/` — annotate new types accordingly.
- All touched types are `internal`; this change is additive and not a breaking change (filtered reference `SelectMany` hard-fails today, in every mode).
- Multi-EF: expect **no `#if`** (identical EF8/EF9/EF10). Task 1's spike explicitly checks the arriving tree on all three; full 3-version `/test-all` runs before squash.
- Reference `SelectMany` has **no driver-LINQ oracle** — supported shapes are proven via `MongoQueryMode.NativeOnly` succeeding + expected in-memory result-set assertions; unsupported shapes hard-fail in every mode.
- Preserve file BOMs.
- Build a single EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Run one class: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- Subagent-driven-development, **stop after every task** for review.

---

## File Structure

- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs` — **modify**: add nullable `Filter` property.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs` — **create**: recursively prefix every `MongoFieldExpression`'s element name with a scope path.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs` — **modify**: `TryBindReferenceNavUnwind` peel/split + filter capture; add private `TrySplitCorrelation`/`FlattenAndAlso`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — **modify**: emit the filter `$match` in the `UnwindSource` block.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — **modify**: as-built note.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs` — **create**.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` — **modify**: flip `filtered_inner_returns_false`; add nested/stacked/decline tests.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs` — **modify**: filter `$match` emission test.
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` — **modify**: flip `Reference_form_filtered_inner_hard_fails_in_every_mode`; add projected/bare/stacked/zero/parametrized/decline tests.

---

## Task 1: Spike — confirm the arriving collection-selector tree

**Files:**
- Create: `.superpowers/sdd/EF-347-ref-filtered-inner-spike.md` (gitignored; NOT committed)

**Interfaces:**
- Produces (findings only, no code): whether EF's nav-expansion emits a **nested** `Where(Where(root, fkPred), userPred)` or a **folded** `Where(root, fkPred && userPred)` for `from o in q from r in o.Refs.Where(r => r.Tag != "x") select …`, and whether it differs across EF8/EF9/EF10. Also: the exact shape for **stacked** `.Where(p1).Where(p2)` and for a **bare-entity** trailing `select r`.

- [ ] **Step 1: Add a temporary probe test that dumps the collection-selector tree**

Add a throwaway `[Fact]` to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` that runs a filtered reference SelectMany under `MongoQueryMode.DriverLinq` inside a `try`/`catch` and, crucially, captures the nav-expanded tree. The simplest reliable probe is to set a breakpoint-free dump: temporarily add a `Console.WriteLine(collectionSelector)` at the top of `NativeSelectManyBinder.TryBindReferenceNavUnwind` (guarded by an env var), OR add an xUnit test that constructs the query and inspects EF's translation via the existing `MongoQueryMode.NativeOnly` throw message. Prefer the env-var dump:

In `TryBindReferenceNavUnwind`, temporarily add at the very top (REMOVE before Task 3):

```csharp
if (Environment.GetEnvironmentVariable("EF_SPIKE_DUMP") == "1")
    System.Console.Error.WriteLine("SELECTMANY-SELECTOR: " + collectionSelector.Body);
```

- [ ] **Step 2: Run the probe on EF10, capture the tree**

Run:
```bash
EF_SPIKE_DUMP=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form_filtered_inner_hard_fails_in_every_mode" 2>&1 | grep SELECTMANY-SELECTOR
```
Expected: one or more `SELECTMANY-SELECTOR: …` lines showing the `Where(...)` shape. Record verbatim.

- [ ] **Step 3: Repeat on EF8 and EF9**

Run the same command with `-c "Debug EF8"` and `-c "Debug EF9"`. Record any differences.

- [ ] **Step 4: Record findings and remove the probe**

Write `.superpowers/sdd/EF-347-ref-filtered-inner-spike.md` with: the captured tree(s) per EF version, the verdict (nested vs folded), the stacked-filter shape, and the bare-entity shape. Remove the temporary `EF_SPIKE_DUMP` dump line from `TryBindReferenceNavUnwind`.

- [ ] **Step 5: STOP for review**

Report findings to the controller. The Task 3 implementation below is written to handle **both** nested and folded (so it is robust regardless), but the controller confirms the primary path matches the spike and that no EF-version divergence needs an `#if`.

---

## Task 2: `MongoUnwindSource.Filter` + `MongoFieldPrefixRewriter`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs`

**Interfaces:**
- Produces: `MongoUnwindSource.Filter` (`MongoExpression?`, `{ get; set; }`, default null). `MongoFieldPrefixRewriter.Rewrite(MongoExpression expr, string prefix) → MongoExpression` — returns a tree identical to `expr` except every `MongoFieldExpression`'s `ElementName` is `prefix + "." + original`.
- Consumes: `MongoExpression` subtypes (`MongoFieldExpression`, `MongoBinaryExpression`, `MongoUnaryExpression`, `MongoInExpression`, `MongoRegexExpression`, `MongoConstantExpression`, `MongoParameterExpression`).

- [ ] **Step 1: Write the failing rewriter test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs` (copy the license header/BOM from a sibling test file):

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoFieldPrefixRewriterTests
{
    private static MongoFieldExpression Field(string name)
        // Property is not needed by the rewriter (it only rewrites ElementName); pass any IProperty-free
        // field via the test's shared helper. Use the same IProperty stand-in sibling tests use, or a real
        // property from a tiny model. Simplest: reuse an existing helper if present, else build from a model.
        => TestFields.Named(name);

    [Fact]
    public void Rewrites_a_bare_field_element_name_with_the_prefix()
    {
        var rewritten = (MongoFieldExpression)MongoFieldPrefixRewriter.Rewrite(Field("Total"), "_lookup_Refs");
        Assert.Equal("_lookup_Refs.Total", rewritten.ElementName);
    }

    [Fact]
    public void Rewrites_fields_nested_in_binary_and_unary_nodes()
    {
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, Field("Total"),
                new MongoConstantExpression(100, forSerialization: null)),
            new MongoUnaryExpression(MongoUnaryOperator.Not,
                new MongoBinaryExpression(MongoBinaryOperator.Equal, Field("Name"),
                    new MongoConstantExpression("x", forSerialization: null))));

        var rewritten = (MongoBinaryExpression)MongoFieldPrefixRewriter.Rewrite(expr, "_lookup_Refs");
        var left = (MongoBinaryExpression)rewritten.Left;
        var right = (MongoBinaryExpression)((MongoUnaryExpression)rewritten.Right).Operand;
        Assert.Equal("_lookup_Refs.Total", ((MongoFieldExpression)left.Left).ElementName);
        Assert.Equal("_lookup_Refs.Name", ((MongoFieldExpression)right.Left).ElementName);
    }
}
```

Note: for `Field(...)`, use the same field-construction helper other unit tests in this folder use to get a real `IProperty` (grep `new MongoFieldExpression(` under `tests/.../UnitTests/Query/NativeTranslation/` for the established pattern, e.g. `MongoQueryLanguageRendererTests`). If none is shared, build a one-entity model inline (`Owner { Total, Name }`) and pull `IProperty` via `entityType.FindProperty("Total")`, then `new MongoFieldExpression(prop, prop.GetElementName())`. Do not invent a `TestFields` helper if none exists — replace `Field` with whichever real pattern the sibling tests use.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoFieldPrefixRewriterTests"`
Expected: FAIL (compile error — `MongoFieldPrefixRewriter` does not exist).

- [ ] **Step 3: Create the rewriter**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs` (copy license header/BOM from `MongoSelectLowerer.cs`):

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Rewrites every <see cref="MongoFieldExpression"/> in a translated predicate tree so its element name is
/// prefixed with a document scope path (e.g. the <c>_lookup_&lt;Nav&gt;</c> alias of a reference SelectMany's
/// unwound element), turning a predicate translated against the inner target entity type
/// (<c>Total</c>) into one that matches the unwound-and-prefixed document (<c>_lookup_Refs.Total</c>).
/// Generalizes the single-field prefixing <c>NativeSelectManyBinder.TryTranslateScopedField</c> performs.
/// </summary>
internal static class MongoFieldPrefixRewriter
{
    public static MongoExpression Rewrite(MongoExpression expr, string prefix)
        => expr switch
        {
            MongoFieldExpression f => new MongoFieldExpression(f.Property, prefix + "." + f.ElementName),
            MongoBinaryExpression b => new MongoBinaryExpression(
                b.Operator, Rewrite(b.Left, prefix), Rewrite(b.Right, prefix)),
            MongoUnaryExpression u => new MongoUnaryExpression(u.Operator, Rewrite(u.Operand, prefix)),
            MongoInExpression i => new MongoInExpression(
                (MongoFieldExpression)Rewrite(i.Field, prefix), Rewrite(i.Values, prefix), i.Negated),
            MongoRegexExpression r => new MongoRegexExpression(
                (MongoFieldExpression)Rewrite(r.Field, prefix), r.Kind, Rewrite(r.Term, prefix), r.Negated),
            MongoConstantExpression or MongoParameterExpression => expr,
            _ => throw new NativeTranslationNotSupportedException(
                $"Cannot prefix-rewrite MongoExpression node '{expr.GetType().Name}'.")
        };
}
```

- [ ] **Step 4: Add the `Filter` property to `MongoUnwindSource`**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs`, after the `WholeElement` property (line ~91), add:

```csharp
    /// <summary>
    /// An inner-element-only user filter (<c>o.Refs.Where(r =&gt; r.Total &gt; 100)</c>) to apply to the
    /// unwound element, already scope-prefixed with <see cref="InnerScopePath"/> (e.g. field refs read as
    /// <c>_lookup_Refs.Total</c>). Set by <c>NativeSelectManyBinder.TryBindReferenceNavUnwind</c>; the lowerer
    /// emits it as a <c>$match</c> immediately after the reference <c>$unwind</c> and before the
    /// <c>$replaceRoot</c>/<c>$project</c>. <see langword="null"/> when the inner collection is unfiltered.
    /// </summary>
    public MongoExpression? Filter { get; set; }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoFieldPrefixRewriterTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs \
  src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs
git commit -m "EF-347: MongoUnwindSource.Filter + MongoFieldPrefixRewriter"
```

- [ ] **Step 7: STOP for review**

---

## Task 3: `TryBindReferenceNavUnwind` peel/split + filter capture

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: `MongoFieldPrefixRewriter.Rewrite`, `MongoUnwindSource.Filter` (Task 2); `NativeCorrelationMatcher.TryMatchCorrelatedCollection` (unchanged); `MongoExpressionTranslator(IEntityType).TryTranslate(Expression, out MongoExpression?)`.
- Produces: `TryBindReferenceNavUnwind` now binds a filtered inner (setting `UnwindSource.Filter`) and still declines correlated-beyond-FK / translator-unsupported filters (returns false, no mutation).

**PREREQUISITE:** Task 1 spike confirmed the arriving shape. The code below handles **both** nested `Where(Where(root, fkPred), userPred)` and folded `Where(root, fkPred && userPred)`; if the spike showed an EF-version-specific shape needing `#if`, add it per findings.

- [ ] **Step 1: Flip the existing unit test and add new binder tests**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`:

(a) **Add a nested-selector helper** next to `ReferenceCollectionSelector` (line ~388):

```csharp
    // Queryable.Where(Queryable.Where(EntityQueryRootExpression<Tag>, fkPred), userPred) — a filtered inner
    // (c.Tags.Where(userPred)) nested form.
    private static LambdaExpression ReferenceCollectionSelectorFiltered(
        IEntityType targetEntityType, ParameterExpression outerParam, LambdaExpression fkPredicate,
        params LambdaExpression[] userPredicates)
    {
        Expression source = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [fkPredicate.Parameters[0].Type],
            new EntityQueryRootExpression(targetEntityType), Expression.Quote(fkPredicate));
        foreach (var userPredicate in userPredicates)
            source = Expression.Call(
                typeof(Queryable), nameof(Queryable.Where), [userPredicate.Parameters[0].Type],
                source, Expression.Quote(userPredicate));
        return Expression.Lambda(source, outerParam);
    }
```

(b) **Replace** `TryBindReferenceNavUnwind_filtered_inner_returns_false` (line ~444) with a nested-form binding test plus a stacked-form test:

```csharp
    [Fact]
    public void TryBindReferenceNavUnwind_nested_filtered_inner_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user = Expression.Lambda(
            Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x")),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource!;
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind.Kind);
        Assert.NotNull(unwind.Filter);
        // The user filter's field ref is prefixed with the lookup scope.
        var binary = Assert.IsType<MongoBinaryExpression>(unwind.Filter);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("_lookup_Tags.Label", field.ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_stacked_filters_bind_and_and_together()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user1 = Expression.Lambda(Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x")), tParam);
        var user2 = Expression.Lambda(Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("y")), tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user1, user2);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, ((MongoBinaryExpression)mongoQ.Select.UnwindSource!.Filter!).Operator);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_folded_filtered_inner_binds_with_filter()
    {
        // The synthetic folded shape Where(root, fkPred && userPred). Handled by TrySplitCorrelation.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var extra = Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x"));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, extra), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.NotNull(mongoQ.Select.UnwindSource!.Filter);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_correlated_beyond_fk_filter_returns_false()
    {
        // A user filter referencing the OUTER param (o.Id) beyond the FK correlation: the inner-scope
        // translator rejects the foreign-rooted member access, so the whole bind declines cleanly.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        // r.OwnerId != o.Id — references o (outer) beyond the FK correlation.
        var user = Expression.Lambda(
            Expression.NotEqual(Expression.Property(tParam, nameof(Tag.OwnerId)), Expression.Property(outerParam, nameof(Owner.Id))),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation
    }
```

(Keep `TryBindReferenceNavUnwind_binds_reference_collection_to_lookup_and_unwind_source`, `_owned_collection_returns_false`, `_non_where_body_returns_false` unchanged — the unfiltered bind must still set `Filter == null`. Add `Assert.Null(unwind.Filter);` to the first of those.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"`
Expected: FAIL — the new nested/stacked/correlated-beyond-FK tests fail (filter not captured; nested `Where` currently returns false).

- [ ] **Step 3: Rewrite `TryBindReferenceNavUnwind` to peel/split + capture the filter**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, replace the body of `TryBindReferenceNavUnwind` (lines ~163–198) with:

```csharp
    internal static bool TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Peel user-predicate Where layers. A filtered inner c.Refs.Where(p1).Where(p2) nav-expands to
        // Where(Where(Where(root, fkPred), p1), p2): the innermost Where over the query root carries the FK
        // correlation EF injects; every OUTER Where is an inner-element-only user filter. (A single Where whose
        // predicate is fkPred && userPred — the "folded" shape — is split below by TrySplitCorrelation.)
        var body = UnwrapAsQueryable(collectionSelector.Body);
        var userPredicates = new List<LambdaExpression>();
        while (body is MethodCallExpression
               {
                   Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var outerDecl },
                   Arguments: [var outerSource, var outerPredArg]
               }
               && outerDecl == typeof(System.Linq.Queryable)
               && UnwrapAsQueryable(outerSource) is not EntityQueryRootExpression)
        {
            userPredicates.Add(outerPredArg.UnwrapLambdaFromQuote());
            body = UnwrapAsQueryable(outerSource);
        }

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

        var outerEntityType = mongoQ.CollectionExpression.EntityType;

        // Isolate the FK correlation (→ the reference navigation) from any user conjunct FOLDED into the
        // innermost predicate; the shared matcher's own reject-extra-conjunct contract is untouched.
        if (!TrySplitCorrelation(predicate.Body, outerEntityType, outerParam, root.EntityType,
                out var navigation, out var foldedUserBody))
            return false;

        // Translate each user filter (peeled Where layers + any folded conjunct) against the inner target
        // entity type, then prefix its field refs with the $lookup scope, ANDing them into one predicate. Any
        // filter the translator can't handle — including one referencing outer members beyond the FK
        // (correlated-beyond-FK, which roots a member access on a non-inner parameter) — declines cleanly.
        var scope = LookupExpression.GetLookupAlias(navigation);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        MongoExpression? filter = null;

        if (foldedUserBody != null)
        {
            if (!innerTranslator.TryTranslate(foldedUserBody, out var foldedExpr))
                return false;
            filter = MongoFieldPrefixRewriter.Rewrite(foldedExpr!, scope);
        }

        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1
                || !innerTranslator.TryTranslate(userPredicate.Body, out var userExpr))
                return false;
            var prefixed = MongoFieldPrefixRewriter.Rewrite(userExpr!, scope);
            filter = filter == null
                ? prefixed
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, prefixed);
        }

        var lookup = new LookupExpression(navigation, forceUnwind: true);
        mongoQ.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(scope, navigation.TargetEntityType, lookup);
        unwind.Filter = filter;
        mongoQ.Select.UnwindSource = unwind;
        return true;
    }

    /// <summary>
    /// Resolves the FK-correlated reference navigation from the innermost <c>Where</c> predicate, isolating it
    /// from any inner-element user filter FOLDED into the same predicate (<c>fkPred &amp;&amp; userPred</c>).
    /// The shared <see cref="NativeCorrelationMatcher"/> is only ever fed the isolated FK-correlation
    /// expression, so its reject-extra-conjunct contract (which keeps a filtered <c>Count</c> on fallback) is
    /// unchanged. A null-guarded FK correlation is itself an <c>AndAlso</c>; if such a correlation is ALSO
    /// folded with a user conjunct, flattening splits its guard from its equality and neither matches alone, so
    /// that rare shape declines cleanly (falls back / hard-fails) rather than mis-binding.
    /// </summary>
    private static bool TrySplitCorrelation(
        Expression predicateBody, IEntityType outerEntityType, ParameterExpression outerParam,
        IEntityType targetEntityType, out INavigation navigation, out Expression? userBody)
    {
        userBody = null;

        // Nested / pure FK correlation: the whole innermost predicate IS the FK correlation.
        if (NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicateBody, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation))
            return true;

        // Folded: fkPred && userPred. Flatten top-level AndAlso conjuncts, find the ONE that is the FK
        // correlation, recombine the rest as the user filter.
        var conjuncts = new List<Expression>();
        FlattenAndAlso(predicateBody, conjuncts);
        if (conjuncts.Count < 2)
            return false;

        Expression? fkConjunct = null;
        var rest = new List<Expression>();
        foreach (var conjunct in conjuncts)
        {
            if (fkConjunct == null
                && NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                    conjunct, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation))
                fkConjunct = conjunct;
            else
                rest.Add(conjunct);
        }

        if (fkConjunct == null || rest.Count == 0)
            return false;

        userBody = rest.Aggregate(Expression.AndAlso);
        return true;
    }

    private static void FlattenAndAlso(Expression expression, List<Expression> conjuncts)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            FlattenAndAlso(andAlso.Left, conjuncts);
            FlattenAndAlso(andAlso.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expression);
        }
    }
```

Add `using System.Linq;` (for `Aggregate`) and `using Microsoft.EntityFrameworkCore.Metadata;` (for `INavigation`/`IEntityType`) to the top of the file if not already present.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"`
Expected: PASS (all binder tests, including the unchanged unfiltered ones with `Filter == null`).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: TryBindReferenceNavUnwind captures inner-element filter"
```

- [ ] **Step 6: STOP for review**

---

## Task 4: Lowerer — emit the filter `$match`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoUnwindSource.Filter` (Task 2), `MongoUnwindSource.Reference` (existing).
- Produces: `Lower` emits a `MongoMatchStage` after the reference `$unwind` (already emitted by `AppendLookupStages`) and before the `$replaceRoot`/`$project`.

- [ ] **Step 1: Write the failing lowerer test**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`, add a test that builds a reference `UnwindSource` with a `Filter` and asserts the `$match` slots in correctly. Model it on the existing reference-SelectMany lowerer test in that file (grep for `UnwindSource`/`MongoUnwindSource.Reference`/`WholeElement` to find the established construction pattern and reuse its `MongoQueryExpression`/`LookupExpression` setup). The assertion:

```csharp
    [Fact]
    public void Reference_unwind_with_filter_emits_match_after_unwind_before_terminal()
    {
        // Build a reference UnwindSource query with a Filter set (mirror the existing reference-SelectMany
        // lowerer test's setup, then set unwind.Filter and WholeElement as below).
        var query = /* existing helper that builds a reference-SelectMany MongoQueryExpression */;
        var unwind = MongoUnwindSource.Reference("_lookup_Refs", /*innerEntityType*/, /*lookup*/);
        unwind.WholeElement = true;
        unwind.Filter = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(/*someProp*/, "_lookup_Refs.Total"),
            new MongoConstantExpression(100, forSerialization: null));
        query.Select.UnwindSource = unwind;

        var stages = new MongoSelectLowerer().Lower(query);

        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var unwindIndex = stages.FindIndex(s => s is MongoUnwindStage);
        var replaceRootIndex = stages.FindIndex(s => s is MongoReplaceRootStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the reference $unwind");
        Assert.True(replaceRootIndex > matchIndex, "filter $match must precede the $replaceRoot");
    }
```

Fill the `/* … */` placeholders using the file's existing reference-SelectMany construction helper (do not invent one — reuse what the sibling reference test in this file already builds; if it uses a `TestQuery()`-style helper plus `mongoQ.AddLookup(...)`, follow that exactly). `List<T>.FindIndex` requires `System.Collections.Generic`; the returned list is `IReadOnlyList<MongoPipelineStage>` so cast to `List<MongoPipelineStage>` or use LINQ `Select`/`ToList().FindIndex`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests.Reference_unwind_with_filter"`
Expected: FAIL — no `MongoMatchStage` is emitted for `unwind.Filter`.

- [ ] **Step 3: Emit the filter `$match` in the lowerer**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, inside `if (select.UnwindSource is { } unwind)` (line ~122), after the owned `MongoUnwindFieldStage` add (line ~127) and before the `if (unwind.WholeElement)` block (line ~129), insert:

```csharp
            // Inner-element-only user filter (o.Refs.Where(pred)): a $match on the unwound element, emitted
            // after the $unwind (owned: just above; reference: already emitted by AppendLookupStages) and
            // before the $replaceRoot (WholeElement) / $project (projected). Already scope-prefixed by the
            // binder (e.g. "_lookup_Refs.Total"). EF-347 filtered-inner slice — reference only for now.
            if (unwind.Filter is { } filter)
                stages.Add(new MongoMatchStage(filter));
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS (new test + all existing lowerer tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-347: lowerer emits filtered-inner \$match after reference \$unwind"
```

- [ ] **Step 6: STOP for review**

---

## Task 5: End-to-end functional tests + `AGENTS.md`

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: the full mechanism (Tasks 2–4) end-to-end.
- Produces: the observable behavior — filtered projected + bare-entity reference SelectMany go native; correlated-beyond-FK / computed filters hard-fail every mode; filtered `Count` still falls back.

- [ ] **Step 1: Flip the existing hard-fail functional test to native success**

In `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`, replace `Reference_form_filtered_inner_hard_fails_in_every_mode` (line ~1114) with:

```csharp
    [Fact]
    public void Reference_form_filtered_inner_projected_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_projected_goes_native), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal; the "Widget" row is excluded.
        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, x => x.Tag == "Widget");
    }
```

- [ ] **Step 2: Add the remaining functional tests**

Add, after the test above:

```csharp
    [Fact]
    public void Reference_form_filtered_inner_bare_entity_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_bare_entity_goes_native), out _, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"))
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

        var expected = items.Where(r => r.Tag != "Widget").Select(r => r.Tag).OrderBy(t => t).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Widget", result);
    }

    [Fact]
    public void Reference_form_filtered_inner_stacked_where_ands_together_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_stacked_where_ands_together_goes_native), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget").Where(r => r.Tag != "Gadget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget" && r.Tag != "Gadget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_filtered_inner_excluding_all_children_contributes_no_rows()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_excluding_all_children_contributes_no_rows), out _, out _);

        // No RefItem has Tag "nonexistent" → every principal contributes zero rows.
        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == "nonexistent"), (o, r) => new { o.Name, r.Tag })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Reference_form_filtered_inner_emits_match_after_unwind_before_project()
    {
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_emits_match_after_unwind_before_project),
            out _, out _, out var spyLogger);

        _ = db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag }).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$lookup", message);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("_lookup_Refs.Tag", message); // filter is scope-prefixed
    }

    [Fact]
    public void Reference_form_filtered_inner_composes_with_parametrized_outer_predicate()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_composes_with_parametrized_outer_predicate), out var owners, out var items);

        var ownerName = "Alice";
        var result = db.Owners
            .Where(o => o.Name == ownerName)
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Tag).ToList();

        var expected = owners.Where(o => o.Name == ownerName)
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Tag).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_correlated_beyond_fk_inner_hard_fails_in_every_mode()
    {
        // A filter referencing the OUTER entity beyond the FK correlation (r.Tag != o.Name) is
        // correlated-beyond-FK — out of scope. The inner-scope translator rejects the o.Name member access,
        // TryBindReferenceNavUnwind returns false, TranslateSelectMany returns null → hard-fails in every mode
        // (reference SelectMany has no driver-LINQ oracle).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_beyond_fk_inner_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag != o.Name), (o, r) => new { o.Name, r.Tag }).ToList());
        }
    }

    [Fact]
    public void Reference_form_computed_filter_operator_hard_fails_in_every_mode()
    {
        // A filter using an operator the native translator does not support (string.ToUpper) declines: the
        // inner-scope translator rejects it → bind returns false → hard-fails in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_computed_filter_operator_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag.ToUpper() == "WIDGET"), (o, r) => new { o.Name, r.Tag }).ToList());
        }
    }
```

Note on `Reference_form_computed_filter_operator_hard_fails_in_every_mode`: if the spike/Task 3 review shows `string.ToUpper()` is in fact translatable natively (unlikely — the computed long tail is out of native scope per `AGENTS.md`), swap it for another genuinely-unsupported inner operator confirmed during Task 3 (e.g. a `Math.*` call or a method the translator's acceptance set excludes). Verify by running the test.

- [ ] **Step 3: Add a filtered-`Count`-still-falls-back guard test**

This confirms the shared matcher's contract is preserved — a filtered `Count` (not a SelectMany) must still fall back, not go native. Add:

```csharp
    [Fact]
    public void Filtered_reference_collection_count_still_falls_back()
    {
        // NOT a SelectMany — a projected filtered Count (c.Refs.Where(pred).Count()). NativeCorrelationMatcher
        // still rejects the extra conjunct for the Count binder, so this stays on fallback exactly as before
        // this slice. Under Native it must run (fall back), not throw.
        using var db = CreateRefContext(MongoQueryMode.Native,
            nameof(Filtered_reference_collection_count_still_falls_back), out var owners, out _);

        var result = db.Owners
            .Select(o => new { o.Name, N = o.Refs.Count(r => r.Tag != "Widget") })
            .AsEnumerable().OrderBy(x => x.Name).ToList();

        Assert.Equal(owners.Length, result.Count); // ran (fell back), did not throw
    }
```

Note: confirm this projected-filtered-`Count` shape is one EF surfaces to the provider (if EF pre-folds it differently, adjust the assertion to whatever the fallback correctly returns — the point is *it does not throw* under `Native`). Verify by running.

- [ ] **Step 4: Run the functional tests**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"
```
Expected: PASS (all new + existing NativeSelectManyTests).

- [ ] **Step 5: Update `AGENTS.md`**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, update the deferred list in the reference-`SelectMany` slice-5 note (which currently says "**Still deferred/hard-fail:** a **filtered or correlated-beyond-FK** inner …") to record that an **inner-element-only filtered** reference `SelectMany` now goes native (both projected and bare-entity), via a post-`$unwind` `$match` — while **correlated-beyond-FK**, computed-filter, computed-leaf, and nested reference `SelectMany` stay deferred; and note that the shared `NativeCorrelationMatcher` contract is unchanged (filtered `Count` still falls back). Add a concise as-built paragraph mirroring the existing slice notes' style: mechanism (`TryBindReferenceNavUnwind` peel/split + `MongoFieldPrefixRewriter` + `MongoUnwindSource.Filter` + lowerer `$match`), the no-oracle hard-fail for out-of-scope filters, and the multi-version (no `#if`) status.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs \
  src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: filtered-inner reference SelectMany functional tests + AGENTS.md"
```

- [ ] **Step 7: STOP for review**

---

## Task 6: Full 3-version verification + finalize

**Files:** none (verification + handoff).

- [ ] **Step 1: Full 3-version `/test-all`**

Invoke the `/test-all` skill (controller runs it in the foreground, per the recorded process lesson). Expected: GREEN, 0 failures, all three assemblies each for EF8/EF9/EF10. Record the pass/skip counts (baseline is `0f53f0c`: EF8 7268/67, EF9 7629/68, EF10 7226/71 — expect a small positive delta from the new tests, zero regressions).

- [ ] **Step 2: NativeOnly spec sweep**

Run the spec suite with `MONGODB_EF_NATIVE_ONLY=1` and diff the pass set against `0f53f0c`. Expected: no regressions; note any Northwind filtered-SelectMany shape that flips to native.

- [ ] **Step 3: Whole-branch review**

Request an opus whole-branch review (`0f53f0c..HEAD`), with the recurring silent-wrong-data / operator-after-terminal hunt. Fold any blocking findings, re-verify green.

- [ ] **Step 4: Squash + handoff**

Back up the pre-squash tip (`git branch -f EF-347-selectmany-ref-filtered-inner-presquash HEAD`), squash the slice to one commit above `0f53f0c` (`git reset --soft 0f53f0c && git commit`), verify the tree is byte-identical to the pre-squash tip, re-run the 3-version `/test-all` on the squashed tip, then give the user the plain fast-forward push command (`git push origin <newtip>:NativeQueryOngoing`).

- [ ] **Step 5: STOP — report the pushable tip to the user.**

---

## Self-Review

**Spec coverage:**
- Reference nav, inner-element-only filter, both result shapes → Tasks 3 (bind), 4 (lower), 5 (functional projected + bare). ✓
- Stacked filters (AND) → Task 3 unit + Task 5 functional. ✓
- Correlated-beyond-FK declines → Task 3 unit + Task 5 functional. ✓
- Computed/unsupported filter declines → Task 5 functional. ✓
- Shared matcher contract preserved (filtered `Count` falls back) → Task 5 functional. ✓
- No-oracle hard-fail for out-of-scope → Task 5 functional (`ThrowsAny` every mode). ✓
- Nested vs folded arriving shape → Task 1 spike; Task 3 handles both. ✓
- Multi-version / no `#if` → Task 1 spike checks; Task 6 `/test-all`. ✓
- Insertion point (`$match` after `$unwind`, before `$replaceRoot`/`$project`) → Task 4 unit + Task 5 MQL assertion. ✓

**Placeholder scan:** The only intentionally-parameterized spots are Task 2 Step 1's `Field(...)` helper and Task 4 Step 1's `MongoQueryExpression` construction, both explicitly instructed to reuse the sibling tests' established pattern (real code exists there; do not invent). No "TBD"/"handle edge cases"/"similar to Task N".

**Type consistency:** `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string) → MongoExpression`, `MongoUnwindSource.Filter` (`MongoExpression?`), `TrySplitCorrelation(Expression, IEntityType, ParameterExpression, IEntityType, out INavigation, out Expression?) → bool` — used consistently across Tasks 2–4. `MongoMatchStage(MongoExpression)`, `MongoBinaryExpression(MongoBinaryOperator, MongoExpression, MongoExpression)`, `NativeCorrelationMatcher.TryMatchCorrelatedCollection(Expression, IEntityType, ParameterExpression, IEntityType, bool, out INavigation)` — match the actual source signatures.
