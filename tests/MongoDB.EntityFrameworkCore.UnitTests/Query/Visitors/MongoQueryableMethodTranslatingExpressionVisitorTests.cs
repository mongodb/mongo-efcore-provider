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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Visitors;

public class MongoQueryableMethodTranslatingExpressionVisitorTests
{
    private class Customer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    private static readonly IQueryable<Customer> Source = new Customer[0].AsQueryable();

    // Reads the OrderBy/ThenBy/... spine of an expression tree, outermost-in, and returns it in
    // chronological (source-to-outermost) order as (method name, argument count) pairs. Argument
    // count distinguishes the 2-arg (key selector only) and 3-arg (key selector + IComparer) overloads.
    private static List<(string Name, int ArgCount)> GetOrderingChainShape(Expression expression)
    {
        var shape = new List<(string, int)>();
        while (expression is MethodCallExpression { Arguments.Count: > 0 } call
               && call.Method.DeclaringType == typeof(Queryable))
        {
            shape.Insert(0, (call.Method.Name, call.Arguments.Count));
            expression = call.Arguments[0];
        }

        return shape;
    }

    [Fact]
    public void Elides_ThenByDescending_duplicating_the_OrderBy_key()
    {
        var query = Source.OrderBy(c => c.Name).ThenByDescending(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal([(nameof(Queryable.OrderBy), 2)], GetOrderingChainShape(result));
    }

    [Fact]
    public void Elides_ThenBy_duplicating_the_OrderBy_key_in_the_same_direction()
    {
        var query = Source.OrderBy(c => c.Name).ThenBy(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal([(nameof(Queryable.OrderBy), 2)], GetOrderingChainShape(result));
    }

    [Fact]
    public void Keeps_two_independent_OrderBy_calls_on_the_same_key()
    {
        // .OrderBy().OrderByDescending() (as opposed to .OrderBy().ThenByDescending()) is two
        // independent orderings - the second entirely supersedes the first - so both must be kept.
        var query = Source.OrderBy(c => c.Name).OrderByDescending(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 2), (nameof(Queryable.OrderByDescending), 2)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Mid_chain_OrderBy_resets_duplicate_tracking()
    {
        // The second OrderByDescending starts a fresh ordering, so the ThenBy(Name) directly under it
        // is a duplicate of *that* ordering (and is elided), while the first ThenBy(Name) - a duplicate
        // of the original OrderBy - is also elided; the two survivors are the resetting pair.
        var query = Source.OrderBy(c => c.Name).ThenBy(c => c.Name)
            .OrderByDescending(c => c.Name).ThenBy(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 2), (nameof(Queryable.OrderByDescending), 2)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Elides_only_the_redundant_ordering_in_a_longer_chain()
    {
        // ThenBy(Name) duplicates OrderBy(Name) and is elided; ThenByDescending(City) is a genuinely
        // new key and survives; the final ThenBy(City) duplicates it and is elided.
        var query = Source.OrderBy(c => c.Name).ThenBy(c => c.Name)
            .ThenByDescending(c => c.City).ThenBy(c => c.City);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 2), (nameof(Queryable.ThenByDescending), 2)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Does_not_elide_ordering_with_an_explicit_comparer()
    {
        var query = Source.OrderBy(c => c.Name).ThenByDescending(c => c.Name, System.StringComparer.Ordinal);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 2), (nameof(Queryable.ThenByDescending), 3)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Does_not_treat_an_earlier_comparer_ordering_as_a_match_for_a_later_plain_ordering()
    {
        var query = Source.OrderBy(c => c.Name, System.StringComparer.Ordinal).ThenByDescending(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 3), (nameof(Queryable.ThenByDescending), 2)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Does_not_elide_a_repeated_computed_key_selector()
    {
        // c.Name.Length is a two-hop computed key (Name, then Length) - EF materializes each such
        // ordering into its own uniquely-named projected field, so repeating it never collides and
        // must not be elided, unlike a direct single-hop property access.
        var query = Source.OrderBy(c => c.Name.Length).ThenBy(c => c.Name.Length);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Equal(
            [(nameof(Queryable.OrderBy), 2), (nameof(Queryable.ThenBy), 2)],
            GetOrderingChainShape(result));
    }

    [Fact]
    public void Leaves_a_non_ordering_expression_unchanged()
    {
        Expression<Func<Customer, bool>> predicate = c => c.Name == "A";
        var query = Source.Where(predicate);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        Assert.Same(query.Expression, result);
    }

    [Fact]
    public void Elides_through_an_intervening_Where_below_the_ordering_chain()
    {
        var query = Source.Where(c => c.City == "X").OrderBy(c => c.Name).ThenByDescending(c => c.Name);

        var result = MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings(query.Expression);

        var call = Assert.IsAssignableFrom<MethodCallExpression>(result);
        Assert.Equal(nameof(Queryable.OrderBy), call.Method.Name);
        var whereCall = Assert.IsAssignableFrom<MethodCallExpression>(call.Arguments[0]);
        Assert.Equal(nameof(Queryable.Where), whereCall.Method.Name);
    }

    [Fact]
    public void KeySelectorsMatch_returns_true_for_the_same_property_with_differently_named_parameters()
    {
        Expression<Func<Customer, string>> a = c => c.Name;
        Expression<Func<Customer, string>> b = x => x.Name;

        Assert.True(MongoQueryableMethodTranslatingExpressionVisitor.KeySelectorsMatch(a, b));
    }

    [Fact]
    public void KeySelectorsMatch_returns_false_for_different_properties()
    {
        Expression<Func<Customer, string>> a = c => c.Name;
        Expression<Func<Customer, string>> b = c => c.City;

        Assert.False(MongoQueryableMethodTranslatingExpressionVisitor.KeySelectorsMatch(a, b));
    }

    [Fact]
    public void KeySelectorsMatch_returns_false_for_a_computed_multi_hop_key()
    {
        Expression<Func<Customer, int>> a = c => c.Name.Length;
        Expression<Func<Customer, int>> b = c => c.Name.Length;

        Assert.False(MongoQueryableMethodTranslatingExpressionVisitor.KeySelectorsMatch(a, b));
    }
}
