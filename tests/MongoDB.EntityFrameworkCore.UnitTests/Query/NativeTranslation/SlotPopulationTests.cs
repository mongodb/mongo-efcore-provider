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
using MongoDB.EntityFrameworkCore.Query.Expressions;
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
    private static MongoQueryExpression TranslateToMongoQuery<T>(
        Func<IQueryable<T>, IQueryable> buildQuery) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();

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

    [Fact]
    public void Mixed_whole_entity_and_arithmetic_leaves_do_not_populate_projection()
    {
        // A whole-entity leaf (`c`) alongside an arithmetic leaf: the whole-entity leaf is not natively
        // representable, so the binder rejects the WHOLE projection → Route stays Fallback and Projection
        // stays empty. This is the precondition the Route == NativeRoute.Projection guard on the new
        // BinaryExpression case in MongoProjectionBindingExpressionVisitor relies on: because Route is
        // Fallback here, that case does NOT fire and the arithmetic leaf is never registered as a native
        // projection leaf that the mixed shaper cannot read. (The end-to-end value-correctness of this mixed
        // shape is out of scope for this slice and carried as a follow-up; Task 4 functional covers it.)
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { c, Total = c.Age * c.Age }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
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
}
