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
    public void LastOrDefault_equality_against_null_char_literal_translates_to_one_character_string()
    {
        // The '\0' (default(char)) edge — review fix (EF-322 Task 4): the comparison's other side must be
        // the one-character string "\0", NOT an Int32 zero. RenderStringFirstOrLast's own empty-source
        // ("then") branch and its non-empty $substrCP ("else") branch must render the SAME BSON type, because
        // a genuinely non-empty source whose real last character IS '\0' (a legal embedded-NUL string) takes
        // the "else" branch and produces a one-character STRING — mixing an Int32 zero into the "then" branch
        // would make that case's comparison cross BSON type brackets and wrongly answer false. See
        // MongoStringFirstOrLastExpression's own remarks.
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
        // TranslateComparisonCore's char-comparison unwrap/re-express fix applies to EVERY comparison
        // operator the general $expr fallback handles, not just Equal — pin a relational operator too
        // (review coverage gap, EF-322 Task 4).
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
        // The OTHER side's runtime value isn't known at translate time for a genuine EF query PARAMETER
        // (a captured/closed-over char in a real compiled query), so there is nothing to re-express in the
        // one-character-string shape — TranslateComparisonCore must decline outright rather than let the
        // original $toInt-wrapped form (which would crash the server against a non-numeral string) reach
        // render/execution time (review coverage gap, EF-322 Task 4). Built by hand in the exact EF
        // query-parameter node shape (see MongoExpressionTranslatorTests' own Test 16b precedent), including
        // the SAME Convert(_, Int32) widening char-comparison lowering wraps both sides in, rather than a
        // plain C# lambda — a closure-captured local in a hand-built Expression<Func<>> is a closure-class
        // MemberExpression, not the EF query-parameter shape NativeQueryParameter recognizes, so it would
        // decline for an unrelated reason (no query-parameter recognition at all) rather than exercising this
        // method's own OTHER-side-not-constant branch.
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

    [Fact]
    public void Join_over_array_literal_with_null_element_translates_to_ifNull_wrapped_interleaved_concat()
    {
        // EF-322 Task 6: string.Join("|", new[] { c.Text, foo, (string?)null, "bar" }) — a field, a captured
        // local (constant-folded), a literal null, and a string constant. Pins three things: (1) the separator
        // is interleaved BETWEEN each pair of elements, not just prefixed/appended, (2) every element is
        // wrapped in a MongoCoalesceExpression (rendered $ifNull) so the null element contributes "" rather
        // than nulling out the whole $concat (the hazard this task's plan calls out explicitly), and (3, final-
        // review fix — finding 4) the separator itself is ALSO wrapped in a MongoCoalesceExpression the same
        // way, so a null/parameterized-null separator degrades the same way rather than nulling the whole
        // $concat.
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
        // Final-review fix — finding 4: string.Join(null, new[] { "a", "b" }) — a literal null separator.
        // Before the fix, only the ELEMENTS were coalesced against "", so a null separator would null out
        // every interleaved position and the whole $concat via $concat's own null-propagation. The separator
        // operand must be wrapped in a MongoCoalesceExpression exactly like each element.
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
