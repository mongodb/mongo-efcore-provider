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

using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoAggregationExpressionRenderer"/>, which renders dialect-agnostic
/// <see cref="MongoExpression"/> subtrees into MongoDB aggregation expressions (the body inside <c>{ $expr: … }</c>).
/// </summary>
public class MongoAggregationExpressionRendererTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Test 1: field-to-field comparison → { $eq: ['$Age', '$Score'] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_field_to_field_comparison()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(age, "Age"),
            new MongoFieldExpression(score, "Score"));

        var rendered = new MongoAggregationExpressionRenderer().Render(expr, new PlaceholderTable());

        Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $eq: ['$Age', '$Score'] }")), rendered);
    }

    // ------------------------------------------------------------------
    // Test 2: arithmetic operand → { $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_arithmetic_operand()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        // Age + Score > 5
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoBinaryExpression(MongoBinaryOperator.Add,
                new MongoFieldExpression(age, "Age"),
                new MongoFieldExpression(score, "Score")),
            new MongoConstantExpression(5, age));

        var rendered = new MongoAggregationExpressionRenderer().Render(expr, new PlaceholderTable());

        Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }")), rendered);
    }
}
