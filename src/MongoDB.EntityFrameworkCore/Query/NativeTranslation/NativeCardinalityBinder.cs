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
/// Populates <see cref="MongoSelectDefinition.Cardinality"/> for reducer and scalar-aggregate terminal operators,
/// mirroring <see cref="NativeProjectionBinder"/> for projections. Called from
/// <see cref="NativeSlotPopulator.PopulateNativeSlots"/>; a <see langword="false"/> return marks the query
/// non-native.
/// </summary>
internal static class NativeCardinalityBinder
{
    /// <summary>
    /// Synthesizes a native <c>$limit</c> (1 for First*, 2 for Single* so "more than one" stays detectable) and
    /// records the reducer kind. EF Core's base cardinality reduction applies the actual First/Single semantics,
    /// including the empty/more-than-one throws.
    /// </summary>
    internal static bool TryBindReducer(MongoQueryExpression mongoQ, MongoReducerKind kind, Type resultType)
    {
        var select = mongoQ.Select;

        // A reducer after a finalized GroupBy(key).Select(anon) must fall back: Route would stay on GroupBy and the
        // $limit would truncate the grouped rows instead of reducing over them. Exempt: a set-op-only terminal
        // (the $limit goes into TrailingOps) and a projected Distinct, whose $limit routes into PostGroupOps via
        // ActiveOps (same carve-out as TryBindAggregate).
        var isPostDistinctReducer = select.IsDistinct && !select.IsGroupBy && select.Grouping != null;

        if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly && !isPostDistinctReducer)
            return false;

        // A reducer after a confirmed two-sided join must fall back: its $limit lands in PipelineOps, emitted
        // before the join's $lookup/$unwind, so First() would limit to the first outer row and then drop it if it
        // has no match. Scalar aggregates are safe (their stage follows the lookups). See
        // MongoSelectDefinition.HasConfirmedJoinLookup.
        if (select.HasConfirmedJoinLookup)
            return false;

        // No HasLimit guard: AppendLimit appends to the tail, and consecutive $limits narrow monotonically, so
        // Take(3).First() emits [$limit 3, $limit 1].

        // Last/LastOrDefault: with a prior sort, flip it and reuse the First $limit:1 path. Without one, mark
        // MongoCardinality.UnorderedLastRow, which the lowerer renders as the fallback's
        // $group{_id:null,_last:{$last:"$$ROOT"}} + $replaceRoot pattern. That collapses the whole input, so it
        // must run after any $lookup; there's no slot for it alongside a projected Distinct's PostGroupOps, so
        // decline that combination.
        var isUnorderedLast = kind is MongoReducerKind.Last or MongoReducerKind.LastOrDefault
            && !select.TryFlipTrailingSortDirection();

        if (isUnorderedLast && isPostDistinctReducer)
            return false;

        var cardinality = MongoCardinality.ForReducer(kind, resultType, isUnorderedLast);

        if (!isUnorderedLast)
        {
            var limit = kind is MongoReducerKind.Single or MongoReducerKind.SingleOrDefault ? 2 : 1;
            select.AppendLimit(new MongoConstantExpression(limit, forSerialization: null));
        }

        // Cardinality and Grouping are ordinarily mutually exclusive; a post-Distinct reducer is the sanctioned
        // exception (see TryBindAggregate).
        if (isPostDistinctReducer)
            select.SetGroupedTerminalAggregate(select.Grouping!, cardinality, postGroupPredicate: null);
        else
            select.Cardinality = cardinality;
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

        // Aggregate directly on a bare GroupBy(key), e.g. GroupBy(o => o.CustomerID).Count(). Checked before the
        // HasTerminalOperator guard, which would always decline it (TranslateGroupBy sets IsGroupBy eagerly). A
        // false return falls through to that guard and declines.
        if (select.PendingGroupKey != null && select.Grouping == null
            && NativeGroupByBinder.TryBindGroupTerminalAggregate(mongoQ, op, predicate, resultType))
        {
            return true;
        }

        // Aggregate directly on a projected Distinct(), e.g. Select(o => o.OrderID).Distinct().Max(). Checked
        // before the guard for the same reason (TranslateDistinct finalizes IsDistinct/Grouping eagerly).
        if (select.IsDistinct && select.Cardinality == null
            && NativeGroupByBinder.TryBindDistinctTerminalAggregate(mongoQ, op, selector, resultType))
        {
            return true;
        }

        // An aggregate after a finalized GroupBy(key).Select(anon) must otherwise fall back: Cardinality would flip
        // Route to ScalarAggregate while the lowerer still emits [$group, $project] with no terminal stage, and the
        // scalar shaper would throw KeyNotFoundException. A set-op-only terminal is exempt (TrailingOps).
        //
        // Carve-out: after a projected Distinct the lowerer always emits PostGroupOps and then the terminal stage,
        // so Count/LongCount/Any/All are safe, as is a selector-bearing Sum/Min/Max/Average (resolved against the
        // Distinct's flattened alias via DistinctAliasScope). The selector-less form is the direct
        // TryBindDistinctTerminalAggregate case above; requiring a selector keeps the two exclusive.
        var isPostDistinctAggregate = select.IsDistinct && !select.IsGroupBy && select.Grouping != null
            && select.Cardinality == null
            && (op is MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All
                || (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
                    or MongoAggregateOperator.Max or MongoAggregateOperator.Average && selector != null));

        // Same carve-out after an ordinary GroupBy(key).Select(aggregate), e.g.
        // GroupBy(o => o.CustomerID).Select(g => g.Sum(o => o.OrderID)).All(v => ...). A GroupBy nested on a
        // projected Distinct (IsDistinct also true) is excluded; MongoSelectLowerer handles it via PriorGrouping.
        var isPostGroupBySelectAggregate = select.IsGroupBy && !select.IsDistinct && select.Grouping != null
            && select.Cardinality == null
            && (op is MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All
                || (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
                    or MongoAggregateOperator.Max or MongoAggregateOperator.Average && selector != null));

        // A selector-less Min()/Max() over the single scalar a preceding GroupBy(key).Select(aggregate) projected
        // (the parameterless overloads require IComparable, so it's always one member). Sum/Average and the
        // Distinct variant are out of scope.
        var isPostGroupBySelectlessMinMax = select.IsGroupBy && !select.IsDistinct && select.Grouping != null
            && select.Cardinality == null && selector == null
            && op is MongoAggregateOperator.Min or MongoAggregateOperator.Max
            && select.Projection.Count == 1;

        var isPostGroupTerminalAggregate =
            isPostDistinctAggregate || isPostGroupBySelectAggregate || isPostGroupBySelectlessMinMax;

        if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly && !isPostGroupTerminalAggregate)
            return false;

        // predicate is null for selector aggregates and bare Any()/Count(); SelfParam is then unused.
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, predicate?.Parameters[0]);

        // Post-group aggregates resolve their predicate/selector against the Select's flattened output alias,
        // not the entity (see MongoExpressionTranslator.DistinctAliasScope).
        if (isPostGroupTerminalAggregate)
            translator.DistinctAliasScope = select.Grouping;

        MongoExpression? operand = null;
        if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
               or MongoAggregateOperator.Max or MongoAggregateOperator.Average)
        {
            if (isPostGroupBySelectlessMinMax)
            {
                // Reduce the preceding Select's single flattened output field; there is no selector.
                var flattened = select.Projection[0];
                operand = new MongoElementRefExpression(flattened.Alias, flattened.Expression.Type);
            }
            // TryTranslateValue accepts member access, widening/nullable Converts and numeric arithmetic, and
            // rejects anything not exactly value-preserving — required by Sum/Average and sufficient for Min/Max.
            else if (selector is null || !translator.TryTranslateValue(selector.Body, out operand))
                return false; // untranslatable selector shape (e.g. a correlated method call) — fall back
        }

        // Injecting a predicate $match is safe after Take/Skip: AddPredicateConjunct appends to the tail, so
        // Take(n).All(pred) evaluates pred over the first n rows only.

        if (op is MongoAggregateOperator.All)
        {
            // All(pred) ≡ no row fails pred: $match the complement and test for presence. The complement is built
            // by MongoExpressionNegator over the translated tree (Expression.Not over the LINQ body would yield an
            // unrenderable MongoUnaryExpression), which also lets De Morgan handle and/or predicates.
            if (predicate is null)
                return false;

            if (!translator.TryTranslate(predicate.Body, out var predicateNode))
                return false;

            // The query-dialect negator declines a bare-accumulator-alias comparison (aggregation-only Left), so
            // post-group aggregates fall back to NativeGroupByBinder.TryNegateGroupComparison. Row-level
            // predicates still go through TryNegate only (pinned by
            // All_with_a_field_to_field_predicate_still_falls_back).
            if (!MongoExpressionNegator.TryNegate(predicateNode, out var negatedNode)
                && !(isPostGroupTerminalAggregate
                     && NativeGroupByBinder.TryNegateGroupComparison(predicateNode, out negatedNode)))
                return false; // no exact complement — decline, so the query falls back to driver-LINQ

            select.AddPredicateConjunct(negatedNode);
        }
        else if (predicate != null)
        {
            // Count(pred)/Any(pred): normally rewritten to Where(pred) + op; handled defensively.
            if (!translator.TryTranslate(predicate.Body, out var predNode))
                return false;

            select.AddPredicateConjunct(predNode);
        }

        BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);

        // Any/All are presence-only: the result depends on whether a row survived, not on a field value.
        var presenceOnly = op is MongoAggregateOperator.Any or MongoAggregateOperator.All;
        object? presentValue = op switch
        {
            MongoAggregateOperator.Any => true,
            MongoAggregateOperator.All => false,
            _ => null
        };

        // A presence-only aggregate after a join chain (e.g. Join(…).Join(…).Where(…).Any()) has no Select between
        // the joins and the aggregate, because nav-expansion only synthesizes one when row shape is needed. So the
        // Select-side confirming arms in TranslateSelect never run; confirm the chain here, using the same
        // eligibility check and commit helper. Placed just before success so a later decline can't leave
        // HasConfirmedJoinLookup set on a query that falls back.
        if (!select.HasConfirmedJoinLookup
            && Visitors.MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope(mongoQ, out _))
        {
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQ, select.JoinScope!);
        }

        var cardinality = MongoCardinality.ForAggregate(
            op, operand, emptyBehavior, emptyValue, resultType, presenceOnly, presentValue);

        // Cardinality and Grouping are ordinarily mutually exclusive; post-group aggregates are the sanctioned
        // exception. postGroupPredicate is null because any predicate built above already went into
        // PostGroupOps via AddPredicateConjunct, not the single-slot HAVING field.
        if (isPostGroupTerminalAggregate)
            select.SetGroupedTerminalAggregate(select.Grouping!, cardinality, postGroupPredicate: null);
        else
            select.Cardinality = cardinality;
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
