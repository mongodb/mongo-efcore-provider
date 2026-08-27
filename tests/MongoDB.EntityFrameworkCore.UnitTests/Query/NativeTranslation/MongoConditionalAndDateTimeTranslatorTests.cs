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

public class MongoConditionalAndDateTimeTranslatorTests
{
    private class Row
    {
        public ObjectId Id { get; set; }
        public int Amount { get; set; }
        public bool Flag { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
    }

    private static (MongoExpressionTranslator Translator, Expression Body) BuildValueBody(
        Expression<Func<Row, object?>> valueSelector)
    {
        using var db = SingleEntityDbContext.Create<Row>();
        var entityType = db.Model.FindEntityType(typeof(Row))!;
        var body = valueSelector.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : valueSelector.Body;
        return (new MongoExpressionTranslator(entityType), body);
    }

    /// <summary>
    /// Same as <see cref="BuildValueBody"/>, but lets the caller attach a non-default serialization
    /// (a <c>ValueConverter</c> or <c>[BsonRepresentation]</c>-equivalent) to a <see cref="Row"/> property, so
    /// <c>AllFieldsDefaultSerialized</c>'s guard against a raw MQL operator running over a non-raw-BSON field
    /// can be pinned.
    /// </summary>
    private static (MongoExpressionTranslator Translator, Expression Body) BuildValueBodyWithNonDefaultSerialization(
        Expression<Func<Row, object?>> valueSelector, Action<ModelBuilder> configure)
    {
        using var db = SingleEntityDbContext.Create<Row>(configure);
        var entityType = db.Model.FindEntityType(typeof(Row))!;
        var body = valueSelector.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : valueSelector.Body;
        return (new MongoExpressionTranslator(entityType), body);
    }

    [Fact]
    public void Conditional_with_field_condition_and_constant_branches_translates()
    {
        var (translator, body) = BuildValueBody(r => r.Flag ? 1 : 2);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoFieldExpression>(conditional.Test);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(conditional.IfTrue).Value);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(conditional.IfFalse).Value);
    }

    [Fact]
    public void Conditional_with_null_check_condition_translates()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset == null ? (DateTime?)null : r.Occurred);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        // The Test is a comparison (r.OccurredOffset == null), not a bare field.
        Assert.IsType<MongoBinaryExpression>(conditional.Test);
        // IfTrue is the null-constant branch ((DateTime?)null) — pins TranslateConditionalBranch's null handling.
        Assert.Null(Assert.IsType<MongoConstantExpression>(conditional.IfTrue).Value);
    }

    [Fact]
    public void Conditional_with_nested_conditional_branch_translates()
    {
        var (translator, body) = BuildValueBody(r => r.Flag ? (r.Amount > 0 ? 1 : 2) : 3);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var outer = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoConditionalExpression>(outer.IfTrue);
    }

    [Fact]
    public void Conditional_declines_when_a_branch_is_unsupported()
    {
        // string.Concat has no native translation at all, so a branch that reaches it must decline the
        // WHOLE conditional, not silently drop that branch.
        var (translator, body) = BuildValueBody(r => r.Flag ? r.Amount : int.Parse("x"));

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Plain_DateTime_Year_translates_directly_with_no_offset_reconstruction()
    {
        var (translator, body) = BuildValueBody(r => r.Occurred.Year);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Year, datePart.Part);
        Assert.IsType<MongoFieldExpression>(datePart.Operand);
    }

    [Fact]
    public void DateTimeOffset_Year_wraps_local_time_reconstruction()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.Year);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Year, datePart.Part);
        var local = Assert.IsType<MongoDateTimeOffsetLocalExpression>(datePart.Operand);
        Assert.Equal("OccurredOffset", local.Operand.ElementName);
    }

    [Fact]
    public void DateTimeOffset_Value_DateTime_Date_three_hop_chain_composes()
    {
        // The motivating shape: BuiltInDataTypesMongoTest.Optional_datetime_reading_null_from_database.
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.DateTime.Date);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Date, datePart.Part);
        var local = Assert.IsType<MongoDateTimeOffsetLocalExpression>(datePart.Operand);
        Assert.Equal("OccurredOffset", local.Operand.ElementName);
    }

    [Fact]
    public void DateTimeOffset_UtcDateTime_reads_the_raw_UTC_subfield_with_no_offset_addition()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.UtcDateTime);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var elementRef = Assert.IsType<MongoElementRefExpression>(result);
        Assert.Equal("OccurredOffset.DateTime", elementRef.Path);
    }

    [Fact]
    public void DateTimeOffset_DateTime_alone_returns_the_local_reconstruction_as_a_value()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.DateTime);

        Assert.True(translator.TryTranslateValue(body, out var result));

        Assert.IsType<MongoDateTimeOffsetLocalExpression>(result);
    }

    [Fact]
    public void TimeOfDay_declines()
    {
        var (translator, body) = BuildValueBody(r => (object)r.Occurred.TimeOfDay);

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Conditional_branch_with_a_date_part_translates()
    {
        var (translator, body) = BuildValueBody(
            r => r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoDatePartExpression>(conditional.IfFalse);
    }

    [Fact]
    public void DatePart_over_a_value_converted_DateTime_declines()
    {
        // Occurred carries a ValueConverter here, so a raw $year over the stored (converted) representation
        // would run against a value that is not actually a raw BSON date — AllFieldsDefaultSerialized must
        // catch this via MongoDatePartExpression's operand, not fall into the catch-all.
        var (translator, body) = BuildValueBodyWithNonDefaultSerialization(
            r => r.Occurred.Year,
            mb => mb.Entity<Row>().Property(r => r.Occurred)
                .HasConversion(v => v, v => v));

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void DatePart_over_a_value_converted_DateTimeOffset_declines()
    {
        // Same hazard as above, but through the DateTimeOffset local-time-reconstruction path: the
        // MongoDateTimeOffsetLocalExpression wrapping the field must also be caught by recursing through its
        // operand, not just the outer MongoDatePartExpression.
        var (translator, body) = BuildValueBodyWithNonDefaultSerialization(
            r => r.OccurredOffset!.Value.Year,
            mb => mb.Entity<Row>().Property(r => r.OccurredOffset)
                .HasConversion(v => v, v => v));

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Conditional_branch_over_a_value_converted_field_declines()
    {
        // The MongoConditionalExpression arm added alongside the two above: a branch that is itself a raw
        // non-default-serialized field read must decline the whole conditional too.
        var (translator, body) = BuildValueBodyWithNonDefaultSerialization(
            r => r.Flag ? r.Amount : r.Amount,
            mb => mb.Entity<Row>().Property(r => r.Amount)
                .HasConversion(v => v, v => v));

        Assert.False(translator.TryTranslateValue(body, out _));
    }
}
