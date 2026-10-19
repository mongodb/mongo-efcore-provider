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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <c>a.Equals(b, StringComparison)</c> (instance) and <c>string.Equals(a, b, StringComparison)</c> (static):
/// <see cref="StringComparison.Ordinal"/> is plain equality, <see cref="StringComparison.OrdinalIgnoreCase"/> is an
/// anchored case-insensitive regex against a constant; parameterized, field-to-field and culture comparisons decline.
/// </summary>
public class MongoExpressionTranslatorStringEqualsTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public string Other { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void Equals_Ordinal_is_plain_equality()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals("Seattle", StringComparison.Ordinal);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Instance_Equals_OrdinalIgnoreCase_is_an_exact_case_insensitive_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals("seattle", StringComparison.OrdinalIgnoreCase);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void Static_Equals_OrdinalIgnoreCase_is_an_exact_case_insensitive_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Equals(w.Text, "seattle", StringComparison.OrdinalIgnoreCase);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void Static_Equals_Ordinal_is_plain_equality()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Equals(w.Text, "Seattle", StringComparison.Ordinal);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Field_to_field_OrdinalIgnoreCase_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals(w.Other, StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }

    // A parameter may be null per execution, which has no regex form; driver-LINQ answers it correctly.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parameterized_OrdinalIgnoreCase_declines(bool isStatic)
    {
        var w = Expression.Parameter(typeof(Widget), "w");
#if EF8 || EF9
        Expression term = Expression.Parameter(typeof(string), QueryCompilationContext.QueryParameterPrefix + "p_0");
#else
        Expression term = new QueryParameterExpression("__p_0", typeof(string));
#endif
        var text = Expression.Property(w, nameof(Widget.Text));
        var ignoreCase = Expression.Constant(StringComparison.OrdinalIgnoreCase);
        var body = isStatic
            ? Expression.Call(
                typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!,
                text, term, ignoreCase)
            : Expression.Call(
                text, typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(StringComparison)])!,
                term, ignoreCase);

        Assert.False(BuildTranslator().TryTranslate(body, out _));
    }

    [Fact]
    public void Null_constant_OrdinalIgnoreCase_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals(null, StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void Culture_comparisons_decline(StringComparison comparison)
    {
        var w = Expression.Parameter(typeof(Widget), "w");
        var body = Expression.Call(
            Expression.Property(w, nameof(Widget.Text)),
            typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(StringComparison)])!,
            Expression.Constant("x"), Expression.Constant(comparison));

        Assert.False(BuildTranslator().TryTranslate(body, out _));
    }
}
