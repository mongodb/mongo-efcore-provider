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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class MongoConditionalAndDatePartExpressionTests
{
    [Fact]
    public void MongoConditionalExpression_type_is_the_IfTrue_branch_type()
    {
        var test = new MongoConstantExpression(true, forSerialization: null);
        var ifTrue = new MongoConstantExpression(1, forSerialization: null);
        var ifFalse = new MongoConstantExpression(2, forSerialization: null);

        var conditional = new MongoConditionalExpression(test, ifTrue, ifFalse);

        Assert.Same(test, conditional.Test);
        Assert.Same(ifTrue, conditional.IfTrue);
        Assert.Same(ifFalse, conditional.IfFalse);
        Assert.Equal(typeof(int), conditional.Type);
    }

    [Fact]
    public void MongoConditionalExpression_type_prefers_IfFalse_type_when_IfTrue_is_a_null_constant()
    {
        // A null MongoConstantExpression's own .Type falls back to typeof(object) (it carries no other type
        // information), so the conditional's overall Type must not simply mirror IfTrue in that case — it
        // should report the meaningful type from IfFalse instead. Live path: MongoSelectLowerer reads a
        // computed sort key's KeySelector.Type, and a conditional can be a computed sort key.
        var test = new MongoConstantExpression(true, forSerialization: null);
        var ifTrue = new MongoConstantExpression(null, forSerialization: null);
        var ifFalse = new MongoConstantExpression(DateTime.UtcNow, forSerialization: null);

        var conditional = new MongoConditionalExpression(test, ifTrue, ifFalse);

        Assert.Equal(typeof(DateTime), conditional.Type);
    }

    // NOTE ON TEST SHAPE: MongoDatePart is internal, and a public [Theory] method cannot expose an internal
    // type in its signature (CS0051) while the test class stays public (required for xUnit discovery).
    // The [MemberData] rows are boxed as `object` here and cast back to `MongoDatePart` inside the method,
    // which keeps the [Theory]/[MemberData] shape intact.
    [Theory]
    [MemberData(nameof(DatePartTestData))]
    public void MongoDatePartExpression_type_matches_the_part(object part, Type expectedType)
    {
        var mongoPart = (MongoDatePart)part;
        var operand = new MongoConstantExpression(DateTime.UtcNow, forSerialization: null);

        var datePart = new MongoDatePartExpression(operand, mongoPart);

        Assert.Same(operand, datePart.Operand);
        Assert.Equal(mongoPart, datePart.Part);
        Assert.Equal(expectedType, datePart.Type);
    }

    public static IEnumerable<object[]> DatePartTestData()
    {
        yield return [MongoDatePart.Year, typeof(int)];
        yield return [MongoDatePart.Month, typeof(int)];
        yield return [MongoDatePart.Day, typeof(int)];
        yield return [MongoDatePart.Hour, typeof(int)];
        yield return [MongoDatePart.Minute, typeof(int)];
        yield return [MongoDatePart.Second, typeof(int)];
        yield return [MongoDatePart.Millisecond, typeof(int)];
        yield return [MongoDatePart.DayOfYear, typeof(int)];
        yield return [MongoDatePart.Date, typeof(DateTime)];
        yield return [MongoDatePart.DayOfWeek, typeof(DayOfWeek)];
    }

    [Fact]
    public void MongoDateTimeOffsetLocalExpression_type_is_DateTime()
    {
        var field = new MongoFieldExpression(property: null!, "DateTimeOffsetField");

        var local = new MongoDateTimeOffsetLocalExpression(field);

        Assert.Same(field, local.Operand);
        Assert.Equal(typeof(DateTime), local.Type);
    }
}
