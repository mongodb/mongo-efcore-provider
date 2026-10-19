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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorMathTests
{
    private class Widget
    {
        public int Id { get; set; }
        public double Amount { get; set; }
    }

    private static (MongoExpressionTranslator Translator, IProperty AmountProperty) BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return (new MongoExpressionTranslator(entityType), entityType.FindProperty(nameof(Widget.Amount))!);
    }

    private static IProperty NonDefaultSerializedAmountProperty()
    {
        using var db = SingleEntityDbContext.Create<Widget>(
            mb => mb.Entity<Widget>().Property(w => w.Amount).HasConversion<string>());

        return db.Model.FindEntityType(typeof(Widget))!.FindProperty(nameof(Widget.Amount))!;
    }

    private static IProperty DefaultSerializedAmountProperty()
    {
        using var db = SingleEntityDbContext.Create<Widget>();

        return db.Model.FindEntityType(typeof(Widget))!.FindProperty(nameof(Widget.Amount))!;
    }

    [Fact]
    public void A_math_expression_over_a_non_default_serialized_field_is_not_treated_as_safe()
    {
        // MongoMathExpression must recurse into its operands rather than hit the switch's `_ => true` default.
        var nonDefaultField = new MongoFieldExpression(NonDefaultSerializedAmountProperty(), "Amount");
        var math = new MongoMathExpression(MongoMathFunction.Abs, [nonDefaultField], typeof(double));

        Assert.False(MongoExpressionTranslator.AllFieldsDefaultSerialized(math));
    }

    [Fact]
    public void Math_Round_with_a_MidpointRounding_argument_declines_rather_than_misreading_it_as_digits()
    {
        // Same arity as Math.Round(double, int); misreading it as RoundDigits would silently render
        // {"$round": [x, 1]} for AwayFromZero.
        var (translator, _) = BuildTranslator();
        Expression<Func<Widget, double>> selector = w => Math.Round(w.Amount, MidpointRounding.AwayFromZero);

        Assert.False(translator.TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void Sign_renders_as_a_switch_with_an_explicit_zero_default()
    {
        var field = new MongoFieldExpression(DefaultSerializedAmountProperty(), "Amount");
        var math = new MongoMathExpression(MongoMathFunction.Sign, [field], typeof(int));

        var rendered = MongoAggregationExpressionRenderer.Render(math, new PlaceholderTable());

        var switchDoc = Assert.IsType<BsonDocument>(rendered)["$switch"].AsBsonDocument;
        var branches = switchDoc["branches"].AsBsonArray;
        Assert.Equal(2, branches.Count);
        Assert.Equal(0, switchDoc["default"].AsInt32);
    }
}
