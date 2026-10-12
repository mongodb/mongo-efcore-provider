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
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class NativeProjectionBinderConditionalAndDateTimeTests
{
    private class Row
    {
        public ObjectId Id { get; set; }
        public bool Flag { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
        public List<Item> Items { get; set; } = null!;
    }

    private class Item
    {
        public int Quantity { get; set; }
    }

    private class RowDto
    {
        public ObjectId Id { get; set; }
        public DateTime? DT { get; set; }
        public DateTime? UtcDT { get; set; }
    }

    private static readonly Action<ModelBuilder> ModelWithOwnedCollection =
        mb => mb.Entity<Row>().OwnsMany(r => r.Items);

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Row>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Row))!);
    }

    private static MongoQueryExpression TestQueryWithOwnedCollection()
    {
        using var db = SingleEntityDbContext.Create<Row>(ModelWithOwnedCollection);
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Row))!);
    }

    [Fact]
    public void Conditional_wrapped_projection_leaf_is_admitted()
    {
        var mongoQ = TestQuery();
        Expression<Func<Row, RowDto>> selector = r => new RowDto
        {
            Id = r.Id,
            DT = r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date
        };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var dtProjection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "DT");
        Assert.IsType<MongoConditionalExpression>(dtProjection.Expression);
    }

    [Fact]
    public void Conditional_bare_projection_leaf_is_admitted()
    {
        var mongoQ = TestQuery();
        Expression<Func<Row, DateTime?>> selector =
            r => r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.IsType<MongoConditionalExpression>(projection.Expression);
    }

    [Fact]
    public void UtcDateTime_wrapped_projection_leaf_is_admitted()
    {
        // .UtcDateTime as a wrapped leaf translates to a MongoElementRefExpression over the stored ".DateTime"
        // sub-field (no $dateAdd reconstruction; that's only for local-time variants).
        var mongoQ = TestQuery();
        Expression<Func<Row, RowDto>> selector = r => new RowDto
        {
            Id = r.Id,
            UtcDT = r.OccurredOffset!.Value.UtcDateTime
        };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var utcDtProjection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "UtcDT");
        Assert.IsType<MongoElementRefExpression>(utcDtProjection.Expression);
    }

    [Fact]
    public void Bare_conditional_over_an_owned_collection_count_is_declined_by_the_subtree_check()
    {
        var mongoQ = TestQueryWithOwnedCollection();
        Expression<Func<Row, int>> selector = x => x.Flag ? x.Items.Count : 0;

        // A conditional top node passes the node-kind gate, but its IfTrue is a MongoSizeExpression; a fallback
        // push-down would render a bare $size that errors on a missing/null array. Proves the conditional arm runs
        // IsArrayFreeComputedSubtree.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }
}
