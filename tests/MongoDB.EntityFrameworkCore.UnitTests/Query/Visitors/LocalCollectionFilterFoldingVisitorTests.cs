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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Visitors;

/// <summary>
/// Unit tests for the compile-time half of <see cref="LocalCollectionFilterFoldingVisitor"/> (constant sources and the shapes it
/// must leave alone). The per-execution runtime-parameter half needs a <c>QueryCompilationContext</c> and is covered by the functional tests.
/// </summary>
public class LocalCollectionFilterFoldingVisitorTests
{
    private static readonly List<string?> Source = ["ABCDE", null, "FGHIJ"];

    private static LocalCollectionFilterFoldingVisitor Visitor() => new(null!);

    // Enumerable.Contains(Enumerable.Where(source, predicate), value)
    private static MethodCallExpression ContainsOverWhere(Expression source, Expression<Func<string?, bool>> predicate, Expression value)
        => Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
            Expression.Call(typeof(Enumerable), nameof(Enumerable.Where), [typeof(string)], source, predicate),
            value);

    [Fact]
    public void Folds_a_closed_filter_over_a_constant_collection_into_a_filtered_constant()
    {
        var call = ContainsOverWhere(
            Expression.Constant(Source), e => e != null, Expression.Constant("ABCDE", typeof(string)));

        var visited = Assert.IsAssignableFrom<MethodCallExpression>(Visitor().Visit(call));

        Assert.Equal(nameof(Enumerable.Contains), visited.Method.Name);
        var folded = Assert.IsType<ConstantExpression>(visited.Arguments[0]);
        Assert.Equal(new[] { "ABCDE", "FGHIJ" }, Assert.IsType<string[]>(folded.Value));
        Assert.Same(call.Arguments[1], visited.Arguments[1]);
    }

    [Fact]
    public void Folding_a_filter_that_removes_everything_yields_an_empty_constant()
    {
        var call = ContainsOverWhere(
            Expression.Constant(Source), e => e == "nope", Expression.Constant("ABCDE", typeof(string)));

        var visited = Assert.IsAssignableFrom<MethodCallExpression>(Visitor().Visit(call));

        Assert.Empty(Assert.IsType<string[]>(Assert.IsType<ConstantExpression>(visited.Arguments[0]).Value));
    }

    [Fact]
    public void Leaves_a_predicate_with_a_free_variable_untouched()
    {
        var captured = Expression.Parameter(typeof(string), "captured");
        var element = Expression.Parameter(typeof(string), "e");
        var predicate = Expression.Lambda<Func<string?, bool>>(Expression.Equal(element, captured), element);
        var call = ContainsOverWhere(Expression.Constant(Source), predicate, Expression.Constant("ABCDE", typeof(string)));

        Assert.Same(call, Visitor().Visit(call));
    }

    [Fact]
    public void Leaves_a_predicate_with_a_nested_lambda_untouched()
    {
        var call = ContainsOverWhere(
            Expression.Constant(Source), e => new[] { "x" }.Any(x => x == e), Expression.Constant("ABCDE", typeof(string)));

        Assert.Same(call, Visitor().Visit(call));
    }

    [Fact]
    public void Leaves_a_non_deterministic_predicate_untouched()
    {
        var call = ContainsOverWhere(
            Expression.Constant(Source), e => e != Guid.NewGuid().ToString(), Expression.Constant("ABCDE", typeof(string)));

        Assert.Same(call, Visitor().Visit(call));
    }

    [Fact]
    public void Leaves_a_Contains_over_a_plain_collection_untouched()
    {
        var call = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
            Expression.Constant(Source), Expression.Constant("ABCDE", typeof(string)));

        Assert.Same(call, Visitor().Visit(call));
    }

    [Fact]
    public void Leaves_a_Where_over_an_unfoldable_source_untouched()
    {
        // Neither a constant, a query parameter nor a closed inline array: nothing to evaluate at compile time or per execution.
        var source = Expression.Property(Expression.Constant(new Holder(Source)), nameof(Holder.Items));
        var call = ContainsOverWhere(source, e => e != null, Expression.Constant("ABCDE", typeof(string)));

        Assert.Same(call, Visitor().Visit(call));
    }

    [Fact]
    public void Folds_a_Contains_nested_inside_another_expression()
    {
        var inner = ContainsOverWhere(
            Expression.Constant(Source), e => e != null, Expression.Constant("ABCDE", typeof(string)));
        var outer = Expression.Not(inner);

        var visited = Assert.IsAssignableFrom<UnaryExpression>(Visitor().Visit(outer));

        var folded = Assert.IsType<ConstantExpression>(Assert.IsAssignableFrom<MethodCallExpression>(visited.Operand).Arguments[0]);
        Assert.Equal(new[] { "ABCDE", "FGHIJ" }, Assert.IsType<string[]>(folded.Value));
    }

    private sealed class Holder(List<string?> items)
    {
        public List<string?> Items { get; } = items;
    }
}
