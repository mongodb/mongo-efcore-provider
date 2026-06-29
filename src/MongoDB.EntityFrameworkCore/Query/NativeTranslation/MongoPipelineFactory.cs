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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates a typed <see cref="MongoPipelineStage"/> list into a cached <see cref="BsonDocument"/>
/// template and binds per-execution parameter values via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// Constructed once per compiled query via <see cref="Create"/>. At compile time the stage-walk renders
/// each stage to a <see cref="BsonDocument"/>, baking constants inline and recording parameter sites as
/// placeholder sentinels in a shared <see cref="PlaceholderTable"/>. The resulting template is immutable.
/// </para>
/// <para>
/// At execution time <see cref="Build"/> clones the template and substitutes every sentinel with the
/// serialized runtime value. Constants are already baked — they are never touched by Build.
/// No EF-version-conditional code appears here; bridging <c>QueryContext.Parameters</c> (EF10) vs
/// <c>QueryContext.ParameterValues</c> (EF8/EF9) is the caller's responsibility.
/// </para>
/// </remarks>
internal sealed class MongoPipelineFactory
{
    private readonly IReadOnlyList<BsonDocument> _template;
    private readonly PlaceholderTable _placeholders;

    private MongoPipelineFactory(IReadOnlyList<BsonDocument> template, PlaceholderTable placeholders)
    {
        _template = template;
        _placeholders = placeholders;
    }

    // ------------------------------------------------------------------
    // Stage-walk: compile-time template construction
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders each stage in <paramref name="stages"/> to a <see cref="BsonDocument"/> using one
    /// shared <see cref="PlaceholderTable"/>, then returns a <see cref="MongoPipelineFactory"/>
    /// that can bind parameter values per execution.
    /// </summary>
    /// <param name="stages">The typed pipeline stages produced by the lowerer (Task 8).</param>
    /// <param name="renderer">The renderer used to emit <c>$match</c> bodies and scalar values.</param>
    public static MongoPipelineFactory Create(
        IReadOnlyList<MongoPipelineStage> stages,
        MongoQueryLanguageRenderer renderer)
    {
        var placeholders = new PlaceholderTable();
        var template = new BsonDocument[stages.Count];

        for (var i = 0; i < stages.Count; i++)
            template[i] = RenderStage(stages[i], renderer, placeholders);

        return new MongoPipelineFactory(template, placeholders);
    }

    private static BsonDocument RenderStage(
        MongoPipelineStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
        => stage switch
        {
            MongoMatchStage match => RenderMatch(match, renderer, placeholders),
            MongoSortStage sort => RenderSort(sort),
            MongoSkipStage skip => RenderSkip(skip, renderer, placeholders),
            MongoLimitStage limit => RenderLimit(limit, renderer, placeholders),
            MongoLookupStage lookup => RenderLookup(lookup.Lookup),
            MongoUnwindStage unwind => RenderUnwind(unwind.Lookup),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoPipelineFactory does not support stage type '{stage.GetType().Name}'.")
        };

    private static BsonDocument RenderMatch(
        MongoMatchStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
        => new BsonDocument("$match", renderer.Render(stage.Predicate, placeholders));

    private static BsonDocument RenderSort(MongoSortStage stage)
    {
        var body = new BsonDocument();
        foreach (var ordering in stage.Orderings)
        {
            if (ordering.KeySelector is not MongoFieldExpression field)
                throw new NativeTranslationNotSupportedException(
                    $"$sort key selector must be a MongoFieldExpression; got '{ordering.KeySelector.GetType().Name}'. "
                    + "Non-field sort keys should have been rejected by the translator.");

            body.Add(field.ElementName, ordering.Ascending ? BsonInt32.Create(1) : BsonInt32.Create(-1));
        }

        return new BsonDocument("$sort", body);
    }

    private static BsonDocument RenderSkip(
        MongoSkipStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
        => new BsonDocument("$skip", renderer.RenderValue(stage.Offset, placeholders));

    private static BsonDocument RenderLimit(
        MongoLimitStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
        => new BsonDocument("$limit", renderer.RenderValue(stage.Limit, placeholders));

    private static BsonDocument RenderLookup(LookupExpression lookup)
        => new BsonDocument("$lookup", new BsonDocument
        {
            { "from", lookup.From },
            { "localField", lookup.LocalField },
            { "foreignField", lookup.ForeignField },
            { "as", lookup.As }
        });

    private static BsonDocument RenderUnwind(LookupExpression lookup)
        => new BsonDocument("$unwind", new BsonDocument
        {
            { "path", "$" + lookup.As },
            { "preserveNullAndEmptyArrays", true }
        });

    // ------------------------------------------------------------------
    // Per-execution binding
    // ------------------------------------------------------------------

    /// <summary>
    /// Clones the compiled template and substitutes every placeholder sentinel with the
    /// serialized runtime value for the corresponding entry in <see cref="_placeholders"/>.
    /// </summary>
    /// <param name="parameterValues">
    /// The named parameter values for this execution. Must contain an entry for every
    /// parameter name recorded in <see cref="_placeholders"/>; a missing key is a bug
    /// in the caller and throws <see cref="InvalidOperationException"/>.
    /// </param>
    /// <returns>A freshly materialized <see cref="BsonDocument"/> array ready to send to the server.</returns>
    public BsonDocument[] Build(IReadOnlyDictionary<string, object?> parameterValues)
    {
        var result = new BsonDocument[_template.Count];
        for (var i = 0; i < _template.Count; i++)
            result[i] = SubstituteDocument((BsonDocument)_template[i].DeepClone(), parameterValues);

        // Validate paging bounds: MongoDB rejects $limit <= 0 and $skip < 0 server-side;
        // throw the EF-correct exception (ArgumentOutOfRangeException) client-side to match
        // driver-LINQ behaviour (which threw it client-side for Take(0)).
        ValidatePagingStages(result);

        return result;
    }

    private static void ValidatePagingStages(BsonDocument[] pipeline)
    {
        foreach (var stage in pipeline)
        {
            if (stage.TryGetValue("$limit", out var limitValue))
            {
                var limit = limitValue.ToInt64();
                if (limit <= 0)
                    throw new ArgumentOutOfRangeException("count",
                        $"Take must be positive; got {limit}.");
            }
            else if (stage.TryGetValue("$skip", out var skipValue))
            {
                var skip = skipValue.ToInt64();
                if (skip < 0)
                    throw new ArgumentOutOfRangeException("count",
                        $"Skip must be non-negative; got {skip}.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Deep-walk substitution
    // ------------------------------------------------------------------

    private BsonDocument SubstituteDocument(
        BsonDocument doc,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        // Work on the element list directly so we can replace in place.
        for (var i = 0; i < doc.ElementCount; i++)
        {
            var element = doc.GetElement(i);
            var newValue = SubstituteValue(element.Value, parameterValues);
            if (!ReferenceEquals(newValue, element.Value))
                doc[i] = newValue;
        }

        return doc;
    }

    private BsonValue SubstituteValue(
        BsonValue value,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        // Test for sentinel BEFORE recursing — a sentinel is a one-element BsonDocument.
        if (PlaceholderTable.TryGetPlaceholderIndex(value, out var index))
            return SerializeParameter(index, parameterValues);

        return value switch
        {
            BsonDocument doc => SubstituteDocument(doc, parameterValues),
            BsonArray array => SubstituteArray(array, parameterValues),
            _ => value   // scalar — already baked constant, no substitution needed
        };
    }

    private BsonArray SubstituteArray(
        BsonArray array,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        for (var i = 0; i < array.Count; i++)
            array[i] = SubstituteValue(array[i], parameterValues);
        return array;
    }

    // ------------------------------------------------------------------
    // Parameter value serialization
    // ------------------------------------------------------------------

    private BsonValue SerializeParameter(
        int index,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        var (name, serializer) = _placeholders.Entries[index];

        if (!parameterValues.TryGetValue(name, out var rawValue))
            throw new InvalidOperationException(
                $"MongoPipelineFactory.Build: parameter '{name}' (placeholder index {index}) "
                + "is not present in parameterValues. This is a bug in the query compilation pipeline.");

        // Property-less primitive (e.g. Skip/Take count): serialize via BsonValue.Create.
        if (serializer is null)
            return BsonValue.Create(rawValue);

        // Coerce the CLR value to the serializer's expected type, then serialize through the shared
        // "v"-wrapper block so a run-time parameter and a compile-time constant of the same value emit
        // identical BSON. The compile-time path (MongoQueryLanguageRenderer.ToBsonValue) coerces to the
        // property's ClrType; here we coerce to the serializer's ValueType — these differ for
        // value-converted properties. No try/catch: the value was already validated at translation time.
        rawValue = BsonValueSerializer.Coerce(serializer.ValueType, rawValue);
        return BsonValueSerializer.SerializeThroughWriter(serializer, rawValue);
    }
}
