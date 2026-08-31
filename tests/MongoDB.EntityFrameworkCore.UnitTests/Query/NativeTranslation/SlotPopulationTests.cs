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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests that <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/> populates the native-query
/// slots on <see cref="MongoQueryExpression"/> (EF-323 Task 6: QMTEV slot population).
/// </summary>
public class SlotPopulationTests
{
    // ── Entity model used across all tests ───────────────────────────────────────

    private class Customer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public string Name { get; set; } = "";
    }

    // Used only by Mixed_owned_reference_entity_and_arithmetic_leaves_do_not_populate_projection: an
    // OWNED-reference entity leaf is the "entity leaf that still declines" control for EF-412's root-entity arm.
    private class CustomerWithOwnedAddress
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public OwnedAddress Address { get; set; } = null!;
    }

    private class OwnedAddress
    {
        public string City { get; set; } = "";
    }

    // Used only by Non_embedded_owned_reference_entity_leaf_declines_to_fallback (final-review Finding 1): a
    // non-embedded owned navigation — one with its own Mongo:CollectionName annotation, so it is its own
    // document root stored in a SEPARATE collection rather than a nested sub-document of its owner — must NOT
    // be admitted by the EF-441 owned-nav-entity-leaf gate. IsOwned() is true for BOTH this shape and the
    // embedded CustomerWithOwnedAddress/OwnedAddress shape above; only IsEmbedded() tells them apart.
    private class ProbeCustomer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public ProbeAddress Address { get; set; } = null!;
    }

    private class ProbeAddress
    {
        public ObjectId Id { get; set; }
        public string City { get; set; } = "";
    }

    // ── Test harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives a LINQ query expression through the real QMTEV pipeline and returns the resulting
    /// <see cref="MongoQueryExpression"/> so tests can inspect its native slots.
    ///
    /// Strategy: obtain a real <see cref="IQueryable{T}"/> from the DbSet so the expression tree is
    /// rooted in a proper <see cref="EntityQueryRootExpression"/>, apply operators to get a method-call
    /// chain, then feed that chain through the QMTEV directly — bypassing the preprocessing step
    /// (which is not needed for these simple flat-entity tests).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="buildQuery">
    /// A function that applies LINQ operators to the DbSet's <see cref="IQueryable{T}"/> — e.g.
    /// <c>q => q.Where(c => c.Age > 21)</c>. The result's <c>.Expression</c> is fed into the visitor.
    /// </param>
    /// <param name="modelBuilderAction">
    /// Optional model customization, for the few tests whose shape needs more than a flat entity (e.g. an
    /// owned navigation). Threaded straight through to <see cref="SingleEntityDbContext.Create{T}"/>.
    /// </param>
    private static MongoQueryExpression TranslateToMongoQuery<T>(
        Func<IQueryable<T>, IQueryable> buildQuery,
        Action<ModelBuilder>? modelBuilderAction = null) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>(modelBuilderAction);

        // Obtain the factory and compilation context from EF's DI container.
        var visitorFactory = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>();
        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        // Create the QMTEV.
        var visitor = visitorFactory.Create(compilationContext);

        // Build the expression tree: the DbSet<T> implements IQueryable<T>, so its .Expression
        // is a ConstantExpression(DbSet<T>). We need an EntityQueryRootExpression at the bottom.
        // Use the entity type from the compiled model to build the root directly.
        var entityType = db.Model.FindEntityType(typeof(T))!;
        var rootExpression = new EntityQueryRootExpression(entityType);

        // Wrap it in a minimal stub IQueryable so we can apply LINQ operators.
        // The stub's .Expression property returns the EntityQueryRootExpression.
        // This mimics the preprocessed form the QMTEV normally receives.
        var rootQueryable = new RootExpressionQueryable<T>(rootExpression);
        var query = buildQuery(rootQueryable);

        // Visit the top-level expression tree.
        var result = visitor.Visit(query.Expression);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    /// <summary>
    /// Like <see cref="TranslateToMongoQuery{T}"/>, but builds a <c>Union</c> of two independently-constructed
    /// operand queries over the SAME entity type and root, then feeds the combined tree through the QMTEV. Used
    /// by the EF-441 set-op-gate regression test — this harness bypasses EF's nav-expansion/preprocessing
    /// phase entirely (like <see cref="TranslateToMongoQuery{T}"/> does), so it does NOT exercise the
    /// nav-expansion-level operand-sharing quirk a full functional-test Union hits for a wrapped projected
    /// operand (see the functional test file's own remarks); it exists to isolate and pin the ONE fact this
    /// task owns — the emit-side gate (<c>HasArrayProjectionLeaf</c>/<c>IsPlainProjectedSelect</c>) — from that
    /// separate, deeper concern.
    /// </summary>
    private static MongoQueryExpression TranslateUnionToMongoQuery<T, TResult>(
        Func<IQueryable<T>, IQueryable<TResult>> buildLeft,
        Func<IQueryable<T>, IQueryable<TResult>> buildRight,
        Action<ModelBuilder>? modelBuilderAction = null) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>(modelBuilderAction);

        var visitorFactory = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>();
        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);
        var visitor = visitorFactory.Create(compilationContext);

        var entityType = db.Model.FindEntityType(typeof(T))!;
        var left = buildLeft(new RootExpressionQueryable<T>(new EntityQueryRootExpression(entityType)));
        var right = buildRight(new RootExpressionQueryable<T>(new EntityQueryRootExpression(entityType)));
        var unioned = System.Linq.Queryable.Union(left, right);

        var result = visitor.Visit(unioned.Expression);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    /// <summary>
    /// A minimal <see cref="IQueryable{T}"/> and <see cref="IOrderedQueryable{T}"/> stub that wraps
    /// a root expression node. When LINQ operators such as <c>Where</c>, <c>OrderBy</c>, <c>Take</c>,
    /// <c>Select</c> are applied to this queryable via <see cref="Queryable"/>-extension methods, the
    /// C# compiler constructs <see cref="MethodCallExpression"/> trees rooted in <see cref="Expression"/>.
    /// Those trees can then be fed directly to the QMTEV.
    /// Implements both <see cref="IOrderedQueryable{T}"/> and <see cref="IQueryable{T}"/> so that both
    /// <c>OrderBy</c> (which requires <c>IOrderedQueryable</c> for <c>ThenBy</c>) and plain operators work.
    /// </summary>
    private sealed class RootExpressionQueryable<T> : IOrderedQueryable<T>
    {
        private readonly Expression _expression;

        public RootExpressionQueryable(Expression expression)
        {
            _expression = expression;
        }

        public Type ElementType => typeof(T);
        public Expression Expression => _expression;
        public IQueryProvider Provider => new ThrowingProvider();
        public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Test stub only.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException("Test stub only.");

        /// <summary>
        /// A provider that throws on any attempt to execute — this stub is only used to build expression trees.
        /// </summary>
        private sealed class ThrowingProvider : IQueryProvider
        {
            public IQueryable CreateQuery(Expression expression) => new RootExpressionQueryable<T>(expression);
            public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
                => new RootExpressionQueryable<TElement>(expression);
            public object? Execute(Expression expression) => throw new NotSupportedException();
            public TResult Execute<TResult>(Expression expression) => throw new NotSupportedException();
        }
    }

    // ── Test 1: Where → Predicate slot populated ─────────────────────────────────

    [Fact]
    public void Where_populates_the_predicate_slot()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Where(c => c.Age > 21));

        Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.Equal(NativeRoute.WholeEntity, mongoQ.Select.Route);
        Assert.NotNull(mongoQ.CapturedExpression);
    }

    // ── Test 2: OrderBy + ThenByDescending → Orderings slot populated ─────────────

    [Fact]
    public void OrderBy_then_ThenBy_preserves_order()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(
            q => q.OrderBy(c => c.Age).ThenByDescending(c => c.Name));

        var sort = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.Equal(2, sort.Orderings.Count);
        Assert.True(sort.Orderings[0].Ascending);
        Assert.False(sort.Orderings[1].Ascending);
    }

    // ── Test 3: Where after Take → non-canonical, now natively representable (EF-347 Task 2) ────
    // The lowerer emits PipelineOps verbatim in arrival order, so a $match recorded AFTER a $limit
    // is emitted AFTER it too — correct by MongoDB's sequential pipeline semantics. No more guard.

    [Fact]
    public void Where_after_Take_is_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Take(10).Where(c => c.Age > 21));

        Assert.Collection(mongoQ.Select.PipelineOps,
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoMatchOp>(o));
        Assert.False(mongoQ.Select.Route == NativeRoute.Fallback);
        Assert.NotNull(mongoQ.CapturedExpression);
    }

    // ── EF-347 Task 2: non-canonical Skip/Take families now go native ────────────────────────────
    // Correctness is by MongoDB's sequential pipeline semantics — PipelineOps are emitted verbatim
    // in arrival order, so these are no longer forced to Fallback. See QueryModeGateTests for the
    // end-to-end (NativeOnly) proof that these shapes actually execute natively and return correct
    // rows; these unit tests assert only the recorded op ordering / Route.

    [Fact]
    public void Take_before_Skip_is_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Take(10).Skip(5));

        Assert.Collection(mongoQ.Select.PipelineOps,
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoSkipOp>(o));
        Assert.False(mongoQ.Select.Route == NativeRoute.Fallback);
    }

    [Fact]
    public void Where_after_Skip_is_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Skip(1).Where(c => c.Age > 21));

        Assert.Collection(mongoQ.Select.PipelineOps,
            o => Assert.IsType<MongoSkipOp>(o),
            o => Assert.IsType<MongoMatchOp>(o));
        Assert.False(mongoQ.Select.Route == NativeRoute.Fallback);
    }

    [Fact]
    public void Repeated_paging_is_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Skip(2).Take(3).Skip(1));

        Assert.Collection(mongoQ.Select.PipelineOps,
            o => Assert.IsType<MongoSkipOp>(o),
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoSkipOp>(o));
        Assert.False(mongoQ.Select.Route == NativeRoute.Fallback);
    }

    // ── Test 4: a Select the projection binder DECLINES → Route = Fallback ───────

    // FLIPPED by EF-322 step 3a. This test used to use a BARE scalar body (`c => c.Name`) as its example of a
    // non-representable projection; that shape is now native (see Bare_scalar_projection_is_native above), so the
    // example has to be one the binder still declines or the test would be asserting the opposite of the truth.
    // A WIDENING cast (`(long)c.Age`) was this test's example through EF-410; that shape is now ALSO native (a
    // widening Convert is admitted as a bare MongoFieldExpression — see NativeProjectionBinder's tier-2 gate),
    // so the example moved again, to a NARROWING cast with no admissible MQL conversion operator ($toShort does
    // not exist — see MongoConvertExpression.ToOperatorFor). That is the still-declining computed long tail,
    // unrelated to the bare/wrapped boundary — so what this test pins is unchanged: a declined projection drives
    // Route to Fallback.
    [Fact]
    public void A_declined_projecting_Select_is_not_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Select(c => (short)c.Age));

        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
        Assert.Empty(mongoQ.Select.Projection);
    }

    // ── Test 5: Native projection slot population (EF-331 Task 4) ────────────────

    [Fact]
    public void Anonymous_member_projection_populates_projection_slot()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { c.Name, c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        Assert.Equal(2, mongoQuery.Select.Projection.Count);
        Assert.Equal("Name", mongoQuery.Select.Projection[0].Alias);
        Assert.Equal("Age", mongoQuery.Select.Projection[1].Alias);
        Assert.IsType<MongoFieldExpression>(mongoQuery.Select.Projection[0].Expression);
    }

    // ── EF-347 Task 3: arithmetic computed leaves are natively representable ─────
    // Before this, ANY computed member projection (arithmetic included) fell back to driver-LINQ. Now a
    // top-level arithmetic (+ - * / %) binary leaf populates Select.Projection as a MongoBinaryExpression,
    // provided every operand is a numeric type with no integer-division divergence and no value-converted
    // field (see MongoExpressionTranslator.TryTranslateValue). This supersedes the old
    // Computed_member_projection_is_not_native test, which asserted `c.Age * 2` fell back — that assertion
    // is now the opposite of correct behavior.

    [Fact]
    public void Arithmetic_member_projection_is_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { Doubled = c.Age * c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var p = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("Doubled", p.Alias);
        Assert.IsType<MongoBinaryExpression>(p.Expression);
    }

    [Fact]
    public void String_concat_leaf_does_not_populate_projection()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = c.Name + "!" }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    [Fact]
    public void Integer_division_leaf_populates_projection_as_IntegerDivide()
    {
        // Was Integer_division_leaf_does_not_populate_projection. EF-434 replaced TryTranslateValue's blanket
        // integer-division decline with a truncating translation, so this leaf is native now; the operator, not
        // just the route, is asserted, because a plain Divide here would silently reintroduce the double result
        // that failed to deserialize into an int member.
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = c.Age / c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var div = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.IntegerDivide, div.Operator);
    }

    [Fact]
    public void Bare_constant_leaf_does_not_populate_projection() // projection-safety: $project would misread {X:5}
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = 5 }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    [Fact]
    public void Mixed_field_and_arithmetic_leaves_both_populate()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { c.Name, Total = c.Age * c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        Assert.Equal(2, mongoQuery.Select.Projection.Count);
        Assert.Equal("Name", mongoQuery.Select.Projection[0].Alias);
        Assert.IsType<MongoFieldExpression>(mongoQuery.Select.Projection[0].Expression);
        Assert.Equal("Total", mongoQuery.Select.Projection[1].Alias);
        Assert.IsType<MongoBinaryExpression>(mongoQuery.Select.Projection[1].Expression);
    }

    // INVERTED by EF-412, which is the whole point of that slice. This test previously asserted
    // Route == Fallback / Projection empty for `new { c, Total = ... }`, on the premise that a whole-entity
    // leaf is never natively representable. That premise is now false for the specific case of the WHOLE ROOT
    // ENTITY: NativeProjectionBinder.TryTranslateLeaf admits the selector's own parameter as a
    // MongoElementRefExpression("$ROOT") when it appears inside a WRAPPED (new{}/member-init) body, so the
    // projection populates and emits {"c": "$$ROOT", "Total": {...}}. The removed assertion's INTENT — that
    // SOME entity leaves still decline — is not lost: it moved to the sibling test below, which pins an
    // entity leaf that is genuinely still out of scope.
    [Fact]
    public void Mixed_whole_root_entity_and_arithmetic_leaves_populate_projection()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { c, Total = c.Age * c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        Assert.Equal(2, mongoQuery.Select.Projection.Count);
        Assert.Equal("c", mongoQuery.Select.Projection[0].Alias);
        Assert.Equal("$ROOT", Assert.IsType<MongoElementRefExpression>(mongoQuery.Select.Projection[0].Expression).Path);
        Assert.Equal("Total", mongoQuery.Select.Projection[1].Alias);
        Assert.IsType<MongoBinaryExpression>(mongoQuery.Select.Projection[1].Expression);
    }

    // The SIBLING that carries the inverted test's original intent. As of EF-441, an OWNED single-reference
    // navigation entity leaf (`c.Address`, an embedded sub-document) DOES have its own native arm
    // (TryGetOwnedReferenceNavigationLeaf) and is, on its own, admitted — so this is no longer a case of
    // TryTranslateLeaf outright rejecting the leaf (that was true before EF-441, when no such arm existed).
    // What still declines the WHOLE projection is the sibling-readability sweep
    // (NativeProjectionBinder.IsWholeDocumentReadableLeaf, run over every OTHER leaf once an owned-array or
    // owned-nav-entity leaf is admitted): a computed leaf like `Total = c.Age * c.Age` has no document path of
    // its own, so it fails that sweep and the whole projection declines before anything is mutated — Route
    // stays Fallback and Projection stays empty, same observable result as before EF-441, for a different and
    // now more precise reason.
    //
    // This matters beyond bookkeeping: Route == Fallback here is the precondition that keeps the
    // Route == NativeRoute.Projection-gated arms in MongoProjectionBindingExpressionVisitor from firing for a
    // shape the native shaper cannot read, so the arithmetic sibling is never registered as a native leaf the
    // mixed shaper would then misread. If a future slice widens the sibling-readability sweep (or gives a
    // computed leaf its own document-path story) to admit this shape, this test must fail rather than the
    // widening landing silently.
    [Fact]
    public void Mixed_owned_reference_entity_and_arithmetic_leaves_do_not_populate_projection()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { c.Address, Total = c.Age * c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // The POSITIVE case (EF-441): an owned single-reference navigation entity leaf mixed with a plain FIELD
    // sibling (as opposed to the computed sibling above) is fully native — the field sibling is
    // whole-document-readable, so the sibling-readability sweep admits it instead of declining the projection.
    [Fact]
    public void Mixed_owned_reference_entity_and_field_leaves_populate_projection_natively()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        Assert.Equal(3, mongoQuery.Select.Projection.Count);
        Assert.Equal("Address", mongoQuery.Select.Projection[0].Alias);
        Assert.Equal(
            "Address", Assert.IsType<MongoElementRefExpression>(mongoQuery.Select.Projection[0].Expression).Path);
        Assert.Equal("Age", mongoQuery.Select.Projection[1].Alias);
        // The owner-key retention (EF-441, mirroring the owned-array leaf): a $project that carried only the
        // requested aliases would have no _id, and the owned Address element's shadow-key read resolves the
        // owner's _id off the document root.
        Assert.Equal("_id", mongoQuery.Select.Projection[2].Alias);
        Assert.True(mongoQuery.Select.HasArrayProjectionLeaf);
    }

    // The RENAMED-alias negative control: `new { Addr = c.Address, c.Age }` must decline outright, because the
    // late-fallback leg's correctness depends on the emitted alias naming a real element the driver-LINQ bridge
    // also renders under that same name (see TryTranslateLeaf's alias-must-equal-document-path conjunct).
    [Fact]
    public void Renamed_owned_reference_entity_leaf_declines()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { Addr = c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // Final-review Finding 1: before the fix, TryGetOwnedReferenceNavigationLeaf gated on
    // `!nav.TargetEntityType.IsOwned()`, which is FALSE for both an embedded owned type (a nested sub-document
    // of the same document — what this feature handles) and a non-embedded owned type (its own document root,
    // in its own collection, per a Mongo:CollectionName annotation) — so the gate wrongly admitted the
    // non-embedded shape too, and would have emitted `"Address": "$Address"` in the $project for data that
    // isn't in the document at all. The fix keys on `!nav.IsEmbedded()` instead, matching every other owned-nav
    // gate in this codebase (MongoExpressionTranslator.Members.cs, MongoSelectLowerer.cs). This must decline to
    // Fallback; before the fix it incorrectly showed Route == Projection.
    [Fact]
    public void Non_embedded_owned_reference_entity_leaf_declines_to_fallback()
    {
        var mongoQuery = TranslateToMongoQuery<ProbeCustomer>(
            q => q.Select(c => new { c.Address, c.Age }),
            mb => mb.Entity<ProbeCustomer>().OwnsOne(c => c.Address, a =>
            {
                a.HasKey(x => x.Id);
                a.Property(x => x.Id).HasElementName("_id");
                a.HasAnnotation("Mongo:CollectionName", "addresses");
            }));

        var navigation = mongoQuery.CollectionExpression.EntityType.FindNavigation(nameof(ProbeCustomer.Address))!;
        Assert.False(navigation.IsEmbedded());
        Assert.True(navigation.TargetEntityType.IsOwned());
        Assert.True(navigation.TargetEntityType.IsDocumentRoot());

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // Final-review Finding 3: the re-entrancy guard at the top of NativeProjectionBinder.TryPopulateNativeProjection
    // (a wrapped body reached with Projection already populated declines rather than re-running every leaf arm)
    // has zero coverage in the existing suite — every shape that reaches it in practice also hits a separate,
    // pre-existing InvalidCastException bug first (see Query/AGENTS.md's EF-441 paragraph), so the guard's own
    // return value never gets a chance to matter end-to-end. This test reaches the guard DIRECTLY (bypassing
    // both nav-expansion and that unrelated bug) by calling NativeProjectionBinder.TryPopulateNativeProjection a
    // second time on a MongoQueryExpression whose Projection is already populated — exactly the precondition the
    // guard's own `Projection.Count > 0` check tests. A mutation removing the guard would re-run every leaf arm
    // and duplicate the Projection list (or throw from AddProjectionAliasOverride's write-once Dictionary.Add).
    [Fact]
    public void Reentrant_wrapped_projection_call_declines_without_duplicating_projection()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var countBeforeReentry = mongoQuery.Select.Projection.Count;
        Assert.True(countBeforeReentry > 0);

        Expression<Func<CustomerWithOwnedAddress, object>> reentrantSelector = c => new { c.Address, c.Age };

        var result = NativeProjectionBinder.TryPopulateNativeProjection(mongoQuery, reentrantSelector);

        Assert.False(result);
        Assert.Equal(countBeforeReentry, mongoQuery.Select.Projection.Count);
    }

    [Fact]
    // FLIPPED by EF-322 step 3a (the bare-projection boundary), which is the whole point of that slice: a bare
    // selector body now populates the native Projection with the leaf's own document path as the alias, so this
    // asserts the opposite of what it used to. The alias, its tier, and the every-leaf-kind decline set are
    // covered by NativeProjectionBinderBareBodyTests; what belongs HERE is only that slot population reaches
    // Route == Projection for the shape this file is about.
    public void Bare_scalar_projection_is_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => c.Name));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("Name", projection.Alias);
        Assert.True(mongoQuery.Select.IsBareProjection);
    }

    [Fact]
    // A WIDENING cast member (`(long)c.Age`) was this test's example through EF-410; that shape is now native
    // (see NativeCastTests.Widening_cast_projection_leaf_now_goes_native), so this uses a NARROWING cast to a
    // target with no admissible MQL conversion operator ($toShort does not exist) instead, which still declines.
    public void Cast_member_projection_is_not_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { Position = (short)c.Age }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    [Fact]
    public void Case_insensitively_colliding_projection_aliases_are_not_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { Name = c.Name, name = c.Age }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // ── GroupBy wiring (EF-344 Task 5) ────────────────────────────────────────────
    // These prove the QMTEV no longer HARD-THROWS on GroupBy(k).Select(agg) (it previously produced
    // NotTranslatedExpression and failed translation): a supported group routes native (Route = GroupBy);
    // any unsupported shape marks the query non-native (Route = Fallback) so it falls back to driver-LINQ.

    [Fact]
    public void GroupBy_key_with_aggregate_Select_routes_native_GroupBy()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Age).Select(g => new { g.Key, Count = g.Count() }));

        Assert.Equal(NativeRoute.GroupBy, mongoQuery.Select.Route);
        Assert.NotNull(mongoQuery.Select.Grouping);
        Assert.NotNull(mongoQuery.CapturedExpression);
    }

    [Fact]
    public void GroupBy_key_with_sum_aggregate_Select_routes_native_GroupBy()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Name).Select(g => new { g.Key, Total = g.Sum(c => c.Age) }));

        Assert.Equal(NativeRoute.GroupBy, mongoQuery.Select.Route);
        Assert.NotNull(mongoQuery.Select.Grouping);
    }

    [Fact]
    public void GroupBy_with_computed_key_falls_back_without_throwing()
    {
        // A computed key (c.Age + 1) is not natively representable; translation must complete (no hard-throw)
        // and mark the query for driver-LINQ fallback.
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Age + 1).Select(g => new { g.Key, Count = g.Count() }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.NotNull(mongoQuery.CapturedExpression);
    }

    [Fact]
    public void GroupBy_without_terminal_Select_falls_back_without_throwing()
    {
        // A bare GroupBy(key) (no aggregate Select) binds the key but never finalizes the grouping projection,
        // so no accumulator is produced; the query must still translate and fall back rather than hard-throw.
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.GroupBy(c => c.Age));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.NotNull(mongoQuery.CapturedExpression);
    }

    // The set-op-gate regression (EF-441 Task 1's "also decide and implement" item): a nav-entity-leaf
    // projection's emitted _id would leak into a set operation's whole-document comparison/dedup key exactly
    // like the owned-ARRAY leaf's does, so NativeProjectionBinder sets the SAME HasArrayProjectionLeaf flag
    // for this leaf kind (rather than inventing a parallel one) — MongoQueryableMethodTranslatingExpressionVisitor
    // .IsPlainProjectedSelect already gates set-op operand admission on that flag, so no change was needed
    // there. This pins that the flag really does end up true on the operand's select once combined into a
    // Union — i.e. the gate has real data to decline on, not that the whole query throws (a full round-trip
    // Union of two wrapped nav-entity-leaf projections hits a SEPARATE, deeper concern in the nav-expansion/
    // shaper-binding layer once real preprocessing is involved — see the functional test file's own coverage
    // and remarks; this unit-level test isolates the ONE fact this task owns).
    [Fact]
    public void Owned_reference_entity_leaf_projection_sets_HasArrayProjectionLeaf_for_the_set_op_gate()
    {
        var mongoQuery = TranslateUnionToMongoQuery(
            (IQueryable<CustomerWithOwnedAddress> q) => q.Select(c => new { c.Address, c.Age }),
            (IQueryable<CustomerWithOwnedAddress> q) => q.Select(c => new { c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.True(mongoQuery.Select.HasArrayProjectionLeaf);
        // The SAME flag is what IsPlainProjectedSelect gates on, so the Union must NOT have gone native as a
        // projected-operand set op (SetOperation stays unset) — it must instead have marked non-natively
        // representable, the graceful-fallback disposition Union/Concat get (see TryTranslateSetOperation).
        Assert.Null(mongoQuery.Select.SetOperation);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  EF-447: a CONSTRUCTED (non-navigation) sub-entity leaf — `new { Copy = new CustomerDto { Id =
    //  c.Id, ... } }` — mixed with a computed sibling in a projection.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    // A plain, unmapped DTO type reconstructed from root-level scalar fields — NOT a navigation, and not
    // itself part of the model. Distinguishes this leaf from EF-441's owned-nav-entity leaf, which aliases an
    // ALREADY-STORED owned sub-document.
    private class CustomerDto
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Fact]
    public void Document_construction_leaf_populates_projection_natively()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.Select(c => new { Copy = new CustomerDto { Id = c.Id, Name = c.Name, Age = c.Age } }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var p = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("Copy", p.Alias);
        var construction = Assert.IsType<MongoDocumentConstructionExpression>(p.Expression);
        Assert.Equal(3, construction.Members.Count);
        Assert.Equal("Id", construction.Members[0].MemberName);
        Assert.Equal("_id", Assert.IsType<MongoFieldExpression>(construction.Members[0].Value).ElementName);
        Assert.Equal("Name", construction.Members[1].MemberName);
        Assert.Equal("Age", construction.Members[2].MemberName);
        // This leaf carries NO owner-key hazard (every member is a plain root-relative field, readable at its
        // own natural path on a whole/un-projected document too — see the mixed-visitor read side), so unlike
        // the owned-array/owned-nav-entity leaves it must NOT set HasArrayProjectionLeaf.
        Assert.False(mongoQuery.Select.HasArrayProjectionLeaf);
    }

    // The POSITIVE case this ticket is actually about: a constructed sub-entity leaf mixed with a COMPUTED
    // sibling goes native, unlike EF-441's owned-nav-entity leaf (which forces the sibling-readability sweep
    // and therefore declines a computed sibling). No sweep applies here because this leaf's own members are
    // independently readable off a whole document by their own natural paths, so a computed sibling's lack of
    // a document path is not a hazard for THIS leaf the way it is for the array/owned-nav-entity leaves.
    [Fact]
    public void Document_construction_leaf_mixed_with_computed_sibling_populates_projection_natively()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.Select(c => new
            {
                Copy = new CustomerDto { Id = c.Id, Name = c.Name, Age = c.Age },
                Total = c.Age * c.Age
            }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        Assert.Equal(2, mongoQuery.Select.Projection.Count);
        Assert.Equal("Copy", mongoQuery.Select.Projection[0].Alias);
        Assert.IsType<MongoDocumentConstructionExpression>(mongoQuery.Select.Projection[0].Expression);
        Assert.Equal("Total", mongoQuery.Select.Projection[1].Alias);
        Assert.IsType<MongoBinaryExpression>(mongoQuery.Select.Projection[1].Expression);
    }

    // A member value that is not a plain top-level scalar field (here, `c.Name.Length` — a MemberExpression
    // whose OWN receiver is `c.Name`, not the selector parameter `c` itself) declines the WHOLE leaf, not just
    // that member — this is a strict, minimal widening, not a general nested-projection engine.
    [Fact]
    public void Document_construction_leaf_with_computed_member_declines_to_fallback()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.Select(c => new { Copy = new CustomerDto { Id = c.Id, Name = c.Name, Age = c.Name.Length } }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

}
