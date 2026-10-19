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
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// A projected reference-collection-navigation LIST leaf (<c>Orders = c.Orders.ToList()</c>) — the
/// translate-time recognizer's accept/decline matrix, mirroring
/// <see cref="NativeCorrelatedReducerLeafTests"/>'s harness pattern.
/// </summary>
public class NativeProjectedCollectionListLeafTests
{
    private class Customer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public ICollection<Order> Orders { get; set; } = null!;
    }

    private class Order
    {
        public int Id { get; set; }
        public string CustomerId { get; set; } = "";
        public Customer Customer { get; set; } = null!;
        public List<OrderDetail> OrderDetails { get; set; } = null!;
    }

    private class OrderDetail
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order Order { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> Model = mb =>
    {
        mb.Entity<Customer>().HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
        mb.Entity<Order>().HasMany(o => o.OrderDetails).WithOne(d => d.Order).HasForeignKey(d => d.OrderId);
    };

    private static (bool Accepted, MongoQueryExpression Query) Bind(Func<IQueryable<Customer>, IQueryable> buildQuery)
    {
        using var db = new TestDbContext<Customer>(Model);

        var compilationContext = db.GetService<IQueryCompilationContextFactory>().Create(async: false);
        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);

        var entityType = db.Model.FindEntityType(typeof(Customer))!;
        var root = new RootExpressionQueryable<Customer>(new EntityQueryRootExpression(entityType));

        var preprocessed = ClosureCaptureParameterizer.Parameterize(
            preprocessor.Process(buildQuery(root).Expression));

        var select = Assert.IsAssignableFrom<MethodCallExpression>(preprocessed);
        Assert.Equal(nameof(Queryable.Select), select.Method.Name);
        var selector = Assert.IsAssignableFrom<LambdaExpression>(select.Arguments[1].UnwrapLambdaFromQuote());

        var mongoQ = new MongoQueryExpression(entityType);
        return (NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector), mongoQ);
    }

    private static MongoQueryExpression BindAccepted(Func<IQueryable<Customer>, IQueryable> buildQuery)
    {
        var (accepted, mongoQ) = Bind(buildQuery);
        Assert.True(accepted);
        return mongoQ;
    }

    private static void AssertDeclined(Func<IQueryable<Customer>, IQueryable> buildQuery)
    {
        var (accepted, mongoQ) = Bind(buildQuery);
        Assert.False(accepted);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.GetPendingLookups());
    }

    [Fact]
    public void Bare_ToList_is_recognized_and_retains_the_lookup_alias()
    {
        var mongoQ = BindAccepted(q => q.Select(c => new { c.Name, Orders = c.Orders.ToList() }));

        var lookup = Assert.Single(mongoQ.GetPendingLookups());
        Assert.Equal("_lookup_Orders", lookup.As);
        Assert.Equal(LookupPipelineKind.None, lookup.PipelineKind);

        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "_lookup_Orders");
        Assert.Equal(
            "_lookup_Orders",
            Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    [Fact]
    public void Bare_ToArray_is_recognized()
    {
        BindAccepted(q => q.Select(c => new { c.Name, Orders = c.Orders.ToArray() }));
    }

    [Fact]
    public void Bare_ToHashSet_declines_because_EF_never_lowers_it_to_the_recognized_shape()
    {
        // NOTE (review finding M8): TryTranslateProjectedCollectionNavigationList's own XML doc claims
        // ".ToArray()/.ToHashSet() on [an ICollection<T>-declared navigation] ... reach the Where-wrapped
        // shape" — mirroring Bare_ToArray_is_recognized (BindAccepted) for .ToHashSet() was expected to pass.
        // It does not: EF Core's NavigationExpandingExpressionVisitor.VisitMethodCall only special-cases
        // Enumerable.ToList/ToArray to unwrap a MaterializeCollectionNavigationExpression into the queryable
        // Where-wrapped shape this recognizer (and the bind-side pass) requires — ToHashSet is not in that
        // list, on ANY declared navigation type, so a projected `nav.ToHashSet()` leaf always arrives as an
        // un-lowered MaterializeCollectionNavigationExpression and this recognizer safely declines it (falls
        // back to driver-LINQ, correct data). Confirmed empirically against this EF version; see also
        // ToArray_on_a_List_declared_navigation_declines_gracefully for the sibling List<T>-declared-nav case.
        AssertDeclined(q => q.Select(c => new { c.Name, Orders = c.Orders.ToHashSet() }));
    }

    [Fact]
    public void ThenInclude_wrapped_shape_is_still_recognized()
    {
        // NOTE: this harness only hands the SELECTOR to TryPopulateNativeProjection — it never runs the
        // bind-side pass (MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigation) that
        // would register a separately-`.Include()`'d/`.ThenInclude()`'d navigation's OWN lookup. So this test
        // does NOT exercise the AddLookup alias-dedup mechanism (a genuinely separate Include registration
        // colliding with this leaf's own registration) — real dedup coverage lives in the spec/functional
        // tests. What this DOES prove: `.ThenInclude()` wraps the projected nav's selector body in an
        // `IncludeExpression` (`o => Include(o, ...)` rather than the plain identity `o => o`), and the
        // recognizer still matches that wrapped shape via IsEntityMaterializingSelector's IncludeExpression
        // unwrap loop.
        var mongoQ = BindAccepted(q =>
            q.Include(c => c.Orders).ThenInclude(o => o.OrderDetails)
                .Select(c => new { c.Name, Orders = c.Orders.ToList() }));

        Assert.Single(mongoQ.GetPendingLookups(), l => l.As == "_lookup_Orders");
    }

    [Fact]
    public void Two_list_leaves_over_the_same_navigation_decline()
    {
        // The design spec describes two sibling list leaves over the SAME navigation as deduping; the shipped
        // recognizer instead declines this combination (safely — it falls back to driver-LINQ with correct
        // data). Both leaves derive the identical "_lookup_Orders" alias, and the second AddLookup-equivalent
        // seenAliases.Add fails, so the whole projection declines rather than dedupe.
        AssertDeclined(q => q.Select(c => new { A = c.Orders.ToList(), B = c.Orders.ToList() }));
    }

    [Fact]
    public void A_Where_directly_on_the_projected_nav_declines()
    {
        AssertDeclined(q => q.Select(c => new { c.Name, Orders = c.Orders.Where(o => o.Id > 0).ToList() }));
    }

    [Fact]
    public void An_OrderBy_directly_on_the_projected_nav_declines()
    {
        AssertDeclined(q => q.Select(c => new { c.Name, Orders = c.Orders.OrderBy(o => o.Id).ToList() }));
    }

    [Fact]
    public void Composes_with_a_plain_scalar_sibling_leaf()
    {
        var mongoQ = BindAccepted(q => q.Select(c => new { c.Name, Orders = c.Orders.ToList() }));

        // The array leaf's presence widens hasArrayLeaf, which (a) requires every sibling scalar leaf to be
        // whole-document-readable (Name's alias equals its own element name, so it qualifies) and (b) retains
        // the owner's "_id" alongside the requested aliases (see the commit block in TryPopulateNativeProjection) —
        // so three projection members are expected here, not two.
        Assert.Equal(3, mongoQ.Select.Projection.Count);
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "Name");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "_lookup_Orders");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "_id");
    }

    // ── A navigation declared as a concrete List<T> ─────────────────────────────────────────────────────────
    // Separate small model: TryTranslateProjectedCollectionNavigationList's own remarks document that
    // .ToArray()/.ToHashSet() on a List<T>-DECLARED navigation take a structurally different, earlier-collapsed
    // nav-expanded shape (MaterializeCollectionNavigationExpression.ToArray()/.ToHashSet(), not the Where-wrapped
    // one this recognizer matches) — confirmed empirically, not merely asserted in the comment. Verified here so
    // this limitation stays proven rather than just documented: the recognizer must gracefully DECLINE (fall
    // back to driver-LINQ), not throw or silently mis-translate.

    private class Vendor
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Product> Products { get; set; } = null!;
    }

    private class Product
    {
        public int Id { get; set; }
        public string VendorId { get; set; } = "";
        public Vendor Vendor { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> ListDeclaredModel = mb =>
    {
        mb.Entity<Vendor>().HasMany(v => v.Products).WithOne(p => p.Vendor).HasForeignKey(p => p.VendorId);
    };

    [Fact]
    public void ToArray_on_a_List_declared_navigation_declines_gracefully()
    {
        using var db = new TestDbContext<Vendor>(ListDeclaredModel);

        var compilationContext = db.GetService<IQueryCompilationContextFactory>().Create(async: false);
        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);

        var entityType = db.Model.FindEntityType(typeof(Vendor))!;
        var root = new RootExpressionQueryable<Vendor>(new EntityQueryRootExpression(entityType));

        Func<IQueryable<Vendor>, IQueryable> buildQuery =
            q => q.Select(v => new { v.Name, Products = v.Products.ToArray() });
        var preprocessed = ClosureCaptureParameterizer.Parameterize(
            preprocessor.Process(buildQuery(root).Expression));

        var select = Assert.IsAssignableFrom<MethodCallExpression>(preprocessed);
        Assert.Equal(nameof(Queryable.Select), select.Method.Name);
        var selector = Assert.IsAssignableFrom<LambdaExpression>(select.Arguments[1].UnwrapLambdaFromQuote());

        var mongoQ = new MongoQueryExpression(entityType);
        var accepted = NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector);

        Assert.False(accepted);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.GetPendingLookups());
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────────────────────────────────
    // Mirrors NativeCorrelatedReducerLeafTests' own private harness classes (each test file in this directory
    // keeps its own copy rather than sharing one) — do not reimplement, only copy.

    /// <summary>
    /// Stands in for EF Core's own funcletization step: rewrites every CLOSURE capture (a member read off the
    /// compiler-generated display-class constant the C# compiler emits for a captured local) into the EF query
    /// parameter node that step would produce — a prefix-named <see cref="ParameterExpression"/> on EF8/EF9, a
    /// <c>QueryParameterExpression</c> on EF10.
    /// </summary>
    private sealed class ClosureCaptureParameterizer : ExpressionVisitor
    {
        private int _index;

        public static Expression Parameterize(Expression expression)
            => new ClosureCaptureParameterizer().Visit(expression);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is ConstantExpression { Value: { } closure }
                && Attribute.IsDefined(closure.GetType(), typeof(CompilerGeneratedAttribute)))
            {
#if EF8 || EF9
                return Expression.Parameter(
                    node.Type,
                    QueryCompilationContext.QueryParameterPrefix + node.Member.Name + "_" + _index++);
#else
                // EF10 dropped QueryCompilationContext.QueryParameterPrefix; the "__" spelling matches
                // MongoExpressionTranslatorTests and NativeCorrelatedReducerLeafTests.
                return new QueryParameterExpression("__" + node.Member.Name + "_" + _index++, node.Type);
#endif
            }

            return base.VisitMember(node);
        }
    }

    private sealed class TestDbContext<TRoot>(Action<ModelBuilder> model) : DbContext(BuildOptions())
        where TRoot : class
    {
        private static DbContextOptions BuildOptions()
            => new DbContextOptionsBuilder()
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            model(modelBuilder);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int Count;

            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref Count);
        }
    }

    /// <summary>
    /// A minimal queryable stub rooted in an <see cref="EntityQueryRootExpression"/>, so applied LINQ operators
    /// build the same method-call chain EF's own preprocessing phase receives. Mirrors
    /// <c>SlotPopulationTests.RootExpressionQueryable</c>/<c>NativeCorrelatedReducerLeafTests.RootExpressionQueryable</c>.
    /// </summary>
    private sealed class RootExpressionQueryable<T>(Expression expression) : IOrderedQueryable<T>
    {
        public Type ElementType => typeof(T);
        public Expression Expression => expression;
        public IQueryProvider Provider => new ThrowingProvider();
        public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Test stub only.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class ThrowingProvider : IQueryProvider
        {
            public IQueryable CreateQuery(Expression e) => throw new NotSupportedException("Test stub only.");
            public IQueryable<TElement> CreateQuery<TElement>(Expression e) => new RootExpressionQueryable<TElement>(e);
            public object Execute(Expression e) => throw new NotSupportedException("Test stub only.");
            public TResult Execute<TResult>(Expression e) => throw new NotSupportedException("Test stub only.");
        }
    }
}
