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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Converts the native-translation IR on a <see cref="MongoQueryExpression"/> into a fully-typed
/// <see cref="MongoPipelineStage"/> list: the filter/sort/page ops (<see cref="MongoSelectDefinition.PipelineOps"/>)
/// are emitted verbatim in their recorded arrival order (no fixed canonical order), followed by
/// <c>$lookup</c>/<c>$unwind</c> and any terminal stage (<c>$unionWith</c>/<c>$group</c>/<c>$project</c>/aggregate).
/// </summary>
/// <remarks>
/// <para>
/// This lowerer is BSON-free. It produces typed stage IR objects only; BSON rendering is the
/// responsibility of the downstream pipeline renderer/factory. An empty <see cref="MongoSelectDefinition.PipelineOps"/>
/// list means no filter/sort/page ops are emitted at all.
/// </para>
/// <para>
/// Lookup eligibility is guarded here. If the query contains a lookup shape the native pipeline
/// cannot handle, a <see cref="NativeTranslationNotSupportedException"/> is thrown. The compile-time
/// gate catches this and falls back to the driver-LINQ path.
/// </para>
/// </remarks>
internal sealed class MongoSelectLowerer
{
    /// <summary>
    /// Lowers the native-translation IR of <paramref name="query"/> into typed pipeline stages.
    /// </summary>
    /// <param name="query">
    /// The <see cref="MongoQueryExpression"/> whose native IR (on its
    /// <see cref="MongoQueryExpression.Select"/>) and lookup state are lowered.
    /// </param>
    /// <returns>
    /// An ordered, read-only list of <see cref="MongoPipelineStage"/> values: the recorded
    /// <see cref="MongoSelectDefinition.PipelineOps"/> in arrival order, then lookups/terminal stages.
    /// Returns an empty list when no ops are populated.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when the query contains a join or lookup shape that the native pipeline does not support.
    /// </exception>
    public IReadOnlyList<MongoPipelineStage> Lower(MongoQueryExpression query)
    {
        var select = query.Select;
        var stages = new List<MongoPipelineStage>();
        var sortFields = new SyntheticSortFieldAllocator(ReservedElementNames(query));

        // 0. $vectorSearch MUST be the first stage in the pipeline — the server rejects it anywhere else
        // (Location40602), for every preceding-stage shape. This is why it lives in a dedicated slot rather
        // than in PipelineOps (which is emitted verbatim in arrival order, so a vector search recorded there
        // would only HAPPEN to come first): a block at the very top makes first-ness structural. The
        // $addFields companion follows it immediately, unconditionally, exactly as the driver-LINQ bridge
        // emits it — that is what keeps the two paths' MQL identical.
        if (select.VectorSearch is { } vectorSearch)
        {
            stages.Add(new MongoVectorSearchStage(vectorSearch));
            stages.Add(new MongoVectorSearchScoreStage());
        }

        // 1. $match / $sort / $skip / $limit ops, emitted verbatim in the order they were recorded
        // (Select.PipelineOps — no fixed canonical order; arrival order is emission order).
        AppendSelectOpStages(select.PipelineOps, stages, sortFields);

        // 2. $lookup/$unwind — cross-collection includes (lookup state stays on the query node).
        // A projected collection-navigation Count registers an IsNativeCollectionLookup $lookup here
        // (InjectAfterRoot=true) so its _lookup_<Nav> array is already present by the time the $project
        // below reads it via $size — placing lookups after the filter/sort/page block (but before $project)
        // satisfies that without any lowerer change.
        //
        // EF-397: a SET-OP query defers this block until AFTER the set-op stage and TrailingOps (see the
        // deferred call below). Emitting a $lookup here would join only source1's rows: the operand pipeline
        // nested inside $unionWith/$setDifference lowers from setOp.OperandSelect.PipelineOps alone and
        // carries no lookups, so every row contributed by the operand would come back with an EMPTY joined
        // array — silent wrong data, which is exactly the failure mode TranslateSelect's collection-Include
        // post-terminal guard used to avoid by declining outright. Running the join over the COMBINED
        // (and, for Union, already-deduped) result joins every row exactly once instead.
        if (select.SetOperation == null)
        {
            AppendLookupStages(query, stages);
        }

        // Set operation terminal ($unionWith [+ dedup] or a set-difference shape for Intersect/Except).
        // Guaranteed whole-entity by the QMTEV guard (the operand is a plain whole-entity select — no
        // grouping/projection/cardinality/lookups), so the operand lowers to its own filter/sort/page ops only.
        if (select.SetOperation is { } setOp)
        {
            // Projected operands: each operand's own $project is part of ITS pipeline and must be emitted
            // before the combine — source1's ahead of the set-op stage (appended to `stages` right after
            // source1's PipelineOps above), the operand's inside the nested `operandStages`. The dedup
            // ($group{_id:$$ROOT}) and Intersect/Except source-tagging then operate over the projected
            // documents (correct: dedup/compare the projected values). Contrast a trailing projection over
            // the combined result, where OperandsProjected is false and select.Projection is emitted after
            // the set-op stage by the fall-through Projection block below.
            if (setOp.OperandsProjected)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            var operandStages = new List<MongoPipelineStage>();
            AppendSelectOpStages(setOp.OperandSelect.PipelineOps, operandStages, sortFields);
            if (setOp.OperandsProjected)
            {
                operandStages.Add(new MongoProjectStage(setOp.OperandSelect.Projection));
            }

            if (setOp.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(setOp.Kind, operandStages, setOp.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            }

            // Post-set-op composition: trailing $match/$sort/$skip/$limit emit after the set-op stage (they
            // operate on the combined result), then fall through to the Projection block (a trailing
            // anonymous/DTO Select after a set op populates Select.Projection, emitted here as a $project
            // after the set-op stage and TrailingOps) and the Cardinality block (post-set-op
            // aggregate/reducer). UnwindSource/Grouping stay empty for a set-op query and their blocks are
            // skipped.
            AppendSelectOpStages(select.TrailingOps, stages, sortFields);

            // EF-397: the deferred $lookup block (see the skipped call at step 2). Emitted here — after the
            // combine and after the trailing filter/sort/page, before the Projection and Cardinality blocks
            // — so it occupies exactly the same slot relative to the surrounding stages that it does on the
            // non-set-op path (ops → $lookup → $project → terminal), just over the combined stream.
            // Placing it after the Union dedup ($group{_id:"$$ROOT"}) / Intersect-Except source tagging is
            // also required for correctness of the SET operation itself: those compare whole documents by
            // value, so a joined array present at that point would join the comparison key and dedup by
            // "entity + its children" rather than by entity.
            // FUTURE EDITORS: this deferred-emission mechanism ASSUMES a PROJECTED set-op operand never
            // carries a $lookup of its own. If one ever did, the pre-combine $project emitted above (the
            // OperandsProjected branch) would run BEFORE this block and read a joined field that does not
            // exist yet — silently projecting a missing value, and dropping the lookup array from the
            // combined stream. Currently unreachable: an operand only reaches here through
            // MongoQueryableMethodTranslatingExpressionVisitor.IsPlainProjectedSelect, which requires
            // Lookups.Count == 0 && !IsJoinQuery (and IsPlainWholeEntitySelect requires the same for the
            // whole-entity form). Weaken either of those and this ordering must be revisited.
            AppendLookupStages(query, stages);
            // NB: no early return — control continues to the Cardinality block.
        }

        // Terminal native SelectMany, then $project the result selector (populated in Select.Projection by
        // NativeSelectManyBinder). Terminal — nothing follows.
        // Owned (embedded): $unwind the embedded array directly here.
        // Reference (cross-collection): the $lookup + $unwind were already appended above by
        // AppendLookupStages (the ForceUnwind-collection branch) — nothing further to add here.
        if (select.UnwindSource is { } unwind)
        {
            if (unwind.Kind == MongoUnwindSourceKind.Owned)
                stages.Add(new MongoUnwindFieldStage(
                    unwind.InnerScopePath,
                    includeArrayIndex: unwind.WholeElement ? MongoReplaceRootStage.OrdinalField : null));

            // Inner-element-only user filter (o.Refs.Where(pred)): a $match on the unwound element, emitted
            // after the $unwind (owned: just above; reference: already emitted by AppendLookupStages) and
            // before the $replaceRoot (WholeElement) / $project (projected). Already scope-prefixed by the
            // binder (reference: "_lookup_Refs.Total"; owned: "Items.Total"); the emission here is
            // kind-agnostic.
            if (unwind.Filter is { } filter)
                stages.Add(new MongoMatchStage(filter));

            if (unwind.WholeElement)
            {
                // Bare whole-inner-element SelectMany: promote the unwound element to root.
                // Owned (embedded, shadow key): $mergeObjects the owner key + array ordinal in under sentinel
                // fields so the owned element's shadow key materializes non-null.
                // Reference (cross-collection): the $lookup + $unwind were already appended by
                // AppendLookupStages above; a reference entity carries its own real stored key, so a plain
                // $replaceRoot suffices — no sentinel merge.
                stages.Add(new MongoReplaceRootStage(
                    unwind.InnerScopePath,
                    mergeOwnerKeySentinels: unwind.Kind == MongoUnwindSourceKind.Owned));
                return stages;
            }

            if (select.Projection.Count > 0)
                stages.Add(new MongoProjectStage(select.Projection));
            return stages;
        }

        // 6b. Keyed $group terminal (GroupBy(key).Select(aggregate)). A GroupBy-route query has only
        // $match + $group by construction — the binder rejects orderings/paging alongside a grouping —
        // so no $sort/$skip/$limit precede it here. The $group is followed by a flattening $project
        // (Select.Projection) that lifts the grouped output — the _id (scalar key), each _id.<Name>
        // composite sub-key, and each accumulator output field — up to top-level result aliases the DOM
        // shaper reads by name (see NativeGroupByBinder / MongoQueryLanguageRenderer). Returning here is
        // safe: no further stages follow a grouping.
        if (select.Grouping is { } grouping)
        {
            stages.Add(new MongoGroupStage(grouping));
            if (select.Projection.Count > 0)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            return stages;
        }

        // 6. $project — server-side projection (terminal member-access anonymous/DTO Select). Emitted last
        // here: the projection is the final logical operation, after the filter/sort/page ops and any
        // $lookup.
        // A projected-operand set op already emitted source1's $project above, ahead of the set-op stage —
        // don't re-emit it here. A trailing projection after a set op (OperandsProjected false) and a plain
        // projected Select (no set op) both still emit here.
        if (select.Projection.Count > 0 && !(select.SetOperation?.OperandsProjected ?? false))
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }

        // 7. Scalar aggregate terminal stage ($count / $group / $limit for Any/All).
        var cardinality = select.Cardinality;
        if (cardinality?.Aggregate is { } aggregate)
        {
            stages.Add(aggregate switch
            {
                MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                    => new MongoCountStage(BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Sum
                    => new MongoGroupAccumulatorStage("$sum", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Min
                    => new MongoGroupAccumulatorStage("$min", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Max
                    => new MongoGroupAccumulatorStage("$max", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Average
                    => new MongoGroupAccumulatorStage("$avg", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Any or MongoAggregateOperator.All
                    => new MongoLimitStage(new MongoConstantExpression(1, forSerialization: null)),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unsupported aggregate operator '{aggregate}'.")
            });
        }

        return stages;
    }

    /// <summary>
    /// Appends the ordered filter/sort/page stages ($match / $sort / $skip / $limit) for <paramref name="ops"/>
    /// in their recorded order. Shared by the outer query's own <see cref="MongoSelectDefinition.PipelineOps"/>,
    /// a set-operation operand's <see cref="MongoSelectDefinition.PipelineOps"/>
    /// (<see cref="MongoSetOperation.OperandSelect"/>, a plain whole-entity select), and the outer query's
    /// post-set-op <see cref="MongoSelectDefinition.TrailingOps"/>.
    /// </summary>
    private static void AppendSelectOpStages(
        IReadOnlyList<MongoSelectOp> ops,
        List<MongoPipelineStage> stages,
        SyntheticSortFieldAllocator sortFields)
    {
        foreach (var op in ops)
        {
            if (op is MongoSortOp sortOp)
            {
                AppendSortStages(sortOp, stages, sortFields);
                continue;
            }

            stages.Add(op switch
            {
                MongoMatchOp m => new MongoMatchStage(m.Predicate),
                MongoSkipOp k => new MongoSkipStage(k.Count),
                MongoLimitOp l => new MongoLimitStage(l.Count),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unknown select op '{op.GetType().Name}'.")
            });
        }
    }

    /// <summary>
    /// Emits one <see cref="MongoSortOp"/>. A key that is already a field path is emitted as-is; a computed
    /// key is materialized into a synthetic field by a preceding <c>$set</c> and removed again by a following
    /// <c>$unset</c>, because MQL <c>$sort</c> accepts field paths only.
    /// </summary>
    /// <remarks>
    /// One <c>$set</c> and one <c>$unset</c> per sort stage, carrying every computed key of that stage — a
    /// <see cref="MongoSortOp"/> already holds a whole <c>OrderBy</c>/<c>ThenBy</c> chain's orderings as one
    /// op, so the three stages bracket the whole sort.
    /// <para>
    /// The no-computed-key early return is load-bearing, not tidiness: an indexed field sort preceded by an
    /// unrelated <c>$set</c> loses its index (measured via <c>explain</c>: an <c>IXSCAN</c> becomes a
    /// <c>COLLSCAN</c>). Emitting the <c>$set</c> unconditionally would silently cost every existing field
    /// sort its index. A mixed sort still pays this cost on its field key — accepted, since the alternative
    /// is not supporting computed sort keys at all.
    /// </para>
    /// </remarks>
    private static void AppendSortStages(
        MongoSortOp sortOp, List<MongoPipelineStage> stages, SyntheticSortFieldAllocator sortFields)
    {
        List<MongoProjection>? computed = null;
        var orderings = new List<MongoOrdering>(sortOp.Orderings.Count);

        foreach (var ordering in sortOp.Orderings)
        {
            if (ordering.KeySelector is MongoFieldExpression)
            {
                orderings.Add(ordering);
                continue;
            }

            var name = sortFields.Allocate();
            (computed ??= []).Add(new MongoProjection(name, ordering.KeySelector));
            orderings.Add(new MongoOrdering(
                new MongoElementRefExpression(name, ordering.KeySelector.Type), ordering.Ascending));
        }

        if (computed is null)
        {
            // Byte-identical to the pre-slice-B emission: the ORIGINAL ordering list, not the rebuilt one.
            stages.Add(new MongoSortStage(sortOp.Orderings));
            return;
        }

        stages.Add(new MongoAddFieldsStage(computed));
        stages.Add(new MongoSortStage(orderings));
        stages.Add(new MongoUnsetStage(computed.Select(f => f.Alias).ToList()));
    }

    /// <summary>
    /// Appends <see cref="MongoLookupStage"/> + <see cref="MongoUnwindStage"/> pairs for each lookup,
    /// after validating that the native pipeline can handle the lookup shape.
    /// </summary>
    private static void AppendLookupStages(MongoQueryExpression query, List<MongoPipelineStage> stages)
    {
        var lookups = query.Lookups;

        // Join-coverage guard: if this is a join query and there are fewer lookups than inner
        // collections, emitting a partial pipeline would silently drop a join and return wrong results.
        if (query.IsJoinQuery && lookups.Count < query.InnerCollections.Count)
        {
            throw new NativeTranslationNotSupportedException(
                "Native pipeline does not support this join shape (only single-level reference includes).");
        }

        foreach (var lookup in lookups)
        {
            if (lookup.IsStreamableReference)
            {
                // The $unwind must follow the navigation's own requiredness (inner for a required nav so a
                // dangling FK drops the row; left-outer for an optional one so it survives with a null
                // navigation), not a fixed default. The registered LookupExpression already carries that
                // decision on PreserveNullAndEmptyArrays (set at confirmation time); it must be threaded
                // through here explicitly, or every reference Include would silently unwind left-outer
                // regardless of requiredness.
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, lookup.PreserveNullAndEmptyArrays));
            }
            else if (lookup.IsNativeCollectionLookup)
            {
                // Collection Include: keep the joined documents as an array under _lookup_<Nav>
                // (no $unwind). The DOM collection materializer reads the array back and runs the
                // IncludeCollection fixup, exactly as on the driver-LINQ path.
                stages.Add(new MongoLookupStage(lookup));
            }
            else if (lookup.Navigation is { IsCollection: true } && lookup.ForceUnwind)
            {
                // A cross-collection reference SelectMany flatten: $lookup the referenced collection, then
                // $unwind to one row per child with inner-join semantics (preserve:false) — a principal with
                // no children drops out. (Include's reference $unwind uses preserve:true / left-join; this
                // is the opposite.)
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: false));
            }
            else
            {
                // Navigation is null for an EF-377 Join hop with no model navigation; name the target
                // entity type instead so the message stays useful rather than throwing a NullReference.
                throw new NativeTranslationNotSupportedException(
                    "Native pipeline does not support lookup for "
                    + (lookup.Navigation is { } nav
                        ? $"navigation '{nav.Name}' "
                        : $"navigation-less join onto '{lookup.TargetEntityType.DisplayName()}' ")
                    + "(only single-level reference and single-level collection includes).");
            }
        }
    }

    // The synthetic field a computed sort key is materialized into. Double-underscore prefix, matching the
    // established sentinel convention — MongoVectorSearchScoreStage.ScoreField "__score",
    // MongoReplaceRootStage.OwnerKeyField "__ownerKey" / .OrdinalField "__ord".
    private const string SyntheticSortFieldPrefix = "__sort";

    /// <summary>
    /// Allocates the synthetic field names a computed sort key is materialized into, for ONE
    /// <see cref="Lower"/> invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-invocation, deliberately: a process-global counter would make emitted synthetic names (and so
    /// committed <c>AssertMql</c> baselines) depend on execution order across runs.
    /// </para>
    /// <para>
    /// The reserved set is a collision guard: <c>$set</c> silently overwrites a same-named existing field, and
    /// a model may map a property to any element name, including one of these, via <c>HasElementName</c>.
    /// </para>
    /// <para>
    /// The two gaps this guard used to carry as "accepted but unverified" were MEASURED REACHABLE and are now
    /// CLOSED (EF-408) — see <see cref="ReservedElementNames"/>. (1) A set-op operand of a DIFFERENT entity
    /// type: a projected-operand <c>Union</c>/<c>Concat</c>/<c>Intersect</c>/<c>Except</c> does not require the
    /// two operands to share an entity type, the operand's own ops lower through this SAME allocator into the
    /// nested <c>$unionWith</c>/set-difference pipeline, and a computed sort there <c>$set</c>-clobbered an
    /// operand element mapped onto the synthetic name. (2) A TPH derived type's own members: every derived
    /// type shares the root's top-level document namespace, but <see cref="IEntityType.GetProperties"/>/
    /// <see cref="IEntityType.GetNavigations"/> on the root return declared and inherited members only. Both
    /// produced silently wrong data (a missing element, or an
    /// <see cref="InvalidOperationException"/> for a required non-nullable property) under the default
    /// <c>Native</c> mode; both are covered by tests in <c>NativeComputedSortTests</c>.
    /// </para>
    /// </remarks>
    private sealed class SyntheticSortFieldAllocator(IReadOnlyCollection<string> reservedElementNames)
    {
        private int _next;

        public string Allocate()
        {
            while (true)
            {
                var name = SyntheticSortFieldPrefix + _next++;
                if (!reservedElementNames.Contains(name))
                    return name;
            }
        }
    }

    // Every top-level element name a synthetic $set could collide with anywhere in THIS pipeline: the root
    // entity type's names, plus — for a set operation — the operand's own entity type's names, because the
    // operand's ops lower through the same allocator into the nested $unionWith / set-difference pipeline and
    // a projected-operand set op does not require the two operands to share an entity type (EF-408 gap 1).
    private static IReadOnlyCollection<string> ReservedElementNames(MongoQueryExpression query)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddTopLevelElementNames(query.CollectionExpression.EntityType, names);

        if (query.Select.SetOperation is { } setOp)
            AddTopLevelElementNames(setOp.OperandEntityType, names);

        return names;
    }

    // Top-level element names of an entity type: every mapped property, plus the containing element name of
    // each owned navigation (an owned sub-document occupies a top-level element too), plus the element name
    // of each complex property (a ComplexProperty occupies its own top-level document slot too —
    // GetProperties() does not see it, mirroring the precedent at
    // MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable's third guard arm).
    //
    // Walks GetDerivedTypesInclusive(), not just the type itself: in a TPH hierarchy every derived type's
    // documents live in the SAME collection and occupy the SAME top-level namespace, but GetProperties()/
    // GetNavigations()/GetComplexProperties() on a base type return declared+inherited members only — never
    // members declared on a derived type. A query over the base whose computed sort key allocated a synthetic
    // name that a derived type had mapped onto via HasElementName $set-clobbered it, and the trailing $unset
    // then REMOVED that real element from the document entirely (EF-408 gap 2, measured reachable).
    private static void AddTopLevelElementNames(IEntityType entityType, HashSet<string> names)
    {
        foreach (var type in entityType.GetDerivedTypesInclusive())
        {
            foreach (var property in type.GetProperties())
                names.Add(property.GetElementName());

            foreach (var navigation in type.GetNavigations())
            {
                if (navigation.IsEmbedded() && navigation.TargetEntityType.GetContainingElementName() is { } elementName)
                    names.Add(elementName);
            }

            foreach (var complexProperty in type.GetComplexProperties())
                names.Add(GetComplexPropertyElementName(complexProperty));
        }
    }

    // The document element name a complex property occupies at its own declaring type's top level. Mirrors
    // MongoQueryableMethodTranslatingExpressionVisitor.GetComplexPropertyElementName: there is no
    // IReadOnlyComplexProperty overload of GetElementName in this provider (no builder API renames a complex
    // property's own element name), so this reads the shared Mongo:ElementName annotation directly, with the
    // identical CLR-member-name fallback GetElementName itself uses for a plain property.
    private static string GetComplexPropertyElementName(IReadOnlyComplexProperty complexProperty)
        => (string?)complexProperty[MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames.ElementName]
           ?? complexProperty.Name;
}
