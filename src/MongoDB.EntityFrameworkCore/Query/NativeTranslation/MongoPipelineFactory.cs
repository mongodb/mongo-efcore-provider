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
/// Translates a typed <see cref="MongoPipelineStage"/> list into a cached template of stage slots and
/// binds per-execution parameter values via
/// <see cref="Build(IReadOnlyDictionary{string, object})"/> /
/// <see cref="Build(in MongoNativeBuildContext)"/>.
/// </summary>
/// <remarks>
/// Constructed once per compiled query via <see cref="Create"/>: the stage-walk renders each stage to a
/// <see cref="BsonDocument"/>, baking constants inline and recording parameter sites as placeholder sentinels
/// in a shared <see cref="PlaceholderTable"/>. A slot is normally such a rendered, immutable document; it may
/// instead be <em>deferred</em> — a builder invoked at Build time — for a stage whose BSON <em>shape</em>
/// (not just its values) depends on runtime state and so can't be expressed as a sentinel. A deferred slot
/// requires <see cref="Build(in MongoNativeBuildContext)"/>; the parameter-values-only overload throws rather
/// than emit a pipeline with a hole. At execution time, Build clones the template and substitutes every
/// sentinel with the serialized runtime value (constants are already baked and untouched); substitution also
/// runs over a deferred slot's freshly built document, so anything it embeds via the shared
/// <see cref="PlaceholderTable"/> resolves in the same pass.
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
    /// One slot of the compiled pipeline template: either a <see cref="BsonDocument"/> rendered once at
    /// <see cref="Create"/> time, or a builder deferred to
    /// <see cref="Build(in MongoNativeBuildContext)"/> time.
    /// </summary>
    /// <remarks>
    /// Deferral exists for a stage whose document SHAPE depends on runtime state — which keys are present
    /// at all, not just which values they carry — so the compile-time template cannot represent it and a
    /// value sentinel cannot substitute for it.
    /// </remarks>
    internal readonly struct StageSlot
    {
        private readonly BsonDocument? _document;
        private readonly Func<MongoNativeBuildContext, BsonDocument>? _builder;

        private StageSlot(BsonDocument? document, Func<MongoNativeBuildContext, BsonDocument>? builder)
        {
            _document = document;
            _builder = builder;
        }

        /// <summary>A slot holding a document rendered at compile time.</summary>
        internal static StageSlot Rendered(BsonDocument document) => new(document, null);

        /// <summary>A slot whose document is constructed per execution, at Build time.</summary>
        internal static StageSlot Deferred(Func<MongoNativeBuildContext, BsonDocument> builder) => new(null, builder);

        /// <summary>Whether this slot's document is built per execution rather than baked at compile time.</summary>
        internal bool IsDeferred => _builder is not null;

        /// <summary>
        /// Deep-clones the baked template document, so per-execution substitution never mutates the template.
        /// Only valid when <see cref="IsDeferred"/> is <see langword="false"/>.
        /// </summary>
        internal BsonDocument CloneDocument() => (BsonDocument)_document!.DeepClone();

        /// <summary>
        /// Invokes the deferred builder to construct this execution's document.
        /// Only valid when <see cref="IsDeferred"/> is <see langword="true"/>.
        /// </summary>
        internal BsonDocument Build(in MongoNativeBuildContext context) => _builder!(context);
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
                    // The sentinels are nested under ONE reserved wrapper field. A real stored property can
                    // therefore only collide with the single ShadowField name, never with OwnerKeyField/
                    // OrdinalField individually — those are no longer top-level keys of the merged document.
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
            // The $vectorSearch score companion: a fixed document, so nothing about it is deferred. It is a
            // payload-free marker stage precisely so this BSON lives here rather than in the lowerer.
            MongoVectorSearchScoreStage => new BsonDocument("$addFields",
                new BsonDocument(MongoVectorSearchScoreStage.ScoreField,
                    new BsonDocument("$meta", "vectorSearchScore"))),
            MongoGroupAccumulatorStage group => RenderGroup(group, placeholders),
            MongoGroupStage keyedGroup => RenderKeyedGroup(keyedGroup, placeholders),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoPipelineFactory does not support stage type '{stage.GetType().Name}'.")
        };

    // ------------------------------------------------------------------
    // $vectorSearch — the one DEFERRED slot
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the deferred slot for a <c>$vectorSearch</c> stage: the pre-filter is rendered now, at
    /// compile time, into the shared placeholder table; everything else is constructed per execution.
    /// </summary>
    /// <remarks>
    /// Deferral is necessary because the body's shape — whether <c>exact</c>/<c>numCandidates</c> is present
    /// at all, and which index is used — depends on a runtime <c>VectorQueryOptions</c>, which no value
    /// sentinel can express. The driver's own stage builder is reused (rather than hand-writing the body)
    /// because it derives <c>numCandidates</c> from the runtime <c>limit</c> when left null, which keeps the
    /// emitted MQL identical to the driver-LINQ path's. The pre-filter is rendered once here so a parameter
    /// inside it lands in the same <see cref="PlaceholderTable"/> as every other stage's and resolves in
    /// Build's ordinary substitution pass; it is deep-cloned per execution because that pass rewrites
    /// sentinels in place.
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

            // Resolved via NativeQueryParameter, which bridges the EF8/EF9-vs-EF10 query-parameter node
            // difference, exactly as the driver-LINQ bridge's own ParamValue<T> does.
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

            // A BsonDocumentFilterDefinition, not the bridge's ExpressionFilterDefinition, since the
            // pre-filter is already rendered; the driver embeds it verbatim so its sentinels ride through to
            // the substitution pass.
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
    /// Resolves one <c>VectorSearch</c> argument node to its runtime value: an EF query parameter is looked
    /// up in this execution's parameter values; a constant is read directly.
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

        // The slot is only ever populated for argument nodes of one of the two shapes above; anything else
        // must have been declined at binding time.
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
            // A computed key arrives here already rewritten by the lowerer into a MongoElementRefExpression
            // naming the synthetic field its preceding $set wrote. $sort takes field paths, so both arms
            // contribute a bare path, never a "$"-prefixed aggregation field reference.
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

    // $set (a.k.a. $addFields) adds computed fields, leaving existing ones alone. Unlike $project, a bare
    // scalar here is a LITERAL, not an inclusion flag — which is what lets a constant sort key
    // (OrderBy(x => 1)) render as { "__sort0" : 1 } and mean it.
    //
    // A top-level bare MongoConstantExpression/MongoParameterExpression body is $literal-wrapped:
    // MongoAggregationExpressionRenderer.Render emits it unwrapped, and MongoDB reads an unwrapped string
    // value starting with '$' as a FIELD PATH rather than a literal — so OrderBy(x => "$Label") (or a
    // captured string parameter whose runtime value happens to start with '$') would otherwise silently sort
    // by the named field instead of tying every row on the literal string. The wrap is scoped to
    // RenderAddFields only: RenderProject and the predicate ($expr) path share the same renderer but a bare
    // constant is never their whole body.
    //
    // Substitution survives the wrap: SubstituteValue tests every BsonValue for a placeholder sentinel before
    // recursing into it as a document, so a parameter sentinel nested inside { "$literal": <sentinel> } is
    // still found and replaced, producing { "$literal": <runtime value> }.
    private static BsonDocument RenderAddFields(MongoAddFieldsStage stage, PlaceholderTable placeholders)
    {
        var body = new BsonDocument();
        foreach (var field in stage.Fields)
        {
            var rendered = MongoAggregationExpressionRenderer.Render(field.Expression, placeholders);
            if (field.Expression is MongoConstantExpression or MongoParameterExpression)
            {
                rendered = new BsonDocument("$literal", rendered);
            }

            body.Add(field.Alias, rendered);
        }

        return new BsonDocument("$set", body);
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
    /// <exception cref="InvalidOperationException">
    /// The template contains a deferred stage slot, which cannot be built from parameter values alone.
    /// </exception>
    public BsonDocument[] Build(IReadOnlyDictionary<string, object?> parameterValues)
    {
        // Fail loudly rather than emit a pipeline with a hole where the deferred stage should be.
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

        // Validate/normalize paging bounds: MongoDB rejects $limit <= 0 and $skip < 0 server-side. A $skip < 0
        // still throws the EF-correct exception (ArgumentOutOfRangeException) client-side; a $limit: 0 is
        // rewritten to an always-false $match instead — see NormalizePagingStages' remarks.
        NormalizePagingStages(result);

        return result;
    }

    /// <summary>
    /// Builds this execution's pipeline: constructs every deferred slot at its own stage position, clones
    /// every baked slot, then substitutes every placeholder sentinel across the whole result.
    /// </summary>
    /// <param name="context">
    /// The per-execution build state. Its <see cref="MongoNativeBuildContext.ParameterValues"/> must contain
    /// an entry for every parameter name recorded in <see cref="_placeholders"/>; a missing key is a bug in
    /// the caller and throws <see cref="InvalidOperationException"/>.
    /// </param>
    /// <returns>A freshly materialized <see cref="BsonDocument"/> array ready to send to the server.</returns>
    public BsonDocument[] Build(in MongoNativeBuildContext context)
    {
        var parameterValues = context.ParameterValues;
        var result = new BsonDocument[_template.Count];

        for (var i = 0; i < _template.Count; i++)
        {
            var slot = _template[i];

            // A deferred slot is constructed HERE, at its own stage position, because its document shape
            // depends on this execution's state; a baked slot is cloned exactly as it always was.
            var document = slot.IsDeferred ? slot.Build(context) : slot.CloneDocument();

            // Substitution then runs over the deferred document as well: whatever the deferred builder
            // embeds may itself have been rendered at compile time into the SHARED PlaceholderTable (a
            // vector-search pre-filter, for instance), so its sentinels must resolve in this same pass.
            result[i] = SubstituteDocument(document, parameterValues);
        }

        NormalizePagingStages(result);

        return result;
    }

    /// <summary>
    /// A <c>$limit: 0</c> is meaningless to MongoDB's own <c>$limit</c> stage (EF-323) — validated and
    /// rejected server-side, and previously rejected here too with a client-side <see
    /// cref="ArgumentOutOfRangeException"/> for the same reason. That client-side reject reintroduced, for the
    /// native path, exactly the gap EF-254 closed on the driver-LINQ bridge
    /// (<c>MongoEFToLinqTranslatingExpressionVisitor.TryRewriteZeroTake</c>): a plain <c>Take(0)</c> should
    /// answer an empty result, not abort. Rather than throw, a <c>$limit: 0</c> stage is rewritten in place to
    /// the same always-false <c>$match</c> the driver-LINQ bridge's <c>Where(_ =&gt; false)</c> rewrite renders
    /// — an impossible BSON type code makes every document fail the match regardless of its <c>_id</c>'s
    /// actual type.
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
                    pipeline[i] = new BsonDocument("$match",
                        new BsonDocument("_id", new BsonDocument("$type", -1)));
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
        {
            // Read the element ONCE: a lazily-materializing array (see below) hands back a fresh BsonValue
            // per access, so re-reading it would defeat the reference check.
            var element = array[i];
            var newValue = SubstituteValue(element, parameterValues);

            // Assign only when the element was actually REPLACED (a sentinel), mirroring SubstituteDocument.
            // Writing back an identical reference is a no-op for a normal array, but it THROWS for a
            // read-only one — and a deferred slot can embed one: the driver renders a $vectorSearch
            // queryVector as its own read-only QueryVectorBsonArray, whose elements are baked scalars with
            // nothing to substitute.
            if (!ReferenceEquals(newValue, element))
                array[i] = newValue;
        }

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
