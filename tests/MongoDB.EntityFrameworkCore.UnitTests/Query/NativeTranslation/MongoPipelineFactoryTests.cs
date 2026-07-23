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

    // ------------------------------------------------------------------
    // Array-binding test: one template, two executions with different collections,
    // each producing a distinct $in array that matches its own source collection.
    // ------------------------------------------------------------------

    [Fact]
    public void Array_placeholder_binds_different_collections_across_executions()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var inExpr = new MongoInExpression(
            field,
            new MongoParameterExpression("ages", ageProperty),
            negated: false);

        var stages = new List<MongoPipelineStage> { new MongoMatchStage(inExpr) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var first = factory.Build(new Dictionary<string, object?> { ["ages"] = new[] { 1, 2, 3 } });
        var second = factory.Build(new Dictionary<string, object?> { ["ages"] = new[] { 4, 5 } });

        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $in: [1, 2, 3] } } }"), first[0]);
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $in: [4, 5] } } }"), second[0]);
        Assert.NotEqual(first[0], second[0]);

        // Re-binding the first collection again must reproduce the same result (template not mutated).
        var third = factory.Build(new Dictionary<string, object?> { ["ages"] = new[] { 1, 2, 3 } });
        Assert.Equal(first[0], third[0]);
    }

    // ------------------------------------------------------------------
    // Inline vs. parameterized $in must serialize elements byte-identically.
    // ------------------------------------------------------------------

    [Fact]
    public void Inline_and_parameterized_in_produce_byte_identical_element_bson()
    {
        var ageProperty = GetProperty<Customer>("Age");

        var inlineStages = new List<MongoPipelineStage>
        {
            new MongoMatchStage(new MongoInExpression(
                new MongoFieldExpression(ageProperty, "Age"),
                new MongoConstantExpression(new[] { 1, 2, 3 }, ageProperty),
                negated: false))
        };
        var inlineFactory = MongoPipelineFactory.Create(inlineStages, new MongoQueryLanguageRenderer());
        var inlineResult = inlineFactory.Build(new Dictionary<string, object?>());

        var paramStages = new List<MongoPipelineStage>
        {
            new MongoMatchStage(new MongoInExpression(
                new MongoFieldExpression(ageProperty, "Age"),
                new MongoParameterExpression("ages", ageProperty),
                negated: false))
        };
        var paramFactory = MongoPipelineFactory.Create(paramStages, new MongoQueryLanguageRenderer());
        var paramResult = paramFactory.Build(new Dictionary<string, object?> { ["ages"] = new[] { 1, 2, 3 } });

        Assert.Equal(inlineResult[0], paramResult[0]);
    }

    // ------------------------------------------------------------------
    // $project stage rendering
    // ------------------------------------------------------------------

    [Fact]
    public void Project_stage_renders_alias_to_field_ref_with_id_suppressed()
    {
        var nameProperty = GetProperty<Customer>("Name");
        var stage = new MongoProjectStage(new[]
        {
            new MongoProjection("Name", new MongoFieldExpression(nameProperty, "Name"))
        });

        var factory = MongoPipelineFactory.Create(new MongoPipelineStage[] { stage }, new MongoQueryLanguageRenderer());
        var pipeline = factory.Build(new Dictionary<string, object?>());

        Assert.Single(pipeline);
        var project = pipeline[0]["$project"].AsBsonDocument;
        Assert.Equal("$Name", project["Name"].AsString);
        Assert.Equal(0, project["_id"].AsInt32);
    }

    // ------------------------------------------------------------------
    // $unionWith stage rendering (Concat/Union set-ops)
    // ------------------------------------------------------------------

    [Fact]
    public void Concat_renders_unionWith_without_dedup()
    {
        var stages = new MongoPipelineStage[]
        {
            new MongoUnionWithStage(new List<MongoPipelineStage>(), "customers", dedup: false)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        var pipeline = factory.Build(new Dictionary<string, object?>());

        Assert.Single(pipeline);
        var unionWith = pipeline[0]["$unionWith"].AsBsonDocument;
        Assert.Equal("customers", unionWith["coll"].AsString);
        Assert.Empty(unionWith["pipeline"].AsBsonArray);
    }

    [Fact]
    public void Union_appends_dollarRoot_dedup_group_and_replaceRoot()
    {
        var stages = new MongoPipelineStage[]
        {
            new MongoUnionWithStage(new List<MongoPipelineStage>(), "customers", dedup: true)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        var pipeline = factory.Build(new Dictionary<string, object?>());

        Assert.Equal(3, pipeline.Length);
        Assert.True(pipeline[0].Contains("$unionWith"));
        Assert.Equal("$$ROOT", pipeline[1]["$group"]["_id"].AsString);
        Assert.Equal("$_id", pipeline[2]["$replaceRoot"]["newRoot"].AsString);
    }

    [Fact]
    public void Operand_predicate_renders_inside_the_union_pipeline()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(ageProperty, "Age"),
            new MongoConstantExpression(21, ageProperty));

        var operand = new List<MongoPipelineStage> { new MongoMatchStage(pred) };
        var stages = new MongoPipelineStage[] { new MongoUnionWithStage(operand, "customers", dedup: false) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        var pipeline = factory.Build(new Dictionary<string, object?>());

        var innerPipeline = pipeline[0]["$unionWith"]["pipeline"].AsBsonArray;
        Assert.True(innerPipeline[0].AsBsonDocument.Contains("$match"));
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: 21 } }"), innerPipeline[0].AsBsonDocument);
    }

    [Fact]
    public void Operand_parameterized_predicate_shares_the_outer_placeholder_table()
    {
        // Proves operand stages render into the SAME PlaceholderTable as the outer pipeline:
        // the parameter substitutes correctly at Build time even though it originates inside
        // the nested $unionWith pipeline.
        var ageProperty = GetProperty<Customer>("Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(ageProperty, "Age"),
            new MongoParameterExpression("minAge", ageProperty));

        var operand = new List<MongoPipelineStage> { new MongoMatchStage(pred) };
        var stages = new MongoPipelineStage[] { new MongoUnionWithStage(operand, "customers", dedup: false) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var pipeline = factory.Build(new Dictionary<string, object?> { ["minAge"] = 18 });

        var innerPipeline = pipeline[0]["$unionWith"]["pipeline"].AsBsonArray;
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 18 } } }"), innerPipeline[0].AsBsonDocument);
    }

    [Fact]
    public void Operand_limit_zero_throws_ArgumentOutOfRangeException_from_Build()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var operand = new List<MongoPipelineStage>
        {
            new MongoLimitStage(new MongoConstantExpression(0, ageProperty))
        };
        var stages = new MongoPipelineStage[] { new MongoUnionWithStage(operand, "customers", dedup: false) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Operand_skip_negative_throws_ArgumentOutOfRangeException_from_Build()
    {
        // Mirrors Operand_limit_zero_throws_ArgumentOutOfRangeException_from_Build, but for $skip:
        // proves ValidatePagingStages recurses into the union operand's stages for $skip too,
        // symmetric with $limit.
        var ageProperty = GetProperty<Customer>("Age");
        var operand = new List<MongoPipelineStage>
        {
            new MongoSkipStage(new MongoConstantExpression(-1, ageProperty))
        };
        var stages = new MongoPipelineStage[] { new MongoUnionWithStage(operand, "customers", dedup: false) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Build(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Outer_and_operand_parameterized_predicates_each_substitute_their_own_value()
    {
        // Strengthens Operand_parameterized_predicate_shares_the_outer_placeholder_table: that test
        // proves a fresh/empty placeholder table still resolves a single operand parameter. This test
        // proves the shared table indexes TWO DIFFERENT parameters correctly — one bound in the OUTER
        // pipeline and a DIFFERENT one nested inside the union OPERAND — guarding against an
        // index-collision regression (not just a fresh-empty-table one).
        var ageProperty = GetProperty<Customer>("Age");

        var outerPred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(ageProperty, "Age"),
            new MongoParameterExpression("outerMinAge", ageProperty));

        var operandPred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(ageProperty, "Age"),
            new MongoParameterExpression("operandMinAge", ageProperty));

        var operand = new List<MongoPipelineStage> { new MongoMatchStage(operandPred) };
        var stages = new MongoPipelineStage[]
        {
            new MongoMatchStage(outerPred),
            new MongoUnionWithStage(operand, "customers", dedup: false)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var pipeline = factory.Build(new Dictionary<string, object?>
        {
            ["outerMinAge"] = 18,
            ["operandMinAge"] = 25
        });

        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 18 } } }"), pipeline[0]);

        var innerPipeline = pipeline[1]["$unionWith"]["pipeline"].AsBsonArray;
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 25 } } }"), innerPipeline[0].AsBsonDocument);
    }

    // ------------------------------------------------------------------
    // MongoUnwindFieldStage — owned-collection SelectMany unwind (EF-347 slice 3)
    // ------------------------------------------------------------------

    [Fact]
    public void UnwindFieldStage_renders_dollar_unwind_of_the_element_path()
    {
        var stages = new List<MongoPipelineStage> { new MongoUnwindFieldStage("Items") };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(BsonDocument.Parse("{ $unwind: \"$Items\" }"), result[0]);
    }

    // ------------------------------------------------------------------
    // MongoUnwindFieldStage.IncludeArrayIndex + MongoReplaceRootStage — bare whole-element owned
    // SelectMany (EF-347 bare-owned spike): $unwind carries the array ordinal via includeArrayIndex,
    // and $replaceRoot merges the owner key + ordinal into the re-rooted element.
    // ------------------------------------------------------------------

    [Fact]
    public void UnwindFieldStage_with_includeArrayIndex_renders_dollar_unwind_with_path_and_index()
    {
        var stages = new List<MongoPipelineStage>
        {
            new MongoUnwindFieldStage("Items", includeArrayIndex: MongoReplaceRootStage.OrdinalField)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(
            BsonDocument.Parse("{ $unwind: { path: \"$Items\", includeArrayIndex: \"__ord\" } }"),
            result[0]);
    }

    [Fact]
    public void ReplaceRootStage_renders_dollar_replaceRoot_with_mergeObjects_owner_key_and_ordinal()
    {
        var stages = new List<MongoPipelineStage> { new MongoReplaceRootStage("Items") };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(
            BsonDocument.Parse(
                "{ $replaceRoot: { newRoot: { $mergeObjects: [ \"$Items\", { __ownerKey: \"$_id\", __ord: \"$__ord\" } ] } } }"),
            result[0]);
    }

    [Fact]
    public void ReplaceRootStage_plain_renders_dollar_replaceRoot_with_bare_newRoot()
    {
        var stages = new List<MongoPipelineStage>
        {
            new MongoReplaceRootStage("_lookup_Refs", mergeOwnerKeySentinels: false)
        };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(
            BsonDocument.Parse("{ $replaceRoot: { newRoot: \"$_lookup_Refs\" } }"),
            result[0]);
    }

    // ------------------------------------------------------------------
    // MongoUnwindStage — preserveNullAndEmptyArrays (EF-347 slice 5, Task 3): the reference-Include
    // $unwind (LEFT-join, unchanged) must keep preserve:true; the NEW ForceUnwind-collection SelectMany
    // flatten (INNER-join) must render preserve:false.
    // ------------------------------------------------------------------

    private class LookupChild
    {
        public ObjectId Id { get; set; }
        public ObjectId ParentId { get; set; }
    }

    private class LookupParent
    {
        public ObjectId Id { get; set; }
        public List<LookupChild> Children { get; set; } = new();
    }

    private static INavigation ChildrenNavigation()
    {
        using var db = SingleEntityDbContext.Create<LookupParent>(mb =>
        {
            mb.Entity<LookupChild>();
            mb.Entity<LookupParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        });
        return db.Model.FindEntityType(typeof(LookupParent))!.FindNavigation(nameof(LookupParent.Children))!;
    }

    private class RefChild
    {
        public ObjectId Id { get; set; }
    }

    private class RefParent
    {
        public ObjectId Id { get; set; }
        public ObjectId ChildId { get; set; }
        public RefChild? Child { get; set; }
    }

    private static INavigation ReferenceNavigation()
    {
        using var db = SingleEntityDbContext.Create<RefParent>(mb =>
        {
            mb.Entity<RefChild>();
            mb.Entity<RefParent>().HasOne(p => p.Child).WithMany().HasForeignKey(p => p.ChildId);
        });
        return db.Model.FindEntityType(typeof(RefParent))!.FindNavigation(nameof(RefParent.Child))!;
    }

    [Fact]
    public void ForceUnwind_collection_unwind_stage_renders_preserve_false()
    {
        var navigation = ChildrenNavigation();
        var lookup = new LookupExpression(navigation, forceUnwind: true);

        var stages = new List<MongoPipelineStage> { new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: false) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(
            BsonDocument.Parse("{ $unwind: { path: \"$_lookup_Children\", preserveNullAndEmptyArrays: false } }"),
            result[0]);
    }

    [Fact]
    public void Reference_include_unwind_stage_still_renders_preserve_true_by_default()
    {
        var navigation = ReferenceNavigation();
        var lookup = new LookupExpression(navigation);

        var stages = new List<MongoPipelineStage> { new MongoUnwindStage(lookup) };
        var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

        var result = factory.Build(new Dictionary<string, object?>());

        Assert.Single(result);
        Assert.Equal(
            BsonDocument.Parse("{ $unwind: { path: \"$_lookup_Child\", preserveNullAndEmptyArrays: true } }"),
            result[0]);
    }
}
