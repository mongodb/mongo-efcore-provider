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

using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class NativeDispositionTests
{
    private static NativeDisposition Classify(
        NativeRoute route,
        bool isGroupByFallbackUnsafe = false,
        bool containsVectorSearch = false,
        MongoQueryMode mode = MongoQueryMode.Native)
        => MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(
            route, isGroupByFallbackUnsafe, containsVectorSearch, mode);

    [Fact]
    public void WholeEntity_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.WholeEntity));

    [Fact]
    public void Projection_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.Projection));

    [Fact]
    public void GroupBy_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.GroupBy));

    // NOTE: ScalarAggregate classifies as Native here — it IS native, just built by TryBuildAggregateFactory
    // rather than the whole-entity TryBuildNativeFactory. The `|| Route == ScalarAggregate` term at the
    // TryBuildNativeFactory call site (which declines it so it falls through to the aggregate factory) is a
    // query-composition decision that needs a full MongoQueryExpression and so is NOT unit-pinnable here; it is
    // covered end-to-end by the scalar-cardinality spec/functional sweep (EF-336) under NativeOnly. Do not
    // "simplify away" that disjunct on the strength of this test.
    [Fact]
    public void ScalarAggregate_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.ScalarAggregate));

    [Fact]
    public void Fallback_route_is_fallback()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.Fallback));

    [Fact]
    public void Vector_search_is_fallback_even_when_route_is_native()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.WholeEntity, containsVectorSearch: true));

    [Fact]
    public void GroupBy_unsafe_is_hard_decline_under_native()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.Native));

    [Fact]
    public void GroupBy_unsafe_is_hard_decline_under_native_only()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.NativeOnly));

    [Fact]
    public void GroupBy_unsafe_is_fallback_under_driver_linq()
        => Assert.Equal(
            NativeDisposition.Fallback,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, mode: MongoQueryMode.DriverLinq));

    [Fact]
    public void Hard_decline_takes_precedence_over_vector_search()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isGroupByFallbackUnsafe: true, containsVectorSearch: true,
                mode: MongoQueryMode.Native));
}
