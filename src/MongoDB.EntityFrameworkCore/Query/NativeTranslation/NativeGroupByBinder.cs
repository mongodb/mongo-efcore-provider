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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates <see cref="MongoSelectDefinition.Grouping"/> for a <c>GroupBy(key).Select(aggregate)</c> shape,
/// emitting a native <c>$group</c>. Mirrors <see cref="NativeCardinalityBinder"/>'s role for scalar aggregates.
/// The key is parsed first (<see cref="TryBindGroupKey"/>, from the <c>GroupBy</c> key selector) and stashed on
/// <see cref="MongoSelectDefinition.PendingGroupKey"/>; the projection is parsed second
/// (<see cref="TryBindGroupProjection"/>, from the <c>Select</c> result selector) and finalizes
/// <see cref="MongoSelectDefinition.Grouping"/>. Either step returns <see langword="false"/> when the shape is
/// not natively representable, so the caller marks the query non-native and falls back to driver-LINQ.
/// </summary>
internal static class NativeGroupByBinder
{
    // The reserved element name the grouping key occupies in the emitted $group document.
    private const string GroupIdFieldName = "_id";

    /// <summary>
    /// Parses the <c>GroupBy</c> key selector into <see cref="MongoSelectDefinition.PendingGroupKey"/>.
    /// A bare <see cref="MemberExpression"/> is a scalar (single, unnamed) key; a <see cref="NewExpression"/>
    /// with members (an anonymous type) is a composite key whose parts each carry the member name. Every part
    /// must be a plain member access translatable to a field-ref; anything else (a computed key such as
    /// <c>x =&gt; x.Date.Year</c>) returns <see langword="false"/> and leaves the pending state unset.
    /// </summary>
    internal static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)
    {
        var select = mongoQ.Select;

        // Post-group paging / ordering on top of a pre-existing select is out of scope; fall back.
        if (select.HasPaging || select.HasOrdering)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var parts = new List<MongoGroupingKeyPart>();

        switch (keySelector.Body)
        {
            case NewExpression { Members: { Count: > 0 } members } newExpr:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    if (newExpr.Arguments[i] is not MemberExpression
                        || !translator.TryTranslateField(newExpr.Arguments[i], out var field)
                        || !HasDefaultKeySerialization(field.Property))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(members[i].Name, field));
                }

                break;

            case MemberExpression:
                if (!translator.TryTranslateField(keySelector.Body, out var scalarField)
                    || !HasDefaultKeySerialization(scalarField.Property))
                    return false;
                parts.Add(new MongoGroupingKeyPart(null, scalarField));
                break;

            default:
                return false; // computed / unsupported key shape
        }

        select.PendingGroupKey = parts;
        return true;
    }

    // A grouping key becomes the group's _id and is read back through a generic CLR-type serializer by the
    // grouped-row shaper (the flattened _id has no backing IProperty). That generic read only reproduces the
    // property's materialized value when the property serializes with the default/identity representation. A
    // property with a value converter (stored as the provider value, needing reverse conversion) or a
    // non-default BsonRepresentation (e.g. enum-as-string) would either throw at materialization or return
    // the raw stored value, diverging from the driver-LINQ path. Reject such keys so the query falls back
    // instead, preserving the Native == DriverLinq invariant. The accumulator operand is deliberately not
    // checked here: Sum/Min/Max over a represented field is a pre-existing shared caveat where native and
    // driver-LINQ are wrong the same way (no divergence).
    // Internal (not private) — also shared by the QMTEV's TranslateOfType discriminator guard, which rejects a
    // value-converted / non-default-BsonRepresentation discriminator for the identical generic-readback reason.
    internal static bool HasDefaultKeySerialization(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() == null;

    /// <summary>
    /// Parses the <c>Select</c> result selector against the pending key from <see cref="TryBindGroupKey"/>,
    /// finalizing <see cref="MongoSelectDefinition.Grouping"/>. The body must be a <see cref="NewExpression"/>
    /// (anonymous type) or <see cref="MemberInitExpression"/> (DTO) where every member is either a grouping-key
    /// access (<c>g.Key</c> / <c>g.Key.&lt;Sub&gt;</c>) or a supported aggregate over the grouping
    /// (<c>g.Count()</c>/<c>g.LongCount()</c> → <c>$sum:1</c>; <c>g.Sum/Min/Max/Average(x =&gt; x.Field)</c>
    /// over a plain member selector). Returns <see langword="false"/> for any other shape, or when no
    /// accumulator is produced, so the caller falls back.
    /// </summary>
    /// <param name="mongoQ">The query whose <see cref="MongoSelectDefinition"/> is being populated.</param>
    /// <param name="resultSelector">The <c>Select</c> result selector lambda over the grouping.</param>
    internal static bool TryBindGroupProjection(
        MongoQueryExpression mongoQ, LambdaExpression resultSelector)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        // EF-449: a HAVING Where was stashed between the GroupBy and this Select (e.g. GroupBy(key)
        // .Where(o => o.Count() > 4).Select(g => new { g.Key, Count = g.Count() })) — recognized by
        // NativeSlotPopulator's Where carve-out on the assumption it might be feeding a terminal
        // Count/LongCount/Any (NativeGroupByBinder.TryBindGroupTerminalAggregate), the ONLY consumer that
        // clears it. A Select reaching here instead means that assumption was wrong: this predicate has NO
        // native $match-after-$group mechanism on the flattening-$project path this method builds, so
        // silently finalizing Grouping without applying it would silently DROP the HAVING filter and return
        // every group. Decline so the whole query falls back to driver-LINQ, matching this shape's
        // pre-existing (pre-EF-449) behavior.
        if (select.PendingGroupPredicate != null)
            return false;

        if (!TryGetProjectionBindings(resultSelector.Body, out var bindings))
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var groupingParameter = resultSelector.Parameters[0];
        var accumulators = new List<MongoGroupAccumulator>();
        var isComposite = keyParts.Count > 1 || keyParts[0].Name != null;

        // Flatten projection: each result member maps to a top-level output alias read back by the DOM
        // shaper. Key members read from the group _id (scalar → "_id", composite sub → "_id.<Name>");
        // accumulator members read from their own top-level output field. Emitted as a trailing $project
        // after the $group (MongoSelectLowerer) so the shaper never needs a nested-_id read.
        var flatten = new List<MongoProjection>();

        foreach (var (memberName, valueExpr) in bindings)
        {
            if (TryGetKeyMemberPath(valueExpr, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite key cannot flatten to a single field

                flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(keyPath, Unwrap(valueExpr).Type)));
                continue;
            }

            if (!TryBindAccumulator(valueExpr, memberName, groupingParameter, translator, out var acc))
                return false;
            accumulators.Add(acc);
            flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(acc.OutputField, Unwrap(valueExpr).Type)));
        }

        if (accumulators.Count == 0)
            return false; // pure key regroup with no aggregate — unsupported here, falls back

        select.Grouping = new MongoGrouping(keyParts, accumulators);
        foreach (var projection in flatten)
            select.AddProjection(projection);
        return true;
    }

    // Classifies a result-member value as a grouping-key access and, if so, yields the group-output element
    // path it reads from. Returns true for a key access; `path` is null only for the unsupported bare-g.Key
    // over a composite key (whole anonymous key object — cannot flatten to one field). Returns false when the
    // value is not a key access (i.e. it is an accumulator).
    private static bool TryGetKeyMemberPath(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        out string? path)
    {
        path = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        // g.Key — the whole key. Only flattenable when the key is scalar (single unnamed part).
        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            path = isComposite ? null : "_id";
            return true;
        }

        // g.Key.<Sub> — a composite sub-member whose name matches a parsed key part.
        if (member.Expression is MemberExpression { Member.Name: "Key" } inner
            && inner.Expression == groupingParameter)
        {
            foreach (var part in keyParts)
            {
                if (part.Name == member.Member.Name)
                {
                    path = "_id." + part.Name;
                    return true;
                }
            }
        }

        return false;
    }

    // Flatten a NewExpression (anonymous type) or MemberInitExpression (DTO) into (memberName, valueExpr) pairs.
    private static bool TryGetProjectionBindings(
        Expression body, [NotNullWhen(true)] out List<(string MemberName, Expression Value)>? bindings)
    {
        bindings = null;

        switch (body)
        {
            case NewExpression { Members: { } members } newExpr:
                bindings = [];
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                    bindings.Add((members[i].Name, newExpr.Arguments[i]));
                return true;

            case MemberInitExpression memberInit:
                bindings = [];
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false; // list/nested member bindings are not supported
                    bindings.Add((assignment.Member.Name, assignment.Expression));
                }

                // The MemberInit's own NewExpression must be a parameterless ctor (no positional args to bind).
                return memberInit.NewExpression.Arguments.Count == 0;

            default:
                return false;
        }
    }

    // Match g.Count()/g.LongCount() → ("$sum", null); g.Sum/Average/Min/Max(x => x.Field) over a plain member
    // selector → the matching operator + field-ref operand. Any other shape (computed operand, unknown method)
    // returns false. The aggregate's SOURCE (call.Arguments[0]) must be the grouping parameter itself — an
    // aggregate whose source is a DIFFERENT sequence (a correlated cross-collection subquery such as
    // Customers.Where(c => c.CustomerID == g.Key).Count(), a navigation, another collection) is NOT a grouped
    // accumulator and must NOT be bound to a $group accumulator (that would silently drop the real subquery
    // computation and return the group's row count instead). Reject it so the projection falls back to
    // driver-LINQ, preserving the Native == DriverLinq invariant.
    private static bool TryBindAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator)
    {
        accumulator = null;

        // The $group document already carries the grouping key under the reserved "_id" field
        // (MongoPipelineFactory.RenderKeyedGroup). An accumulator whose output field is literally "_id"
        // (e.g. Select(g => new { _id = g.Count() })) would add a SECOND "_id" element to that document → a
        // BsonDocument duplicate-key throw at pipeline build, which is an unhandled crash rather than a clean
        // fallback. Reject it so the shape falls back to driver-LINQ (and throws only under NativeOnly). This
        // is scoped to accumulators: a KEY member projected to an "_id" alias reads the group's own "_id"
        // back and does NOT collide (that path never reaches here — it is handled as a key member).
        if (outputField == GroupIdFieldName)
            return false;

        if (Unwrap(expr) is not MethodCallExpression { Method.IsGenericMethod: true } call
            || call.Arguments.Count == 0
            || !IsGroupingSource(call.Arguments[0], groupingParameter))
            return false;

        var definition = call.Method.GetGenericMethodDefinition();

        // Count / LongCount — g.Count() / g.LongCount() with no selector argument → $sum: 1. EF Core lowers a
        // grouped aggregate to the Queryable form over `g.AsQueryable()` (e.g. Queryable.Count(g.AsQueryable()));
        // a hand-authored Enumerable form is accepted too (used by the unit tests).
        if ((definition == EnumerableMethods.CountWithoutPredicate
             || definition == EnumerableMethods.LongCountWithoutPredicate
             || definition == QueryableMethods.CountWithoutPredicate
             || definition == QueryableMethods.LongCountWithoutPredicate)
            && call.Arguments.Count == 1)
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum", null);
            return true;
        }

        // Sum / Average / Min / Max with a selector — g.Sum(x => x.Field) etc. (Enumerable or Queryable form).
        string? op = null;
        if (EnumerableMethods.IsSumWithSelector(call.Method) || QueryableMethods.IsSumWithSelector(call.Method))
            op = "$sum";
        else if (EnumerableMethods.IsAverageWithSelector(call.Method) || QueryableMethods.IsAverageWithSelector(call.Method))
            op = "$avg";
        else if (EnumerableMethods.IsMinWithSelector(call.Method) || definition == QueryableMethods.MinWithSelector)
            op = "$min";
        else if (EnumerableMethods.IsMaxWithSelector(call.Method) || definition == QueryableMethods.MaxWithSelector)
            op = "$max";

        if (op is null || call.Arguments.Count != 2)
            return false;

        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form).
        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out var operand))
            return false; // computed / non-member selector — fall back

        accumulator = new MongoGroupAccumulator(outputField, op, operand);
        return true;
    }

    // True when `source` (the `this`/source argument of an Enumerable/Queryable aggregate) is the grouping
    // parameter `g`. EF Core lowers a grouped aggregate to the Queryable form over `g.AsQueryable()`
    // (e.g. Queryable.Count(g.AsQueryable())); a hand-authored Enumerable form passes `g` directly. Unwrap
    // Convert/ConvertChecked and a single AsQueryable/AsEnumerable wrapper, then require reference equality
    // with the grouping parameter. Anything else (a subquery, navigation, or a different collection) is not
    // the grouping source.
    private static bool IsGroupingSource(Expression source, ParameterExpression groupingParameter)
    {
        source = Unwrap(source);

        if (source is MethodCallExpression { Method: { IsGenericMethod: true } method } call
            && call.Arguments.Count == 1
            && (method.GetGenericMethodDefinition() == QueryableMethods.AsQueryable
                || method.Name == nameof(Enumerable.AsEnumerable)))
            source = Unwrap(call.Arguments[0]);

        return source == groupingParameter;
    }

    // Strip redundant Convert/ConvertChecked wrappers (a projection member typed `object` boxes its value).
    private static Expression Unwrap(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            ? Unwrap(u.Operand)
            : e;

    /// <summary>
    /// Attempts to bind a scalar aggregate terminal operator (<c>Count</c>/<c>LongCount</c>/<c>Any</c>/
    /// <c>All</c>) applied directly to a BARE <c>GroupBy(key)</c> result — no intervening <c>Select</c> — e.g.
    /// <c>GroupBy(o =&gt; o.CustomerID).Count()</c>, <c>.Any(g =&gt; g.Count() &gt; 1)</c>,
    /// <c>.All(g =&gt; g.Count() &gt; 1)</c> (the "GroupBy_without_aggregate" family, EF-449). Finalizes
    /// <see cref="MongoSelectDefinition.Grouping"/> from <see cref="MongoSelectDefinition.PendingGroupKey"/> and
    /// installs a matching <see cref="MongoSelectDefinition.Cardinality"/> atomically via
    /// <see cref="MongoSelectDefinition.SetGroupedTerminalAggregate"/>. Called from
    /// <see cref="NativeCardinalityBinder.TryBindAggregate"/> BEFORE its general post-terminal guard — bare
    /// <c>GroupBy</c> already sets <see cref="MongoSelectDefinition.IsGroupBy"/> unconditionally, so that guard
    /// would otherwise always decline this shape.
    /// </summary>
    /// <remarks>
    /// Scoped narrowly to what the EF Core spec suite's "without_aggregate" family actually needs: a bare
    /// terminal with NO predicate (a plain "how many/any groups"), or a predicate that is a SINGLE comparison
    /// of one group-level aggregate (<c>g.Count()</c>, or <c>g.Sum/Min/Max/Average(x =&gt; x.Field)</c> — the
    /// same accumulator shapes <see cref="TryBindAccumulator"/> already recognizes for the Select-projection
    /// case) against a constant/parameter. A compound (<c>&amp;&amp;</c>/<c>||</c>) predicate declines — no
    /// reachable test shape needs it, and it keeps the <c>All</c> negation (a single De Morgan step) exact.
    /// </remarks>
    internal static bool TryBindGroupTerminalAggregate(
        MongoQueryExpression mongoQ, MongoAggregateOperator op, LambdaExpression? predicate, Type resultType)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts || select.Grouping != null)
            return false;

        if (op is not (MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All))
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var accumulators = new List<MongoGroupAccumulator>();
        MongoExpression? matchPredicate = null;
        MongoGroupAccumulator? accumulator = null;
        MongoExpression? comparisonNode = null;

        if (predicate != null)
        {
            // All(pred) is never rewritten by EF (Where(pred).All() would change semantics — see below), so
            // its predicate always arrives here directly. Count(pred)/Any(pred), by contrast, are USUALLY
            // rewritten by EF's normalizer to Where(pred).Count()/.Any() — but not unconditionally (measured:
            // both shapes reach this method with a non-null predicate depending on query form), so both must
            // be handled.
            if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                    out accumulator, out comparisonNode))
                return false;
        }
        else if (op is MongoAggregateOperator.All)
        {
            // LINQ's All() has no parameterless overload — unreachable in practice, declined defensively.
            return false;
        }
        else if (select.PendingGroupPredicate is { } pending)
        {
            // EF-449: Count(pred)/Any(pred) normalized to Where(pred).Count()/.Any() — the Where already
            // recognized and stashed the group-level comparison (NativeSlotPopulator's Where carve-out +
            // TryBindGroupWherePredicate below); consume it here instead of re-parsing a (now null) predicate.
            (accumulator, comparisonNode) = pending;
        }

        select.PendingGroupPredicate = null; // one-shot: consumed above, or never set for a bare terminal.

        if (accumulator != null)
        {
            accumulators.Add(accumulator);

            if (op is MongoAggregateOperator.All)
            {
                // All(pred) ≡ no group fails pred. Match the EXACT COMPLEMENT, mirroring
                // NativeCardinalityBinder.TryBindAggregate's row-level All handling: presence of any
                // surviving group (after $group + this $match) means at least one group failed pred.
                // NOT MongoExpressionNegator.TryNegate: its public entry point gates on
                // IsQueryDialectRenderable, which a MongoElementRefExpression comparison (aggregation-
                // expression-only, like MongoOuterFieldExpression/a computed leaf) never satisfies — this
                // node is ALWAYS rendered via $expr, so negate directly with the same $eq/$ne-invert,
                // relational-$not-wrap rule the negator's own aggregation-context arm applies.
                if (!TryNegateGroupComparison(comparisonNode!, out var negated))
                    return false;
                matchPredicate = negated;
            }
            else
            {
                matchPredicate = comparisonNode;
            }
        }

        NativeCardinalityBinder.BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);
        var presenceOnly = op is MongoAggregateOperator.Any or MongoAggregateOperator.All;
        object? presentValue = op switch
        {
            MongoAggregateOperator.Any => true,
            MongoAggregateOperator.All => false,
            _ => null
        };

        var cardinality = MongoCardinality.ForAggregate(
            op, selector: null, emptyBehavior, emptyValue, resultType, presenceOnly, presentValue);
        var grouping = new MongoGrouping(keyParts, accumulators);

        select.SetGroupedTerminalAggregate(grouping, cardinality, matchPredicate);
        return true;
    }

    /// <summary>
    /// Recognizes a <c>Where</c> composed DIRECTLY on a BARE <c>GroupBy(key)</c> result as EF Core's own
    /// normalization of <c>Any(pred)</c>/<c>Count(pred)</c>/<c>LongCount(pred)</c> into
    /// <c>Where(pred).Any()</c>/<c>.Count()</c>/<c>.LongCount()</c> — <paramref name="predicate"/>'s parameter
    /// is typed <c>IGrouping&lt;TKey,TElement&gt;</c>, never the root entity. Stashes the recognized
    /// group-level comparison on <see cref="MongoSelectDefinition.PendingGroupPredicate"/> for the terminal
    /// aggregate to consume (<see cref="TryBindGroupTerminalAggregate"/>). Called from
    /// <see cref="NativeSlotPopulator.PopulateNativeSlots"/>'s dedicated Where carve-out, BEFORE the general
    /// Where arm (which would otherwise resolve this predicate's member access against the wrong — entity —
    /// type). Returns <see langword="false"/> for anything outside <see cref="TryBindGroupPredicateComparison"/>'s
    /// scope, so the caller marks the query non-native.
    /// </summary>
    internal static bool TryBindGroupWherePredicate(MongoQueryExpression mongoQ, LambdaExpression predicate)
    {
        var select = mongoQ.Select;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                out var accumulator, out var comparisonNode))
            return false;

        select.PendingGroupPredicate = (accumulator, comparisonNode);
        return true;
    }

    // Recognizes `body` as a single comparison of one group-level aggregate (bound via the SAME
    // TryBindAccumulator shapes the Select-projection path uses) against a constant/parameter, in EITHER
    // operand order. Returns the bound accumulator (output field "__agg0") and the translated comparison node
    // (accumulator field-ref on the left, in normalized — not necessarily source — operator direction).
    private static bool TryBindGroupPredicateComparison(
        Expression body,
        ParameterExpression groupingParameter,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? comparisonNode)
    {
        accumulator = null;
        comparisonNode = null;

        const string outputField = "__agg0";

        if (Unwrap(body) is not BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            return false;
        }

        if (TryBindAccumulator(bin.Left, outputField, groupingParameter, translator, out var acc)
            && TryTranslateComparisonConstant(bin.Right, out var rightNode))
        {
            accumulator = acc;
            comparisonNode = new MongoBinaryExpression(
                MapComparisonOperator(bin.NodeType),
                new MongoElementRefExpression(outputField, Unwrap(bin.Left).Type),
                rightNode);
            return true;
        }

        if (TryBindAccumulator(bin.Right, outputField, groupingParameter, translator, out acc)
            && TryTranslateComparisonConstant(bin.Left, out var leftNode))
        {
            accumulator = acc;
            comparisonNode = new MongoBinaryExpression(
                MapComparisonOperator(FlipComparison(bin.NodeType)),
                new MongoElementRefExpression(outputField, Unwrap(bin.Right).Type),
                leftNode);
            return true;
        }

        return false;
    }

    // The non-accumulator side of a group predicate comparison: a captured literal or a query parameter.
    // Mirrors MongoExpressionTranslator's own private TranslateValue (not accessible from here) — this operand
    // is never a member access, so the full translator is unnecessary.
    private static bool TryTranslateComparisonConstant(
        Expression expr, [NotNullWhen(true)] out MongoExpression? result)
    {
        expr = Unwrap(expr);
        switch (expr)
        {
            case ConstantExpression constant:
                result = new MongoConstantExpression(constant.Value, forSerialization: null);
                return true;
            default:
                if (NativeQueryParameter.TryGetQueryParameterName(expr, out var name))
                {
                    result = new MongoParameterExpression(name, forSerialization: null);
                    return true;
                }

                result = null;
                return false;
        }
    }

    private static MongoBinaryOperator MapComparisonOperator(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.Equal => MongoBinaryOperator.Equal,
        ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
        ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => MongoBinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(nodeType))
    };

    // The comparison `constant OP accumulator` is equivalent to `accumulator OP' constant` for the flipped
    // relational operator OP' (equality/inequality are already symmetric).
    private static ExpressionType FlipComparison(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => nodeType
    };

    // Negates a group-predicate comparison built by TryBindGroupPredicateComparison. Always aggregation-
    // expression-only (the left operand is a MongoElementRefExpression, never query-dialect renderable), so
    // this cannot reuse MongoExpressionNegator's public TryNegate (which gates on IsQueryDialectRenderable) —
    // it applies the SAME rule as that negator's own private aggregation-context arm: $eq/$ne are inverted
    // (they partition every value); the four relational operators are $not-wrapped, never inverted (the
    // general reason — they don't partition a missing/null value — doesn't strictly apply to an accumulator
    // output field, which is never missing/null, but wrapping is exact regardless and keeps one rule instead
    // of a second, narrower one to maintain).
    private static bool TryNegateGroupComparison(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = node is MongoBinaryExpression comparison
            ? comparison.Operator switch
            {
                MongoBinaryOperator.Equal =>
                    new MongoBinaryExpression(MongoBinaryOperator.NotEqual, comparison.Left, comparison.Right),
                MongoBinaryOperator.NotEqual =>
                    new MongoBinaryExpression(MongoBinaryOperator.Equal, comparison.Left, comparison.Right),
                MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual
                    or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual =>
                    new MongoUnaryExpression(MongoUnaryOperator.Not, comparison),
                _ => null
            }
            : null;

        return negated != null;
    }

    /// <summary>
    /// <c>Distinct(projection)</c>: the terminal <c>Select</c> already populated <see cref="MongoSelectDefinition.Projection"/>
    /// with (alias -&gt; field-ref) pairs. Convert them into a key-only grouping (group by the projected
    /// value, zero accumulators) and replace the projections with a flatten that reads the value
    /// back out of <c>_id</c>. Returns <see langword="false"/> (→ fall back) if there is no native projection, or any key
    /// is not a default-serialized field ref (generic <c>_id</c> readback would diverge from DriverLinq).
    /// </summary>
    internal static bool TryBindDistinctFromProjection(MongoQueryExpression mongoQ)
    {
        var select = mongoQ.Select;
        // A projected SelectMany is itself a terminal (its UnwindSource is set): converting its Projection
        // into a degenerate $group here would leave UnwindSource set alongside the new Grouping, and the
        // lowerer's UnwindSource branch runs before its Grouping branch and returns early — silently dropping
        // the $group and emitting a flatten $project that reads "_id.<alias>" fields that were never grouped
        // into existence. Decline so this falls back to driver-LINQ (or hard-fails, for the reference form,
        // which has no driver-LINQ baseline) instead of building a pipeline that silently returns nulls.
        //
        // For a projected-operand set op (SetOperation.OperandsProjected == true), select.Projection is
        // operand-1's own projection — emitted before the set-op stage by the lowerer, not a trailing
        // post-set-op projection — so converting it into a degenerate $group here would corrupt operand-1's
        // pipeline. Declining makes TranslateDistinct fall back gracefully instead. This must stay narrowed
        // to OperandsProjected: true, not a blanket SetOperation != null: a whole-entity set op with a
        // trailing projection (OperandsProjected == false — e.g. Union(A,B).Select(p).Distinct()) has its
        // Projection applied after the set-op stage as a genuine trailing projection, which this method
        // converts to a $group safely and correctly — a documented native capability that must be preserved.
        //
        // EF-395: a bare projection (Select(o => o.Country).Distinct()) is now ADMITTED — this no longer
        // declines on select.IsBareProjection. Binding it clears Projection, installs a Grouping and flips
        // Route to NativeRoute.GroupBy; the mechanical hazard that used to make this unsafe was
        // MongoQueryExpression.ApplyProjection's alias-override lookup being gated on `Route ==
        // NativeRoute.Projection`, which reverted the bare body's alias to null once Route flipped, crashing
        // the shaper. That lookup now ALSO fires when Select.IsDistinct is set (see ApplyProjection) —
        // IsDistinct is set nowhere but here, immediately below, alongside the flatten that re-adds the exact
        // same alias(es) the override describes, so the override is provably still valid whenever IsDistinct
        // is true. Pinned by NativeBareProjectionTests.
        if (select.Projection.Count == 0 || select.Grouping != null || select.Cardinality != null || select.HasPaging
            || select.UnwindSource != null || select.SetOperation is { OperandsProjected: true })
            return false;

        var keyParts = new List<MongoGroupingKeyPart>();
        var flatten = new List<MongoProjection>();
        foreach (var projection in select.Projection)
        {
            if (projection.Expression is not MongoFieldExpression field || !HasDefaultKeySerialization(field.Property))
                return false;
            keyParts.Add(new MongoGroupingKeyPart(projection.Alias, field));
            flatten.Add(new MongoProjection(projection.Alias,
                new MongoElementRefExpression("_id." + projection.Alias, field.Type)));
        }

        select.ClearProjections();
        select.Grouping = new MongoGrouping(keyParts, []);
        // Record Distinct provenance (not IsGroupBy) so the post-group operator guards in NativeSlotPopulator
        // and NativeCardinalityBinder — both keyed on IsGroupBy || IsDistinct — also cover an operator applied
        // after this Distinct. A separate flag is deliberate: the QMTEV's join-decline path treats Distinct+Join
        // as a graceful fallback (driver-LINQ joins a flat row set correctly), whereas a genuine GroupBy+Join
        // is a hard decline (driver-LINQ returns silently-empty joins). See IsDistinct's doc on
        // MongoSelectDefinition and TranslateJoinCore.
        select.IsDistinct = true;
        foreach (var f in flatten)
            select.AddProjection(f);
        return true;
    }
}
