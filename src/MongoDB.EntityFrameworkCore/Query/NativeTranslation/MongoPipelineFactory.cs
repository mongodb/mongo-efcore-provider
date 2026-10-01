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
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a typed <see cref="MongoPipelineStage"/> list once per compiled query into a template of stage slots,
/// then binds per-execution parameter values via <see cref="Build(IReadOnlyDictionary{string, object})"/> /
/// <see cref="Build(in MongoNativeBuildContext)"/>.
/// </summary>
/// <remarks>
/// Constants are baked inline; parameters become sentinels in a shared <see cref="PlaceholderTable"/> that Build
/// substitutes in a cloned template. A slot whose BSON shape (not just values) depends on runtime state is
/// deferred to a builder run at Build time; its output goes through the same substitution pass. Deferred slots
/// require <see cref="Build(in MongoNativeBuildContext)"/>; the values-only overload throws rather than emit a
/// pipeline with a hole.
/// </remarks>
internal sealed class MongoPipelineFactory
{
    private readonly IReadOnlyList<StageSlot> _template;
    private readonly PlaceholderTable _placeholders;
    private readonly bool _hasDeferredSlot;

    internal MongoPipelineFactory(IReadOnlyList<StageSlot> template, PlaceholderTable placeholders)
    {
        _template = template;
        _placeholders = placeholders;

        foreach (var slot in template)
        {
            if (slot.IsDeferred)
            {
                _hasDeferredSlot = true;
                break;
            }
        }
    }

    /// <summary>
    /// One template slot: a <see cref="BsonDocument"/> rendered at <see cref="Create"/> time, or a builder
    /// deferred to <see cref="Build(in MongoNativeBuildContext)"/> for a stage whose key set depends on runtime
    /// state.
    /// </summary>
    internal readonly struct StageSlot
    {
        private readonly BsonDocument? _document;
        private readonly Func<MongoNativeBuildContext, BsonDocument>? _builder;

        private StageSlot(BsonDocument? document, Func<MongoNativeBuildContext, BsonDocument>? builder)
        {
            _document = document;
            _builder = builder;
        }

        internal static StageSlot Rendered(BsonDocument document) => new(document, null);

        internal static StageSlot Deferred(Func<MongoNativeBuildContext, BsonDocument> builder) => new(null, builder);

        internal bool IsDeferred => _builder is not null;

        /// <summary>Deep-clones the template document so substitution never mutates it. Not for deferred slots.</summary>
        internal BsonDocument CloneDocument() => (BsonDocument)_document!.DeepClone();

        /// <summary>Runs the deferred builder. Only for deferred slots.</summary>
        internal BsonDocument Build(in MongoNativeBuildContext context) => _builder!(context);
    }

    /// <summary>
    /// Renders <paramref name="stages"/> into a template sharing one <see cref="PlaceholderTable"/>.
    /// </summary>
    public static MongoPipelineFactory Create(
        IReadOnlyList<MongoPipelineStage> stages,
        MongoQueryLanguageRenderer renderer)
    {
        var placeholders = new PlaceholderTable();
        var template = new List<StageSlot>(stages.Count);

        foreach (var stage in stages)
        {
            if (stage is MongoUnionWithStage unionWith)
                template.AddRange(RenderUnionWith(unionWith, renderer, placeholders).Select(StageSlot.Rendered));
            else if (stage is MongoSetDifferenceStage setDiff)
                template.AddRange(RenderSetDifference(setDiff, renderer, placeholders).Select(StageSlot.Rendered));
            else if (stage is MongoVectorSearchStage vectorSearch)
                template.Add(StageSlot.Deferred(CreateVectorSearchBuilder(vectorSearch.Search, renderer, placeholders)));
            else
                template.Add(StageSlot.Rendered(RenderStage(stage, renderer, placeholders)));
        }

        return new MongoPipelineFactory(template, placeholders);
    }

    // Renders an operand sub-pipeline. A right-nested set op (A.Concat(B.Union(C))) puts a set-op stage inside
    // an operand; it expands to several documents, so it's handled here rather than by single-document
    // RenderStage. Anything else, including a deferred stage, goes to RenderStage and fails closed there.
    private static IEnumerable<BsonDocument> RenderOperandStages(
        IEnumerable<MongoPipelineStage> stages,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        foreach (var stage in stages)
        {
            if (stage is MongoUnionWithStage nestedUnion)
            {
                foreach (var rendered in RenderUnionWith(nestedUnion, renderer, placeholders))
                    yield return rendered;
            }
            else if (stage is MongoSetDifferenceStage nestedSetDiff)
            {
                foreach (var rendered in RenderSetDifference(nestedSetDiff, renderer, placeholders))
                    yield return rendered;
            }
            else
            {
                yield return RenderStage(stage, renderer, placeholders);
            }
        }
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
                new BsonDocument("newRoot", replaceRoot.MergeOwnerKeySentinels
                    // Sentinels are nested under one reserved wrapper field, so a stored property can only
                    // collide with ShadowField, not OwnerKeyField/OrdinalField.
                    ? new BsonDocument("$mergeObjects", new BsonArray
                    {
                        "$" + replaceRoot.NewRoot,
                        new BsonDocument
                        {
                            {
                                MongoReplaceRootStage.ShadowField,
                                new BsonDocument
                                {
                                    { MongoReplaceRootStage.OwnerKeyField, "$_id" },
                                    { MongoReplaceRootStage.OrdinalField, "$" + MongoReplaceRootStage.OrdinalField }
                                }
                            }
                        }
                    })
                    : (BsonValue)("$" + replaceRoot.NewRoot))),
            MongoProjectStage project => RenderProject(project, placeholders),
            MongoAddFieldsStage addFields => RenderAddFields(addFields, placeholders),
            MongoUnsetStage unset => RenderUnset(unset),
            MongoCountStage count => new BsonDocument("$count", count.OutputField),
            // The $vectorSearch score companion: a fixed document, not deferred.
            MongoVectorSearchScoreStage => new BsonDocument("$addFields",
                new BsonDocument(MongoVectorSearchScoreStage.ScoreField,
                    new BsonDocument("$meta", "vectorSearchScore"))),
            MongoGroupAccumulatorStage group => RenderGroup(group, placeholders),
            MongoGroupStage keyedGroup => RenderKeyedGroup(keyedGroup, placeholders),
            // First half of the whole-entity Distinct() dedup, shared with RenderUnionWith's Union dedup.
            MongoGroupByRootStage => GroupByRootStage(),
            // First half of the unordered Last()/LastOrDefault() pattern.
            MongoLastRowStage => new BsonDocument("$group",
                new BsonDocument { { "_id", BsonNull.Value }, { "_last", new BsonDocument("$last", "$$ROOT") } }),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoPipelineFactory does not support stage type '{stage.GetType().Name}'.")
        };

    /// <summary>
    /// Builds the deferred slot for <c>$vectorSearch</c>: the pre-filter is rendered now into the shared
    /// placeholder table; the rest is built per execution.
    /// </summary>
    /// <remarks>
    /// Deferred because the presence of <c>exact</c>/<c>numCandidates</c> and the index depend on runtime
    /// <c>VectorQueryOptions</c>. The driver's stage builder is reused so <c>numCandidates</c> is derived from
    /// <c>limit</c> exactly as on the driver-LINQ path. The pre-filter is deep-cloned per execution because
    /// substitution rewrites sentinels in place.
    /// </remarks>
    private static Func<MongoNativeBuildContext, BsonDocument> CreateVectorSearchBuilder(
        MongoVectorSearch search,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        var preFilterTemplate = search.PreFilter is null
            ? null
            : (BsonDocument)renderer.Render(search.PreFilter, placeholders);

        return context =>
        {
            var entityType = search.EntityType;

            // NativeQueryParameter bridges the EF8/EF9-vs-EF10 query-parameter node difference.
            var queryVector = (QueryVector)ResolveVectorSearchArgument(search.QueryVectorArgument, context.ParameterValues)!;
            var limit = (int)ResolveVectorSearchArgument(search.LimitArgument, context.ParameterValues)!;
            var options = (VectorQueryOptions?)ResolveVectorSearchArgument(search.OptionsArgument, context.ParameterValues);

            // Shared with the driver-LINQ bridge so member/index resolution and its exceptions/warnings
            // (VectorSearchNeedsIndex) surface identically on both paths.
            var resolved = VectorSearchStageBuilder.Resolve(
                entityType, entityType.ClrType, search.PropertyLambda, options, context.QueryLogger);

            // Read back by QueryingEnumerable's zero-results diagnostic (VectorSearchReturnedZeroResults).
            context.AdditionalState[MongoExecutableQuery.VectorQueryProperty] = resolved.Member;
            context.AdditionalState[MongoExecutableQuery.VectorQueryIndexName] = resolved.Options.IndexName!;

            // The pre-filter is already rendered; the driver embeds a BsonDocumentFilterDefinition verbatim, so
            // its sentinels survive to the substitution pass.
            object? filterDefinition = preFilterTemplate is null
                ? null
                : Activator.CreateInstance(
                    typeof(BsonDocumentFilterDefinition<>).MakeGenericType(entityType.ClrType),
                    preFilterTemplate.DeepClone());

            var stage = VectorSearchStageBuilder.CreateStage(
                entityType, search.PropertyLambda, resolved, filterDefinition, queryVector, limit);

            return VectorSearchStageBuilder.RenderStage(
                stage, entityType, context.SerializerFactory.GetEntitySerializer(entityType));
        };
    }

    /// <summary>
    /// Resolves a <c>VectorSearch</c> argument (query parameter or constant) to its runtime value.
    /// </summary>
    private static object? ResolveVectorSearchArgument(
        Expression argument,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        if (NativeQueryParameter.TryGetQueryParameterName(argument, out var name))
        {
            if (!parameterValues.TryGetValue(name, out var value))
                throw new InvalidOperationException(
                    $"MongoPipelineFactory.Build: vector-search parameter '{name}' is not present in "
                    + "parameterValues. This is a bug in the query compilation pipeline.");

            return value;
        }

        if (argument is ConstantExpression constant)
            return constant.Value;

        // Other shapes are declined at binding time.
        throw new NativeTranslationNotSupportedException(
            $"A VectorSearch argument must be a query parameter or a constant; got '{argument.NodeType}'.");
    }

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
            // A computed key was rewritten by the lowerer to a MongoElementRefExpression naming the field its
            // preceding $set wrote. $sort takes bare paths, never "$"-prefixed references.
            var path = ordering.KeySelector switch
            {
                MongoFieldExpression field => field.ElementName,
                MongoElementRefExpression elementRef => elementRef.Path,
                _ => throw new NativeTranslationNotSupportedException(
                    $"$sort key selector must be a MongoFieldExpression or a MongoElementRefExpression; got "
                    + $"'{ordering.KeySelector.GetType().Name}'. A computed sort key should have been rewritten "
                    + "onto a synthetic field by MongoSelectLowerer.")
            };

            body.Add(path, ordering.Ascending ? BsonInt32.Create(1) : BsonInt32.Create(-1));
        }

        return new BsonDocument("$sort", body);
    }

    private static BsonDocument RenderProject(MongoProjectStage stage, PlaceholderTable placeholders)
    {
        // $project reads a bare value as an inclusion/exclusion flag, so a constant/parameter leaf
        // (Select(x => 8)) must be $literal-wrapped, or a 0/false constant aborts the aggregate.
        var body = RenderFields(stage.Projections, placeholders);

        // Suppress the default _id unless the projection deliberately emits an "_id" output field.
        if (!body.Contains("_id"))
        {
            body.Add("_id", 0);
        }

        return new BsonDocument("$project", body);
    }

    // A bare constant/parameter value is $literal-wrapped: MongoDB reads an unwrapped string starting with '$'
    // as a field path, so OrderBy(x => "$Label") (or such a parameter value) would silently sort by that field.
    // SubstituteValue still finds a sentinel inside { "$literal": <sentinel> }.
    private static BsonDocument RenderAddFields(MongoAddFieldsStage stage, PlaceholderTable placeholders)
        => new("$set", RenderFields(stage.Fields, placeholders));

    // One "<alias>: <value>" element per field, each value $literal-wrapped if a constant/parameter (see RenderBranch).
    private static BsonDocument RenderFields(IEnumerable<MongoProjection> fields, PlaceholderTable placeholders)
    {
        var body = new BsonDocument();
        foreach (var field in fields)
            body.Add(field.Alias, MongoAggregationExpressionRenderer.RenderBranch(field.Expression, placeholders));

        return body;
    }

    private static BsonDocument RenderUnset(MongoUnsetStage stage)
        => new("$unset", new BsonArray(stage.FieldNames));

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
                idDoc.Add(part.Name, RenderCompositeKeyPart(part.FieldRef, placeholders));
            id = idDoc;
        }
        else
        {
            // A constant/parameter key part is $literal-wrapped: a "$"-prefixed string as _id (or an _id sub-field)
            // would otherwise be read as a field path and silently group by that field.
            id = MongoAggregationExpressionRenderer.RenderBranch(grouping.Key[0].FieldRef, placeholders);
        }

        var group = new BsonDocument { { "_id", id } };
        foreach (var acc in grouping.Accumulators)
        {
            var operand = acc.Operand is null
                ? 1
                : MongoAggregationExpressionRenderer.RenderBranch(acc.Operand, placeholders);

            group.Add(acc.OutputField, new BsonDocument(acc.Operator, operand));
        }

        return new BsonDocument("$group", group);
    }

    // A composite _id omits a sub-key whose value is missing, so every later read of "_id.<Name>" (HAVING, the
    // flatten $project, projection ternaries) would see missing rather than null: $expr's $eq: [missing, null] is
    // false, and a missing and a null part would form two groups where C# forms one. $ifNull: [part, null] normalizes
    // missing to null once, here, for every part that may be null. A single-part _id needs no wrapping: $group
    // already groups a missing scalar _id as null. Each part is $literal-wrapped as in RenderKeyedGroup.
    private static BsonValue RenderCompositeKeyPart(MongoExpression fieldRef, PlaceholderTable placeholders)
    {
        var rendered = MongoAggregationExpressionRenderer.RenderBranch(fieldRef, placeholders);
        return fieldRef is not (MongoConstantExpression or MongoParameterExpression
                   or MongoFieldExpression { NullSafe: true }) // already renders as $ifNull
               && MongoAggregationExpressionRenderer.MayBeNull(fieldRef)
            ? MongoAggregationExpressionRenderer.IfNull(rendered, BsonNull.Value)
            : rendered;
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
    {
        // CorrelatedReducer uses localField/foreignField plus a pipeline ($match/$sort/$limit:1): an indexable
        // equality join. ToLookupStageDocument's let+pipeline ($expr) shape is for kinds like NestedInclude,
        // which have no single equality field at this level.
        if (lookup.PipelineKind == LookupPipelineKind.CorrelatedReducer)
        {
            var lookupDoc = new BsonDocument
            {
                { "from", lookup.From },
                { "localField", lookup.LocalField },
                { "foreignField", lookup.ForeignField },
                { "pipeline", new BsonArray(lookup.PipelineStages) },
                { "as", lookup.As }
            };
            return new BsonDocument("$lookup", lookupDoc);
        }

        return lookup.ToLookupStageDocument();
    }

    private static BsonDocument RenderUnwind(LookupExpression lookup, bool preserveNullAndEmptyArrays)
        => lookup.ToUnwindStageDocument(preserveNullAndEmptyArrays);

    // Renders $unionWith with the operand pipeline in the shared placeholder table, then, for Union, the
    // full-document dedup.
    private static IEnumerable<BsonDocument> RenderUnionWith(
        MongoUnionWithStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        var innerPipeline = new BsonArray();
        foreach (var rendered in RenderOperandStages(stage.OperandStages, renderer, placeholders))
            innerPipeline.Add(rendered);   // shared placeholders

        yield return new BsonDocument("$unionWith", new BsonDocument
        {
            { "coll", stage.OperandCollectionName },
            { "pipeline", innerPipeline }
        });

        if (stage.Dedup)
        {
            yield return GroupByRootStage();
            yield return ReplaceRootWithIdStage();
        }
    }

    // Intersect/Except as a source-tagging pipeline over the same collection (so $$ROOT equality is
    // well-defined): dedup and tag each side (_a first, _b second), union, regroup by full document, then
    // $match (Intersect: _a && _b; Except: _a && !_b). _a/_b sit beside _doc, so they can't collide with
    // entity fields.
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
        yield return GroupByRootStage();
        yield return Tag(a: true, b: false);

        // Inner (second operand) side, rendered into the shared placeholder table, itself deduped + tagged.
        var innerPipeline = new BsonArray();
        foreach (var operandStage in stage.OperandStages)
            innerPipeline.Add(RenderStage(operandStage, renderer, placeholders));   // shared placeholders
        innerPipeline.Add(GroupByRootStage());
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
        yield return ReplaceRootWithIdStage();
    }

    // { $group: { _id: "$$ROOT" } }: whole-document dedup (Distinct(), Union, each Intersect/Except side).
    private static BsonDocument GroupByRootStage()
        => new("$group", new BsonDocument("_id", "$$ROOT"));

    // { $replaceRoot: { newRoot: "$_id" } }: restores the document a preceding $group put under _id.
    private static BsonDocument ReplaceRootWithIdStage()
        => new("$replaceRoot", new BsonDocument("newRoot", "$_id"));

    /// <summary>
    /// Clones the template and substitutes every placeholder sentinel with its serialized runtime value.
    /// </summary>
    /// <param name="parameterValues">Must contain every parameter recorded in <see cref="_placeholders"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// A parameter is missing, or the template has a deferred slot (use <see cref="Build(in MongoNativeBuildContext)"/>).
    /// </exception>
    public BsonDocument[] Build(IReadOnlyDictionary<string, object?> parameterValues)
    {
        if (_hasDeferredSlot)
            throw new InvalidOperationException(
                "MongoPipelineFactory.Build(parameterValues) cannot bind a template containing a deferred "
                + "stage slot: such a stage is constructed at Build time and needs the serializer factory, "
                + "query logger and additional-state dictionary carried by MongoNativeBuildContext. "
                + "Call Build(in MongoNativeBuildContext) instead. "
                + "This is a bug in the query compilation pipeline.");

        var result = new BsonDocument[_template.Count];
        for (var i = 0; i < _template.Count; i++)
            result[i] = SubstituteDocument(_template[i].CloneDocument(), parameterValues);

        NormalizePagingStages(result);

        return result;
    }

    /// <summary>
    /// Builds this execution's pipeline: runs deferred slots, clones baked ones, then substitutes sentinels
    /// across the whole result (including deferred output, e.g. a vector-search pre-filter).
    /// </summary>
    public BsonDocument[] Build(in MongoNativeBuildContext context)
    {
        var parameterValues = context.ParameterValues;
        var result = new BsonDocument[_template.Count];

        for (var i = 0; i < _template.Count; i++)
        {
            var slot = _template[i];

            var document = slot.IsDeferred ? slot.Build(context) : slot.CloneDocument();

            result[i] = SubstituteDocument(document, parameterValues);
        }

        NormalizePagingStages(result);

        return result;
    }

    /// <summary>
    /// MongoDB rejects <c>$limit: 0</c>, but <c>Take(0)</c> must return empty, so it is rewritten to an
    /// always-false <c>$match</c> (an impossible <c>$type</c>), as the driver-LINQ bridge does
    /// (<c>MongoEFToLinqTranslatingExpressionVisitor.TryRewriteZeroTake</c>). Negative <c>$limit</c> or
    /// <c>$skip</c> throws <see cref="ArgumentOutOfRangeException"/> client-side.
    /// </summary>
    private static void NormalizePagingStages(BsonDocument[] pipeline)
    {
        for (var i = 0; i < pipeline.Length; i++)
        {
            var stage = pipeline[i];

            if (stage.TryGetValue("$limit", out var limitValue))
            {
                var limit = limitValue.ToInt64();
                if (limit == 0)
                {
                    pipeline[i] = new BsonDocument("$match", MongoQueryLanguageRenderer.AlwaysFalseFilter());
                    continue;
                }

                if (limit < 0)
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
                var innerArray = innerPipeline.AsBsonArray.Select(d => d.AsBsonDocument).ToArray();
                NormalizePagingStages(innerArray);
                unionWithValue.AsBsonDocument["pipeline"] = new BsonArray(innerArray);
            }
        }
    }

    private BsonDocument SubstituteDocument(
        BsonDocument doc,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
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
        {
            // Read once: a lazily-materializing array returns a fresh BsonValue per access.
            var element = array[i];
            var newValue = SubstituteValue(element, parameterValues);

            // Assign only on replacement: writing to the driver's read-only $vectorSearch QueryVectorBsonArray
            // throws.
            if (!ReferenceEquals(newValue, element))
                array[i] = newValue;
        }

        return array;
    }

    private BsonValue SerializeParameter(
        int index,
        IReadOnlyDictionary<string, object?> parameterValues)
    {
        var (name, serializer, isArray, regexKind, entityMemberProperty, arrayElementIndex, regexCaseInsensitive) = _placeholders.Entries[index];

        // Coerces to the serializer's ValueType (which differs from the property ClrType the compile-time path
        // uses when a value converter is present) and serializes via the shared path used for constants.
        BsonValue Serialize(object? value)
            => BsonValueSerializer.SerializeThroughWriter(serializer!, BsonValueSerializer.Coerce(serializer!.ValueType, value));

        if (!parameterValues.TryGetValue(name, out var rawValue))
            throw new InvalidOperationException(
                $"MongoPipelineFactory.Build: parameter '{name}' (placeholder index {index}) "
                + "is not present in parameterValues. This is a bug in the query compilation pipeline.");

        // Entity-list Contains (`customers.Contains(c)`): the value is an array of entities; extract each key.
        // Null elements stay BSON null (see MongoExpressionTranslator.TryTranslateEntityListContains). Must
        // precede the single-entity branch, which would misread the array as one entity.
        if (entityMemberProperty is not null && isArray)
        {
            var getter = entityMemberProperty.GetGetter();
            var array = new BsonArray();
            foreach (var element in (System.Collections.IEnumerable)rawValue!)
            {
                if (element is null)
                {
                    array.Add(BsonNull.Value);
                    continue;
                }

                array.Add(Serialize(getter.GetClrValue(element)));
            }

            return array;
        }

        // Entity equality (`c == local`): the value is an entity; extract its key before normal serialization.
        // A NULL entity (`local == null` at run time, or a terminal `Contains(null)`) has no key: compare against BSON
        // null. No persisted primary key matches it ("an entity never equals null"), while a navigation comparison
        // (`o.Customer == local`) correctly matches the null-FK rows. Handing null to the key serializer would throw.
        if (entityMemberProperty is not null)
        {
            if (rawValue is null)
                return BsonNull.Value;

            rawValue = entityMemberProperty.GetGetter().GetClrValue(rawValue);
        }

        // `args[0]` into a compiled query's array parameter (NativeQueryParameter.TryGetParameterArrayElementIndex)
        // or an element of a funcletized Tuple (MongoExpressionTranslator.TryDecomposeTupleOperand): the value is
        // the whole list/tuple, so extract the element per execution.
        if (arrayElementIndex is int elementIndex)
        {
            rawValue = rawValue switch
            {
                System.Collections.IList list => list[elementIndex],
                System.Runtime.CompilerServices.ITuple tuple => tuple[elementIndex],
                _ => throw new InvalidOperationException(
                    $"MongoPipelineFactory.Build: parameter '{name}' (placeholder index {index}) "
                    + $"was expected to be an array/list or tuple but was '{rawValue?.GetType().Name ?? "null"}'.")
            };
        }

        // Parameterized StartsWith/EndsWith/Contains: escape and anchor per execution, matching
        // MongoQueryLanguageRenderer.RenderRegex's constant branch.
        if (regexKind is not null)
        {
            var term = rawValue is char ch ? ch.ToString() : (string)rawValue!;
            var pattern = MongoRegexPatternBuilder.BuildPattern(term, regexKind.Value);
            return new BsonRegularExpression(pattern, regexCaseInsensitive ? "is" : "s");
        }

        // Property-less primitive (e.g. Skip/Take count): serialize via BsonValue.Create. A char has no BSON form;
        // it only reaches here as a string-operator operand (TranslateCharAsString), so it is a one-char string.
        if (serializer is null)
            return rawValue is char c ? new BsonString(c.ToString()) : BsonValue.Create(rawValue);

        // Parameterized $in/$nin collection: serialize each element with the element serializer.
        if (isArray)
        {
            var array = new BsonArray();
            foreach (var element in (System.Collections.IEnumerable)rawValue!)
                array.Add(Serialize(element));

            return array;
        }

        return Serialize(rawValue);
    }
}
