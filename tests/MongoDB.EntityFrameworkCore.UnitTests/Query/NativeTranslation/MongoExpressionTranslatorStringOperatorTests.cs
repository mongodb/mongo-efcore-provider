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
using System.Linq;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c> (EF-322 Task 4) — see
/// <see cref="MongoStringFirstOrLastExpression"/>'s own remarks for the empty-string contract this pins.
/// </summary>
public class MongoExpressionTranslatorStringOperatorTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void FirstOrDefault_equality_against_char_literal_translates_without_toInt_wrap()
    {
        // C# lowers char equality to int (`Convert(x.FirstOrDefault(), Int32) == 83`) — this pins that the
        // translator unwraps that Convert and re-expresses the literal side as the matching one-character
        // string, rather than emitting a $toInt that would crash the server against a non-numeral string
        // (see TranslateComparisonCore's own remarks and MongoStringFirstOrLastExpression's remarks).
        var translator = BuildTranslator();
        Expression<Func<Widget, bool>> pred = w => w.Text.FirstOrDefault() == 'S';

        Assert.True(translator.TryTranslate(pred.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.IsType<MongoStringFirstOrLastExpression>(binary.Left);
        var rightConstant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal("S", rightConstant.Value);
    }

    [Fact]
    public void LastOrDefault_equality_against_null_char_literal_translates_to_int_zero()
    {
        // The '\0' (default(char)) edge: RenderStringFirstOrLast's own empty-source branch renders as the
        // Int32 literal 0, so the comparison's other side must match that shape, not a one-character string.
        var translator = BuildTranslator();
        Expression<Func<Widget, bool>> pred = w => w.Text.LastOrDefault() == '\0';

        Assert.True(translator.TryTranslate(pred.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.IsType<MongoStringFirstOrLastExpression>(binary.Left);
        var rightConstant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal(0, rightConstant.Value);
    }

    [Fact]
    public void FirstOrDefault_over_string_translates_to_a_char_typed_expression()
    {
        var parameter = Expression.Parameter(typeof(Widget), "e");
        var call = Expression.Call(
            typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.FirstOrDefault) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(char)),
            Expression.Property(parameter, nameof(Widget.Text)));

        var translator = BuildTranslator();
        Assert.True(translator.TryTranslateValue(call, out var result));
        Assert.Equal(typeof(char), result!.Type);
        var firstOrLast = Assert.IsType<MongoStringFirstOrLastExpression>(result);
        Assert.Equal(MongoStringFirstOrLastKind.First, firstOrLast.Kind);
    }

    [Fact]
    public void LastOrDefault_over_string_translates_to_a_char_typed_expression()
    {
        var parameter = Expression.Parameter(typeof(Widget), "e");
        var call = Expression.Call(
            typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.LastOrDefault) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(char)),
            Expression.Property(parameter, nameof(Widget.Text)));

        var translator = BuildTranslator();
        Assert.True(translator.TryTranslateValue(call, out var result));
        Assert.Equal(typeof(char), result!.Type);
        var firstOrLast = Assert.IsType<MongoStringFirstOrLastExpression>(result);
        Assert.Equal(MongoStringFirstOrLastKind.Last, firstOrLast.Kind);
    }

    [Fact]
    public void FirstOrDefault_over_string_via_lambda_translates()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, char>> selector = w => w.Text.FirstOrDefault();

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var firstOrLast = Assert.IsType<MongoStringFirstOrLastExpression>(result);
        Assert.Equal(MongoStringFirstOrLastKind.First, firstOrLast.Kind);
        Assert.IsType<MongoFieldExpression>(firstOrLast.Source);
    }

    [Fact]
    public void LastOrDefault_over_string_via_lambda_translates()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, char>> selector = w => w.Text.LastOrDefault();

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var firstOrLast = Assert.IsType<MongoStringFirstOrLastExpression>(result);
        Assert.Equal(MongoStringFirstOrLastKind.Last, firstOrLast.Kind);
        Assert.IsType<MongoFieldExpression>(firstOrLast.Source);
    }

    [Fact]
    public void FirstOrDefault_with_predicate_declines()
    {
        // The predicated overload has no MQL form here; it must fall through, not be misrecognized.
        var translator = BuildTranslator();
        Expression<Func<Widget, char>> selector = w => w.Text.FirstOrDefault(c => c == 'x');

        Assert.False(translator.TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void FirstOrDefault_over_char_array_receiver_declines()
    {
        // The receiver's static type is char[], not string — TryMatchStringFirstOrLastMethod requires the
        // argument's Type to be exactly System.String, so this must not be misrecognized as the string shape.
        var call = Expression.Call(
            typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.FirstOrDefault) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(char)),
            Expression.Constant(Array.Empty<char>()));

        var translator = BuildTranslator();
        Assert.False(translator.TryTranslateValue(call, out _));
    }
}
