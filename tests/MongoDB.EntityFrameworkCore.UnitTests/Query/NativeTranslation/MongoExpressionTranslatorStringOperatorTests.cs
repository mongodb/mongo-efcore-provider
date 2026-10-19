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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Translation of string <c>FirstOrDefault()</c>/<c>LastOrDefault()</c> and <c>string.Join</c>; see
/// <see cref="MongoStringFirstOrLastExpression"/> for the empty-string contract.
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
        // C# lowers char equality to int (`Convert(x.FirstOrDefault(), Int32) == 83`); the translator must
        // compare one-character strings instead, since $toInt on a non-numeral string is a server error.
        var translator = BuildTranslator();
        Expression<Func<Widget, bool>> pred = w => w.Text.FirstOrDefault() == 'S';

        Assert.True(translator.TryTranslate(pred.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.IsType<MongoStringFirstOrLastExpression>(binary.Left);
        var rightConstant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal("S", rightConstant.Value);
    }

    [Fact]
    public void LastOrDefault_equality_against_null_char_literal_translates_to_one_character_string()
    {
        // '\0' must compare as the string "\0", not Int32 zero: a string ending in an embedded NUL yields a
        // one-character string, and comparing it against an int would cross BSON types and answer false.
        var translator = BuildTranslator();
        Expression<Func<Widget, bool>> pred = w => w.Text.LastOrDefault() == '\0';

        Assert.True(translator.TryTranslate(pred.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.IsType<MongoStringFirstOrLastExpression>(binary.Left);
        var rightConstant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal("\0", rightConstant.Value);
    }

    [Fact]
    public void FirstOrDefault_relational_comparison_against_char_literal_translates()
    {
        // The char-comparison rewrite applies to relational operators too, not just Equal.
        var translator = BuildTranslator();
        Expression<Func<Widget, bool>> pred = w => w.Text.FirstOrDefault() > 'a';

        Assert.True(translator.TryTranslate(pred.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, binary.Operator);
        Assert.IsType<MongoStringFirstOrLastExpression>(binary.Left);
        var rightConstant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal("a", rightConstant.Value);
    }

    [Fact]
    public void FirstOrDefault_equality_against_a_parameterized_char_declines()
    {
        // A query-parameter char can't be re-expressed as a string at translate time, so this must decline
        // rather than emit the $toInt form. Built by hand in EF's query-parameter node shape: a lambda-captured
        // local would be a closure MemberExpression and decline for an unrelated reason.
        var wParam = Expression.Parameter(typeof(Widget), "w");
        var firstOrDefaultCall = Expression.Call(
            typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.FirstOrDefault) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(char)),
            Expression.Property(wParam, nameof(Widget.Text)));

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "target_0";
        Expression queryParam = Expression.Parameter(typeof(char), paramName);
#else
        const string paramName = "__target_0";
        Expression queryParam = new QueryParameterExpression(paramName, typeof(char));
#endif

        var predicate = Expression.Equal(
            Expression.Convert(firstOrDefaultCall, typeof(int)), Expression.Convert(queryParam, typeof(int)));

        var translator = BuildTranslator();
        Assert.False(translator.TryTranslate(predicate, out _));
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
        // A char[] receiver must not be misrecognized as the string shape.
        var call = Expression.Call(
            typeof(Enumerable).GetMethods()
                .Single(m => m.Name == nameof(Enumerable.FirstOrDefault) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(char)),
            Expression.Constant(Array.Empty<char>()));

        var translator = BuildTranslator();
        Assert.False(translator.TryTranslateValue(call, out _));
    }

    [Fact]
    public void Join_over_array_literal_with_null_element_translates_to_ifNull_wrapped_interleaved_concat()
    {
        // string.Join("|", new[] { w.Text, "foo", null, "bar" }): the separator is interleaved between elements,
        // and every element and separator is $ifNull-wrapped so a null contributes "" instead of nulling the
        // whole $concat.
        var translator = BuildTranslator();
        var parameter = Expression.Parameter(typeof(Widget), "w");
        var textAccess = Expression.Property(parameter, nameof(Widget.Text));

        var joinCall = Expression.Call(
            null,
            typeof(string).GetMethod(nameof(string.Join), [typeof(string), typeof(string[])])!,
            Expression.Constant("|"),
            Expression.NewArrayInit(
                typeof(string),
                textAccess,
                Expression.Constant("foo"),
                Expression.Constant(null, typeof(string)),
                Expression.Constant("bar")));

        Assert.True(translator.TryTranslateValue(joinCall, out var result));
        var concat = Assert.IsType<MongoConcatExpression>(result);

        // 4 elements + 3 interleaved separators = 7 operands.
        Assert.Equal(7, concat.Operands.Count);

        var expectedElementIndexes = new[] { 0, 2, 4, 6 };
        var expectedSeparatorIndexes = new[] { 1, 3, 5 };

        foreach (var i in expectedSeparatorIndexes)
        {
            var separatorCoalesce = Assert.IsType<MongoCoalesceExpression>(concat.Operands[i]);
            var separatorFallback = Assert.IsType<MongoConstantExpression>(separatorCoalesce.Right);
            Assert.Equal(string.Empty, separatorFallback.Value);
            var separator = Assert.IsType<MongoConstantExpression>(separatorCoalesce.Left);
            Assert.Equal("|", separator.Value);
        }

        foreach (var i in expectedElementIndexes)
        {
            var coalesce = Assert.IsType<MongoCoalesceExpression>(concat.Operands[i]);
            var fallback = Assert.IsType<MongoConstantExpression>(coalesce.Right);
            Assert.Equal(string.Empty, fallback.Value);
        }

        Assert.IsType<MongoFieldExpression>(((MongoCoalesceExpression)concat.Operands[0]).Left);
        Assert.Equal("foo", ((MongoConstantExpression)((MongoCoalesceExpression)concat.Operands[2]).Left).Value);
        Assert.Null(((MongoConstantExpression)((MongoCoalesceExpression)concat.Operands[4]).Left).Value);
        Assert.Equal("bar", ((MongoConstantExpression)((MongoCoalesceExpression)concat.Operands[6]).Left).Value);
    }

    [Fact]
    public void Join_with_null_separator_wraps_separator_in_ifNull_too()
    {
        // A null separator must be coalesced like the elements, or $concat's null-propagation nulls the result.
        var translator = BuildTranslator();
        var joinCall = Expression.Call(
            null,
            typeof(string).GetMethod(nameof(string.Join), [typeof(string), typeof(string[])])!,
            Expression.Constant(null, typeof(string)),
            Expression.NewArrayInit(typeof(string), Expression.Constant("a"), Expression.Constant("b")));

        Assert.True(translator.TryTranslateValue(joinCall, out var result));
        var concat = Assert.IsType<MongoConcatExpression>(result);

        // 2 elements + 1 interleaved separator = 3 operands; the separator is operand index 1.
        Assert.Equal(3, concat.Operands.Count);
        var separatorCoalesce = Assert.IsType<MongoCoalesceExpression>(concat.Operands[1]);
        var separatorFallback = Assert.IsType<MongoConstantExpression>(separatorCoalesce.Right);
        Assert.Equal(string.Empty, separatorFallback.Value);
        var separator = Assert.IsType<MongoConstantExpression>(separatorCoalesce.Left);
        Assert.Null(separator.Value);
    }

    [Fact]
    public void Join_over_a_parameterized_runtime_array_declines()
    {
        // No fixed arity to interleave a separator into at translate time — must decline, not guess.
        var translator = BuildTranslator();
        var arrayParam = Expression.Parameter(typeof(string[]), "elements");
        var joinCall = Expression.Call(
            null,
            typeof(string).GetMethod(nameof(string.Join), [typeof(string), typeof(string[])])!,
            Expression.Constant("|"),
            arrayParam);

        Assert.False(translator.TryTranslateValue(joinCall, out _));
    }

    [Fact]
    public void Join_over_empty_array_literal_translates_to_empty_string_constant()
    {
        var translator = BuildTranslator();
        var joinCall = Expression.Call(
            null,
            typeof(string).GetMethod(nameof(string.Join), [typeof(string), typeof(string[])])!,
            Expression.Constant("|"),
            Expression.NewArrayInit(typeof(string)));

        Assert.True(translator.TryTranslateValue(joinCall, out var result));
        var constant = Assert.IsType<MongoConstantExpression>(result);
        Assert.Equal(string.Empty, constant.Value);
    }
}
