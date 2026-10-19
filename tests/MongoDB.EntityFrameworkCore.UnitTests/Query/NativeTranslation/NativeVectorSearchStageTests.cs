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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for the native <c>$vectorSearch</c> IR: the dedicated
/// <see cref="MongoSelectDefinition.VectorSearch"/> slot, the lowerer block that emits it FIRST, and the
/// <see cref="MongoPipelineFactory"/> arms that render it (a deferred slot for the search itself, a constant
/// document for the score companion).
/// </summary>
/// <remarks>
/// Nothing populates the slot yet — the slot populator branch is a later task — so every select here is
/// hand-built. That is deliberate: these tests pin the emission and rendering independently of the gate.
/// The rendered bodies are asserted byte-for-byte against the MQL baselines the specification suite already
/// commits for the driver-LINQ path, because "the native path re-baselines nothing" is the whole point.
/// </remarks>
public class NativeVectorSearchStageTests
{
    // A cut-down stand-in for the specification suite's vector-search Book, carrying exactly what the
    // committed baselines below depend on: a "Floats" vector property, a "FloatsIndex" vector index, and the
    // is_published element rename the pre-filter baseline asserts.
    private class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public bool IsPublished { get; set; }
        public float[] Floats { get; set; } = [];
    }

    private static SingleEntityDbContext<Book> CreateContext()
        => SingleEntityDbContext.Create<Book>(mb =>
        {
            mb.Entity<Book>(b =>
            {
                b.Property(x => x.IsPublished).HasElementName("is_published");
                b.HasIndex(x => x.Floats, "FloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
            });
        });

    // The exact input vector the specification suite's VectorSearch_floats uses.
    private static readonly QueryVector InputVector = new[] { 0.33f, -0.52f };

    // Committed baselines, copied verbatim from tests/.../SpecificationTests/Query/VectorSearchMongoTest.cs.
    private const string FloatsBaseline =
        """{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex", "queryVector" : [0.33000001311302185, -0.51999998092651367] } }""";

    private const string BoolPreFilterBaseline =
        """{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex", "filter" : { "is_published" : true }, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }""";

    private const string ScoreBaseline =
        """{ "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }""";

    private static MongoVectorSearch VectorSearch(
        IEntityType entityType,
        MongoExpression? preFilter = null,
        Expression? queryVectorArgument = null,
        Expression? limitArgument = null,
        Expression? optionsArgument = null)
        => new(
            entityType,
            (Expression<Func<Book, float[]>>)(b => b.Floats),
            preFilter,
            queryVectorArgument ?? Expression.Constant(InputVector),
            limitArgument ?? Expression.Constant(4),
            optionsArgument ?? Expression.Constant(
                new VectorQueryOptions("FloatsIndex"), typeof(VectorQueryOptions?)));

    private static MongoNativeBuildContext BuildContext(
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        IDictionary<string, object>? additionalState = null)
        => new(
            parameterValues ?? new Dictionary<string, object?>(),
            new BsonSerializerFactory(),
            // Only touched when the requested index is absent from the model, which no test here arranges.
            QueryLogger: null!,
            additionalState ?? new Dictionary<string, object>());

    private static MongoExpression TitleEquals(IEntityType entityType, string title)
        => new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(entityType.FindProperty(nameof(Book.Title))!, "Title"),
            new MongoConstantExpression(title, entityType.FindProperty(nameof(Book.Title))!));

    // ------------------------------------------------------------------
    // Emission order — $vectorSearch, $addFields, then the composed ops
    // ------------------------------------------------------------------

    [Fact]
    public void Vector_search_lowers_first_then_its_score_companion_then_the_where()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(entityType);
        query.Select.AddPredicateConjunct(TitleEquals(entityType, "Action"));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Equal(3, stages.Count);
        Assert.IsType<MongoVectorSearchStage>(stages[0]);
        Assert.IsType<MongoVectorSearchScoreStage>(stages[1]);
        Assert.IsType<MongoMatchStage>(stages[2]);
    }

    [Fact]
    public void Vector_search_slot_is_an_anchor_not_a_terminal_operator()
    {
        // A .Where composed after a vector search must keep recording into PipelineOps, which is only true
        // while the slot stays OUT of HasTerminalOperator. Route is unaffected too.
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(entityType);
        query.Select.AddPredicateConjunct(TitleEquals(entityType, "Action"));

        Assert.False(query.Select.HasTerminalOperator);
        Assert.Equal(NativeRoute.WholeEntity, query.Select.Route);
        Assert.Single(query.Select.PipelineOps);
        Assert.Empty(query.Select.TrailingOps);
    }

    // ------------------------------------------------------------------
    // Rendering — byte-equal to the committed driver-LINQ baselines
    // ------------------------------------------------------------------

    [Fact]
    public void Rendered_body_is_byte_equal_to_the_committed_VectorSearch_floats_baseline()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(entityType);
        query.Select.AddPredicateConjunct(TitleEquals(entityType, "Action"));

        var pipeline = MongoPipelineFactory
            .Create(new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer())
            .Build(BuildContext());

        Assert.Equal(3, pipeline.Length);
        Assert.Equal(FloatsBaseline, pipeline[0].ToString());
        Assert.Equal(ScoreBaseline, pipeline[1].ToString());
        Assert.Equal(BsonDocument.Parse("""{ "$match" : { "Title" : "Action" } }"""), pipeline[2]);
    }

    [Fact]
    public void Absent_options_resolve_the_single_vector_index_declared_in_the_model()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(
            entityType, optionsArgument: Expression.Constant(null, typeof(VectorQueryOptions?)));

        var pipeline = MongoPipelineFactory
            .Create(new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer())
            .Build(BuildContext());

        // Same body: the index name is resolved from the model when the query does not name one.
        Assert.Equal(FloatsBaseline, pipeline[0].ToString());
    }

    [Fact]
    public void Pre_filter_is_rendered_at_compile_time_and_embedded_in_the_body()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(
            entityType,
            preFilter: new MongoFieldExpression(entityType.FindProperty(nameof(Book.IsPublished))!, "is_published"));

        var pipeline = MongoPipelineFactory
            .Create(new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer())
            .Build(BuildContext());

        Assert.Equal(BoolPreFilterBaseline, pipeline[0].ToString());
    }

    // ------------------------------------------------------------------
    // Per-execution binding
    // ------------------------------------------------------------------

    [Fact]
    public void Parameterized_pre_filter_substitutes_per_execution_without_consuming_the_template()
    {
        // The pre-filter is rendered ONCE, into the shared placeholder table; Build's substitution pass
        // rewrites sentinels IN PLACE, so a second execution would see a spent template if the deferred
        // builder embedded the rendered document itself rather than a clone.
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;
        var isPublished = entityType.FindProperty(nameof(Book.IsPublished))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(
            entityType,
            preFilter: new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(isPublished, "is_published"),
                new MongoParameterExpression("p0", isPublished)));

        var factory = MongoPipelineFactory.Create(
            new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer());

        var first = factory.Build(BuildContext(new Dictionary<string, object?> { ["p0"] = true }));
        var second = factory.Build(BuildContext(new Dictionary<string, object?> { ["p0"] = false }));

        Assert.Equal(BoolPreFilterBaseline, first[0].ToString());
        Assert.Equal(
            BoolPreFilterBaseline.Replace(""""is_published" : true"""", """"is_published" : false""""),
            second[0].ToString());
    }

    [Fact]
    public void Runtime_arguments_are_resolved_from_this_executions_query_parameters()
    {
        // The shape of a query-parameter node differs across EF versions; NativeQueryParameter is what keeps
        // the resolution version-agnostic, and this is the test that exercises that arm rather than the
        // constant one. (The #if is in the TEST only — src/ has none on this path.)
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

#if EF8 || EF9
        const string vectorName = QueryCompilationContext.QueryParameterPrefix + "vector_0";
        const string limitName = QueryCompilationContext.QueryParameterPrefix + "limit_0";
        const string optionsName = QueryCompilationContext.QueryParameterPrefix + "options_0";
        Expression vectorArgument = Expression.Parameter(typeof(QueryVector), vectorName);
        Expression limitArgument = Expression.Parameter(typeof(int), limitName);
        Expression optionsArgument = Expression.Parameter(typeof(VectorQueryOptions?), optionsName);
#else
        const string vectorName = "__vector_0";
        const string limitName = "__limit_0";
        const string optionsName = "__options_0";
        Expression vectorArgument = new QueryParameterExpression(vectorName, typeof(QueryVector));
        Expression limitArgument = new QueryParameterExpression(limitName, typeof(int));
        Expression optionsArgument = new QueryParameterExpression(optionsName, typeof(VectorQueryOptions?));
#endif

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(
            entityType,
            queryVectorArgument: vectorArgument,
            limitArgument: limitArgument,
            optionsArgument: optionsArgument);

        var factory = MongoPipelineFactory.Create(
            new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer());

        var pipeline = factory.Build(BuildContext(new Dictionary<string, object?>
        {
            [vectorName] = InputVector,
            [limitName] = 4,
            [optionsName] = new VectorQueryOptions("FloatsIndex")
        }));

        Assert.Equal(FloatsBaseline, pipeline[0].ToString());

        // The limit is genuinely read per execution — and the driver derives numCandidates from it.
        var second = factory.Build(BuildContext(new Dictionary<string, object?>
        {
            [vectorName] = InputVector,
            [limitName] = 3,
            [optionsName] = new VectorQueryOptions("FloatsIndex")
        }));

        Assert.Equal(
            FloatsBaseline.Replace(""""limit" : 4, "numCandidates" : 40"""", """"limit" : 3, "numCandidates" : 30""""),
            second[0].ToString());
    }

    [Fact]
    public void Build_records_the_zero_results_diagnostic_state()
    {
        // QueryingEnumerable's zero-results warning reads these two entries back out of AdditionalState; if
        // Build did not write them a natively-routed empty vector query would throw KeyNotFoundException.
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(entityType);

        var additionalState = new Dictionary<string, object>();
        MongoPipelineFactory
            .Create(new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer())
            .Build(BuildContext(additionalState: additionalState));

        Assert.Same(
            entityType.FindProperty(nameof(Book.Floats)),
            additionalState["VectorQueryProperty"]);
        Assert.Equal("FloatsIndex", additionalState["VectorQueryIndexName"]);
    }

    [Fact]
    public void The_parameter_values_only_Build_overload_refuses_a_vector_search_template()
    {
        // A vector-search template always has a deferred slot, so the old overload must fail loudly rather
        // than emit a pipeline with a hole.
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(Book))!;

        var query = new MongoQueryExpression(entityType);
        query.Select.VectorSearch = VectorSearch(entityType);

        var factory = MongoPipelineFactory.Create(
            new MongoSelectLowerer().Lower(query), new MongoQueryLanguageRenderer());

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Build(new Dictionary<string, object?>()));

        Assert.Contains("deferred", exception.Message);
    }
}
