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
using System.Text.RegularExpressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-322 (Task 5): the REVERSED-argument shape of <see cref="Regex.IsMatch(string, string)"/> — a
/// compile-time-constant input tested against a document-field-valued pattern (e.g.
/// <c>Regex.IsMatch("Seattle", e.Text)</c>). The forward shape (field input, constant pattern) is untouched by
/// this plan — see <c>MongoExpressionTranslatorRegexTests</c> for the sibling StartsWith/EndsWith/Contains
/// coverage this mirrors.
/// </summary>
public class MongoExpressionTranslatorRegexIsMatchTests
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
    public void Constant_input_against_field_pattern_translates_to_IsMatch_regex_expression()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => Regex.IsMatch("Seattle", e.Text);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.IsMatch, regex.Kind);
        Assert.False(regex.CaseInsensitive);
        Assert.False(regex.Negated);

        var fieldNode = Assert.IsType<MongoFieldExpression>(regex.Field);
        Assert.Equal(nameof(Entity.Text), fieldNode.ElementName);

        var termNode = Assert.IsType<MongoConstantExpression>(regex.Term);
        Assert.Equal("Seattle", termNode.Value);
    }

    [Fact]
    public void Constant_input_against_field_pattern_with_IgnoreCase_sets_CaseInsensitive()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => Regex.IsMatch("seattle", e.Text, RegexOptions.IgnoreCase);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.IsMatch, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void Constant_input_against_field_pattern_with_None_does_not_set_CaseInsensitive()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => Regex.IsMatch("Seattle", e.Text, RegexOptions.None);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.False(regex.CaseInsensitive);
    }

    [Theory]
    [InlineData(RegexOptions.Multiline)]
    [InlineData(RegexOptions.Singleline)]
    [InlineData(RegexOptions.CultureInvariant)]
    [InlineData(RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    public void Constant_input_against_field_pattern_with_unsupported_options_declines(RegexOptions options)
    {
        var translator = BuildTranslator();
        var call = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)])!,
            Expression.Constant("Seattle"),
            Expression.Property(Expression.Parameter(typeof(Entity), "e"), nameof(Entity.Text)),
            Expression.Constant(options));

        Assert.False(translator.TryTranslate(call, out var result));
        Assert.Null(result);
    }

    // Forward shape (field input, constant pattern) is a DISTINCT, pre-existing shape this plan does not
    // touch — it must keep declining here (it goes native nowhere in this codebase; it currently succeeds
    // only via the driver-LINQ fallback, which this translator-level test cannot exercise).
    [Fact]
    public void Field_input_against_constant_pattern_forward_shape_still_declines()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => Regex.IsMatch(e.Text, "^S");

        Assert.False(translator.TryTranslate(predicate.Body, out var result));
        Assert.Null(result);
    }

    // Both operands computed/non-constant (no fixed input, no resolvable field pattern) declines.
    [Fact]
    public void Non_constant_input_declines()
    {
        var translator = BuildTranslator();
        Expression<Func<Entity, bool>> predicate = e => Regex.IsMatch(e.Text + "x", e.Text);

        Assert.False(translator.TryTranslate(predicate.Body, out var result));
        Assert.Null(result);
    }
}
