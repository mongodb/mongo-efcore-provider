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
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorStringCompareTests
{
    private class Widget
    {
        public int Id { get; set; }

        // The UnitTests project doesn't enable NRT, so an unannotated string is nullable to EF; [Required] makes
        // Text non-nullable so the fold-safe (relational fold) vs $cmp (nullable) distinction is exercised.
        [Required]
        public string Text { get; set; } = "";

        public string? Maybe { get; set; }
    }

    private static MongoExpressionTranslator BuildTranslator(Action<Microsoft.EntityFrameworkCore.ModelBuilder>? configure = null)
    {
        using var db = SingleEntityDbContext.Create<Widget>(configure);
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void CompareTo_eq_1_on_required_field_with_constant_folds_to_greater_than()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.CompareTo("Seattle") == 1;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.GreaterThan, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Constant_on_left_is_mirrored()
    {
        Expression<Func<Widget, bool>> predicate = w => -1 == w.Text.CompareTo("Seattle");

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.LessThan, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    // Call on the right with an asymmetric operator: `1 <= cmp` is `cmp >= 1`, i.e. `a > b`; `0 >= cmp` is
    // `cmp <= 0`, i.e. `a <= b`.
    [Fact]
    public void Call_on_right_with_asymmetric_operator_is_mirrored_before_folding()
    {
        Expression<Func<Widget, bool>> greater = w => 1 <= w.Text.CompareTo("x");
        Assert.True(BuildTranslator().TryTranslate(greater.Body, out var result));
        Assert.Equal(MongoBinaryOperator.GreaterThan, Assert.IsType<MongoBinaryExpression>(result).Operator);

        Expression<Func<Widget, bool>> lessOrEqual = w => 0 >= w.Text.CompareTo("x");
        Assert.True(BuildTranslator().TryTranslate(lessOrEqual.Body, out result));
        Assert.Equal(MongoBinaryOperator.LessThanOrEqual, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Compare_eq_42_folds_to_false()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Text, "Seattle") == 42;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(false, Assert.IsType<MongoConstantExpression>(result).Value);
    }

    [Fact]
    public void Nullable_receiver_uses_cmp()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Maybe, "a") == -1;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.IsType<MongoStringCompareExpression>(Assert.IsType<MongoBinaryExpression>(result).Left);
    }

    [Fact]
    public void Compare_with_StringComparison_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Text, "a", StringComparison.Ordinal) == 0;

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }

    // $cmp over converted storage compares the converted BSON values, not the CLR strings, so it is not exact
    // either; a relational fold over a converted field is excluded by IsFoldSafe (see the "why" note there), and
    // the $cmp fallback declines too via AllFieldsDefaultSerialized. The whole comparison must decline.
    [Fact]
    public void Value_converted_required_field_declines_instead_of_using_an_inexact_cmp()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.CompareTo("Seattle") == 1;

        var translator = BuildTranslator(
            mb => mb.Entity<Widget>().Property(w => w.Text)
                .HasConversion(new ValueConverter<string, string>(v => v, v => v)));

        Assert.False(translator.TryTranslate(predicate.Body, out _));
    }
}
