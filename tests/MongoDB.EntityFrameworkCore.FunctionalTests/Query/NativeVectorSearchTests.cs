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
/// EF-322 VectorSearch slice, Task 4 — the behavioural net for the NATIVE <c>$vectorSearch</c> path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every data assertion in this file pins ORDER (or a score), never a row count alone.</b> The failure this
/// slice is built to make unreachable — both gates open, no <c>$vectorSearch</c> stage emitted — returns the
/// RIGHT NUMBER OF ROWS, in INSERTION order rather than score order, with no exception. A count assertion
/// cannot tell the two apart; an ordered-label assertion can. The seed below is therefore inserted in an order
/// that is deliberately NOT score order (see <see cref="InsertionOrder"/> vs <see cref="ScoreOrder"/>), and
/// <see cref="Bare_vector_search_returns_score_order"/> asserts that non-equality explicitly so a future edit
/// to the seed cannot make this whole file vacuous.
/// </para>
/// <para>
/// <b>"Goes native" is proven by <see cref="MongoQueryMode.NativeOnly"/> succeeding, never by MQL shape.</b>
/// Before this slice, a <c>$vectorSearch</c> stage in captured MQL reliably proved the query went to the
/// driver-LINQ fallback, because only that path could emit one. The moment the native path emits a
/// structurally identical stage — which is exactly what buys zero MQL re-baselining — that inference dies. So
/// every routing claim here is a <c>NativeOnly</c> run that succeeds (or, for a decline, one that throws
/// <see cref="NativeTranslationNotSupportedException"/>), and
/// <see cref="Vector_search_emits_the_stage_first"/> is captioned as a STAGE-ORDER pin only.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeVectorSearchTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    // The query vector every test searches with. Cosine similarity against the seed below is therefore
    // "how close is the document's embedding to the x-axis", which makes the expected order hand-checkable.
    private static readonly float[] QueryVector = [1.0f, 0.0f];

    // Score order for QueryVector: A (sim 1.000), B (0.981), C (0.894), D (0.707), E (0.000).
    private static readonly string[] ScoreOrder = ["A", "B", "C", "D", "E"];

    // The order the documents are INSERTED in — deliberately different from ScoreOrder, and different in the
    // FIRST position, which is what makes an order assertion discriminate. A dropped $vectorSearch stage
    // yields this order (and, with no $limit either, all five rows).
    private static readonly string[] InsertionOrder = ["D", "C", "A", "B", "E"];

    // ---------------------------------------------------------------------------------------------------
    // 1-5: the capability. Ordered labels only; NativeOnly succeeding is the routing proof.
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

        // The non-vacuity guard for this whole file: if the seed were ever changed so that insertion order
        // happened to match score order, every ordered assertion here would stop discriminating.
        Assert.NotEqual(InsertionOrder.Take(4), labels);
    }

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Pre_filter_restricts_and_preserves_score_order(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Flag is true on B, C and E only. The pre-filter runs INSIDE $vectorSearch, so the score ordering of
        // the surviving documents is preserved: B (0.981), C (0.894), E (0.000).
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

        // Exact (ENN) changes the SHAPE of the emitted $vectorSearch body — `exact: true` is present and
        // `numCandidates` is absent — which is the reason the stage has to be constructed at Build time rather
        // than substituted into a baked template.
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

        // The other body shape: an explicit numCandidates, rather than the limit*10 the driver's builder
        // derives when it is left null.
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

        // A Where composed AFTER the vector search records into PipelineOps exactly as it would over a plain
        // collection scan (VectorSearch is a root anchor, not a terminal), and the resulting $match runs after
        // $vectorSearch — so score order survives it.
        var labels = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Where(e => e.Label != "C")
            .ToList()
            .Select(e => e.Label);

        Assert.Equal(["A", "B", "D"], labels);
    }

    // ---------------------------------------------------------------------------------------------------
    // 6-7: the __score projection leaf (Task 5). Both spellings, both proven by NativeOnly SUCCEEDING.
    //
    // These two are also the first EXECUTION of the read-back chain the design settled by reading: a native
    // $project emitting `Score: "$__score"`, whose alias has no backing IProperty, read raw by
    // MongoProjectionBindingRemovingExpressionVisitor via BsonBinding.CreateGetElementValue.
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
    // EF-412: the entity-and-score projection on the LATE-FALLBACK leg.
    //
    // `new { Doc = e, Score = ... }` mixes a WHOLE-ROOT-ENTITY leaf with the synthetic __score leaf. EF-412
    // made that shape go native ({"Doc": "$$ROOT", "Score": "$__score"}); before it, an entity-bearing
    // projection was never push-downable (ProjectionAnalyzer.CanPushDown was false) and this only ever ran
    // through the mixed-strip path. So the late-fallback leg is NEW EXPOSURE from EF-412, and it is not
    // covered by the c/Total case in NativeComputedProjectionTests: that one proved the driver-LINQ bridge
    // renders the same member-name alias for a plain arithmetic sibling, which says nothing about whether it
    // also re-emits the $addFields{__score} companion this shape's Score leaf depends on.
    //
    // The captured local in StartsWith is the trigger: it binds natively and then throws at RENDER time in
    // MongoQueryLanguageRenderer.RenderRegex, i.e. AFTER CompileShapedQuery has already built the
    // alias-addressed native shaper — the same mechanism Ef362OwnedHopArrayProjectionTests documents.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Entity_and_score_projection_behind_a_parameterized_filter_reads_correct_values()
    {
        // A CAPTURED LOCAL, not a constant. A constant StartsWith renders natively and would never reach the
        // late-fallback leg, leaving this test falsely green.
        var prefix = "A";

        // THE DISCRIMINATOR. NativeOnly forbids the fallback, so this throw is what proves the decline happens
        // MID-COMPILE for this exact query and therefore that the Native leg below really is the driver-LINQ
        // bridge running underneath an already alias-addressed native shaper. Without it, a shape that simply
        // went fully native would satisfy every assertion below and prove nothing about the fallback.
        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly))
        {
            var declined = nativeOnly.Docs
                .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
                .Where(e => e.Label.StartsWith(prefix))
                .Select(e => new { Doc = e, Score = EF.Property<double>(e, "__score") });
            var declineReason = Assert.Throws<NativeTranslationNotSupportedException>(() => declined.ToList());

            // And the decline must be the LATE, RENDER-time one, not an early routing decline. If the shape
            // ever stops routing native at translate time (Route == Fallback), the throw above would still
            // happen — it would just come from the projected-query gate instead, the query would take the
            // pre-EF-412 MIXED path, and this test would silently stop covering the late-fallback leg it
            // exists for. MEASURED: this message comes from MongoQueryLanguageRenderer.RenderRegex, which runs
            // inside TryBuildNativeFactory, i.e. AFTER the alias-addressed shaper has been built.
            Assert.Contains("Only constant regex terms are natively representable", declineReason.Message);
        }

        using var db = CreateContext(MongoQueryMode.Native, out var spyLogger);

        var rows = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .Where(e => e.Label.StartsWith(prefix))
            .Select(e => new { Doc = e, Score = EF.Property<double>(e, "__score") })
            .ToList();

        // VALUES on both leaves, because both can fail silently and independently. A lost entity leaf presents
        // as a null/default Doc; a lost $addFields{__score} companion presents as Score == 0, not an exception.
        var row = Assert.Single(rows);
        Assert.Equal("A", row.Doc.Label);
        Assert.Equal(1.0, row.Doc.Weight);
        Assert.Equal("note-A", row.Doc.Meta.Note); // the owned hop inside the entity leaf survives too
        Assert.Equal(1.0, row.Score, 3);           // "A" IS the query vector, so its cosine score is exactly 1

        // WHY this passes with no code change, recorded so the negative result is not re-derived. The
        // driver-LINQ bridge re-emits BOTH halves the native shaper reads by: the $addFields{__score}
        // companion AND a $project whose keys are the projection MEMBER names ("Doc", "Score") with "$$ROOT"
        // for the entity leaf. So no ProjectionAliasTier.DocumentPath alias override exists,
        // HasDocumentPathAliasOverride stays false, and ShouldStripBareProjectionOnFallback correctly does not
        // fire — stripping here would be actively WRONG, since it would hand whole documents to a visitor with
        // ReadsUnprojectedDocuments == false. Asserted on the MQL because the two halves fail independently and
        // both fail SILENTLY (a lost $addFields gives Score == 0, a renamed alias gives a null entity).
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$addFields\" : { \"__score\" : { \"$meta\" : \"vectorSearchScore\" } } }", mql);
        Assert.Contains("\"Doc\" : \"$$ROOT\"", mql);
        Assert.Contains("\"Score\" : \"$__score\"", mql);
    }

    // Pins SCORE and ORDER, never a row count. A dropped $vectorSearch stage returns the right number of rows
    // in insertion order; a dropped $addFields companion (or a mis-read alias) returns rows whose scores are
    // not strictly descending — and, on this seed, whose top row is not "A".
    private static void AssertScoreOrdered(IEnumerable<(string Label, double Score)> rows)
    {
        var list = rows.ToList();

        Assert.Equal(ScoreOrder.Take(4), list.Select(r => r.Label));

        // "A" IS the query vector, so its normalized cosine score is 1. This is the strongest single value
        // available here and it is what proves the leaf carries the real $meta score rather than a default.
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
    // 8: the streaming __score-skip. The slice's one genuinely open EXECUTION risk (design §9.4).
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Whole_entity_vector_search_streams_and_skips_the_score_field(MongoQueryMode mode)
    {
        // A bare (whole-entity) vector search materializes through the ONE-PASS STREAMING materializer, over
        // documents that carry an extra top-level "__score" element the entity model knows nothing about — the
        // $addFields companion is emitted unconditionally, whether or not anything projects the score. Nothing
        // in the model maps that element, so MongoStreamingEntityMaterializerRewriter.BuildFillLoop's forward
        // name-dispatch must fall to its reader.SkipValue() base case for it. Before this test that base case
        // was established by READING the rewriter; it had never been EXECUTED, because nothing carrying a
        // $vectorSearch went native.
        //
        // VectorDoc owns a Meta (an OwnsOne single reference) precisely so this mirrors the specification
        // suite's Book/Preface shape, which is what makes the 70 bare spec cases stream too.
        using var db = CreateContext(mode);

        // The PREMISE, asserted directly rather than assumed: if this entity were NOT streaming-eligible the
        // query would materialize through the DOM shaper and this test would silently stop measuring the thing
        // it exists to measure.
        var entityType = db.Model.FindEntityType(typeof(VectorDoc))!;
        Assert.True(StreamingEligibility.IsEligible(entityType),
            "VectorDoc must be streaming-eligible or this test does not exercise the streaming materializer.");

        var docs = db.Docs
            .VectorSearch(e => e.Embedding, QueryVector, limit: 4)
            .ToList();

        // Order first — a dropped $vectorSearch stage returns the right number of rows in insertion order.
        Assert.Equal(ScoreOrder.Take(4), docs.Select(d => d.Label));

        // Then every scalar the entity DOES map, including the ones stored AFTER "__score" would be read in
        // document order, plus the owned sub-document: a mis-skipped unknown element derails the forward reader
        // for everything that follows it, so asserting the whole shape is what makes the skip observable.
        Assert.Equal([1.0, 2.0, 3.0, 4.0], docs.Select(d => d.Weight));
        Assert.Equal([false, true, true, false], docs.Select(d => d.Flag));
        Assert.Equal(["keep", "skip", "keep", "keep"], docs.Select(d => d.Tags.Single()));
        Assert.Equal(["note-A", "note-B", "note-C", "note-D"], docs.Select(d => d.Meta.Note));
        Assert.Equal([1, 2, 3, 4], docs.Select(d => d.Meta.Pages));
        Assert.All(docs, d => Assert.Equal(2, d.Embedding.Length));
        Assert.All(docs, d => Assert.NotEqual(ObjectId.Empty, d.Id));
    }

    // ---------------------------------------------------------------------------------------------------
    // 14-15: the guards. Each of the three guards of the recognizer has a case here whose ONLY reason to
    // decline is that guard, so mutating the guard flips exactly this test.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Mql_Field_for_a_non_score_element_declines()
    {
        // GUARD A — the literal "__score" element name. `Weight` is a real, DOUBLE-typed stored element, so the
        // CLR-type guard would admit it and the receiver is the selector's own parameter: the element NAME is
        // the ONLY thing declining this leaf. Widen the name to "any string" and this shape goes native, so the
        // NativeOnly leg below stops throwing.
        using (var db = CreateContext(MongoQueryMode.Native))
        {
            // Falls back to driver-LINQ, which resolves Mql.Field correctly — a graceful decline, not a
            // failure. What is asserted is the ORDER of the values, never the row count, so a dropped
            // $vectorSearch stage is still caught here.
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

        // GUARD B — the double/double? CLR type. The element name IS "__score", so guard A admits it and the
        // requested CLR type is the ONLY thing declining it. The native read-back IGNORES any serializer the
        // caller supplied and reads through CreateTypeSerializer(<requested type>), so admitting a non-double
        // would read the score through a serializer the fallback path would never have used. Drop the type
        // restriction and this goes native, so the NativeOnly leg below stops throwing.
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
        // GUARD C — Select.VectorSearch is not null. There is no vector search here, so nothing emits the
        // $addFields{__score} companion and no document carries a "__score" element. Admitting the leaf would
        // emit a $project alias reading an element no stage writes. Drop this guard and the shape goes native,
        // and the NativeOnly leg below stops throwing NativeTranslationNotSupportedException.
        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Docs
                    .Select(e => new { e.Label, Score = EF.Property<double>(e, "__score") })
                    .ToList());
        }

        // The default mode's disposition is UNCHANGED by this slice: the projection declines, the query falls
        // back to driver-LINQ, and whatever driver-LINQ makes of a "__score" that nothing wrote is what a user
        // sees — the same outcome, from the same path, as an explicit DriverLinq run. Asserting the two modes
        // AGREE pins "the guard kept this on the fallback path" without pinning the driver's own behaviour for
        // an element that does not exist, which is not this slice's contract.
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
    // 9-10: the diagnostics, raised from the NATIVE path (the shared VectorSearchStageBuilder / the
    // AdditionalState the executor reads back).
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Zero_results_logs_the_diagnostic_natively()
    {
        // The zero-results warning is raised by QueryingEnumerable from AdditionalState, which on the native
        // path is filled by the deferred $vectorSearch slot at Build time. NativeOnly forbids the fallback, so
        // this can only be the native path's own diagnostic.
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
        // Index resolution (and this warning) happen inside VectorSearchStageBuilder.Resolve, which the native
        // deferred slot and the driver-LINQ bridge both call — so raising it here proves the native path runs
        // the same resolution, in the same place, with the same logger.
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
    // 11-12: exception parity across all three modes. These pin the reason VectorSearchStageBuilder is split
    // three ways and keeps its reflection boundary — not decoration.
    // ---------------------------------------------------------------------------------------------------

    [AtlasTheory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Exact_with_num_candidates_throws_InvalidOperationException_in_every_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        // Thrown from ORDINARY code in VectorSearchStageBuilder.Resolve, BEFORE CreateStage's reflection
        // Invoke — so it surfaces unwrapped. Moving the guard inside the reflection boundary would turn this
        // into a TargetInvocationException on BOTH paths.
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

        // A non-positive limit is rejected by the DRIVER's own builder, inside the reflection Invoke that both
        // paths share — hence TargetInvocationException wrapping ArgumentOutOfRangeException('limit'), pinned
        // here as the OUTER type AND the inner type/parameter name. Two ways to break this parity, both
        // deliberately not taken: adding BindingFlags.DoNotWrapExceptions to CreateStage's Invoke (which would
        // unwrap it on the released driver-LINQ path too), and teaching
        // MongoPipelineFactory.ValidatePagingStages to look inside a $vectorSearch body (which would produce
        // the Take(0)-style ArgumentOutOfRangeException('count') instead, CREATING a divergence).
        var outer = Assert.Throws<TargetInvocationException>(
            () => db.Docs.VectorSearch(e => e.Embedding, QueryVector, limit: 0).ToList());

        var inner = Assert.IsType<ArgumentOutOfRangeException>(outer.InnerException);
        Assert.Equal("limit", inner.ParamName);
    }

    // ---------------------------------------------------------------------------------------------------
    // 13: stage order — a SERVER constraint, not a routing proof.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Vector_search_emits_the_stage_first()
    {
        // NOT A ROUTING PROOF. Both the native path and the driver-LINQ fallback emit a structurally identical
        // $vectorSearch + $addFields pair — that is by design, and it is what keeps every committed AssertMql
        // baseline unchanged. So this test says nothing about WHICH path ran; it pins only that
        // $vectorSearch is stage 0 (the server rejects it anywhere else — Location40602) and that the
        // __score companion immediately follows it, ahead of anything the user composed.
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
    // 16: EF-382 follow-up — the array-field-contains-value pre-filter now goes NATIVE.
    // ---------------------------------------------------------------------------------------------------

    [AtlasFact]
    public void Array_contains_pre_filter_now_goes_native_with_correct_rows()
    {
        // `Tags.Contains("keep")` is an ARRAY-FIELD-contains-VALUE predicate. Before EF-382 the native
        // predicate translator declined it (its Contains arm handled only the opposite shape — a collection
        // of values containing a FIELD), so this pre-filter fell back to driver-LINQ. EF-382 added the mirror
        // arm (MongoArrayContainsExpression), so this now goes native too — proven by NativeOnly succeeding,
        // never by MQL shape (the two paths render an identical $vectorSearch.filter for this predicate).
        using (var db = CreateContext(MongoQueryMode.Native))
        {
            var labels = db.Docs
                .VectorSearch(e => e.Embedding, e => e.Tags.Contains("keep"), QueryVector, limit: 4)
                .ToList()
                .Select(e => e.Label);

            // Tags contains "keep" on A, C and D. Score order among those: A, C, D — the exact REVERSE of
            // their insertion order (D, C, A), so this discriminates a dropped stage as sharply as possible.
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
    // 16a (EF-382 review fix): the fallback-correctness tripwire, restored on a DIFFERENT still-declining
    // shape. A PARAMETERIZED Contains item (a captured local, not a literal constant) still declines: the
    // new arm's dispatch guard requires `Unwrap(containsItem) is ConstantExpression`
    // (MongoExpressionTranslator.cs), so this falls through unmatched to the $in arm, which declines it
    // exactly as it always has (the item doesn't resolve via TryResolveMember either). Unlike the string-
    // transform shape below, driver-LINQ's own Contains-over-array-field translator renders this as the
    // SAME $expr-free `{ Tags: <value> }` query-dialect form Atlas Search's vectorSearch filter accepts, so
    // "declines → driver-LINQ fallback → correct rows" is still a real, exercised disposition for vector-
    // search pre-filters — this is the shape that proves it now that the literal-constant example (above)
    // goes native.
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
    // 16b: a genuinely still-unsupported pre-filter shape — a string transform (the native predicate
    // translator has no support for ToUpper/ToLower et al., per the Query AGENTS.md "computed long tail"
    // note). UNLIKE the array-contains shape EF-382 just promoted, this is NOT a "declines gracefully with
    // correct rows" example: ANY computed (non-query-dialect) pre-filter needs $expr, and Atlas Search's
    // vectorSearch `filter`/`parentFilter` REJECTS $expr outright (a server constraint, not a driver or
    // provider limitation) — so this throws identically in every mode, mirroring
    // Limit_zero_throws_identically_in_every_mode's pattern. Native declines at TRANSLATE time (no server
    // round trip); the driver-LINQ fallback (used under Native, and always under DriverLinq) builds the SAME
    // rejected $expr and the SERVER throws.
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

        // NativeOnly forbids the driver-LINQ fallback, so this must throw from EF's own query compilation —
        // before ever reaching the server — not the server-side MongoCommandException the other modes hit.
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

    // Seeds ONCE per test-class run (the class fixture gives one database; xUnit creates a fresh test-class
    // instance per test, and this suite runs serially). Documents are inserted FIRST and the vector index is
    // built afterwards — the order the VectorSearchReturnedZeroResults warning itself recommends, so a query
    // does not race an index that is still ingesting.
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

            // Weight ascends in SCORE order (A=1 … E=5), NOT in insertion order — so an assertion over Weight
            // discriminates a dropped $vectorSearch stage exactly as an assertion over Label does. It exists
            // for the element-NAME guard case of Mql_Field_for_a_non_score_element_declines, which needs a real
            // stored element that is DOUBLE-typed (so only the name guard can be what declines it).
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

        // An OWNED single reference, mirroring the specification suite's Book/Preface. It is what makes a bare
        // whole-entity query here representative of the 70 bare spec cases: an owned reference keeps the entity
        // streaming-eligible (see StreamingEligibility.IsEligible), so the query materializes through the
        // one-pass streaming reader over documents carrying the extra "__score" element.
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
