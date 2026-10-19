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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// `e.City.AsEnumerable()`/`.ToList()`/`.ToArray()` — treating a string as its own <c>IEnumerable&lt;char&gt;</c>
/// — has no server-side MQL form (MongoDB has no char/char-sequence BSON representation), so
/// <see cref="NativeProjectionBinder.TryTranslateLeaf"/> admits it as a BARE field leaf (the wrapping call
/// dropped entirely on the emit side, same as an uncast member access) — the char-sequence materialization
/// itself happens client-side in the compiled shaper (see the projection-binding visitor tests for that half).
/// </summary>
public class NativeProjectionBinderStringSequenceTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string City { get; set; } = "";
        public int[] Numbers { get; set; } = [];
    }

    private class CityDto
    {
        public IEnumerable<char> Property { get; set; } = null!;
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Customer>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Customer))!);
    }

    [Fact]
    public void Wrapped_AsEnumerable_over_string_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, CityDto>> selector = c => new CityDto { Property = c.City.AsEnumerable() };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "Property");
        var field = Assert.IsType<MongoFieldExpression>(projection.Expression);
        Assert.Equal("City", field.ElementName);

        // The provenance flag the push-down gate keys off. Route == Projection alone cannot express what this
        // leaf needs: "every leaf resolved to a bare field" is true, but this one is only correct when a shaper
        // THIS provider built reads it back — the driver's own LINQ v3 provider cannot project an
        // Enumerable.*-over-string call at all (EF-250/EF-231). See
        // MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery's CanPushDown gate.
        Assert.True(mongoQ.Select.HasStringSequenceProjectionLeaf);
    }

    [Fact]
    public void A_plain_string_member_leaf_does_not_set_the_string_sequence_provenance_flag()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, string>> selector = c => c.City;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        Assert.False(mongoQ.Select.HasStringSequenceProjectionLeaf);
    }

    [Fact]
    public void Wrapped_ToList_over_string_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, CityDto>> selector = c => new CityDto { Property = c.City.ToList() };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "Property");
        Assert.IsType<MongoFieldExpression>(projection.Expression);
    }

    [Fact]
    public void Wrapped_ToArray_over_string_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, CityDto>> selector = c => new CityDto { Property = c.City.ToArray() };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "Property");
        Assert.IsType<MongoFieldExpression>(projection.Expression);
    }

    [Fact]
    public void Bare_AsEnumerable_over_string_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, IEnumerable<char>>> selector = c => c.City.AsEnumerable();

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.IsType<MongoFieldExpression>(projection.Expression);

        // The BARE-body spelling must set the provenance flag too — it reaches the binder through
        // TryBindAsBareProjection rather than the wrapped-member loop.
        Assert.True(mongoQ.Select.HasStringSequenceProjectionLeaf);
    }

    [Fact]
    public void AsEnumerable_over_a_non_string_source_is_not_recognized_by_this_leaf()
    {
        Expression<Func<Customer, int[]>> selector = c => c.Numbers.ToArray();
        var call = (MethodCallExpression)selector.Body;

        Assert.False(NativeProjectionBinder.IsStringSequenceMaterializationCall(call));
    }
}
