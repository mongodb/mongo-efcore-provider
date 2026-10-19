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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

/// <summary>
/// EF-373. Unit coverage for <see cref="MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede"/>,
/// the check that re-verifies the emitted <c>$lookup</c> order against the actual <c>localField</c>
/// dependency chain once the interleaved path splits the previously contiguous lookup group.
/// <para>
/// It exists because that check has no discriminating FUNCTIONAL test and cannot get one: no ordinary-LINQ
/// shape has been found that violates the invariant (splitting along the join order preserves it by
/// construction), so mutating the check to always return <see langword="true"/> turns nothing red at the
/// functional level. These tests pin its ordering logic directly instead, which is the part a future edit
/// could get wrong — a lookup emitted before the one whose unwound output its <c>localField</c> reads
/// matches nothing, and every row is silently dropped.
/// </para>
/// </summary>
public class Ef373DependenciesPrecedeTests
{
    [Fact]
    public void Independent_lookups_are_accepted_in_either_order()
    {
        var a = Lookup("_lookup_A", "mid_id");
        var b = Lookup("_lookup_B", "other_id");

        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([a, b]));
        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([b, a]));
    }

    [Fact]
    public void Dependent_lookup_emitted_after_the_one_it_reads_is_accepted()
    {
        var first = Lookup("_lookup_Orders", "_id");
        var transitive = Lookup("_lookup_OrderDetails", "_lookup_Orders._id");

        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([first, transitive]));
    }

    [Fact]
    public void Dependent_lookup_emitted_before_the_one_it_reads_is_rejected()
    {
        var first = Lookup("_lookup_Orders", "_id");
        var transitive = Lookup("_lookup_OrderDetails", "_lookup_Orders._id");

        // The transitive lookup's localField reads _lookup_Orders' unwound output, so emitting it first
        // would match nothing: this is exactly the order the check has to refuse.
        Assert.False(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([transitive, first]));
    }

    [Fact]
    public void A_chain_of_three_is_accepted_in_dependency_order_and_rejected_out_of_it()
    {
        var one = Lookup("_lookup_One", "_id");
        var two = Lookup("_lookup_Two", "_lookup_One.next_id");
        var three = Lookup("_lookup_Three", "_lookup_Two.next_id");

        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([one, two, three]));

        // Only the middle pair is transposed, so the violation is not adjacent to the ends.
        Assert.False(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([two, one, three]));
    }

    [Fact]
    public void An_alias_that_is_merely_a_string_prefix_of_another_is_not_treated_as_a_dependency()
    {
        // "_lookup_A" is a string prefix of "_lookup_AB", but "_lookup_AB.x" does not read _lookup_A's
        // output — the separating '.' is what makes it a dependency, so this order must be accepted.
        var ab = Lookup("_lookup_AB", "root_id");
        var reader = Lookup("_lookup_C", "_lookup_AB.x");
        var a = Lookup("_lookup_A", "root_id");

        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([reader, a]));
        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([ab, reader, a]));
    }

    [Fact]
    public void An_empty_or_single_element_order_is_accepted()
    {
        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([]));
        Assert.True(MongoEFToLinqTranslatingExpressionVisitor.DependenciesPrecede([Lookup("_lookup_A", "mid_id")]));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────
    //
    // The check reads only As and LocalField, both settable, so one real navigation is enough to build as
    // many distinctly-addressed lookups as a test needs.
    private static LookupExpression Lookup(string @as, string localField)
    {
        var lookup = new LookupExpression(Navigation(), forceUnwind: true) { As = @as, LocalField = localField };

        Assert.Equal(@as, lookup.As);
        Assert.Equal(localField, lookup.LocalField);

        return lookup;
    }

    private static INavigation Navigation()
    {
        using var db = new TwoEntityDbContext();
        var order = db.Model.FindEntityType(typeof(Order))!;

        return order.GetNavigations().Single(n => !n.IsCollection);
    }

    private class Customer
    {
        public ObjectId Id { get; set; }
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private class TwoEntityDbContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Customer)
                .WithMany()
                .HasForeignKey(o => o.CustomerId);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
