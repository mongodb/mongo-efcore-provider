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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
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
        // MongoSelectDefinition.HasConfirmedJoinLookup. Exempt: every join is row-count-preserving (a left-outer
        // reference-navigation $lookup, e.g. `Select(o => o.Customer.City).First()`), whose $unwind neither drops nor
        // multiplies rows, so a $limit before it equals one after it (the same argument the paging path relies on).
        if (select.HasConfirmedJoinLookup && !mongoQ.AreAllJoinsRowCountPreserving())
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
    /// The single server-computed value a preceding bare scalar <c>Select</c> projected — <c>Select(o =&gt; o.OrderID)</c>,
    /// <c>Select(o =&gt; o.OrderID * 2)</c>, <c>Select(o =&gt; (long)o.OrderID)</c> — when <paramref name="sourceShaper"/>
    /// reads exactly that value back with no client-side computation layered on top; otherwise
    /// <see langword="null"/>. A selector-less terminal (<c>Sum()</c>/<c>Min()</c>/<c>Max()</c>/<c>Average()</c>,
    /// <c>Contains(item)</c>) reduces this value.
    /// </summary>
    /// <remarks>
    /// The shaper check is what rejects a client-evaluated projection: a client method call or other client
    /// computation leaves a non-<see cref="ProjectionBindingExpression"/> node in the shaper. The string-sequence
    /// leaf is the one client-applied leaf that ERASES its call from the shaper (it is registered as one projection
    /// member — see <see cref="MongoSelectDefinition.HasStringSequenceProjectionLeaf"/>), so it is excluded by flag.
    /// The alias is deliberately NOT checked: a bare member leaf keeps its document-path alias, only a computed one
    /// takes <see cref="NativeProjectionBinder.SyntheticBareProjectionAlias"/>. A one-member anonymous/DTO
    /// projection has a single projection too, but its shaper is a construction, not a bare binding.
    /// </remarks>
    internal static MongoProjection? TryGetBareServerValueProjection(MongoQueryExpression mongoQ, Expression sourceShaper)
    {
        var select = mongoQ.Select;
        if (select.Grouping != null
            || select.HasTerminalOperator
            || select.HasStringSequenceProjectionLeaf
            || select.Projection is not [var projection])
        {
            return null;
        }

        while (sourceShaper is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            sourceShaper = convert.Operand;

        return sourceShaper is ProjectionBindingExpression ? projection : null;
    }

    /// <summary>
    /// Binds a terminal <c>source.Contains(item)</c> as the equivalent <c>source.Any(x =&gt; x == item)</c>:
    /// over a bare scalar-field <c>Select</c> (<c>Select(c =&gt; c.CustomerID).Contains("ALFKI")</c>) as an equality
    /// <c>$match</c> on that field, or over a whole-entity source (<c>Where(...).Contains(order)</c>,
    /// <c>.Contains(null)</c>) via the ordinary entity-equality translation. Returns <see langword="false"/> for any
    /// other shape (a computed or joined projection, a client-wrapped shaper), so the caller marks the query non-native.
    /// </summary>
    internal static bool TryBindContains(MongoQueryExpression mongoQ, Expression sourceShaper, Expression item)
    {
        var select = mongoQ.Select;

        if (TryGetBareServerValueProjection(mongoQ, sourceShaper) is { } bareSource)
        {
            // Joins.Count == 0: a joined field (`_lookup_X.City`) would be matched in PipelineOps, BEFORE its $lookup.
            // An ARRAY-typed field is excluded too: a query-dialect {field: value} match also matches element-wise
            // (`Select(r => r.Tags).Contains(null)` would match a Tags array CONTAINING a null), whereas LINQ compares
            // the item with each whole array.
            if (mongoQ.Joins.Count != 0
                || bareSource.Expression is not MongoFieldExpression field
                || IsArrayTyped(field.Property.ClrType)
                || !TryTranslateContainsItem(item, field.Property, out var itemNode))
            {
                return false;
            }

            if (!TryBindAggregate(mongoQ, MongoAggregateOperator.Any, selector: null, predicate: null, typeof(bool)))
                return false;

            // Committed only after TryBindAggregate succeeded (no mutate-then-decline). AddPredicateConjunct appends
            // at the TAIL of the op list, so a Take/Skip recorded earlier still runs first.
            select.AddPredicateConjunct(new MongoBinaryExpression(MongoBinaryOperator.Equal, field, itemNode));
            return true;
        }

        // Joins.Count == 0 / !HasConfirmedJoinLookup: a whole-entity source reached THROUGH a navigation or join
        // (`Select(e => e.Manager).Contains(emp)`, a bare `ti => ti.Inner` leaf) keeps Route == WholeEntity and the
        // root CLR type (a self-referencing navigation), but its elements are the JOINED entities — translating
        // `e == item` against the root would answer "is item any root row?" instead.
        var elementType = sourceShaper.Type;
        if (select.Route != NativeRoute.WholeEntity
            || mongoQ.Joins.Count != 0
            || select.HasConfirmedJoinLookup
            || select.HasClientWrappedWholeEntityShaper
            || !mongoQ.CollectionExpression.EntityType.ClrType.IsAssignableFrom(elementType)
            || !elementType.IsAssignableFrom(item.Type))
        {
            return false;
        }

        var element = Expression.Parameter(elementType, "e");
        var comparand = item.Type == elementType ? item : Expression.Convert(item, elementType);
        var predicate = Expression.Lambda(Expression.Equal(element, comparand), element);
        return TryBindAggregate(mongoQ, MongoAggregateOperator.Any, selector: null, predicate, typeof(bool));
    }

    private static bool IsArrayTyped(Type type)
        => type != typeof(string) && type != typeof(byte[]) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    private static bool TryTranslateContainsItem(
        Expression item, IProperty property, [NotNullWhen(true)] out MongoExpression? node)
    {
        var unwrapped = MongoExpressionTranslator.Unwrap(item);
        node = unwrapped switch
        {
            ConstantExpression constant => new MongoConstantExpression(constant.Value, property),
            _ when NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var name)
                => new MongoParameterExpression(name, property),
            _ => null
        };
        return node != null;
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
        Type resultType,
        MongoProjection? bareSourceProjection = null)
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

        // A predicate after a keyed GroupBy.Select records into ActiveOps, which lower right after this grouping's
        // flatten only when IsFinalizedKeyedGroupOutput holds. Over a nested GroupBy or a set op it would run ahead of
        // the $group, before the aliases it reads exist (GroupBy.Select.GroupBy.Select.All(pred) answered wrongly).
        // Predicate-free and selector-only forms are unaffected: the terminal stage follows every op.
        if (isPostGroupBySelectAggregate && (op is MongoAggregateOperator.All || predicate != null)
            && !select.IsFinalizedKeyedGroupOutput)
            return false;

        // predicate is null for selector aggregates and bare Any()/Count(); SelfParam is then unused.
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, predicate?.Parameters[0]);

        // Post-group aggregates resolve their predicate/selector against the preceding stage's flattened output,
        // never the entity. After a projected Distinct its key-part names are the aliases (DistinctAliasScope). After
        // a keyed GroupBy.Select the aliases are the Select's member names, which may shadow a key-part name with a
        // different value, so only the projection is consulted (ProjectedAliasScope). At most one of predicate and
        // selector is non-null; with neither, the translator is unused (isPostGroupBySelectlessMinMax reads the sole
        // projection directly below).
        if (isPostDistinctAggregate)
            translator.DistinctAliasScope = select.Grouping;
        else if (isPostGroupBySelectAggregate && (predicate ?? selector)?.Parameters[0] is { } aliasParameter)
            translator.ProjectedAliasScope = new MongoProjectedAliasScope(aliasParameter, select.Projection);

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
            else if (selector is null && bareSourceProjection is { } bareSource)
            {
                // The $group reduces the STORED value, so a value-converted/non-default-represented leaf must decline
                // ($sum ignores a string-stored int, $max compares it lexicographically), as TryTranslateValue does for
                // a selector.
                if (!MongoExpressionTranslator.AllFieldsDefaultSerialized(bareSource.Expression))
                    return false;

                // Selector-less Sum()/Min()/Max()/Average() over a bare scalar Select: reduce that Select's $project
                // output field, which the lowerer emits before this aggregate's $group.
                operand = new MongoElementRefExpression(bareSource.Alias, bareSource.Expression.Type);
            }
            // TryTranslateValue accepts member access, widening/nullable Converts and numeric arithmetic, and
            // rejects anything not exactly value-preserving — required by Sum/Average and sufficient for Min/Max.
            else if (selector is null || !translator.TryTranslateValue(selector.Body, out operand))
                return false; // untranslatable selector shape (e.g. a correlated method call) — fall back

            // A non-nullable Min/Max/Average of a Length/IndexOf over a possibly-null string reduces all-null rows to
            // null, read back as 0 where EF throws; as for the grouped accumulators. Sum skips a null, as EF's SUM does.
            if (op is not MongoAggregateOperator.Sum
                && MongoAggregationExpressionRenderer.ReadsNullAsDefault(resultType, operand))
                return false;
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
