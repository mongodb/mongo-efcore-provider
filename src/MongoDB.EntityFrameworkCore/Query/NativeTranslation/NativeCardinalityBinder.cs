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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates <see cref="MongoSelectDefinition.Cardinality"/> for an entity-reducer terminal operator
/// (First/FirstOrDefault/Single/SingleOrDefault), mirroring <see cref="NativeProjectionBinder"/>'s role
/// for projections. Called from <see cref="NativeSlotPopulator.PopulateNativeSlots"/>. Returns
/// <see langword="false"/> when the operator is not natively representable (e.g. a limit is already
/// present from a preceding Take/Skip); the caller then marks the query non-native.
/// </summary>
internal static class NativeCardinalityBinder
{
    /// <summary>
    /// Synthesizes a native <c>$limit</c> (1 for First*, 2 for Single*, so the server-side reducer can
    /// still distinguish "more than one" from "exactly one" for the Single family) and records the
    /// reducer kind on <see cref="MongoSelectDefinition.Cardinality"/>. EF Core's base cardinality
    /// reduction (over the returned <see cref="System.Collections.Generic.IEnumerable{T}"/>) performs the
    /// actual First/Single semantics, including the empty-throw / more-than-one-throw behavior.
    /// </summary>
    internal static bool TryBindReducer(MongoQueryExpression mongoQ, MongoReducerKind kind, Type resultType)
    {
        var select = mongoQ.Select;

        // A reducer applied after a finalized GroupBy(key).Select(anon)/Distinct must fall back: setting
        // Cardinality.Reducer would leave Route on GroupBy, so the lowerer still emits [$group, $project] and
        // the reducer's own $limit (below) would truncate the grouped rows instead of reducing over them.
        // A set-op-only terminal is exempt: a reducer composed after a set op goes native, recording its
        // $limit into TrailingOps (after the set-op stage) instead of PipelineOps.
        if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly)
            return false;

        // A user Take/Skip already populated the limit slot; composing a reducer limit on top is not
        // representable in canonical order. Fall back rather than reconcile two limits.
        // HasLimit only scans PipelineOps, not TrailingOps: after a set-op terminal, a preceding Take's limit
        // lives in TrailingOps and this guard doesn't see it, so the reducer appends a second $limit onto
        // TrailingOps too. Two consecutive $limit stages compose correctly (the second only narrows the
        // first), so this is a deliberate, safe divergence from the non-set-op path, not a bug.
        if (select.HasLimit)
            return false;

        var limit = kind is MongoReducerKind.Single or MongoReducerKind.SingleOrDefault ? 2 : 1;
        select.AppendLimit(new MongoConstantExpression(limit, forSerialization: null));
        select.Cardinality = MongoCardinality.ForReducer(kind, resultType);
        return true;
    }

    /// <summary>
    /// Attempts to bind a scalar aggregate terminal operator (Count/LongCount/Any/All/Sum/Min/Max/Average)
    /// to <see cref="MongoSelectDefinition.Cardinality"/>. Returns <see langword="false"/> for any shape
    /// outside the current native acceptance set (e.g. a computed selector), so the caller marks the query
    /// non-native and falls back to driver-LINQ.
    /// </summary>
    internal static bool TryBindAggregate(
        MongoQueryExpression mongoQ,
        MongoAggregateOperator op,
        LambdaExpression? selector,
        LambdaExpression? predicate,
        Type resultType)
    {
        var select = mongoQ.Select;

        // A scalar aggregate applied after a finalized GroupBy(key).Select(anon)/Distinct must fall back:
        // setting Cardinality on an already-grouped select flips Route to ScalarAggregate (which takes
        // priority over Grouping), but the lowerer's grouping branch still emits [$group, $project] with no
        // terminal $count/aggregate stage — the scalar shaper then reads a nonexistent element and crashes
        // with KeyNotFoundException instead of falling back cleanly.
        // A set-op-only terminal is exempt: an aggregate composed after a set op goes native, recording its
        // injected predicate/$limit into TrailingOps (after the set-op stage) instead of PipelineOps.
        if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        MongoFieldExpression? operand = null;
        if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
               or MongoAggregateOperator.Max or MongoAggregateOperator.Average)
        {
            // Selector must be a plain member access → field ref. Computed selectors fall back.
            if (selector?.Body is not MemberExpression || !translator.TryTranslateField(selector.Body, out operand))
                return false;
        }

        // An aggregate that injects a predicate as a $match (All always does; Count/Any defensively when an
        // unnormalized predicate overload reaches here) is safe to inject even when paging (Take/Skip) is
        // already present: AddPredicateConjunct always appends to the TAIL of the ordered op list, i.e. after
        // any $skip/$limit already recorded, never hoisting ahead of it. So Take(n).All(pred)/Count(pred)/
        // Any(pred) correctly evaluate the predicate over only the first n rows.

        if (op is MongoAggregateOperator.All)
        {
            // All(pred) ≡ no row fails pred. Push the EXACT COMPLEMENT of the predicate as a $match; presence
            // of any surviving row (after $count) means at least one row failed pred, so All is false.
            // The complement is built by MongoExpressionNegator over the TRANSLATED tree (not by wrapping the
            // LINQ body in Expression.Not, which would translate to a MongoUnaryExpression(Not, comparison)
            // the renderer can't render). Negating after translation also means De Morgan applies, so a
            // conjunctive/disjunctive predicate goes native too.
            if (predicate is null)
                return false;

            if (!translator.TryTranslate(predicate.Body, out var predicateNode))
                return false;

            if (!MongoExpressionNegator.TryNegate(predicateNode, out var negatedNode))
                return false; // no exact complement — decline, so the query falls back to driver-LINQ

            select.AddPredicateConjunct(negatedNode);
        }
        else if (predicate != null)
        {
            // Count(pred)/Any(pred) — the normalizer usually rewrites these to Where(pred) + op, but handle
            // defensively in case an unnormalized predicate-taking overload reaches here.
            if (!translator.TryTranslate(predicate.Body, out var predNode))
                return false;

            select.AddPredicateConjunct(predNode);
        }

        BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);

        // Any/All are presence-only: the result is determined by whether a row survived the terminal $limit
        // stage, not by deserializing a field from it. See MongoSelectLowerer / ExecuteAggregate.
        var presenceOnly = op is MongoAggregateOperator.Any or MongoAggregateOperator.All;
        object? presentValue = op switch
        {
            MongoAggregateOperator.Any => true,
            MongoAggregateOperator.All => false,
            _ => null
        };

        select.Cardinality = MongoCardinality.ForAggregate(
            op, operand, emptyBehavior, emptyValue, resultType, presenceOnly, presentValue);
        return true;
    }

    /// <summary>
    /// Maps each aggregate's empty-input semantics to the BCL LINQ contract: what value (if any) the
    /// aggregate yields when the server returns zero rows.
    /// </summary>
    internal static void BuildEmptyBehavior(
        MongoAggregateOperator op, Type resultType, out object? emptyValue, out MongoEmptyAggregateBehavior behavior)
    {
        emptyValue = null;
        switch (op)
        {
            case MongoAggregateOperator.Count:
                emptyValue = 0;
                behavior = MongoEmptyAggregateBehavior.DefaultValue;
                break;
            case MongoAggregateOperator.LongCount:
                emptyValue = 0L;
                behavior = MongoEmptyAggregateBehavior.DefaultValue;
                break;
            case MongoAggregateOperator.Any:
                emptyValue = false;
                behavior = MongoEmptyAggregateBehavior.DefaultValue;
                break;
            case MongoAggregateOperator.All:
                emptyValue = true;
                behavior = MongoEmptyAggregateBehavior.DefaultValue;
                break;
            case MongoAggregateOperator.Sum:
                // Sum over empty is 0 (typed), including for nullable numeric result types — never null.
                emptyValue = TypedZero(resultType);
                behavior = MongoEmptyAggregateBehavior.DefaultValue;
                break;
            case MongoAggregateOperator.Min:
            case MongoAggregateOperator.Max:
            case MongoAggregateOperator.Average:
                behavior = Nullable.GetUnderlyingType(resultType) != null
                    ? MongoEmptyAggregateBehavior.ReturnNull
                    : MongoEmptyAggregateBehavior.Throw;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    private static object TypedZero(Type resultType)
    {
        var t = Nullable.GetUnderlyingType(resultType) ?? resultType;
        return Convert.ChangeType(0, t);
    }
}
