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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="NativeProjectionBinder.IsRowIndependentLeaf"/> and the binder's client-evaluated leaves: admitted only
/// after the <c>$literal</c> path declined, never over the selector parameter, committed with
/// <see cref="MongoSelectDefinition.HasClientEvaluatedProjectionLeaf"/>, and backed by a constant sentinel when nothing
/// else is projected.
/// </summary>
public class NativeProjectionBinderRowIndependentLeafTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    private class Dto
    {
        public Dto()
        {
        }

        public Dto(string value)
        {
            Value = value;
        }

        public Dto(Customer customer)
        {
            Value = customer.Name;
        }

        public string? Value { get; }
        public string? City { get; set; }
    }

    private class Item
    {
        public int X { get; set; }
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Customer>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Customer))!);
    }

    // The post-extraction query-parameter node: a prefixed ParameterExpression on EF8/EF9, QueryParameterExpression on
    // EF10.
    private static Expression QueryParameter(Type type)
#if EF8 || EF9
        => Expression.Parameter(type, QueryCompilationContext.QueryParameterPrefix + "p_0");
#else
        => new QueryParameterExpression("__p_0", type);
#endif

    private static readonly ParameterExpression C = Expression.Parameter(typeof(Customer), "c");

    public static IEnumerable<object[]> RowIndependentLeaves()
    {
        yield return [Expression.Constant(5)];
        yield return [QueryParameter(typeof(Item))];
        yield return [Expression.New(typeof(DateTime))];
        yield return [Expression.New(typeof(DateTime).GetConstructor([typeof(int), typeof(int), typeof(int)])!,
            Expression.Constant(2020), Expression.Constant(1), Expression.Constant(2))];
        yield return [Expression.MemberInit(
            Expression.New(typeof(Dto).GetConstructor([typeof(string)])!, QueryParameter(typeof(string))),
            Expression.Bind(typeof(Dto).GetProperty(nameof(Dto.City))!, Expression.Constant("x")))];
        yield return [Expression.NewArrayInit(typeof(Item), QueryParameter(typeof(Item)))];
        yield return [Expression.ListInit(Expression.New(typeof(List<Item>)), QueryParameter(typeof(Item)))];
        yield return [Expression.Convert(QueryParameter(typeof(Item)), typeof(object))];
        yield return [Expression.Field(Expression.Constant(new Holder()), nameof(Holder.Value))];
    }

    public static IEnumerable<object[]> RowDependentLeaves()
    {
        // The selector parameter itself, and a construction over it (a whole-entity operand is row-dependent).
        yield return [C];
        yield return [Expression.New(typeof(Dto).GetConstructor([typeof(Customer)])!, C)];
        yield return [Expression.Convert(C, typeof(object))];
        // A field of the row.
        yield return [Expression.Property(C, nameof(Customer.Name))];
        yield return [Expression.New(typeof(Dto).GetConstructor([typeof(string)])!, Expression.Property(C, nameof(Customer.Name)))];
        // A plain lambda parameter that is not a query parameter.
        yield return [Expression.Parameter(typeof(Item), "other")];
        // A method call, even over constants.
        yield return [Expression.Call(Expression.Constant("abc"), typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!)];
        // A static member (DateTime.Now is per-evaluation, not per-execution).
        yield return [Expression.Property(null, typeof(DateTime).GetProperty(nameof(DateTime.Now))!)];
    }

    private sealed class Holder
    {
        public int Value = 3;
    }

    [Theory]
    [MemberData(nameof(RowIndependentLeaves))]
    public void Row_independent_leaves_are_recognized(Expression leaf)
        => Assert.True(NativeProjectionBinder.IsRowIndependentLeaf(leaf, C));

    [Theory]
    [MemberData(nameof(RowDependentLeaves))]
    public void Row_dependent_leaves_are_rejected(Expression leaf)
        => Assert.False(NativeProjectionBinder.IsRowIndependentLeaf(leaf, C));

    [Fact]
    public void Renderable_constant_leaf_keeps_the_literal_path_and_sets_no_flag()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object>> selector = c => new { c.Name, A = 10 };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(["Name", "A"], mongoQ.Select.Projection.Select(p => p.Alias));
        Assert.IsType<MongoConstantExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.False(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
    }

    [Fact]
    public void Unrenderable_row_independent_leaf_is_left_to_the_shaper_and_flagged()
    {
        var mongoQ = TestQuery();
        var selector = Expression.Lambda(
            Expression.New(
                typeof(Tuple<string, DateTime>).GetConstructor([typeof(string), typeof(DateTime)])!,
                [Expression.Property(C, nameof(Customer.Name)), Expression.New(typeof(DateTime))],
                typeof(Tuple<string, DateTime>).GetProperty("Item1")!, typeof(Tuple<string, DateTime>).GetProperty("Item2")!),
            C);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(["Item1"], mongoQ.Select.Projection.Select(p => p.Alias));
        Assert.True(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void All_client_body_stages_a_constant_sentinel_so_the_route_stays_projection()
    {
        var mongoQ = TestQuery();
        var selector = Expression.Lambda(QueryParameter(typeof(Item)), C);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var sentinel = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal(NativeProjectionBinder.ClientEvaluatedSentinelAlias, sentinel.Alias);
        Assert.Equal(true, Assert.IsType<MongoConstantExpression>(sentinel.Expression).Value);
        Assert.True(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void MemberInit_with_query_parameter_constructor_argument_binds_only_its_bindings()
    {
        var mongoQ = TestQuery();
        var selector = Expression.Lambda(
            Expression.MemberInit(
                Expression.New(typeof(Dto).GetConstructor([typeof(string)])!, QueryParameter(typeof(string))),
                Expression.Bind(typeof(Dto).GetProperty(nameof(Dto.City))!, Expression.Property(C, nameof(Customer.City)))),
            C);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(["City"], mongoQ.Select.Projection.Select(p => p.Alias));
        Assert.True(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
    }

    private static string ClientOnly(string s) => s;

    [Fact]
    public void Untranslatable_row_dependent_member_still_declines()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object>> selector = c => new { c.Name, U = ClientOnly(c.City) };

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
    }

    [Fact]
    public void MemberInit_with_row_dependent_constructor_argument_declines()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, Dto>> selector = c => new Dto(c.Name) { City = c.City };

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
    }
}
