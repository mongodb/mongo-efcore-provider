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
        bool isFallbackWrongData = false,
        bool hasUnboundVectorSearch = false,
        MongoQueryMode mode = MongoQueryMode.Native)
        => MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(
            route, isFallbackWrongData, hasUnboundVectorSearch, mode);

    [Fact]
    public void WholeEntity_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.WholeEntity));

    [Fact]
    public void Projection_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.Projection));

    [Fact]
    public void GroupBy_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.GroupBy));

    // ScalarAggregate is native (built by TryBuildAggregateFactory). The `|| Route == ScalarAggregate` term at the
    // TryBuildNativeFactory call site needs a full MongoQueryExpression, so it isn't pinnable here; it's covered
    // by the scalar-cardinality tests under NativeOnly. Don't remove that disjunct on the strength of this test.
    [Fact]
    public void ScalarAggregate_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.ScalarAggregate));

    [Fact]
    public void Fallback_route_is_fallback()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.Fallback));

    // Silent-drop guard: an unbound VectorSearch must not classify Native, or the lowerer emits no $vectorSearch
    // stage and returns rows in insertion order without error. Falling back lets driver-LINQ execute it.
    [Fact]
    public void Unbound_vector_search_is_fallback_even_when_route_is_native()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.WholeEntity, hasUnboundVectorSearch: true));

    // Degenerate at this level (same as WholeEntity_is_native); the real check that bound vector search goes
    // native is NativeVectorSearchTests under MongoQueryMode.NativeOnly.
    [Fact]
    public void Bound_vector_search_is_native()
        => Assert.Equal(NativeDisposition.Native, Classify(NativeRoute.WholeEntity, hasUnboundVectorSearch: false));

    [Fact]
    public void Fallback_wrong_data_is_hard_decline_under_native()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isFallbackWrongData: true, mode: MongoQueryMode.Native));

    [Fact]
    public void Fallback_wrong_data_is_hard_decline_under_native_only()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isFallbackWrongData: true, mode: MongoQueryMode.NativeOnly));

    [Fact]
    public void Fallback_wrong_data_is_fallback_under_driver_linq()
        => Assert.Equal(
            NativeDisposition.Fallback,
            Classify(NativeRoute.Fallback, isFallbackWrongData: true, mode: MongoQueryMode.DriverLinq));

    [Fact]
    public void Hard_decline_takes_precedence_over_unbound_vector_search()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isFallbackWrongData: true, hasUnboundVectorSearch: true,
                mode: MongoQueryMode.Native));
}
