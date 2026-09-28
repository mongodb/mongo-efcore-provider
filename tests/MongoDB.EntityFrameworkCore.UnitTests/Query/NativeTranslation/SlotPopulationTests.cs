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
/// slots on <see cref="MongoQueryExpression"/>.
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

    // An owned-reference entity leaf: the "entity leaf that still declines" control for the root-entity arm.
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

    // A non-embedded owned navigation (own Mongo:CollectionName, stored in a separate collection) must not be
    // admitted by the owned-nav-entity-leaf gate. IsOwned() is true for both this and OwnedAddress; only
    // IsEmbedded() tells them apart.
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
    /// Drives a LINQ query through the real QMTEV and returns the resulting <see cref="MongoQueryExpression"/>.
    /// The chain is rooted in an <see cref="EntityQueryRootExpression"/> and fed to the QMTEV directly, skipping
    /// preprocessing (not needed for these flat-entity tests).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="buildQuery">
    /// Applies LINQ operators to the root <see cref="IQueryable{T}"/>, e.g. <c>q => q.Where(c => c.Age > 21)</c>.
    /// </param>
    /// <param name="modelBuilderAction">
    /// Optional model customization (e.g. an owned navigation), passed to <see
    /// cref="SingleEntityDbContext.Create{T}"/>.
    /// </param>
    private static MongoQueryExpression TranslateToMongoQuery<T>(
        Func<IQueryable<T>, IQueryable> buildQuery,
        Action<ModelBuilder>? modelBuilderAction = null) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>(modelBuilderAction);

        var visitorFactory = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>();
        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var visitor = visitorFactory.Create(compilationContext);

        // DbSet<T>.Expression is a ConstantExpression; build an EntityQueryRootExpression from the model instead.
        var entityType = db.Model.FindEntityType(typeof(T))!;
        var rootExpression = new EntityQueryRootExpression(entityType);

        // A stub IQueryable over the root lets LINQ operators build the preprocessed-shaped tree.
        var rootQueryable = new RootExpressionQueryable<T>(rootExpression);
        var query = buildQuery(rootQueryable);

        var result = visitor.Visit(query.Expression);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    /// <summary>
    /// Like <see cref="TranslateToMongoQuery{T}"/>, but for a <c>Union</c> of two operand queries over the same root.
    /// Skips nav-expansion, so it isolates the set-op emit-side gate
    /// (<c>HasArrayProjectionLeaf</c>/<c>IsPlainProjectedSelect</c>) from nav-expansion operand-sharing issues.
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
    /// Minimal <see cref="IOrderedQueryable{T}"/> over a root expression, so <see cref="Queryable"/> operators
    /// (including <c>ThenBy</c>) build <see cref="MethodCallExpression"/> trees for the QMTEV.
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
        /// Throws on execution; only used to build expression trees.
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

    // ── Where → Predicate slot populated ─────────────────────────────────────────

    [Fact]
    public void Where_populates_the_predicate_slot()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Where(c => c.Age > 21));

        Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.Equal(NativeRoute.WholeEntity, mongoQ.Select.Route);
        Assert.NotNull(mongoQ.CapturedExpression);
    }

    // ── OrderBy + ThenByDescending → Orderings slot populated ──────────────────────

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

    // ── Where after Take → non-canonical but native ──────────────────────────────
    // PipelineOps are emitted in arrival order, so a $match after a $limit stays after it.

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

    // ── Non-canonical Skip/Take families are native ───────────────────────────────
    // PipelineOps are emitted in arrival order. These assert op ordering / Route only; QueryModeGateTests has
    // the end-to-end NativeOnly proof.

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

    // ── A Select the projection binder declines → Route = Fallback ───────────────

    // Uses a narrowing cast with no MQL conversion operator ($toShort doesn't exist; see
    // MongoConvertExpression.ToOperatorFor), which the binder still declines.
    [Fact]
    public void A_declined_projecting_Select_is_not_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Select(c => (short)c.Age));

        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
        Assert.Empty(mongoQ.Select.Projection);
    }

    // ── Native projection slot population ─────────────────────────────────────────

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

    // ── Arithmetic computed leaves are native ─────────────────────────────────────
    // A top-level arithmetic leaf populates Select.Projection as a MongoBinaryExpression when every operand is
    // numeric with no integer-division divergence and no value-converted field (see TryTranslateValue).

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
    public void String_concat_leaf_populates_projection_as_MongoConcatExpression()
    {
        // String concatenation translates via MongoConcatExpression ($concat); see TranslateStringConcat.
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = c.Name + "!" }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var concat = Assert.IsType<MongoConcatExpression>(projection.Expression);
        Assert.Equal(2, concat.Operands.Count);
    }

    [Fact]
    public void Integer_division_leaf_populates_projection_as_IntegerDivide()
    {
        // Integer division is translated with truncation. Assert the operator, not just the route: a plain
        // Divide would produce a double that fails to deserialize into the int member.
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = c.Age / c.Age }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var div = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.IntegerDivide, div.Operator);
    }

    [Fact]
    public void Constant_leaf_now_populates_projection_via_the_literal_wrap() // MongoPipelineFactory.RenderProject $literal-wraps a bare constant/parameter value, so $project can no longer misread {X:5} as a flag
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { X = 5 }));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("X", projection.Alias);
        Assert.IsType<MongoConstantExpression>(projection.Expression);
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

    // The whole root entity inside a wrapped body is admitted as MongoElementRefExpression("$ROOT"), emitting
    // {"c": "$$ROOT", "Total": {...}}. The sibling test below pins an entity leaf that still declines.
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

    // An owned single-reference nav entity leaf (`c.Address`) is admitted on its own
    // (TryGetOwnedReferenceNavigationLeaf), but it triggers the sibling-readability sweep
    // (IsWholeDocumentReadableLeaf), and a computed sibling like `Total = c.Age * c.Age` has no document path,
    // so the whole projection declines before anything is mutated.
    //
    // Route == Fallback here keeps the Route == Projection arms in MongoProjectionBindingExpressionVisitor from
    // registering a leaf the mixed shaper would misread. If the sweep is widened to admit this, this test must
    // fail rather than the widening landing silently.
    [Fact]
    public void Mixed_owned_reference_entity_and_arithmetic_leaves_do_not_populate_projection()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { c.Address, Total = c.Age * c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // Positive case: an owned-nav entity leaf with a plain field sibling is native — the field is
    // whole-document-readable, so the sweep admits it.
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
        // Owner key retained (as for the owned-array leaf): the owned Address's shadow-key read resolves the
        // owner's _id off the document root.
        Assert.Equal("_id", mongoQuery.Select.Projection[2].Alias);
        Assert.True(mongoQuery.Select.HasArrayProjectionLeaf);
    }

    // Renamed-alias control: `new { Addr = c.Address, c.Age }` must decline, because the late-fallback leg needs
    // the alias to name a real element the driver-LINQ bridge renders under the same name (see TryTranslateLeaf).
    [Fact]
    public void Renamed_owned_reference_entity_leaf_declines()
    {
        var mongoQuery = TranslateToMongoQuery<CustomerWithOwnedAddress>(
            q => q.Select(c => new { Addr = c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    // A non-embedded owned type lives in its own collection, so admitting it would emit `"Address": "$Address"`
    // for data not in the document. The gate keys on !nav.IsEmbedded() (not IsOwned()), like other owned-nav
    // gates (MongoExpressionTranslator.Members.cs, MongoSelectLowerer.cs). Must decline to Fallback.
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

    // Re-entrancy guard in NativeProjectionBinder.TryPopulateNativeProjection: a second call with Projection
    // already populated must decline. Called directly because end-to-end shapes hit an unrelated
    // InvalidCastException first. Without the guard every leaf arm re-runs, duplicating Projection (or throwing
    // from AddProjectionAliasOverride's Dictionary.Add).
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
    // A bare selector body populates Projection with the leaf's document path as alias. Alias/tier details and
    // the decline set are in NativeProjectionBinderBareBodyTests; this pins only Route == Projection.
    public void Bare_scalar_projection_is_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => c.Name));

        Assert.Equal(NativeRoute.Projection, mongoQuery.Select.Route);
        var projection = Assert.Single(mongoQuery.Select.Projection);
        Assert.Equal("Name", projection.Alias);
        Assert.True(mongoQuery.Select.IsBareProjection);
    }

    [Fact]
    // A narrowing cast with no MQL conversion operator ($toShort doesn't exist) still declines; widening casts
    // are native (NativeCastTests).
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

    // ── GroupBy wiring ────────────────────────────────────────────────────────────
    // GroupBy(k).Select(agg) must never hard-throw: a supported group routes native (Route = GroupBy); an
    // unsupported shape marks the query non-native (Route = Fallback).

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
    public void GroupBy_key_with_computed_expression_routes_native_GroupBy()
    {
        // A computed key (c.Age + 1) binds via NativeGroupByBinder.TryBindGroupKey's TryTranslateValue path.
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Age + 1).Select(g => new { g.Key, Count = g.Count() }));

        Assert.Equal(NativeRoute.GroupBy, mongoQuery.Select.Route);
        Assert.NotNull(mongoQuery.Select.Grouping);
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

    [Fact]
    public void Skip_on_bare_GroupBy_result_defers_to_PendingGroupPaging_without_marking_non_native()
    {
        // Skip/Take on the ungrouped GroupBy(key) result must be deferred (PendingGroupPaging), not declined by
        // the post-terminal guard. Route is Fallback either way (no terminal Select finalizes Grouping), so
        // HasUnsupportedOperator and PendingGroupPaging are the discriminators.
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.GroupBy(c => c.Age).Skip(0));

        Assert.False(mongoQuery.Select.HasUnsupportedOperator);
        var op = Assert.Single(mongoQuery.Select.PendingGroupPaging!);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoSkipOp>(op).Count).Value);
    }

    [Fact]
    public void Where_HAVING_after_Skip_on_bare_GroupBy_result_declines_instead_of_misordering()
    {
        // A HAVING Where after a Skip already in PendingGroupPaging must decline: the lowerer always emits
        // GroupHavingPredicate before GroupPagingOps, which would reverse the LINQ evaluation order.
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Age).Skip(1).Where(g => g.Count() >= 2));

        Assert.True(mongoQuery.Select.HasUnsupportedOperator);
        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
    }

    [Fact]
    public void OrderBy_after_Skip_on_bare_GroupBy_result_declines_instead_of_misordering()
    {
        // An OrderBy after a recorded Skip must decline for the same reason: GroupOrderOp is always emitted
        // before GroupPagingOps.
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.GroupBy(c => c.Age).Skip(1).OrderByDescending(g => g.Key));

        Assert.True(mongoQuery.Select.HasUnsupportedOperator);
        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
    }

    // A nav-entity-leaf projection's emitted _id would leak into a set operation's dedup key like the owned-array
    // leaf's does, so NativeProjectionBinder sets the same HasArrayProjectionLeaf flag, which IsPlainProjectedSelect
    // gates on. This pins that the flag is set on the combined operand (a full round-trip Union of such operands
    // hits a separate nav-expansion issue; see the functional tests).
    [Fact]
    public void Owned_reference_entity_leaf_projection_sets_HasArrayProjectionLeaf_for_the_set_op_gate()
    {
        var mongoQuery = TranslateUnionToMongoQuery(
            (IQueryable<CustomerWithOwnedAddress> q) => q.Select(c => new { c.Address, c.Age }),
            (IQueryable<CustomerWithOwnedAddress> q) => q.Select(c => new { c.Address, c.Age }),
            mb => mb.Entity<CustomerWithOwnedAddress>().OwnsOne(c => c.Address));

        Assert.True(mongoQuery.Select.HasArrayProjectionLeaf);
        // IsPlainProjectedSelect gates on that flag, so SetOperation stays unset and the query is marked
        // non-native (Union/Concat's graceful fallback; see TryTranslateSetOperation).
        Assert.Null(mongoQuery.Select.SetOperation);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  A constructed (non-navigation) sub-entity leaf — `new { Copy = new CustomerDto { Id = c.Id, ... } }` —
    //  mixed with a computed sibling in a projection.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    // An unmapped DTO rebuilt from root-level scalar fields — unlike the owned-nav-entity leaf, which aliases an
    // already-stored owned sub-document.
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
        // No owner-key hazard (every member is a root-relative field readable on an unprojected document), so
        // unlike the owned-array/owned-nav-entity leaves it must not set HasArrayProjectionLeaf.
        Assert.False(mongoQuery.Select.HasArrayProjectionLeaf);
    }

    // A constructed sub-entity leaf with a computed sibling goes native: its members are readable off a whole
    // document by their natural paths, so no sibling-readability sweep is needed (unlike owned-nav-entity leaves).
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

    // A member that isn't a plain top-level field (`c.Name.Length`) declines the whole leaf — this is a minimal
    // widening, not a general nested-projection engine.
    [Fact]
    public void Document_construction_leaf_with_computed_member_declines_to_fallback()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(
            q => q.Select(c => new { Copy = new CustomerDto { Id = c.Id, Name = c.Name, Age = c.Name.Length } }));

        Assert.Equal(NativeRoute.Fallback, mongoQuery.Select.Route);
        Assert.Empty(mongoQuery.Select.Projection);
    }

}
