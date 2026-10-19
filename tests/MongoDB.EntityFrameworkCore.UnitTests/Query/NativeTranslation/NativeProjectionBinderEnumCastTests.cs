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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// An enum-to-enum CAST projection leaf — <c>(TargetEnum)c.SourceEnum</c> — has no <c>$toX</c> form
/// (<see cref="MongoConvertExpression.ToOperatorFor"/> only maps <c>int</c>/<c>long</c>/<c>double</c>/
/// <c>decimal</c>), so it used to decline the WHOLE projecting <c>Select</c> outright (the closing gap behind
/// <c>BuiltInDataTypesMongoTest.Can_filter_projection_with_captured_enum_variable</c> /
/// <c>_inline_enum_variable</c>). It needs no server-side computation at all — the cast only changes the
/// leaf's DECLARED CLR type, never the stored value — so <see cref="NativeProjectionBinder.TryTranslateLeaf"/>
/// now admits it as a bare field leaf (the cast dropped entirely), same as an uncast member access.
/// </summary>
public class NativeProjectionBinderEnumCastTests
{
    private enum SourceStatus
    {
        Active = 0,
        Inactive = 1
    }

    private enum TargetStatusDto
    {
        Active = 0,
        Inactive = 1
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public SourceStatus Status { get; set; }
    }

    private class OrderDto
    {
        public ObjectId Id { get; set; }
        public TargetStatusDto Status { get; set; }
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Order>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Order))!);
    }

    [Fact]
    public void Enum_to_enum_cast_wrapped_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, OrderDto>> selector =
            o => new OrderDto { Id = o.Id, Status = (TargetStatusDto)o.Status };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var statusProjection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "Status");
        var field = Assert.IsType<MongoFieldExpression>(statusProjection.Expression);
        Assert.Equal("Status", field.ElementName);
    }

    [Fact]
    public void Enum_to_enum_cast_bare_leaf_is_admitted_as_a_bare_field_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, TargetStatusDto>> selector = o => (TargetStatusDto)o.Status;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.IsType<MongoFieldExpression>(projection.Expression);
    }
}
