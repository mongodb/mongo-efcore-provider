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
/// Unit tests for <see cref="MongoQueryLanguageRenderer"/>, which renders dialect-agnostic
/// <see cref="MongoExpression"/> predicates into MongoDB <c>$match</c>-dialect BSON filter bodies.
/// </summary>
public class MongoQueryLanguageRendererTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Age { get; set; }
        public bool Active { get; set; }
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Test 1: simple GreaterThan comparison → { Age: { $gt: 21 } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_greater_than_in_query_dialect()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            field,
            new MongoConstantExpression(21, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 2: AndAlso of two ranges on the same field merges operator docs
    //         Age > 21 && Age < 65 → { Age: { $gt: 21, $lt: 65 } }
    // ------------------------------------------------------------------

    [Fact]
    public void Merges_two_ranges_on_one_field()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                field,
                new MongoConstantExpression(21, ageProperty)),
            new MongoBinaryExpression(
                MongoBinaryOperator.LessThan,
                field,
                new MongoConstantExpression(65, ageProperty)));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21, $lt: 65 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 3: Equal comparison → bare { Age: value } (no $eq wrapper)
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_equal_as_bare_value()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            field,
            new MongoConstantExpression(30, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: 30 }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 4: NotEqual → { Age: { $ne: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_not_equal_with_ne_operator()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.NotEqual,
            field,
            new MongoConstantExpression(0, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $ne: 0 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 5: LessThanOrEqual → { Age: { $lte: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_less_than_or_equal()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.LessThanOrEqual,
            field,
            new MongoConstantExpression(100, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $lte: 100 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 6: GreaterThanOrEqual → { Age: { $gte: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_greater_than_or_equal()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            field,
            new MongoConstantExpression(18, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gte: 18 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 7: OrElse → { $or: [ { Age: { $lt: 18 } }, { Age: { $gt: 65 } } ] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_or_else_as_or_array()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.OrElse,
            new MongoBinaryExpression(
                MongoBinaryOperator.LessThan,
                field,
                new MongoConstantExpression(18, ageProperty)),
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                field,
                new MongoConstantExpression(65, ageProperty)));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ $or: [ { Age: { $lt: 18 } }, { Age: { $gt: 65 } } ] }"),
            rendered);
    }

    // ------------------------------------------------------------------
    // Test 8: bare bool field → { Active: true }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_bare_bool_field_as_true()
    {
        var activeProperty = GetProperty<Customer>("Active");
        var field = new MongoFieldExpression(activeProperty, "Active");

        var rendered = new MongoQueryLanguageRenderer().Render(field, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Active: true }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 9: Not(bool field) → { Active: { $ne: true } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_not_bool_field_as_ne_true()
    {
        var activeProperty = GetProperty<Customer>("Active");
        var field = new MongoFieldExpression(activeProperty, "Active");
        var pred = new MongoUnaryExpression(MongoUnaryOperator.Not, field);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Active: { $ne: true } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 10: B2 parameter placeholder — renders sentinel, records in PlaceholderTable
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_parameter_as_sentinel_and_records_in_placeholder_table()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            field,
            new MongoParameterExpression("p0", ageProperty));

        var placeholders = new PlaceholderTable();
        var rendered = new MongoQueryLanguageRenderer().Render(pred, placeholders);

        // The placeholder table must record one entry named "p0" with a non-null serializer.
        Assert.Single(placeholders.Entries);
        Assert.Equal("p0", placeholders.Entries[0].Name);
        Assert.NotNull(placeholders.Entries[0].Serializer);

        // The rendered body must be { Age: { $gt: <sentinel> } } where the sentinel is
        // a placeholder marker document that TryGetPlaceholderIndex recognises as index 0.
        var rendered_doc = Assert.IsType<BsonDocument>(rendered);
        var ageCond = Assert.IsType<BsonDocument>(rendered_doc["Age"]);
        var sentinelValue = ageCond["$gt"];
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(sentinelValue, out var index));
        Assert.Equal(0, index);
    }

    // ------------------------------------------------------------------
    // Test 11: AndAlso with two different fields — no merge, remain flat { f1: ..., f2: ... }
    // ------------------------------------------------------------------

    [Fact]
    public void And_with_two_different_fields_stays_flat()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var activeProperty = GetProperty<Customer>("Active");

        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(ageProperty, "Age"),
                new MongoConstantExpression(21, ageProperty)),
            new MongoFieldExpression(activeProperty, "Active"));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21 }, Active: true }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 12: MongoConstantExpression with null ForSerialization (Skip/Take count)
    //          → BsonValue.Create(value), no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_constant_with_null_ForSerialization_as_BsonValue()
    {
        // MongoConstantExpression(5, forSerialization: null) — Skip/Take count
        var constant = new MongoConstantExpression(5, forSerialization: null);
        var placeholders = new PlaceholderTable();
        var renderer = new MongoQueryLanguageRenderer();

        var result = renderer.RenderValue(constant, placeholders);

        Assert.Equal(new BsonInt32(5), result);
    }

    // ------------------------------------------------------------------
    // Test 13: MongoParameterExpression with null ForSerialization (Skip/Take count)
    //          → placeholder with null serializer, no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_parameter_with_null_ForSerialization_as_null_serializer_placeholder()
    {
        // MongoParameterExpression("p", forSerialization: null) — Skip/Take count
        var parameter = new MongoParameterExpression("p", forSerialization: null);
        var placeholders = new PlaceholderTable();
        var renderer = new MongoQueryLanguageRenderer();

        var result = renderer.RenderValue(parameter, placeholders);

        // Must record a placeholder entry with null serializer.
        Assert.Single(placeholders.Entries);
        Assert.Equal("p", placeholders.Entries[0].Name);
        Assert.Null(placeholders.Entries[0].Serializer);

        // The returned value must be a valid sentinel.
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
    }
}
