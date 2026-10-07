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
/// <c>string.IsNullOrEmpty</c>/<c>IsNullOrWhiteSpace</c> rewrite to the equivalent <c>== null || == ""</c>
/// (or <c>.Trim() == ""</c>) disjunction and translate through the existing null/empty machinery.
/// </summary>
public class MongoExpressionTranslatorIsNullOrTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string? Text { get; set; }
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void IsNullOrEmpty_translates_to_null_or_empty_disjunction()
    {
        Expression<Func<Widget, bool>> predicate = w => string.IsNullOrEmpty(w.Text);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.OrElse, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void IsNullOrWhiteSpace_uses_native_trim()
    {
        Expression<Func<Widget, bool>> predicate = w => string.IsNullOrWhiteSpace(w.Text);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var or = Assert.IsType<MongoBinaryExpression>(result);
        var trimCompare = Assert.IsType<MongoBinaryExpression>(or.Right);
        Assert.IsType<MongoTrimExpression>(trimCompare.Left);
    }

    [Fact]
    public void IsNullOrEmpty_negated_translates_to_exact_De_Morgan_complement()
    {
        Expression<Func<Widget, bool>> predicate = w => !string.IsNullOrEmpty(w.Text);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var and = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        Assert.Equal(MongoBinaryOperator.NotEqual, Assert.IsType<MongoBinaryExpression>(and.Left).Operator);
        Assert.Equal(MongoBinaryOperator.NotEqual, Assert.IsType<MongoBinaryExpression>(and.Right).Operator);
    }

    [Fact]
    public void IsNullOrWhiteSpace_negated_declines_rather_than_render_wrongly_for_a_missing_field()
    {
        // The Trim() == "" disjunct isn't a query-native comparison (its left side is a MongoTrimExpression, not
        // a bare field), so MongoExpressionNegator can't De Morgan an exact complement; translation must decline
        // rather than fall back to the generic Not wrap, which mishandles a missing field (see the functional
        // NativeStringIsNullOrTests coverage).
        Expression<Func<Widget, bool>> predicate = w => !string.IsNullOrWhiteSpace(w.Text);

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Null(result);
    }
}
