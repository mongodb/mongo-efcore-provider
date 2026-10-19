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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoAggregationExpressionRendererDatePartTests
{
    // Mirrors MongoAggregationExpressionRendererTests' own Customer/GetProperty<T> pattern exactly, so a
    // MongoFieldExpression built here carries a real IProperty rather than a null double — some renderer
    // paths (e.g. AllFieldsDefaultSerialized, reached transitively from CanRender for other node kinds in
    // this same file) do read Property, so a null double would throw a NullReferenceException instead of the
    // renderer's own exception type, masking the actual thing under test.
    private class Row
    {
        public ObjectId Id { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset OccurredOffset { get; set; }
    }

    private static IProperty GetProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Row>();
        return db.Model.FindEntityType(typeof(Row))!.FindProperty(propertyName)!;
    }

    private static MongoFieldExpression Field(string propertyName, string elementName)
        => new(GetProperty(propertyName), elementName);

    [Fact]
    public void Conditional_renders_as_cond()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$cond", new BsonDocument { { "if", true }, { "then", 1 }, { "else", 2 } }),
            rendered);
    }

    [Fact]
    public void DateTimeOffsetLocal_renders_as_dateAdd_of_DateTime_and_Offset_subfields()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoDateTimeOffsetLocalExpression(Field("OccurredOffset", "Occurred"));

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$dateAdd", new BsonDocument
            {
                { "startDate", "$Occurred.DateTime" },
                { "unit", "minute" },
                { "amount", "$Occurred.Offset" }
            }),
            rendered);
    }

    // NOTE ON TEST SHAPE: MongoDatePart is internal, and a public [Theory] method cannot expose an internal
    // type in its signature (CS0051) while the test class stays public (required for xUnit discovery). Rows
    // are supplied via [MemberData], boxing the actual MongoDatePart enum value as object — the identical
    // idiom already established in MongoConditionalAndDatePartExpressionTests.cs's DatePartTestData() and in
    // MongoAggregationExpressionRendererTests.cs for MongoBinaryOperator (also internal) — rather than
    // round-tripping through a raw int, which would have no compile-time/attribute-level connection to
    // MongoDatePart and no safety net against an out-of-range value.
    [Theory]
    [MemberData(nameof(DatePartOperatorTestData))]
    public void DatePart_renders_as_the_matching_operator(object part, string operatorName)
    {
        var mongoPart = (MongoDatePart)part;
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), mongoPart);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(new BsonDocument(operatorName, "$Occurred"), rendered);
    }

    public static IEnumerable<object[]> DatePartOperatorTestData()
    {
        yield return [MongoDatePart.Year, "$year"];
        yield return [MongoDatePart.Month, "$month"];
        yield return [MongoDatePart.Day, "$dayOfMonth"];
        yield return [MongoDatePart.Hour, "$hour"];
        yield return [MongoDatePart.Minute, "$minute"];
        yield return [MongoDatePart.Second, "$second"];
        yield return [MongoDatePart.Millisecond, "$millisecond"];
        yield return [MongoDatePart.DayOfYear, "$dayOfYear"];
    }

    [Fact]
    public void DatePart_DayOfWeek_subtracts_one_to_match_dotnet_numbering()
    {
        // MongoDB's $dayOfWeek returns 1 (Sunday)..7 (Saturday); .NET's DayOfWeek is 0 (Sunday)..6 (Saturday).
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.DayOfWeek);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$subtract", new BsonArray { new BsonDocument("$dayOfWeek", "$Occurred"), 1 }),
            rendered);
    }

    [Fact]
    public void DatePart_Date_renders_as_dateTrunc_day()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.Date);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$Occurred" }, { "unit", "day" } }),
            rendered);
    }

    [Fact]
    public void CanRender_is_true_for_all_three_new_node_kinds()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));
        var local = new MongoDateTimeOffsetLocalExpression(Field("OccurredOffset", "Occurred"));
        var datePart = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.Year);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(conditional));
        Assert.True(MongoAggregationExpressionRenderer.CanRender(local));
        Assert.True(MongoAggregationExpressionRenderer.CanRender(datePart));
    }
}
