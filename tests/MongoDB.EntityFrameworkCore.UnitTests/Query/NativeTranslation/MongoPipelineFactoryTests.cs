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
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoPipelineFactory"/> — compile-time template construction
/// and per-execution parameter binding.
/// </summary>
public class MongoPipelineFactoryTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Age { get; set; }
        public string Name { get; set; } = "";
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Test 1 (headline): same factory, different parameter values, template NOT mutated
    // ------------------------------------------------------------------

    [Fact]
    public void Same_template_binds_different_parameter_values_across_executions()
    {
        // Build a factory whose $match is { Age: { $gt: <param p0> } } with an Int32 serializer.
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            field,
            new MongoParameterExpression("p0", ageProperty));

        var stages = new List<MongoPipelineStage> { new MongoMatchStage(pred) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var first = factory.Build(new Dictionary<string, object?> { ["p0"] = 21 });
        var second = factory.Build(new Dictionary<string, object?> { ["p0"] = 40 });

        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 21 } } }"), first[0]);
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 40 } } }"), second[0]);

        // Template is not mutated between builds: building p0=21 again must equal first result.
        var third = factory.Build(new Dictionary<string, object?> { ["p0"] = 21 });
        Assert.Equal(first[0], third[0]);
    }

    // ------------------------------------------------------------------
    // Test 2: constant value baked into template — Build with empty dict works
    // ------------------------------------------------------------------

    [Fact]
    public void Constant_value_is_baked_into_template()
    {
        var ageProperty = GetProperty<Customer>("Age");

        var stages = new List<MongoPipelineStage>
        {
            new MongoLimitStage(new MongoConstantExpression(10, ageProperty))
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(BsonDocument.Parse("{ $limit: 10 }"), result[0]);
    }

    // ------------------------------------------------------------------
    // Test 3: $sort stage — ascending + descending orderings
    // ------------------------------------------------------------------

    [Fact]
    public void Sort_stage_renders_ascending_and_descending_orderings()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var nameProperty = GetProperty<Customer>("Name");

        var orderings = new List<MongoOrdering>
        {
            new MongoOrdering(new MongoFieldExpression(ageProperty, "Age"), Ascending: true),
            new MongoOrdering(new MongoFieldExpression(nameProperty, "Name"), Ascending: false)
        };

        var stages = new List<MongoPipelineStage> { new MongoSortStage(orderings) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(BsonDocument.Parse("{ $sort: { Age: 1, Name: -1 } }"), result[0]);
    }

    // ------------------------------------------------------------------
    // Test 4: multi-stage canonical pipeline: match + sort + skip + limit
    // ------------------------------------------------------------------

    [Fact]
    public void Multi_stage_canonical_pipeline_produces_stages_in_order()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var nameProperty = GetProperty<Customer>("Name");

        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(ageProperty, "Age"),
            new MongoParameterExpression("minAge", ageProperty));

        var orderings = new List<MongoOrdering>
        {
            new MongoOrdering(new MongoFieldExpression(nameProperty, "Name"), Ascending: true)
        };

        var stages = new List<MongoPipelineStage>
        {
            new MongoMatchStage(pred),
            new MongoSortStage(orderings),
            new MongoSkipStage(new MongoConstantExpression(5, ageProperty)),
            new MongoLimitStage(new MongoConstantExpression(10, ageProperty))
        };

        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?> { ["minAge"] = 18 });

        Assert.Equal(4, result.Length);
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 18 } } }"), result[0]);
        Assert.Equal(BsonDocument.Parse("{ $sort: { Name: 1 } }"), result[1]);
        Assert.Equal(BsonDocument.Parse("{ $skip: 5 }"), result[2]);
        Assert.Equal(BsonDocument.Parse("{ $limit: 10 }"), result[3]);
    }

    // ------------------------------------------------------------------
    // Test 5: $skip with a parameterized count (forSerialization: null) — BsonValue.Create path
    // ------------------------------------------------------------------

    [Fact]
    public void Null_serializer_placeholder_substitutes_BsonValue_Create()
    {
        // $skip with a PARAMETERIZED count (forSerialization: null)
        var skipParam = new MongoParameterExpression("skip_count", forSerialization: null);
        var stages = new List<MongoPipelineStage> { new MongoSkipStage(skipParam) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?> { ["skip_count"] = 5 });

        Assert.Single(result);
        Assert.Equal(BsonDocument.Parse("{ $skip: 5 }"), result[0]);
    }

    // ------------------------------------------------------------------
    // Paging bounds validation tests
    // ------------------------------------------------------------------

    [Fact]
    public void Build_throws_ArgumentOutOfRangeException_for_limit_zero_constant()
    {
        // A baked constant $limit: 0 must throw before reaching MongoDB.
        var ageProperty = GetProperty<Customer>("Age");
        var stages = new List<MongoPipelineStage>
        {
            new MongoLimitStage(new MongoConstantExpression(0, ageProperty))
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Build_throws_ArgumentOutOfRangeException_for_limit_negative_constant()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var stages = new List<MongoPipelineStage>
        {
            new MongoLimitStage(new MongoConstantExpression(-1, ageProperty))
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Build_throws_ArgumentOutOfRangeException_for_limit_zero_parameter()
    {
        // A parameterized Take that binds to 0 at execution time must throw.
        var limitParam = new MongoParameterExpression("take_count", forSerialization: null);
        var stages = new List<MongoPipelineStage> { new MongoLimitStage(limitParam) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?> { ["take_count"] = 0 }));
    }

    [Fact]
    public void Build_throws_ArgumentOutOfRangeException_for_skip_negative_parameter()
    {
        var skipParam = new MongoParameterExpression("skip_count", forSerialization: null);
        var stages = new List<MongoPipelineStage> { new MongoSkipStage(skipParam) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?> { ["skip_count"] = -1 }));
    }

    [Fact]
    public void Build_does_not_throw_for_valid_skip_and_limit()
    {
        // Skip(1), Take(2) — both valid — must build without throwing.
        var skipParam = new MongoParameterExpression("skip_count", forSerialization: null);
        var limitParam = new MongoParameterExpression("take_count", forSerialization: null);
        var stages = new List<MongoPipelineStage>
        {
            new MongoSkipStage(skipParam),
            new MongoLimitStage(limitParam)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>
        {
            ["skip_count"] = 1,
            ["take_count"] = 2
        });

        Assert.Equal(2, result.Length);
        Assert.Equal(BsonDocument.Parse("{ $skip: 1 }"), result[0]);
        Assert.Equal(BsonDocument.Parse("{ $limit: 2 }"), result[1]);
    }

    [Fact]
    public void Build_does_not_throw_for_skip_zero()
    {
        // Skip(0) is valid — $skip accepts 0.
        var skipParam = new MongoParameterExpression("skip_count", forSerialization: null);
        var stages = new List<MongoPipelineStage> { new MongoSkipStage(skipParam) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        // Should NOT throw
        var result = factory.Build(new Dictionary<string, object?> { ["skip_count"] = 0 });
        Assert.Equal(BsonDocument.Parse("{ $skip: 0 }"), result[0]);
    }
}
