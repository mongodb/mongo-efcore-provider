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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoPipelineFactory"/>'s <em>deferred</em> stage slots — a slot whose
/// document is constructed at Build time because its BSON shape, not merely its values, depends on
/// runtime state.
/// </summary>
/// <remarks>
/// Nothing in the tree produces a deferred slot yet, so every slot here is a hand-built fake. That is
/// deliberate: these tests pin the slot mechanism itself, independently of the first stage
/// (<c>$vectorSearch</c>) that will use it.
/// </remarks>
public class MongoPipelineFactoryDeferredSlotTests
{
    private static readonly BsonDocument MatchStage = BsonDocument.Parse("{ $match: { Age: { $gt: 21 } } }");
    private static readonly BsonDocument LimitStage = BsonDocument.Parse("{ $limit: 5 }");

    // The two services a deferred slot may need are not exercised by any fake in this file (Task 1 adds no
    // real deferred slot), so a real BsonSerializerFactory is cheap to supply and the logger is left null.
    private static MongoNativeBuildContext Context(
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        IDictionary<string, object>? additionalState = null)
        => new(
            parameterValues ?? new Dictionary<string, object?>(),
            new BsonSerializerFactory(),
            QueryLogger: null!,
            additionalState ?? new Dictionary<string, object>());

    // ------------------------------------------------------------------
    // (a) The deferred document lands at its own stage position
    // ------------------------------------------------------------------

    [Fact]
    public void Deferred_slot_document_lands_at_its_stage_position()
    {
        var deferred = BsonDocument.Parse("{ $vectorSearch: { path: 'Floats', limit: 4 } }");

        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Rendered(MatchStage),
                MongoPipelineFactory.StageSlot.Deferred(_ => (BsonDocument)deferred.DeepClone()),
                MongoPipelineFactory.StageSlot.Rendered(LimitStage)
            ],
            new PlaceholderTable());

        var result = factory.Build(Context());

        Assert.Equal(3, result.Length);
        Assert.Equal(MatchStage, result[0]);
        Assert.Equal(deferred, result[1]);
        Assert.Equal(LimitStage, result[2]);
    }

    [Fact]
    public void Deferred_slot_is_built_once_per_execution_and_sees_the_build_context()
    {
        var invocations = 0;
        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(context =>
                {
                    invocations++;
                    context.AdditionalState["built"] = invocations;
                    return new BsonDocument("$vectorSearch",
                        new BsonDocument("limit", (int)context.ParameterValues["p0"]!));
                })
            ],
            new PlaceholderTable());

        var firstState = new Dictionary<string, object>();
        var first = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = 4 }, firstState));

        var secondState = new Dictionary<string, object>();
        var second = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = 7 }, secondState));

        Assert.Equal(BsonDocument.Parse("{ $vectorSearch: { limit: 4 } }"), first[0]);
        Assert.Equal(BsonDocument.Parse("{ $vectorSearch: { limit: 7 } }"), second[0]);
        Assert.Equal(2, invocations);
        Assert.Equal(1, firstState["built"]);
        Assert.Equal(2, secondState["built"]);
    }

    // ------------------------------------------------------------------
    // (b) Sentinels INSIDE the deferred document are substituted by the same pass
    // ------------------------------------------------------------------

    [Fact]
    public void Sentinels_inside_a_deferred_document_are_substituted_by_the_same_pass()
    {
        // A pre-filter rendered at COMPILE time into the shared placeholder table, embedded verbatim inside
        // a document the deferred slot builds at EXECUTION time. Its sentinel must still resolve.
        var placeholders = new PlaceholderTable();
        var sentinel = placeholders.CreatePlaceholder("p0", serializer: null);

        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(_ => new BsonDocument("$vectorSearch", new BsonDocument
                {
                    { "path", "Floats" },
                    { "filter", new BsonDocument("is_published", sentinel.DeepClone()) },
                    { "limit", 4 }
                }))
            ],
            placeholders);

        var result = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = true }));

        Assert.Equal(
            BsonDocument.Parse("{ $vectorSearch: { path: 'Floats', filter: { is_published: true }, limit: 4 } }"),
            result[0]);
    }

    [Fact]
    public void Sentinels_beside_a_deferred_slot_are_still_substituted()
    {
        var placeholders = new PlaceholderTable();
        var sentinel = placeholders.CreatePlaceholder("p0", serializer: null);

        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(_ => BsonDocument.Parse("{ $vectorSearch: { limit: 4 } }")),
                MongoPipelineFactory.StageSlot.Rendered(
                    new BsonDocument("$match", new BsonDocument("Age", new BsonDocument("$gt", sentinel))))
            ],
            placeholders);

        var result = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = 21 }));

        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 21 } } }"), result[1]);
    }

    // ------------------------------------------------------------------
    // (c) The parameter-values-only overload refuses a template with a deferred slot
    // ------------------------------------------------------------------

    [Fact]
    public void Build_with_parameter_values_throws_when_a_deferred_slot_is_present()
    {
        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(_ => BsonDocument.Parse("{ $vectorSearch: { limit: 4 } }")),
                MongoPipelineFactory.StageSlot.Rendered(MatchStage)
            ],
            new PlaceholderTable());

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Build(new Dictionary<string, object?>()));

        Assert.Contains("deferred", exception.Message);
        Assert.Contains("MongoNativeBuildContext", exception.Message);
    }

    [Fact]
    public void Build_with_parameter_values_still_works_when_no_slot_is_deferred()
    {
        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Rendered(MatchStage),
                MongoPipelineFactory.StageSlot.Rendered(LimitStage)
            ],
            new PlaceholderTable());

        var viaParameterValues = factory.Build(new Dictionary<string, object?>());
        var viaContext = factory.Build(Context());

        Assert.Equal(new[] { MatchStage, LimitStage }, viaParameterValues);
        Assert.Equal(viaParameterValues, viaContext);
    }

    [Fact]
    public void Building_does_not_mutate_the_baked_template()
    {
        var placeholders = new PlaceholderTable();
        var sentinel = placeholders.CreatePlaceholder("p0", serializer: null);
        var baked = new BsonDocument("$match", new BsonDocument("Age", new BsonDocument("$gt", sentinel)));

        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(_ => BsonDocument.Parse("{ $vectorSearch: { limit: 4 } }")),
                MongoPipelineFactory.StageSlot.Rendered(baked)
            ],
            placeholders);

        var first = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = 21 }));
        var second = factory.Build(Context(new Dictionary<string, object?> { ["p0"] = 40 }));

        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 21 } } }"), first[1]);
        Assert.Equal(BsonDocument.Parse("{ $match: { Age: { $gt: 40 } } }"), second[1]);
    }

    // ------------------------------------------------------------------
    // (d) Paging validation still keys on a TOP-LEVEL $limit/$skip only
    // ------------------------------------------------------------------

    [Fact]
    public void Paging_validation_ignores_a_limit_nested_one_level_down()
    {
        // A $vectorSearch body carries its own `limit`, which is NOT a $limit stage. Normalizing it here
        // would rewrite it into an always-false $match for a shape that must instead surface the driver's
        // own vector-search error — see the design's limit:0 parity rule.
        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(
                    _ => BsonDocument.Parse("{ $vectorSearch: { path: 'Floats', limit: 0 } }"))
            ],
            new PlaceholderTable());

        var result = factory.Build(Context());

        Assert.Equal(BsonDocument.Parse("{ $vectorSearch: { path: 'Floats', limit: 0 } }"), result[0]);
    }

    [Fact]
    public void Paging_validation_still_normalizes_a_top_level_limit_of_zero()
    {
        // The discriminating control for the test above: the normalizer is not simply switched off — a
        // genuine top-level $limit: 0 stage is still rewritten to an always-false $match.
        var factory = new MongoPipelineFactory(
            [
                MongoPipelineFactory.StageSlot.Deferred(_ => BsonDocument.Parse("{ $vectorSearch: { limit: 4 } }")),
                MongoPipelineFactory.StageSlot.Rendered(BsonDocument.Parse("{ $limit: 0 }"))
            ],
            new PlaceholderTable());

        var result = factory.Build(Context());

        Assert.Equal(BsonDocument.Parse("{ $match: { _id: { $type: -1 } } }"), result[1]);
    }
}
