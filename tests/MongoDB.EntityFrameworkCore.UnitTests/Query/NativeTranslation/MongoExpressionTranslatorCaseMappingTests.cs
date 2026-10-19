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

public class MongoExpressionTranslatorCaseMappingTests
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
    public void ToLower_eq_lowercase_constant_is_exact_case_insensitive_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToLower() == "seattle";

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
        Assert.True(regex.CaseInsensitive);
        Assert.False(regex.Negated);
        Assert.Equal("seattle", Assert.IsType<MongoConstantExpression>(regex.Term).Value);
    }

    [Fact]
    public void ToLower_eq_constant_with_uppercase_folds_to_false()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToLower() == "Seattle";

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(false, Assert.IsType<MongoConstantExpression>(result).Value);
    }

    [Fact]
    public void ToUpper_ne_constant_with_lowercase_folds_to_true()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToUpper() != "Seattle";

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(true, Assert.IsType<MongoConstantExpression>(result).Value);
    }

    [Fact]
    public void Fold_uses_the_invariant_mapping_for_non_ascii()
    {
        Expression<Func<Widget, bool>> lower = w => w.Text.ToLower() == "école";
        Expression<Func<Widget, bool>> notLower = w => w.Text.ToLowerInvariant() == "École";

        Assert.IsType<MongoRegexExpression>(Translate(lower));
        Assert.Equal(false, Assert.IsType<MongoConstantExpression>(Translate(notLower)).Value);
    }

    [Fact]
    public void ToUpper_ne_uppercase_constant_is_negated_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => "SEATTLE" != w.Text.ToUpper();

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.True(regex.Negated);
        Assert.True(regex.CaseInsensitive);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
    }

    [Fact]
    public void ToUpperInvariant_eq_uppercase_constant_is_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToUpperInvariant() == "SEATTLE";

        Assert.IsType<MongoRegexExpression>(Translate(predicate));
    }

    [Fact]
    public void ToLower_as_a_value_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.ToLower();

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void ToLower_over_a_computed_receiver_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Trim().ToLower() == "seattle";

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }

    private static MongoExpression? Translate(Expression<Func<Widget, bool>> predicate)
    {
        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        return result;
    }
}
