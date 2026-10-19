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

/// <summary>
/// The <see cref="StringComparison"/> overload of <c>string.StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>:
/// <see cref="StringComparison.Ordinal"/> and <see cref="StringComparison.OrdinalIgnoreCase"/> translate natively;
/// culture-based members decline.
/// </summary>
public class MongoExpressionTranslatorRegexTests
{
    private class Entity
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Entity>();
        var entityType = db.Model.FindEntityType(typeof(Entity))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void Contains_with_StringComparison_OrdinalIgnoreCase_sets_CaseInsensitive()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.Contains("eattl", StringComparison.OrdinalIgnoreCase);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Contains, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void Contains_with_StringComparison_Ordinal_does_not_set_CaseInsensitive()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.Contains("eattl", StringComparison.Ordinal);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Contains, regex.Kind);
        Assert.False(regex.CaseInsensitive);
    }

    [Fact]
    public void StartsWith_with_StringComparison_Ordinal_translates_to_regex_expression()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.StartsWith("Se", StringComparison.Ordinal);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
        Assert.False(regex.CaseInsensitive);
    }

    [Fact]
    public void StartsWith_with_StringComparison_OrdinalIgnoreCase_translates_to_regex_expression()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.StartsWith("Se", StringComparison.OrdinalIgnoreCase);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void EndsWith_with_StringComparison_Ordinal_translates_to_regex_expression()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.EndsWith("le", StringComparison.Ordinal);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.EndsWith, regex.Kind);
        Assert.False(regex.CaseInsensitive);
    }

    [Fact]
    public void EndsWith_with_StringComparison_OrdinalIgnoreCase_translates_to_regex_expression()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => e.Text.EndsWith("LE", StringComparison.OrdinalIgnoreCase);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.EndsWith, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void Contains_with_StringComparison_culture_based_declines(StringComparison comparison)
    {
        var translator = BuildTranslator();
        var call = Expression.Call(
            Expression.Property(Expression.Parameter(typeof(Entity), "e"), nameof(Entity.Text)),
            typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!,
            Expression.Constant("eattl"),
            Expression.Constant(comparison));

        Assert.False(translator.TryTranslate(call, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void StartsWith_with_StringComparison_culture_based_declines(StringComparison comparison)
    {
        var translator = BuildTranslator();
        var call = Expression.Call(
            Expression.Property(Expression.Parameter(typeof(Entity), "e"), nameof(Entity.Text)),
            typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string), typeof(StringComparison)])!,
            Expression.Constant("Se"),
            Expression.Constant(comparison));

        Assert.False(translator.TryTranslate(call, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void EndsWith_with_StringComparison_culture_based_declines(StringComparison comparison)
    {
        var translator = BuildTranslator();
        var call = Expression.Call(
            Expression.Property(Expression.Parameter(typeof(Entity), "e"), nameof(Entity.Text)),
            typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string), typeof(StringComparison)])!,
            Expression.Constant("le"),
            Expression.Constant(comparison));

        Assert.False(translator.TryTranslate(call, out var result));
        Assert.Null(result);
    }
}
