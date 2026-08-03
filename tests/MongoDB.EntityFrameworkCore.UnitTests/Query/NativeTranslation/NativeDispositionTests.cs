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
    // TODO(CSHARP-6017): part of the removal checklist in
    // docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6. Collapsing
    // MongoSelectDefinition.IsFallbackWrongData back to IsGroupByFallbackUnsafe when the paging guard is deleted
    // means renaming this helper's `isFallbackWrongData` parameter back to `isGroupByFallbackUnsafe` and renaming
    // the four tests below that use it (Fallback_wrong_data_* / the DriverLinq and vector-search cases). The
    // BEHAVIOUR of those four is permanent — the GroupBy+Join half of the union survives the driver fix — so this
    // is a rename, NOT a deletion.
    private static NativeDisposition Classify(
        NativeRoute route,
        bool isFallbackWrongData = false,
        bool containsVectorSearch = false,
        MongoQueryMode mode = MongoQueryMode.Native)
        => MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition(
            route, isFallbackWrongData, containsVectorSearch, mode);

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
    public void Hard_decline_takes_precedence_over_vector_search()
        => Assert.Equal(
            NativeDisposition.HardDecline,
            Classify(NativeRoute.Fallback, isFallbackWrongData: true, containsVectorSearch: true,
                mode: MongoQueryMode.Native));
}
