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

        Assert.NotNull(mongoQ.Predicate);
        Assert.True(mongoQ.IsNativeRepresentable);
        Assert.NotNull(mongoQ.CapturedExpression);
    }

    // ── Test 2: OrderBy + ThenByDescending → Orderings slot populated ─────────────

    [Fact]
    public void OrderBy_then_ThenBy_preserves_order()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(
            q => q.OrderBy(c => c.Age).ThenByDescending(c => c.Name));

        Assert.Equal(2, mongoQ.Orderings.Count);
        Assert.True(mongoQ.Orderings[0].Ascending);
        Assert.False(mongoQ.Orderings[1].Ascending);
    }

    // ── Test 3: Where after Take → non-canonical → IsNativeRepresentable = false ──

    [Fact]
    public void Where_after_Take_is_not_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Take(10).Where(c => c.Age > 21));

        Assert.False(mongoQ.IsNativeRepresentable);
        Assert.NotNull(mongoQ.CapturedExpression);
    }

    // ── Test 4: Projecting Select → IsNativeRepresentable = false ────────────────

    [Fact]
    public void Projecting_Select_is_not_native_representable()
    {
        var mongoQ = TranslateToMongoQuery<Customer>(q => q.Select(c => c.Name));

        Assert.False(mongoQ.IsNativeRepresentable);
    }
}
