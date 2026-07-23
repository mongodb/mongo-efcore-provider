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
    /// <param name="stages">The typed pipeline stages produced by the lowerer.</param>
    /// <param name="renderer">The renderer used to emit <c>$match</c> bodies and scalar values.</param>
    public static MongoPipelineFactory Create(
        IReadOnlyList<MongoPipelineStage> stages,
        MongoQueryLanguageRenderer renderer)
    {
        var placeholders = new PlaceholderTable();
        var template = new List<BsonDocument>(stages.Count);

        foreach (var stage in stages)
        {
            if (stage is MongoUnionWithStage unionWith)
                template.AddRange(RenderUnionWith(unionWith, renderer, placeholders));
            else if (stage is MongoSetDifferenceStage setDiff)
                template.AddRange(RenderSetDifference(setDiff, renderer, placeholders));
            else
                template.Add(RenderStage(stage, renderer, placeholders));
        }

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
            MongoSkipStage skip => RenderSkip(skip, placeholders),
            MongoLimitStage limit => RenderLimit(limit, placeholders),
            MongoLookupStage lookup => RenderLookup(lookup.Lookup),
            MongoUnwindStage unwind => RenderUnwind(unwind.Lookup, unwind.PreserveNullAndEmptyArrays),
            MongoUnwindFieldStage unwindField => unwindField.IncludeArrayIndex is null
                ? new BsonDocument("$unwind", "$" + unwindField.ElementPath)
                : new BsonDocument("$unwind", new BsonDocument
                {
                    { "path", "$" + unwindField.ElementPath },
                    { "includeArrayIndex", unwindField.IncludeArrayIndex }
                }),
            MongoReplaceRootStage replaceRoot => new BsonDocument("$replaceRoot",
                new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray
                {
                    "$" + replaceRoot.NewRoot,
                    new BsonDocument
                    {
                        { MongoReplaceRootStage.OwnerKeyField, "$_id" },
                        { MongoReplaceRootStage.OrdinalField, "$" + MongoReplaceRootStage.OrdinalField }
                    }
                }))),
            MongoProjectStage project => RenderProject(project, placeholders),
            MongoCountStage count => new BsonDocument("$count", count.OutputField),
            MongoGroupAccumulatorStage group => RenderGroup(group, placeholders),
            MongoGroupStage keyedGroup => RenderKeyedGroup(keyedGroup, placeholders),
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

    private static BsonDocument RenderProject(MongoProjectStage stage, PlaceholderTable placeholders)
    {
        var body = new BsonDocument();
        foreach (var projection in stage.Projections)
        {
            body.Add(projection.Alias, MongoAggregationExpressionRenderer.Render(projection.Expression, placeholders));
        }

        // Suppress the default _id unless the projection deliberately emits an "_id" output field.
        if (!body.Contains("_id"))
        {
            body.Add("_id", 0);
        }

        return new BsonDocument("$project", body);
    }

    private static BsonDocument RenderGroup(MongoGroupAccumulatorStage stage, PlaceholderTable placeholders)
        => new BsonDocument("$group", new BsonDocument
        {
            { "_id", BsonNull.Value },
            { stage.OutputField, new BsonDocument(
                stage.Accumulator, MongoAggregationExpressionRenderer.Render(stage.Operand, placeholders)) }
        });

    private static BsonDocument RenderKeyedGroup(MongoGroupStage stage, PlaceholderTable placeholders)
    {
        var grouping = stage.Grouping;

        BsonValue id;
        if (grouping.IsCompositeKey)
        {
            var idDoc = new BsonDocument();
            foreach (var part in grouping.Key)
                idDoc.Add(part.Name, MongoAggregationExpressionRenderer.Render(part.FieldRef, placeholders));
            id = idDoc;
        }
        else
        {
            id = MongoAggregationExpressionRenderer.Render(grouping.Key[0].FieldRef, placeholders);
        }

        var group = new BsonDocument { { "_id", id } };
        foreach (var acc in grouping.Accumulators)
        {
            var operand = acc.Operand is null
                ? (BsonValue)1
                : MongoAggregationExpressionRenderer.Render(acc.Operand, placeholders);
            group.Add(acc.OutputField, new BsonDocument(acc.Operator, operand));
        }

        return new BsonDocument("$group", group);
    }

    private static BsonDocument RenderSkip(
        MongoSkipStage stage,
        PlaceholderTable placeholders)
        => new BsonDocument("$skip", MongoValueRenderer.RenderValue(stage.Offset, placeholders));

    private static BsonDocument RenderLimit(
        MongoLimitStage stage,
        PlaceholderTable placeholders)
        => new BsonDocument("$limit", MongoValueRenderer.RenderValue(stage.Limit, placeholders));

    private static BsonDocument RenderLookup(LookupExpression lookup)
        => new BsonDocument("$lookup", new BsonDocument
        {
            { "from", lookup.From },
            { "localField", lookup.LocalField },
            { "foreignField", lookup.ForeignField },
            { "as", lookup.As }
        });

    private static BsonDocument RenderUnwind(LookupExpression lookup, bool preserveNullAndEmptyArrays)
        => new BsonDocument("$unwind", new BsonDocument
        {
            { "path", "$" + lookup.As },
            { "preserveNullAndEmptyArrays", preserveNullAndEmptyArrays }
        });

    // Renders a $unionWith over the operand's nested pipeline into the SAME placeholder table (so a
    // parameter inside the operand substitutes at Build time), then, for Union, the full-document dedup.
    private static IEnumerable<BsonDocument> RenderUnionWith(
        MongoUnionWithStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        var innerPipeline = new BsonArray();
        foreach (var operandStage in stage.OperandStages)
            innerPipeline.Add(RenderStage(operandStage, renderer, placeholders));   // shared placeholders

        yield return new BsonDocument("$unionWith", new BsonDocument
        {
            { "coll", stage.OperandCollectionName },
            { "pipeline", innerPipeline }
        });

        if (stage.Dedup)
        {
            yield return new BsonDocument("$group", new BsonDocument("_id", "$$ROOT"));
            yield return new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_id"));
        }
    }

    // Renders a synthesized Intersect/Except as a source-tagging pipeline. Both operands are the SAME
    // collection, so full-document ($$ROOT) value-equality is well-defined. Each side is deduped and tagged
    // (_a for the outer/first operand, _b for the inner/second), unioned, re-unified by full document
    // ($group{_id:"$_doc"}), then discriminated by the final $match. Intersect keeps rows present in both
    // (_a && _b); Except keeps rows in the first operand only (_a && !_b). The operand stages render into the
    // SAME placeholder table (a parameter inside the operand substitutes at Build time). _a/_b are siblings
    // of the wrapped document (under _doc), so they never collide with real entity fields.
    private static IEnumerable<BsonDocument> RenderSetDifference(
        MongoSetDifferenceStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        static BsonDocument Tag(bool a, bool b) => new("$project", new BsonDocument
        {
            { "_id", 0 },
            { "_doc", "$_id" },
            { "_a", new BsonDocument("$literal", a) },
            { "_b", new BsonDocument("$literal", b) }
        });

        // Outer (first operand) side: dedup + tag as _a.
        yield return new BsonDocument("$group", new BsonDocument("_id", "$$ROOT"));
        yield return Tag(a: true, b: false);

        // Inner (second operand) side, rendered into the shared placeholder table, itself deduped + tagged.
        var innerPipeline = new BsonArray();
        foreach (var operandStage in stage.OperandStages)
            innerPipeline.Add(RenderStage(operandStage, renderer, placeholders));   // shared placeholders
        innerPipeline.Add(new BsonDocument("$group", new BsonDocument("_id", "$$ROOT")));
        innerPipeline.Add(Tag(a: false, b: true));
        yield return new BsonDocument("$unionWith", new BsonDocument
        {
            { "coll", stage.OperandCollectionName },
            { "pipeline", innerPipeline }
        });

        // Re-unify by full document; collapse the side flags (BSON false < true, so $max over the group is
        // "present on that side").
        yield return new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$_doc" },
            { "_a", new BsonDocument("$max", "$_a") },
            { "_b", new BsonDocument("$max", "$_b") }
        });

        // Discriminate. Intersect: in both (_b true). Except: in the first only (_b false).
        var keepInB = stage.Kind == MongoSetOperationKind.Intersect;
        yield return new BsonDocument("$match", new BsonDocument { { "_a", true }, { "_b", keepInB } });

        // Restore the plain document (the re-unify $group put _doc under _id).
        yield return new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_id"));
    }

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

            if (stage.TryGetValue("$unionWith", out var unionWithValue)
                && unionWithValue.AsBsonDocument.TryGetValue("pipeline", out var innerPipeline))
            {
                ValidatePagingStages(innerPipeline.AsBsonArray.Select(d => d.AsBsonDocument).ToArray());
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
        var (name, serializer, isArray) = _placeholders.Entries[index];

        if (!parameterValues.TryGetValue(name, out var rawValue))
            throw new InvalidOperationException(
                $"MongoPipelineFactory.Build: parameter '{name}' (placeholder index {index}) "
                + "is not present in parameterValues. This is a bug in the query compilation pipeline.");

        // Property-less primitive (e.g. Skip/Take count): serialize via BsonValue.Create.
        if (serializer is null)
            return BsonValue.Create(rawValue);

        // Array placeholder (a parameterized $in/$nin collection): serialize each element through
        // the field's element serializer into a BsonArray.
        if (isArray)
        {
            var array = new BsonArray();
            foreach (var element in (System.Collections.IEnumerable)rawValue!)
            {
                var coerced = BsonValueSerializer.Coerce(serializer.ValueType, element);
                array.Add(BsonValueSerializer.SerializeThroughWriter(serializer, coerced));
            }

            return array;
        }

        // Coerce the CLR value to the serializer's expected type, then serialize through the shared
        // "v"-wrapper block so a run-time parameter and a compile-time constant of the same value emit
        // identical BSON. The compile-time path (MongoQueryLanguageRenderer.ToBsonValue) coerces to the
        // property's ClrType; here we coerce to the serializer's ValueType — these differ for
        // value-converted properties. No try/catch: the value was already validated at translation time.
        rawValue = BsonValueSerializer.Coerce(serializer.ValueType, rawValue);
        return BsonValueSerializer.SerializeThroughWriter(serializer, rawValue);
    }
}
