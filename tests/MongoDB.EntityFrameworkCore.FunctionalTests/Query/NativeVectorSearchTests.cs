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
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Behavioural tests for the native <c>$vectorSearch</c> path.
/// </summary>
/// <remarks>
/// Data assertions pin order (or a score), never a row count alone: a dropped <c>$vectorSearch</c> stage returns
/// the right number of rows in insertion order, so the seed is inserted deliberately out of score order. Routing is
/// proven by <see cref="MongoQueryMode.NativeOnly"/> succeeding (or throwing for a decline), never by MQL shape,
/// since both paths emit a structurally identical stage.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeVectorSearchTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    // Cosine similarity against this is "how close is the embedding to the x-axis", so expected order is
    // hand-checkable.
    private static readonly float[] QueryVector = [1.0f, 0.0f];

    // Score order for QueryVector: A (sim 1.000), B (0.981), C (0.894), D (0.707), E (0.000).
    private static readonly string[] ScoreOrder = ["A", "B", "C", "D", "E"];

    // Deliberately differs from ScoreOrder, including the first position. A dropped $vectorSearch stage yields
    // this order.
    private static readonly string[] InsertionOrder = ["D", "C", "A", "B", "E"];

    // ---------------------------------------------------------------------------------------------------
    // Capability: ordered labels; NativeOnly succeeding is the routing proof.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_vector_search_returns_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var labels = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .ToList()
            .Select(e => e.Label)
            .ToList();

        Assert.Equal(ScoreOrder.Take(4), labels);

        // Non-vacuity guard: if the seed ever matched score order, no ordered assertion here would discriminate.
        Assert.NotEqual(InsertionOrder.Take(4), labels);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Pre_filter_restricts_and_preserves_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Flag is true on B, C and E. The pre-filter runs inside $vectorSearch, so score order is preserved.
        var labels = db.Docs
            .VectorSearch(e => e.Embedding, e => e.Flag, QueryVector, limit: 4)
            .ToList()
            .Select(e => e.Label);

        Assert.Equal(["B", "C", "E"], labels);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Exact_search_returns_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Exact (ENN) changes the body shape (`exact: true`, no `numCandidates`), so the stage must be built at
        // Build time rather than substituted into a baked template.
        var labels = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4, new VectorQueryOptions(Exact: true))
            .ToList()
            .Select(e => e.Label);

        Assert.Equal(["A", "B", "C", "D"], labels);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Num_candidates_returns_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Explicit numCandidates, rather than the limit*10 the driver derives when null.
        var labels = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4, new VectorQueryOptions(NumberOfCandidates: 20))
            .ToList()
            .Select(e => e.Label);

        Assert.Equal(["A", "B", "C", "D"], labels);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Where_after_vector_search_preserves_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // VectorSearch is a root anchor, not a terminal: the Where becomes a $match after $vectorSearch, so score
        // order survives it.
        var labels = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Where(e => e.Label != "C")
            .ToList()
            .Select(e => e.Label);

        Assert.Equal(["A", "B", "D"], labels);
    }

    // ---------------------------------------------------------------------------------------------------
    // The __score projection leaf, both spellings. The $project alias `Score: "$__score"` has no backing IProperty
    // and is read raw via BsonBinding.CreateGetElementValue.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Score_projection_via_EF_Property_goes_native(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var rows = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Select(e => new { e.Label, Score = EF.Property<double>(e, "__score") })
            .ToList();

        AssertScoreOrdered(rows.Select(r => (r.Label, r.Score)));
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Score_projection_via_Mql_Field_goes_native(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var rows = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Select(e => new { e.Label, Score = Mql.Field(e, "__score", DoubleSerializer.Instance) })
            .ToList();

        AssertScoreOrdered(rows.Select(r => (r.Label, r.Score)));
    }

    // ---------------------------------------------------------------------------------------------------
    // Entity-and-score projection: `new { Doc = e, Score = ... }` renders {"Doc": "$$ROOT", "Score": "$__score"}.
    // Uses a parameterized StartsWith to prove both leaves read correctly with a non-baked query parameter.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Entity_and_score_projection_behind_a_parameterized_filter_reads_correct_values()
    {
        var prefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly))
        {
            var nativeOnlyRows = nativeOnly.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                .Where(e => e.Label.StartsWith(prefix))
                .Select(e => new { Doc = e, Score = EF.Property<double>(e, "__score") })
                .ToList();
            Assert.Single(nativeOnlyRows);
        }

        using var db = CreateContext(MongoQueryMode.Native, out var spyLogger);

        var rows = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Where(e => e.Label.StartsWith(prefix))
            .Select(e => new { Doc = e, Score = EF.Property<double>(e, "__score") })
            .ToList();

        // Assert values on both leaves; each fails silently. A lost entity leaf gives a null/default Doc, a lost
        // $addFields{__score} companion gives Score == 0.
        var row = Assert.Single(rows);
        Assert.Equal("A", row.Doc.Label);
        Assert.Equal(1.0, row.Doc.Weight);
        Assert.Equal("note-A", row.Doc.Meta.Note); // the owned hop inside the entity leaf survives too
        Assert.Equal(1.0, row.Score, 3);           // "A" IS the query vector, so its cosine score is exactly 1

        // Pins the native $project/$addFields shape, since both halves fail independently and silently.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$addFields\" : { \"__score\" : { \"$meta\" : \"vectorSearchScore\" } } }", mql);
        Assert.Contains("\"Doc\" : \"$$ROOT\"", mql);
        Assert.Contains("\"Score\" : \"$__score\"", mql);
    }

    // Pins score and order, never a row count. A dropped $addFields companion (or mis-read alias) yields scores
    // that are not strictly descending and, on this seed, a top row that is not "A".
    private static void AssertScoreOrdered(IEnumerable<(string Label, double Score)> rows)
    {
        var list = rows.ToList();

        Assert.Equal(ScoreOrder.Take(4), list.Select(r => r.Label));

        // "A" is the query vector, so its normalized cosine score is 1 — proves the leaf carries the real $meta
        // score rather than a default.
        Assert.Equal(1.0, list[0].Score, 3);

        for (var i = 0; i < list.Count; i++)
        {
            Assert.True(list[i].Score > 0, $"Score at {i} was {list[i].Score}");
            if (i > 0)
            {
                Assert.True(list[i - 1].Score > list[i].Score,
                    $"Scores are not strictly descending: {list[i - 1].Score} then {list[i].Score}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Streaming materializer skipping the unmapped __score element.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Whole_entity_vector_search_streams_and_skips_the_score_field(MongoQueryMode mode)
    {
        // A bare vector search materializes through the one-pass streaming materializer over documents carrying an
        // extra top-level "__score" element (the $addFields companion is always emitted). Nothing maps it, so
        // MongoStreamingEntityMaterializerRewriter.BuildFillLoop must fall to its reader.SkipValue() base case.
        // VectorDoc owns a Meta (OwnsOne) to mirror the spec suite's Book/Preface shape.
        using var db = CreateContext(mode);

        // Premise: if the entity weren't streaming-eligible, it would use the DOM shaper and this test would silently
        // stop measuring the skip.
        var entityType = db.Model.FindEntityType(typeof(VectorDoc))!;
        Assert.True(StreamingEligibility.IsEligible(entityType),
            "VectorDoc must be streaming-eligible or this test does not exercise the streaming materializer.");

        var docs = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .ToList();

        // Order first: a dropped $vectorSearch stage returns insertion order.
        Assert.Equal(ScoreOrder.Take(4), docs.Select(d => d.Label));

        // Then every mapped scalar plus the owned sub-document: a mis-skipped unknown element derails the forward
        // reader for everything after it.
        Assert.Equal([1.0, 2.0, 3.0, 4.0], docs.Select(d => d.Weight));
        Assert.Equal([false, true, true, false], docs.Select(d => d.Flag));
        Assert.Equal(["keep", "skip", "keep", "keep"], docs.Select(d => d.Tags.Single()));
        Assert.Equal(["note-A", "note-B", "note-C", "note-D"], docs.Select(d => d.Meta.Note));
        Assert.Equal([1, 2, 3, 4], docs.Select(d => d.Meta.Pages));
        Assert.All(docs, d => Assert.Equal(2, d.Embedding.Length));
        Assert.All(docs, d => Assert.NotEqual(ObjectId.Empty, d.Id));
    }

    // ---------------------------------------------------------------------------------------------------
    // Recognizer guards. Each case's only reason to decline is one guard, so mutating that guard flips the test.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Mql_Field_for_a_non_score_element_declines()
    {
        // Guard A: the literal "__score" name. `Weight` is a real double-typed element and the receiver is the
        // selector's parameter, so only the name guard declines it.
        using (var db = CreateContext(MongoQueryMode.Native))
        {
            // Falls back to driver-LINQ, which resolves Mql.Field correctly. Values are asserted in order so a dropped
            // $vectorSearch stage is still caught.
            var values = db.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                .Select(e => new { e.Label, Value = Mql.Field(e, "Weight", DoubleSerializer.Instance) })
                .ToList();

            Assert.Equal(ScoreOrder.Take(4), values.Select(v => v.Label));
            Assert.Equal([1.0, 2.0, 3.0, 4.0], values.Select(v => v.Value));
        }

        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Docs
                    .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                    .Select(e => new { e.Label, Value = Mql.Field(e, "Weight", DoubleSerializer.Instance) })
                    .ToList());
        }

        // Guard B: the double/double? CLR type. The name is "__score", so only the type declines it. The native
        // read-back ignores any caller-supplied serializer and reads through CreateTypeSerializer(<requested type>),
        // so admitting a non-double would read through a serializer the fallback would never use.
        using (var db = CreateContext(MongoQueryMode.Native))
        {
            var scores = db.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                .Select(e => new { e.Label, Score = EF.Property<float>(e, "__score") })
                .ToList();

            Assert.Equal(ScoreOrder.Take(4), scores.Select(s => s.Label));
            Assert.Equal(1.0f, scores[0].Score, 3);
            for (var i = 1; i < scores.Count; i++)
            {
                Assert.True(scores[i - 1].Score > scores[i].Score,
                    $"Scores are not strictly descending: {scores[i - 1].Score} then {scores[i].Score}");
            }
        }

        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Docs
                    .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                    .Select(e => new { e.Label, Score = EF.Property<float>(e, "__score") })
                    .ToList());
        }
    }

    [AtlasFact]
    public void Score_leaf_without_a_vector_search_declines()
    {
        // Guard C: Select.VectorSearch is not null. With no vector search nothing writes "__score", so admitting the
        // leaf would emit a $project alias reading an element no stage writes.
        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Docs
                    .Select(e => new { e.Label, Score = EF.Property<double>(e, "__score") })
                    .ToList());
        }

        // Default mode declines and falls back to driver-LINQ. Asserting it agrees with an explicit DriverLinq run
        // pins "stayed on the fallback path" without pinning the driver's behaviour for a nonexistent element.
        using (var native = CreateContext(MongoQueryMode.Native))
        using (var driverLinq = CreateContext(MongoQueryMode.DriverLinq))
        {
            var underNative = Record.Exception(
                () => native.Docs.Select(e => new { e.Label, Score = EF.Property<double>(e, "__score") }).ToList());
            var underDriverLinq = Record.Exception(
                () => driverLinq.Docs.Select(e => new { e.Label, Score = EF.Property<double>(e, "__score") }).ToList());

            Assert.Equal(underDriverLinq?.GetType(), underNative?.GetType());
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Diagnostics raised from the native path.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Zero_results_logs_the_diagnostic_natively()
    {
        // Raised by QueryingEnumerable from AdditionalState, filled by the deferred $vectorSearch slot at Build time.
        // NativeOnly forbids the fallback, so this is the native path's own diagnostic.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            warnings: w => w.Throw(MongoEventId.VectorSearchReturnedZeroResults));

        var message = Assert.Throws<InvalidOperationException>(
            () => db.Docs
                .VectorSearch(e => e.Embedding, e => e.Label == "nope", QueryVector, limit: 4)
                .ToList()).Message;

        Assert.Contains("VectorSearchReturnedZeroResults", message);
        Assert.Contains("returned zero results", message);
    }

    [AtlasFact]
    public void Unmatched_index_name_warns_natively()
    {
        // Raised in VectorSearchStageBuilder.Resolve, shared by the native deferred slot and the driver-LINQ bridge.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            warnings: w => w.Throw(MongoEventId.VectorSearchNeedsIndex));

        var message = Assert.Throws<InvalidOperationException>(
            () => db.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4,
                    new VectorQueryOptions(IndexName: "NoSuchIndexInTheModel"))
                .ToList()).Message;

        Assert.Contains("VectorSearchNeedsIndex", message);
    }

    // ---------------------------------------------------------------------------------------------------
    // Exception parity across all three modes. Pins why VectorSearchStageBuilder keeps its reflection boundary.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Exact_with_num_candidates_throws_InvalidOperationException_in_every_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Thrown from ordinary code in VectorSearchStageBuilder.Resolve, before CreateStage's reflection Invoke, so it
        // surfaces unwrapped. Moving the guard inside the reflection boundary would wrap it on both paths.
        var exception = Assert.Throws<InvalidOperationException>(
            () => db.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4,
                    new VectorQueryOptions(NumberOfCandidates: 10, Exact: true))
                .ToList());

        Assert.Contains(
            "The option 'Exact' is set to 'true' on a call to 'VectorQuery', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set.",
            exception.Message);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Limit_zero_throws_identically_in_every_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // The driver's builder rejects a non-positive limit inside the shared reflection Invoke, hence
        // TargetInvocationException wrapping ArgumentOutOfRangeException('limit'). Don't add
        // BindingFlags.DoNotWrapExceptions (changes the driver-LINQ path too) or teach
        // MongoPipelineFactory.ValidatePagingStages to inspect $vectorSearch (yields a divergent 'count' exception).
        var outer = Assert.Throws<TargetInvocationException>(
            () => db.Docs.VectorSearch(e => e.Embedding, QueryVector, limit: 0).ToList());

        var inner = Assert.IsType<ArgumentOutOfRangeException>(outer.InnerException);
        Assert.Equal("limit", inner.ParamName);
    }

    // ---------------------------------------------------------------------------------------------------
    // Stage order: a server constraint, not a routing proof.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Vector_search_emits_the_stage_first()
    {
        // Not a routing proof: both paths emit an identical $vectorSearch + $addFields pair. Pins only that
        // $vectorSearch is stage 0 (the server rejects it elsewhere — Location40602) and the __score companion
        // immediately follows it, ahead of user-composed stages.
        using var db = CreateContext(MongoQueryMode.Native, out var spyLogger);

        db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Where(e => e.Label != "C")
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var pipeline = message[(message.IndexOf(".aggregate([", StringComparison.Ordinal) + ".aggregate([".Length)..];

        Assert.StartsWith("{ \"$vectorSearch\" : ", pipeline);

        var addFields = pipeline.IndexOf(
            "{ \"$addFields\" : { \"__score\" : { \"$meta\" : \"vectorSearchScore\" } } }", StringComparison.Ordinal);
        var match = pipeline.IndexOf("{ \"$match\" : ", StringComparison.Ordinal);

        Assert.True(addFields > 0, $"No $addFields{{__score}} companion in: {pipeline}");
        Assert.True(match > addFields, $"The composed $match must follow the score companion in: {pipeline}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Array-field-contains-value pre-filter.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Array_contains_pre_filter_now_goes_native_with_correct_rows()
    {
        // `Tags.Contains("keep")` (array field contains value) translates natively via MongoArrayContainsExpression.
        // Routing proven by NativeOnly; both paths render an identical $vectorSearch.filter.
        using (var db = CreateContext(MongoQueryMode.Native))
        {
            var labels = db.Docs
                .VectorSearch(e => e.Embedding, e => e.Tags.Contains("keep"), QueryVector, limit: 4)
                .ToList()
                .Select(e => e.Label);

            // Tags contains "keep" on A, C and D; score order A, C, D is the reverse of insertion order.
            Assert.Equal(["A", "C", "D"], labels);
        }

        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            var labels = db.Docs
                .VectorSearch(e => e.Embedding, e => e.Tags.Contains("keep"), QueryVector, limit: 4)
                .ToList()
                .Select(e => e.Label);

            Assert.Equal(["A", "C", "D"], labels);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Fallback-correctness tripwire: a parameterized Contains item still declines (the array-contains arm requires
    // a ConstantExpression item), and driver-LINQ renders the same $expr-free `{ Tags: <value> }` form the
    // vectorSearch filter accepts, so "decline -> fallback -> correct rows" stays exercised.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Parameterized_array_contains_pre_filter_still_falls_back_with_correct_rows()
    {
        var wanted = "keep"; // captured local -> a query PARAMETER, not a ConstantExpression, once EF
                              // parameterizes the closure before the native translator ever sees it.

        using (var db = CreateContext(MongoQueryMode.Native))
        {
            var labels = db.Docs
                .VectorSearch(e => e.Embedding, e => e.Tags.Contains(wanted), QueryVector, limit: 4)
                .ToList()
                .Select(e => e.Label);

            // Same discriminating set as the literal-constant case: A, C, D in score order.
            Assert.Equal(["A", "C", "D"], labels);
        }

        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Docs
                    .VectorSearch(e => e.Embedding, e => e.Tags.Contains(wanted), QueryVector, limit: 4)
                    .ToList());
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // An unsupported computed pre-filter (string transform). Any computed pre-filter needs $expr, which the
    // vectorSearch `filter` rejects server-side, so this throws in every mode: native declines at translate time;
    // the driver-LINQ fallback builds the rejected $expr and the server throws.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Untranslatable_pre_filter_throws_from_the_server_in_every_fallback_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var ex = Assert.Throws<MongoCommandException>(
            () => db.Docs
                .VectorSearch(e => e.Embedding, e => e.Label.ToUpper() == e.Label.ToUpper(), QueryVector, limit: 4)
                .ToList());

        Assert.Contains("$expr", ex.Message);
    }

    [AtlasFact]
    public void Untranslatable_pre_filter_declines_at_translate_time_under_native_only()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly);

        // NativeOnly forbids the fallback, so this throws at query compilation, not as a server-side
        // MongoCommandException.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Docs
                .VectorSearch(e => e.Embedding, e => e.Label.ToUpper() == e.Label.ToUpper(), QueryVector, limit: 4)
                .ToList());
    }

    // ---------------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------------

    private static readonly object SeedLock = new();
    private static string? SeededCollection;

    private VectorDocContext CreateContext(
        MongoQueryMode mode,
        ILoggerFactory? loggerFactory = null,
        Action<WarningsConfigurationBuilder>? warnings = null)
        => new(database, EnsureSeeded(), mode, loggerFactory, warnings);

    private VectorDocContext CreateContext(MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateContext(mode, loggerFactory);
    }

    // Seeds once per test-class run (tests run serially). Documents are inserted before the vector index is built,
    // as the VectorSearchReturnedZeroResults warning recommends, so queries don't race index ingestion.
    private string EnsureSeeded()
    {
        lock (SeedLock)
        {
            if (SeededCollection != null)
            {
                return SeededCollection;
            }

            var collection = TemporaryDatabaseFixtureBase.CreateCollectionName("NativeVectorSearch")
                             + Guid.NewGuid().ToString("N")[..8];

            using var db = new VectorDocContext(database, collection, MongoQueryMode.Native, null, null);

            db.Database.EnsureCreated(
                new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, WaitForVectorIndexes: false));

            // Weight ascends in score order (A=1 … E=5), not insertion order, and is double-typed so only the name
            // guard can decline it in Mql_Field_for_a_non_score_element_declines.
            db.Docs.AddRange(
                new VectorDoc
                {
                    Label = "D", Embedding = [1.0f, 1.0f], Flag = false, Tags = ["keep"], Weight = 4.0,
                    Meta = new VectorMeta { Note = "note-D", Pages = 4 }
                },
                new VectorDoc
                {
                    Label = "C", Embedding = [1.0f, 0.5f], Flag = true, Tags = ["keep"], Weight = 3.0,
                    Meta = new VectorMeta { Note = "note-C", Pages = 3 }
                },
                new VectorDoc
                {
                    Label = "A", Embedding = [1.0f, 0.0f], Flag = false, Tags = ["keep"], Weight = 1.0,
                    Meta = new VectorMeta { Note = "note-A", Pages = 1 }
                },
                new VectorDoc
                {
                    Label = "B", Embedding = [1.0f, 0.2f], Flag = true, Tags = ["skip"], Weight = 2.0,
                    Meta = new VectorMeta { Note = "note-B", Pages = 2 }
                },
                new VectorDoc
                {
                    Label = "E", Embedding = [0.0f, 1.0f], Flag = true, Tags = ["skip"], Weight = 5.0,
                    Meta = new VectorMeta { Note = "note-E", Pages = 5 }
                });
            db.SaveChanges();

            db.Database.CreateMissingVectorIndexes();
            db.Database.WaitForVectorIndexes();

            SeededCollection = collection;
            return collection;
        }
    }

    private class VectorDoc
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public float[] Embedding { get; set; } = [];
        public bool Flag { get; set; }
        public List<string> Tags { get; set; } = [];
        public double Weight { get; set; }

        // Owned reference mirroring the spec suite's Book/Preface; keeps the entity streaming-eligible (see
        // StreamingEligibility.IsEligible) so bare queries use the one-pass streaming reader.
        public VectorMeta Meta { get; set; } = new();
    }

    private class VectorMeta
    {
        public string Note { get; set; } = "";
        public int Pages { get; set; }
    }

    private class VectorDocContext(
        AtlasTemporaryDatabaseFixture database,
        string collection,
        MongoQueryMode mode,
        ILoggerFactory? loggerFactory,
        Action<WarningsConfigurationBuilder>? warnings)
        : DbContext(Configure(database, mode, loggerFactory, warnings))
    {
        public DbSet<VectorDoc> Docs { get; set; } = null!;

        private static DbContextOptions<VectorDocContext> Configure(
            AtlasTemporaryDatabaseFixture database,
            MongoQueryMode mode,
            ILoggerFactory? loggerFactory,
            Action<WarningsConfigurationBuilder>? warnings)
        {
            var builder = new DbContextOptionsBuilder<VectorDocContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x =>
                {
                    x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
                    warnings?.Invoke(x);
                });

            if (loggerFactory != null)
            {
                builder = builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return builder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<VectorDoc>(b =>
            {
                b.ToCollection(collection);
                b.OwnsOne(e => e.Meta);
                b.HasIndex(e => e.Embedding, "VecIndex").IsVectorIndex(VectorSimilarity.Cosine, 2, i =>
                {
                    i.AllowsFiltersOn(e => e.Flag);
                    i.AllowsFiltersOn(e => e.Label);
                    i.AllowsFiltersOn(e => e.Tags);
                });
            });
        }
    }
}
