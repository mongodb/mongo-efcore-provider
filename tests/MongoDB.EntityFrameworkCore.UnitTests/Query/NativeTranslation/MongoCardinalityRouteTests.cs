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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoCardinalityRouteTests
{
    [Fact]
    public void No_cardinality_routes_whole_entity()
        => Assert.Equal(NativeRoute.WholeEntity, new MongoSelectDefinition().Route);

    [Fact]
    public void Reducer_with_limit_routes_whole_entity()
    {
        var select = new MongoSelectDefinition
        {
            Cardinality = MongoCardinality.ForReducer(MongoReducerKind.First, typeof(object))
        };
        select.AppendLimit(new MongoConstantExpression(1, forSerialization: null));
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
    }

    [Fact]
    public void Aggregate_routes_scalar_aggregate()
    {
        var select = new MongoSelectDefinition
        {
            Cardinality = MongoCardinality.ForAggregate(
                MongoAggregateOperator.Count, selector: null, MongoEmptyAggregateBehavior.DefaultValue,
                emptyValue: 0, typeof(int), presenceOnly: false, presentValue: null)
        };
        Assert.Equal(NativeRoute.ScalarAggregate, select.Route);
    }

    [Fact]
    public void Unsupported_operator_beats_aggregate()
    {
        var select = new MongoSelectDefinition
        {
            Cardinality = MongoCardinality.ForAggregate(
                MongoAggregateOperator.Count, selector: null, MongoEmptyAggregateBehavior.DefaultValue,
                emptyValue: 0, typeof(int), presenceOnly: false, presentValue: null)
        };
        select.MarkNotNativelyRepresentable();
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }

    [Fact]
    public void EmptyBehavior_for_count_is_default_value()
    {
        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Count, typeof(int), out var value, out var behavior);
        Assert.Equal(MongoEmptyAggregateBehavior.DefaultValue, behavior);
        Assert.NotNull(value);
    }

    [Fact]
    public void EmptyBehavior_for_sum_is_default_value()
    {
        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Sum, typeof(long), out var value, out var behavior);
        Assert.Equal(MongoEmptyAggregateBehavior.DefaultValue, behavior);
        Assert.NotNull(value);
    }

    [Fact]
    public void EmptyBehavior_for_min_nonnullable_is_throw()
    {
        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Min, typeof(int), out _, out var behavior);
        Assert.Equal(MongoEmptyAggregateBehavior.Throw, behavior);
    }

    [Fact]
    public void EmptyBehavior_for_min_nullable_is_return_null()
    {
        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Min, typeof(int?), out _, out var behavior);
        Assert.Equal(MongoEmptyAggregateBehavior.ReturnNull, behavior);
    }

    [Fact]
    public void EmptyBehavior_for_any_and_all_are_default_value()
    {
        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Any, typeof(bool), out var anyValue, out var anyBehavior);
        Assert.Equal(MongoEmptyAggregateBehavior.DefaultValue, anyBehavior);
        Assert.Equal(false, anyValue);

        NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.All, typeof(bool), out var allValue, out var allBehavior);
        Assert.Equal(MongoEmptyAggregateBehavior.DefaultValue, allBehavior);
        Assert.Equal(true, allValue);
    }
}
