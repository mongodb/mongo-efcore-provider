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

    // EF-322 VectorSearch slice. The ASSERTION is unchanged, but what it pins is not: this is no longer
    // "vector search is never native" (it is, since Task 4) — it is the SILENT-DROP GUARD. A captured chain
    // carrying a VectorSearch that the native slot populator did NOT bind must not classify Native, because
    // the lowerer would then emit a pipeline with no $vectorSearch stage at all: the right ROW COUNT, in
    // INSERTION order rather than score order, with no exception. Falling back keeps the VectorSearch in the
    // captured chain, where driver-LINQ executes it correctly.
    [Fact]
    public void Unbound_vector_search_is_fallback_even_when_route_is_native()
        => Assert.Equal(NativeDisposition.Fallback, Classify(NativeRoute.WholeEntity, hasUnboundVectorSearch: true));

    // The complement of the test above, kept as documentation of intent — and its weakness is stated rather
    // than hidden: at this PURE level it is WholeEntity_is_native with an explicit `false`, so it is degenerate
    // and is NOT the discriminator for "a bound vector search goes native". The real discrimination is
    // end-to-end: NativeVectorSearchTests succeeding under MongoQueryMode.NativeOnly, plus the Task-4 mutation
    // that forces NativeVectorSearchBinder.TryBind to return false and shows those tests flip to
    // NativeTranslationNotSupportedException while default Native still returns correct, score-ordered rows.
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
