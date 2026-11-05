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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorTrimTests
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
    public void TrimStart_zero_arg_translates_to_MongoTrimExpression_Start_with_null_chars()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.TrimStart();

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Start, trim.Side);
        Assert.Null(trim.Chars);
    }

    [Fact]
    public void TrimEnd_zero_arg_translates_to_MongoTrimExpression_End_with_null_chars()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.TrimEnd();

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.End, trim.Side);
        Assert.Null(trim.Chars);
    }

    [Fact]
    public void Trim_zero_arg_translates_to_MongoTrimExpression_Both_with_null_chars()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.Trim();

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Both, trim.Side);
        Assert.Null(trim.Chars);
    }

    [Fact]
    public void Trim_with_char_argument_translates_chars_to_a_single_char_string_constant()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.Trim('S');

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Both, trim.Side);
        var chars = Assert.IsType<MongoConstantExpression>(trim.Chars);
        Assert.Equal("S", chars.Value);
    }

    [Fact]
    public void Trim_with_char_array_argument_translates_chars_to_a_joined_string_constant()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.Trim('S', 'e');

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Both, trim.Side);
        var chars = Assert.IsType<MongoConstantExpression>(trim.Chars);
        Assert.Equal("Se", chars.Value);
    }

    [Fact]
    public void TrimStart_with_char_array_literal_translates_chars_to_a_joined_string_constant()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.TrimStart(new[] { 'S', 'e' });

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Start, trim.Side);
        var chars = Assert.IsType<MongoConstantExpression>(trim.Chars);
        Assert.Equal("Se", chars.Value);
    }

    [Fact]
    public void Trim_with_computed_char_array_argument_declines()
    {
        // A parameterized/computed char[] operand has no compile-time string to build; TryTranslateTrim must
        // decline rather than guess.
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.Trim(w.Text.ToCharArray());

        Assert.False(translator.TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void TrimEnd_with_char_array_argument_translates_chars_to_a_joined_string_constant()
    {
        var translator = BuildTranslator();
        Expression<Func<Widget, string>> selector = w => w.Text.TrimEnd('S', 'e');

        Assert.True(translator.TryTranslateValue(selector.Body, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.End, trim.Side);
        var chars = Assert.IsType<MongoConstantExpression>(trim.Chars);
        Assert.Equal("Se", chars.Value);
    }
}
