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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="NativeDateTimeKindReadBack.HasUnreproducibleReadBack"/> fails closed: a DateTime-typed node it has no
/// arm for counts as kind-sensitive, so a future DateTime-producing node declines rather than silently reading back
/// Kind=Utc. The functional suite can't reach this (every node producing a DateTime today has an arm), so it is
/// pinned here with a synthetic node.
/// </summary>
public class NativeDateTimeKindReadBackTests
{
    private class Row
    {
        public ObjectId Id { get; set; }
        public DateTime LocalDate { get; set; }
        public DateTime PlainDate { get; set; }
    }

    private static IProperty Property(string name)
    {
        using var db = SingleEntityDbContext.Create<Row>(mb =>
            mb.Entity<Row>().Property(r => r.LocalDate).HasDateTimeKind(DateTimeKind.Local));
        return db.Model.FindEntityType(typeof(Row))!.FindProperty(name)!;
    }

    private static bool Declines(MongoExpression leaf)
    {
        var select = new MongoSelectDefinition();
        select.AddProjection(new MongoProjection("D", leaf));
        return NativeDateTimeKindReadBack.HasUnreproducibleReadBack(select);
    }

    private static MongoFieldExpression Field(string name) => new(Property(name), name);

    [Fact]
    public void Plain_local_kind_field_is_reproducible()
        => Assert.False(Declines(Field(nameof(Row.LocalDate))));

    [Fact]
    public void Known_computed_node_over_a_local_kind_field_declines_and_over_a_default_field_does_not()
    {
        Assert.True(Declines(new MongoDateAddExpression(
            Field(nameof(Row.LocalDate)), MongoDateAddUnit.Day, new MongoConstantExpression(1, null))));
        Assert.False(Declines(new MongoDateAddExpression(
            Field(nameof(Row.PlainDate)), MongoDateAddUnit.Day, new MongoConstantExpression(1, null))));
    }

    [Fact]
    public void Unknown_DateTime_typed_node_fails_closed()
        // MongoMathExpression never produces a DateTime today; typed as one it stands in for a node kind the walk has
        // no arm for. It declines even over a default-kind field: the walk can't tell what it reads.
        => Assert.True(Declines(new MongoMathExpression(
            MongoMathFunction.Max, [Field(nameof(Row.PlainDate))], typeof(DateTime))));

    [Fact]
    public void Untraceable_DateTime_element_reference_fails_closed()
        => Assert.True(Declines(new MongoElementRefExpression("somewhere.D", typeof(DateTime))));

    // A whole composite g.Key read through its type's class map: NativeGroupByBinder rebuilds it as a construction when
    // a part is kind-sensitive, so a surviving class-map "_id" read over such a part must decline.
    [Theory]
    [InlineData(nameof(Row.LocalDate), true)]
    [InlineData(nameof(Row.PlainDate), false)]
    public void Class_map_read_of_a_composite_key_declines_only_over_a_kind_sensitive_part(string dateProperty, bool declines)
    {
        var select = new MongoSelectDefinition();
        select.Grouping = new MongoGrouping(
            [new MongoGroupingKeyPart("Id", Field(nameof(Row.Id))), new MongoGroupingKeyPart("D", Field(dateProperty))],
            []);
        select.AddProjection(new MongoProjection("Key", new MongoElementRefExpression("_id", typeof(Row))));
        Assert.Equal(declines, NativeDateTimeKindReadBack.HasUnreproducibleReadBack(select));
    }

    [Fact]
    public void Element_reference_carrying_its_value_property_is_classified_by_that_property()
    {
        Assert.False(Declines(new MongoElementRefExpression(
            "_lookup_X.D", typeof(DateTime), valueProperty: Property(nameof(Row.PlainDate)))));
        Assert.False(Declines(new MongoElementRefExpression(
            "_lookup_X.D", typeof(DateTime), valueProperty: Property(nameof(Row.LocalDate)))));
    }
}
