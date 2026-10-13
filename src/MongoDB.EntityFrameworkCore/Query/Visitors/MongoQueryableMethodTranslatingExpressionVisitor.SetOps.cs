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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Union, Concat, Intersect and Except translation and the operand-shape gates.
internal sealed partial class MongoQueryableMethodTranslatingExpressionVisitor
{
    // Native whole-entity or projected Union/Concat/Intersect/Except, appended to source1's select. Union/Concat always
    // return source1: native when admissible, otherwise marked non-native for a graceful fallback. Intersect/Except return
    // null when inadmissible (see below).
    private ShapedQueryExpression? TryTranslateSetOperation(
        ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
    {
        var mongo1 = (MongoQueryExpression)source1.QueryExpression;
        var mongo2 = (MongoQueryExpression)source2.QueryExpression;

        if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            AppendSetOperationLink(mongo1, mongo2, kind);
            return source1;
        }

        // Nested whole-entity Concat/Union, on either side:
        //   Left-nested (A.Concat(B).Concat(C)): append another link to source1's chain. Each Union link's dedup is emitted right
        //     after its own $unionWith, which makes Concat(Union(A,B),C) correct.
        //   Right-nested (A.Concat(B.Union(C))): can't be flattened, since A.Concat(B).Union(C) would also dedup A. The operand
        //     keeps its own chain, emitted inside the outer $unionWith's pipeline.
        // These compose to any depth (IsWholeEntitySetOpOperandSelect recurses). Intersect/Except are excluded: they lower to
        // MongoSetDifferenceStage and have no driver-LINQ oracle, so they keep hard-failing below.
        if (kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
            && (IsPlainWholeEntitySelect(mongo1) || IsWholeEntitySetOpChainSelect(mongo1))
            && (IsPlainWholeEntitySelect(mongo2) || IsWholeEntitySetOpChainSelect(mongo2))
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            // IsSetOp is already true when mongo1 is itself a chain; setting it is needed when it is plain.
            AppendSetOperationLink(mongo1, mongo2, kind);
            return source1;
        }

        // Projected operands: each side may be a plain projected Select, a projected Distinct, or a GroupBy(key).Select(aggregate);
        // the combine compares flattened values by alias. ProjectionShapesMatch is a correctness guard (dedup/source-tagging
        // compare whole projected documents), and OperandSerializationsMatch requires each alias to be stored the same way on
        // both sides.
        //
        // source1's own pending lookups are admitted because the lowerer emits them ahead of source1's $project; source2's
        // operand select carries no lookup plumbing, so it must have none.
        //
        // source1's shaper is reused for every combined row, so a constant/parameter leaf baked into it would show source1's
        // value on source2's rows (HasShaperUnsafeConstantLeaf rejects that; the server pipeline is fine either way). Exception:
        // a bare `$literal`-rendered constant/parameter of a round-trip-safe type (CanRebindConstantLeafToDocument) has its shaper
        // rebound to read the value per document, after every check has passed.
        //
        // Bare scalar operands may differ in alias (a column by document path e.g. `_id`; a computed value, constant or grouped
        // aggregate by `_v`). CanAlignBareScalarAliases admits those; only then is mongo2's single projection re-aliased to
        // mongo1's, after every check has passed.
        var rebindConstantLeaf = false;
        var alignBareScalarAliases = false;
        if ((IsPlainProjectedSelect(mongo1, allowPreCombineLookups: true) || IsPlainDistinctSelect(mongo1, allowPreCombineLookups: true)
                || IsPlainGroupBySelect(mongo1, allowPreCombineLookups: true))
            && (!HasShaperUnsafeConstantLeaf(mongo1)
                || (rebindConstantLeaf = CanRebindConstantLeafToDocument(source1, mongo1)))
            && (IsPlainProjectedSelect(mongo2) || IsPlainDistinctSelect(mongo2) || IsPlainGroupBySelect(mongo2))
            && ((ProjectionShapesMatch(mongo1.Select.Projection, mongo2.Select.Projection)
                 && OperandSerializationsMatch(mongo1.Select, mongo2.Select))
                // Aligned bare scalars already require default serialization on both sides (ReadsOnlyDefaultSerializedFields).
                || (alignBareScalarAliases = CanAlignBareScalarAliases(source1, mongo1, source2, mongo2))))
        {
            if (rebindConstantLeaf)
            {
                // Admitted through CanRebindConstantLeafToDocument. Read the `$literal`-projected value by alias, so
                // source2's rows read their own value instead of source1's baked-in one.
                source1 = source1.UpdateShaperExpression(
                    BindProjectionByAlias(mongo1, mongo1.Select.Projection[0].Alias, source1.ShaperExpression));
            }

            if (alignBareScalarAliases)
            {
                // Admitted through CanAlignBareScalarAliases. Combined rows are read through source1's shaper (source1's alias); the
                // kind-shape check (NativeDateTimeKindReadBack.HasKindMismatch, at compile time) pairs operand leaves by alias, so it
                // now sees this pair too.
                mongo2.Select.ReplaceSingleProjectionAlias(mongo1.Select.Projection[0].Alias);
            }

            AppendSetOperationLink(mongo1, mongo2, kind, operandsProjected: true);
            return source1;
        }

        // Out of scope. Union/Concat fall back gracefully. Intersect/Except have no driver-LINQ fallback, so
        // return null to hard-fail cleanly in every mode.
        if (kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
        {
            return null;
        }

        mongo1.Select.MarkNotNativelyRepresentable();
        return source1;
    }

    // Appends mongo2 as the next set-op link of mongo1's select and marks mongo1 a set op.
    private static void AppendSetOperationLink(
        MongoQueryExpression mongo1, MongoQueryExpression mongo2, MongoSetOperationKind kind, bool operandsProjected = false)
    {
        mongo1.Select.AppendSetOperation(new MongoSetOperation(
            kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, mongo2.CollectionExpression.EntityType,
            operandsProjected));
        mongo1.Select.IsSetOp = true;
    }

    // Whether the select is already a whole-entity Concat/Union nesting, so it can take another link or serve as an operand.
    // IsWholeEntitySetOpOperandSelect adds the recursive structural checks; the rest mirrors IsPlainWholeEntitySelect.
    private static bool IsWholeEntitySetOpChainSelect(MongoQueryExpression mongo)
        => mongo.Select.IsSetOpTerminalOnly
           && mongo.Select.SetOperations.Count > 0
           && IsWholeEntitySetOpOperandSelect(mongo.Select)
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !mongo.Select.HasClientWrappedWholeEntityShaper
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // Recursive: a whole-entity Concat/Union tree the lowerer can emit as a self-contained sub-pipeline; a plain whole-entity
    // select is the base case. Each conjunct rejects something operand lowering doesn't emit, which would be silently dropped:
    // no $project/$group/$unwind for a whole-entity operand (a whole-entity Distinct is a MongoDistinctOp in PipelineOps, so
    // fine), and projected or Intersect/Except links lower differently.
    // TrailingOps may be non-empty: ops between two links (A.Union(B).OrderBy(..).Take(1).Union(C)) become the next link's
    // PrecedingOps (MongoSelectDefinition.AppendSetOperation) and are emitted before its stage.
    private static bool IsWholeEntitySetOpOperandSelect(MongoSelectDefinition select)
        => select.Projection.Count == 0
           && select.Grouping == null
           && select.Cardinality == null
           && select.UnwindSources.Count == 0
           && select.SetOperations.All(
               link => !link.OperandsProjected
                       && link.Kind is MongoSetOperationKind.Concat or MongoSetOperationKind.Union
                       && IsWholeEntitySetOpOperandSelect(link.OperandSelect));

    // Filter/sort/paging only: no projection, grouping, scalar cardinality, own set op, SelectMany unwind, lookups, or
    // VectorSearch. UnwindSource: a whole-element owned SelectMany has Route == WholeEntity and no lookups, but operand
    // lowering emits no $unwind for mongo2 and source1's $unwind (plus its inner-element filter) would run after the
    // $unionWith over both sides, silently returning wrong rows. Pinned by
    // NativeSelectManyTests.Whole_owned_element_SelectMany_as_set_op_operand_does_not_go_native.
    private static bool IsPlainWholeEntitySelect(MongoQueryExpression mongo)
        => mongo.Select.Route == NativeRoute.WholeEntity
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.Projection.Count == 0
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && mongo.Lookups.Count == 0
           && !mongo.Select.HasClientWrappedWholeEntityShaper
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // The projected analogue of IsPlainWholeEntitySelect: a terminal anonymous/DTO Select is the only thing done.
    //
    // HasArrayProjectionLeaf: an owned-collection array leaf (Select(b => new { b.Title, b.Posts })) makes NativeProjectionBinder
    // emit the owner key into the projected document. Projected-operand dedup ($group{_id:"$$ROOT"}) and source-tagging compare
    // the whole document, so that key would turn value dedup into identity dedup (pinned by
    // Projected_operand_union_dedups_over_projected_values_not_whole_entities). Declining lets Union/Concat fall back;
    // Intersect/Except (no oracle) hard-fail. Bare projected operands are admitted (pinned by NativeBareProjectionTests).
    //
    // allowPreCombineLookups is passed only for source1, whose InjectAfterRoot projected-Count lookups (and a confirmed
    // single-level join's $lookup, IsPreCombineJoinScope) the lowerer emits ahead of its $project. Other lookups decline.
    private static bool IsPlainProjectedSelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => mongo.Select.Route == NativeRoute.Projection
           && mongo.Select.Projection.Count > 0
           && !mongo.Select.HasArrayProjectionLeaf
           // The raw case-mapping receiver would be what the combine dedups/compares.
           && !mongo.Select.HasClientCaseMappingProjectionLeaf
           // A client-evaluated leaf is never projected: the combine would dedup without it, and source1's shaper
           // would show source1's value on source2's rows.
           && !mongo.Select.HasClientEvaluatedProjectionLeaf
           && mongo.Select.SetOperation == null
           && !mongo.Select.IsSetOp
           && mongo.Select.Grouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && (allowPreCombineLookups
               ? (!mongo.IsJoinQuery || IsPreCombineJoinScope(mongo))
                 && mongo.Lookups.All(l => l.InjectAfterRoot || mongo.Joins.Any(j => ReferenceEquals(j.Lookup, l)))
               : !mongo.IsJoinQuery && mongo.Lookups.Count == 0)
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // A projected source1 over a single-level join scope whose confirming Select registered the join's $lookup.
    // HasConfirmedJoinLookup is set on a projected, ungrouped, unreduced select only by TranslateSelect's join-scope arms, each
    // gated by TryAdmitSingleEligibleNativeJoinScope, so this reuses that decision rather than re-running it (it may defer
    // PipelineOps). MongoSelectLowerer's OperandsProjected branch emits the join like the non-set-op path: $lookup/$unwind,
    // then PostJoinOps and PostLookupPagingOps, all ahead of source1's $project. The gate and that emission must move
    // together, else the inner-side $match is silently dropped.
    // Single-level only: a join chain is unvalidated here. The HasConfirmedJoinLookup conjunct is defence in depth (an
    // unconfirmed candidate already makes Route Fallback). Serves every set-op kind, Intersect/Except included
    // (NativeGroupByOverJoinTests.Intersect_/Except_of_a_join_scope_operand_...).
    private static bool IsPreCombineJoinScope(MongoQueryExpression mongo)
        => mongo.Select.HasConfirmedJoinLookup
           && mongo.Select.JoinScope is { Levels.Count: 1 };

    // A projected Distinct (Select(new {...}).Distinct(), Route == GroupBy) as a set-op operand; its $group + flattening
    // $project become its pre-combine pipeline. IsDistinct and IsGroupBy are mutually exclusive (a real GroupBy is
    // IsPlainGroupBySelect); PriorGrouping excludes a GroupBy nested on the Distinct.
    private static bool IsPlainDistinctSelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsDistinct
           && !mongo.Select.IsGroupBy
           // Same reason as IsPlainProjectedSelect: the Distinct keeps the flag of the Select it deduplicates.
           && !mongo.Select.HasClientEvaluatedProjectionLeaf;

    // A real GroupBy(key).Select(aggregate) as a set-op operand. The join wrong-data hazard of grouped sources doesn't apply
    // to Union/Concat, and the lowerer emits a Grouping-bearing operand's $group + $project the same way for Distinct and
    // GroupBy (AppendSetOpChainStages).
    private static bool IsPlainGroupBySelect(MongoQueryExpression mongo, bool allowPreCombineLookups = false)
        => IsPlainGroupedOperandSelect(mongo, allowPreCombineLookups)
           && mongo.Select.IsGroupBy
           // Operand lowering emits only the operand's $group + flattening $project, so post-group Skip/Take, HAVING, or OrderBy
           // would be silently dropped; such operands fall through to the out-of-scope decline.
           && mongo.Select.GroupPagingOps.Count == 0
           && mongo.Select.GroupHavingPredicate == null
           && mongo.Select.GroupOrderOp == null;

    // The conjuncts IsPlainDistinctSelect and IsPlainGroupBySelect share: a Grouping-bearing GroupBy-route select with no
    // prior grouping, scalar cardinality, SelectMany unwind, join or VectorSearch, and no lookups beyond (source1 only)
    // InjectAfterRoot ones.
    private static bool IsPlainGroupedOperandSelect(MongoQueryExpression mongo, bool allowPreCombineLookups)
        => mongo.Select.Route == NativeRoute.GroupBy
           && mongo.Select.Grouping != null
           && mongo.Select.PriorGrouping == null
           && mongo.Select.Cardinality == null
           && mongo.Select.UnwindSource == null
           && !mongo.IsJoinQuery
           && (allowPreCombineLookups ? mongo.Lookups.All(l => l.InjectAfterRoot) : mongo.Lookups.Count == 0)
           && !mongo.CapturedExpression.ContainsVectorSearch();

    // mongo1's shaper is reused for every combined row, so a constant/parameter leaf (never read from the document) would
    // repeat mongo1's value. Only unsafe in that context (standalone it is fine: NativeComputedBareProjectionTests); the one
    // exception, a bare round-trip-safe leaf on an ungrouped select, is CanRebindConstantLeafToDocument.
    // A projected Distinct moves the projected values into its key parts but leaves the shaper as built, constant baked in, so
    // its key parts are checked too. Scoped to IsDistinct: a real GroupBy's constant key is read back from `_id`.
    private static bool HasShaperUnsafeConstantLeaf(MongoQueryExpression mongo)
        => mongo.Select.Projection.Any(p => IsConstantOrParameterLeaf(p.Expression))
           || mongo.Select is { IsDistinct: true, Grouping: { } grouping }
           && grouping.Key.Any(k => IsConstantOrParameterLeaf(k.FieldRef));

    private static bool IsConstantOrParameterLeaf(MongoExpression expression)
        => expression is MongoConstantExpression or MongoParameterExpression;

    // Whether source1's constant/parameter leaf can be read from the document instead of the shaper: a bare scalar whose single
    // projection is a server-rendered (`$literal`) constant or parameter of a type whose BSON round-trip returns the C# value.
    // The gate and the shaper rebind in TryTranslateSetOperation both use this (internal for
    // NativeSetOperationConstantLeafRebindTests).
    // - Bare scalar only: a construction's members are read by name from its own shaper.
    // - Constant/parameter leaf, trial-rendered by TryProbeBareValueRenders: never a client-evaluated one (defence in depth).
    // - Round-trip-safe CLR type: a DateTime reads back UTC-kind, a Guid depends on the representation, an enum on its
    //   storage type; those decline.
    // - Ungrouped select only: the rebind binds a plain projection member, which a grouped shaper doesn't read (it throws
    //   "Document element '_v0' is missing"). A projected Distinct is grouped too.
    // - Nothing already bound under the leaf's alias in the read-side projection list: the rebind goes through AddToProjection,
    //   which renames a colliding alias (`_v` -> `_v0`), a field the pipeline never emits. Overlaps the ungrouped conjunct, so
    //   neither is killable by mutation alone (removing both fails
    //   Union_of_grouped_constant_result_and_column_declines and Grouped_constant_result_does_not_rebind).
    internal static bool CanRebindConstantLeafToDocument(ShapedQueryExpression source1, MongoQueryExpression mongo1)
        => IsBareScalarOperand(source1, mongo1)
           && mongo1.Select.Grouping == null
           && !mongo1.Projection.Any(
               pe => string.Equals(pe.Alias, mongo1.Select.Projection[0].Alias, StringComparison.OrdinalIgnoreCase))
           && mongo1.Select.Projection[0].Expression is MongoConstantExpression or MongoParameterExpression
           && NativeSlotPopulator.TryProbeBareValueRenders(mongo1.Select.Projection[0].Expression, source1.ShaperExpression.Type)
           && IsRoundTripSafeConstantLeafType(source1.ShaperExpression.Type);

    private static bool IsRoundTripSafeConstantLeafType(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(bool)
           || type == typeof(string);

    // A bare scalar operand (Select(x => x.Id), GroupBy(..).Select(g => g.Count())): a single projected value whose shaper
    // isn't a construction. Detected by shape; IsBareProjection isn't set for a grouped g => g.Count().
    private static bool IsBareScalarOperand(ShapedQueryExpression source, MongoQueryExpression mongo)
        => mongo.Select.Projection is [{ Expression: not MongoDocumentConstructionExpression }]
           && source.ShaperExpression.RemoveConvert() is not (NewExpression or MemberInitExpression);

    // Whether two bare scalar operands with different aliases can be combined by re-aliasing source2's single projection to
    // source1's. The gate and the rewrite in TryTranslateSetOperation both use this (internal for
    // NativeSetOperationAliasAlignmentTests).
    // - Only a bare scalar: a construction's members are read by name, so its aliases must match (ProjectionShapesMatch).
    // - No post-group ops on source2: they read its own flattened alias, which the re-alias would orphan (a Where would
    //   silently match nothing, dropping every source2 row).
    // - Default serialization on both sides: every combined row is read through source1's shaper, so a converted or
    //   non-default-representation column on either side would be read with the other side's serializer.
    internal static bool CanAlignBareScalarAliases(
        ShapedQueryExpression source1, MongoQueryExpression mongo1, ShapedQueryExpression source2, MongoQueryExpression mongo2)
        => IsBareScalarOperand(source1, mongo1)
           && IsBareScalarOperand(source2, mongo2)
           && mongo2.Select.PostGroupOps.Count == 0
           && ReadsOnlyDefaultSerializedFields(mongo1.Select)
           && ReadsOnlyDefaultSerializedFields(mongo2.Select);

    // A grouped operand's flattening projection reads the $group output, so its key parts and accumulator operands are what
    // reach the stored fields. The key-part conjunct is defence in depth (StoredSerialization.HasDefaultKeySerialization).
    private static bool ReadsOnlyDefaultSerializedFields(MongoSelectDefinition select)
        => select.Projection.All(p => MongoExpressionTranslator.AllFieldsDefaultSerialized(p.Expression))
           && (select.Grouping is not { } grouping
               || (grouping.Key.All(k => MongoExpressionTranslator.AllFieldsDefaultSerialized(k.FieldRef))
                   && grouping.Accumulators.All(
                       a => a.Operand is null || MongoExpressionTranslator.AllFieldsDefaultSerialized(a.Operand))));

    // Operands must have the same top-level alias set: dedup and source-tagging compare whole projected documents. Field
    // refs may differ. EF already requires a common anonymous type, so this is defence in depth.
    private static bool ProjectionShapesMatch(
        IReadOnlyList<MongoProjection> p1, IReadOnlyList<MongoProjection> p2)
    {
        if (p1.Count != p2.Count)
        {
            return false;
        }

        var aliases = new HashSet<string>(p1.Count);
        foreach (var projection in p1)
        {
            aliases.Add(projection.Alias);
        }

        foreach (var projection in p2)
        {
            if (!aliases.Contains(projection.Alias))
            {
                return false;
            }
        }

        return true;
    }

    // Each alias's values must be stored the same way on both operands: dedup and Intersect/Except compare stored BSON, and
    // source1's shaper reads every combined row through source1's property serializer. Without this, Converted.Select(o =>
    // o.Status) (stored "Shipped") against Other.Select(o => o.Status) (stored 1) dedups nothing, intersects to empty, or throws
    // reading 1 as a string. Covers bare, wrapped and projected-Distinct operands (key part read back from "_id.<alias>"); a
    // real GroupBy key is already default-serialized. DateTimeKind is checked separately (NativeDateTimeKindReadBack).
    private static bool OperandSerializationsMatch(MongoSelectDefinition select1, MongoSelectDefinition select2)
    {
        foreach (var projection1 in select1.Projection)
        {
            // MongoProjection is a struct: an absent alias is default, with a null Expression.
            var expression2 = select2.Projection.FirstOrDefault(p => p.Alias == projection1.Alias).Expression as MongoExpression;

            // A DateTime.TimeOfDay leaf stores milliseconds (a long), unlike any other TimeSpan (default string form): the combine
            // would dedup/intersect a long against a string, and the alias would be read through only source1's reader
            // (MongoSelectDefinition.IsTimeOfDayProjection). Both sides TimeOfDay store and read the same way.
            if (MongoDatePartExpression.IsTimeOfDay(projection1.Expression)
                != (expression2 is not null && MongoDatePartExpression.IsTimeOfDay(expression2)))
            {
                return false;
            }

            var field1 = StoredField(select1, projection1.Expression);
            var field2 = expression2 is not null ? StoredField(select2, expression2) : null;

            if (!StoredSerializationsMatch(field1, field2))
            {
                return false;
            }
        }

        return true;
    }

    // Both sides default-serialized (a computed value, null here, has no property and so no converter); or the same property;
    // or two properties stored identically (StoredSerialization.StoredAlike, shared with the anonymous-key join), e.g. two
    // HasConversion<string>() enums. A converter or representation against none, or two different ones, declines.
    private static bool StoredSerializationsMatch(MongoFieldExpression? field1, MongoFieldExpression? field2)
    {
        var default1 = field1 == null || StoredSerialization.HasDefaultKeySerialization(field1.Property);
        var default2 = field2 == null || StoredSerialization.HasDefaultKeySerialization(field2.Property);
        if (default1 && default2)
        {
            return true;
        }

        if (field1 == null || field2 == null)
        {
            return false;
        }

        return field1.Property == field2.Property
               || StoredSerialization.StoredAlike(field1.Property, field2.Property);
    }

    // The entity field whose stored value a projected operand's alias carries: the projected field itself, or, for a
    // projected Distinct/GroupBy operand, the key part its flattened "_id.<alias>" (or single-key "_id") reads.
    private static MongoFieldExpression? StoredField(MongoSelectDefinition select, MongoExpression value)
        => value switch
        {
            MongoFieldExpression field => field,
            // A marked Distinct key's flatten restores MISSING from the marker: $cond [marker, $$REMOVE, "_id.<alias>"]. Without
            // this a marked value-converted key (ClassifyMalformedFieldRead) would look default-serialized and match an operand
            // stored differently.
            MongoConditionalExpression { IfFalse: MongoElementRefExpression keyRef } when select is { IsDistinct: true, Grouping: not null }
                => StoredField(select, keyRef),
            MongoElementRefExpression { Path: var path } when select.Grouping is { } grouping
                => (path == "_id" && grouping.Key.Count == 1
                        ? grouping.Key[0]
                        : grouping.Key.FirstOrDefault(k => k.Name != null && path == "_id." + k.Name))
                    ?.FieldRef as MongoFieldExpression,
            _ => null
        };
}
