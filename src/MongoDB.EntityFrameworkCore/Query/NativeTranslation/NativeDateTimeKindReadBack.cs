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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Honours a property's configured <c>DateTimeKind</c> (<c>HasDateTimeKind</c>) when the native pipeline reads a
/// <c>DateTime</c> back through a generic CLR-type serializer rather than the property's own serializer: a
/// <c>$group</c> key, a <c>$min</c>/<c>$max</c>/<c>$first</c>/<c>$last</c> accumulator, a terminal
/// <c>Min</c>/<c>Max</c>, a nullable cast, a join-scope or dotted owned-reference leaf.
/// </summary>
/// <remarks>
/// <para>
/// The generic <c>DateTime</c> serializer returns <c>Kind=Utc</c>; a <c>Local</c>-kind property's serializer
/// converts to local time. <c>Utc</c> and the default (<c>Unspecified</c>, which reads back as <c>Utc</c>) agree with
/// the generic read, so only <c>Local</c> is kind-sensitive.
/// </para>
/// <para>
/// Two halves that must agree: <see cref="FindForProjectionAlias"/>/<see cref="FindForProjectionValue"/> name the
/// property whose stored value a read-back surfaces unchanged (the shaper then reads through that property's
/// kind); <see cref="HasUnreproducibleReadBack"/> finds a <c>DateTime</c> read-back that is computed from a
/// kind-sensitive property (<c>AddDays</c>, <c>.Date</c>, a ternary mixing kinds). There is no property to read
/// such a value back through, and the server computes it in UTC, so the query declines.
/// </para>
/// <para>
/// Element references are resolved through the stage that produces them: a <c>$group</c> output field
/// (<c>_id</c>, <c>_id.&lt;Name&gt;</c>, an accumulator) to its key part or accumulator operand, and a nested
/// GroupBy's input to the prior stage's flattened <c>$project</c> (<see cref="MongoSelectDefinition.PriorGroupingProjection"/>).
/// Filters are unaffected: they compare server-side, where the stored value is the same instant whatever the kind.
/// </para>
/// </remarks>
internal static class NativeDateTimeKindReadBack
{
    // Where an element reference is evaluated: which document's field names it names.
    private enum Level
    {
        // A root (or $lookup-joined) document: refs are whole-document/sentinel paths, never a property value.
        Document,

        // A top-level output alias of the select's own $project (a terminal aggregate's operand).
        ProjectionOutput,

        // The select's $group output: "_id", "_id.<Name>" or an accumulator field.
        GroupOutput,

        // The select's $group input when it nests on a prior grouping: the prior stage's flattened aliases.
        GroupInput,

        // The prior grouping's own $group output.
        PriorGroupOutput
    }

    /// <summary>
    /// Whether <paramref name="property"/>'s configured kind makes its values read back differently from the generic
    /// <c>DateTime</c> serializer.
    /// </summary>
    internal static bool IsKindSensitive(IReadOnlyProperty property)
        => property.ClrType.UnwrapNullableType() == typeof(DateTime)
           && property.GetDateTimeKind() is not (DateTimeKind.Unspecified or DateTimeKind.Utc);

    /// <summary>
    /// Whether <paramref name="value"/> is (or is computed from) a kind-sensitive <c>DateTime</c> property, so a server-side
    /// date part or calendar-unit add over it would be evaluated in UTC while the C# value is in local time.
    /// </summary>
    /// <remarks>
    /// Context-free: it walks only the nodes that carry a <c>DateTime</c> through (fields, date arithmetic, truncation,
    /// branches, casts). Every property test goes through <see cref="IsKindSensitive"/>, the one predicate shared with
    /// the read-back checks above and with the driver-LINQ bridge. The pass-through node set (casts, <c>.Date</c>,
    /// <c>AddXxx</c>, <c>??</c>, <c>?:</c>) mirrors the bridge's LINQ-tree walker
    /// <c>MongoEFToLinqTranslatingExpressionVisitor.FindLocalKindProperty</c>; change the two together.
    /// </remarks>
    internal static bool ReferencesKindSensitiveProperty(MongoExpression value)
        => value switch
        {
            MongoFieldExpression field => IsKindSensitive(field.Property),
            MongoOuterFieldExpression outerField => IsKindSensitive(outerField.Property),
            MongoDateAddExpression dateAdd => ReferencesKindSensitiveProperty(dateAdd.StartDate),
            MongoDatePartExpression { Part: MongoDatePart.Date } datePart => ReferencesKindSensitiveProperty(datePart.Operand),
            MongoConvertExpression convert => ReferencesKindSensitiveProperty(convert.Operand),
            MongoConditionalExpression conditional
                => ReferencesKindSensitiveProperty(conditional.IfTrue) || ReferencesKindSensitiveProperty(conditional.IfFalse),
            MongoCoalesceExpression coalesce
                => ReferencesKindSensitiveProperty(coalesce.Left) || ReferencesKindSensitiveProperty(coalesce.Right),
            MongoElementRefExpression { ValueProperty: { } valueProperty } => IsKindSensitive(valueProperty),
            _ => false
        };

    /// <summary>
    /// Whether adding <paramref name="unit"/>s is calendar arithmetic whose result depends on the time zone (a day is
    /// 23 or 25 hours across a DST change, a month ends at a local midnight). Hour and smaller units are exact elapsed
    /// time and agree between UTC and local.
    /// </summary>
    internal static bool IsCalendarUnit(MongoDateAddUnit unit)
        => unit is MongoDateAddUnit.Year or MongoDateAddUnit.Month or MongoDateAddUnit.Day;

    /// <summary>
    /// The kind-sensitive property whose stored value the <paramref name="select"/>'s projection output
    /// <paramref name="alias"/> holds unchanged, or <see langword="null"/> when there is none (the generic read is then
    /// already correct, or the query was declined by <see cref="HasUnreproducibleReadBack"/>).
    /// </summary>
    internal static IReadOnlyProperty? FindForProjectionAlias(MongoSelectDefinition select, string alias)
        => TryFindProjection(select, alias, out var projection)
            ? FindForProjectionValue(select, projection.Expression)
            : null;

    /// <summary>
    /// As <see cref="FindForProjectionAlias"/>, for a value that is (or is a member of) one of
    /// <paramref name="select"/>'s projection leaves.
    /// </summary>
    internal static IReadOnlyProperty? FindForProjectionValue(MongoSelectDefinition select, MongoExpression value)
        => Find(value, select, ProjectionLevel(select));

    /// <summary>
    /// As <see cref="FindForProjectionAlias"/>, for a terminal scalar aggregate's operand
    /// (<see cref="MongoCardinality.Selector"/>): a field of the source document or an output alias of the select's own
    /// <c>$project</c>.
    /// </summary>
    internal static IReadOnlyProperty? FindForAggregateOperand(MongoSelectDefinition select, MongoExpression operand)
        => Find(operand, select, Level.ProjectionOutput);

    /// <summary>
    /// Whether any <c>DateTime</c> value <paramref name="select"/> reads back (a projection leaf, a terminal
    /// <c>Min</c>/<c>Max</c>, a set-op operand's leaf) is computed from a kind-sensitive property, so no property's
    /// serializer reproduces it.
    /// </summary>
    internal static bool HasUnreproducibleReadBack(MongoSelectDefinition select)
    {
        foreach (var projection in select.Projection)
        {
            if (IsUnreproducible(projection.Expression, select, ProjectionLevel(select)))
                return true;
        }

        if (select.Cardinality is { Aggregate: MongoAggregateOperator.Min or MongoAggregateOperator.Max, Selector: { } operand }
            && IsUnreproducible(operand, select, Level.ProjectionOutput))
            return true;

        foreach (var setOperation in select.SetOperations)
        {
            if (HasUnreproducibleReadBack(setOperation.OperandSelect)
                || HasKindMismatch(select, setOperation.OperandSelect))
                return true;
        }

        return false;
    }

    // The combined rows are all read back through the outer select's leaf, so an operand whose same-alias DateTime
    // leaf (or same-named member of a nested construction leaf) reads back with a different kind (a Local-kind
    // property on one side only) can't be reproduced.
    private static bool HasKindMismatch(MongoSelectDefinition outer, MongoSelectDefinition operand)
    {
        foreach (var operandProjection in operand.Projection)
        {
            if (TryFindProjection(outer, operandProjection.Alias, out var outerProjection)
                && HasKindMismatch(outer, outerProjection.Expression, operand, operandProjection.Expression))
                return true;
        }

        return false;
    }

    // Compared by the leaves' kind shapes (KindShape), not node by node, so a pairing of different node kinds can't slip
    // through: a composite key rebuilt as a construction (a kind-sensitive side) against the same key type read whole
    // by class map (a default-kind side) differs in its DateTime member's kind and declines. Only a difference that
    // involves a kind-sensitive position counts, so set ops with no Local-kind source are unaffected.
    private static bool HasKindMismatch(
        MongoSelectDefinition outer, MongoExpression outerValue, MongoSelectDefinition operand, MongoExpression operandValue)
    {
        var outerShape = KindShape(outerValue, outer, ProjectionLevel(outer));
        var operandShape = KindShape(operandValue, operand, ProjectionLevel(operand));
        return outerShape != operandShape && (outerShape.Contains(SensitiveShape) || operandShape.Contains(SensitiveShape));
    }

    private const string SensitiveShape = "#L";

    // How a leaf's DateTime positions read back: "#L" for a kind-sensitive source, "#U" for any other DateTime, "#-" for
    // any other leaf (a non-DateTime scalar, or a whole value read by class map, e.g. a composite "_id"), and
    // "{Name:shape,...}" (members in name order) for a document construction. A composite key with a kind-sensitive part
    // is always a construction (NativeGroupByBinder rebuilds it, or HasUnreproducibleReadBack declines it), so pairing
    // it with the same key read whole by class map differs structurally and, containing "#L", declines.
    private static string KindShape(MongoExpression value, MongoSelectDefinition select, Level level)
    {
        switch (value)
        {
            case MongoDocumentConstructionExpression construction:
                return "{" + string.Join(",", construction.Members
                    .OrderBy(m => m.MemberName, StringComparer.Ordinal)
                    .Select(m => m.MemberName + ":" + KindShape(m.Value, select, level))) + "}";

            case var _ when IsDateTimeTyped(value):
                return Find(value, select, level) != null || ReferencesKindSensitiveDate(value, select, level) ? SensitiveShape : "#U";

            default:
                return "#-";
        }
    }

    /// <summary>
    /// Whether a <c>$group</c> key part of <paramref name="select"/> (its <see cref="MongoGroupingKeyPart.FieldRef"/>,
    /// evaluated over the group's input) carries or derives from a kind-sensitive DateTime. Used at bind time to read a
    /// whole composite key member by member rather than through its type's class map.
    /// </summary>
    internal static bool IsKindSensitiveKeyPart(MongoSelectDefinition select, MongoExpression fieldRef)
        => CarriesKindSensitiveDate(fieldRef, select, GroupInputLevel(select));

    // Whether `value` passes a kind-sensitive property's value through unchanged (Find), or is a DateTime derived from one.
    private static bool CarriesKindSensitiveDate(MongoExpression value, MongoSelectDefinition select, Level level)
        => Find(value, select, level) != null
           || (IsDateTimeTyped(value) && ReferencesKindSensitiveDate(value, select, level));

    // The level a $group's input is evaluated at: the prior grouping's output when one exists, else the document.
    private static Level GroupInputLevel(MongoSelectDefinition select)
        => select.PriorGrouping != null ? Level.GroupInput : Level.Document;

    private static Level ProjectionLevel(MongoSelectDefinition select)
        => select.Grouping != null ? Level.GroupOutput : Level.Document;

    private static bool IsUnreproducible(MongoExpression value, MongoSelectDefinition select, Level level)
    {
        if (value is MongoDocumentConstructionExpression construction)
        {
            foreach (var (_, member) in construction.Members)
            {
                if (IsUnreproducible(member, select, level))
                    return true;
            }

            return false;
        }

        if (IsDateTimeTyped(value))
            return Find(value, select, level) == null && ReferencesKindSensitiveDate(value, select, level);

        // A non-DateTime leaf read back whole through a class map (a composite g.Key's "_id") reads its DateTime
        // members with the default serializer, so it must not carry a kind-sensitive date. NativeGroupByBinder rebuilds
        // such a key as a document construction; anything it couldn't rebuild declines here.
        return HoldsKindSensitiveDateInClassMapRead(value, select, level);
    }

    // Whether `value`, read back whole by class map, contains a DateTime from a kind-sensitive source: an element ref
    // to a composite $group "_id" with such a part, possibly through branches or an upstream alias.
    private static bool HoldsKindSensitiveDateInClassMapRead(MongoExpression value, MongoSelectDefinition select, Level level)
    {
        switch (value)
        {
            case MongoElementRefExpression { Path: GroupIdPath }
                when GroupingAt(select, level) is ({ IsCompositeKey: true } grouping, var inputLevel):
                foreach (var part in grouping.Key)
                {
                    if (CarriesKindSensitiveDate(part.FieldRef, select, inputLevel)
                        || HoldsKindSensitiveDateInClassMapRead(part.FieldRef, select, inputLevel))
                        return true;
                }

                return false;

            case MongoElementRefExpression elementRef:
                return TryResolve(elementRef, select, level, valuePreservingOnly: false, out var target, out var targetLevel)
                       && HoldsKindSensitiveDateInClassMapRead(target, select, targetLevel);

            case MongoConditionalExpression conditional:
                return HoldsKindSensitiveDateInClassMapRead(conditional.IfTrue, select, level)
                       || HoldsKindSensitiveDateInClassMapRead(conditional.IfFalse, select, level);

            case MongoCoalesceExpression coalesce:
                return HoldsKindSensitiveDateInClassMapRead(coalesce.Left, select, level)
                       || HoldsKindSensitiveDateInClassMapRead(coalesce.Right, select, level);

            default:
                return false;
        }
    }

    private const string GroupIdPath = "_id";

    // The grouping whose output `level` is, and the level that grouping's input (key parts, accumulator operands) is
    // evaluated at; no grouping for a non-group-output level.
    private static (MongoGrouping? Grouping, Level InputLevel) GroupingAt(MongoSelectDefinition select, Level level)
        => level switch
        {
            Level.GroupOutput => (select.Grouping, GroupInputLevel(select)),
            Level.PriorGroupOutput => (select.PriorGrouping, Level.Document),
            _ => (null, Level.Document)
        };

    // The kind-sensitive property whose stored value `value` passes through unchanged. A ternary or coalesce is admitted
    // when every branch is such a value (any Local-kind properties read back alike) or a null constant.
    private static IReadOnlyProperty? Find(MongoExpression value, MongoSelectDefinition select, Level level)
    {
        switch (value)
        {
            case MongoFieldExpression field:
                return IsKindSensitive(field.Property) ? field.Property : null;

            // A root-side leaf of a manual Join (`(ow, o) => new { ow.LocalDate, ... }`).
            case MongoOuterFieldExpression outerField:
                return IsKindSensitive(outerField.Property) ? outerField.Property : null;

            case MongoConditionalExpression conditional:
                return FindCommon(conditional.IfTrue, conditional.IfFalse, select, level);

            case MongoCoalesceExpression coalesce:
                return FindCommon(coalesce.Left, coalesce.Right, select, level);

            case MongoElementRefExpression { ValueProperty: { } valueProperty }:
                return IsKindSensitive(valueProperty) ? valueProperty : null;

            case MongoElementRefExpression elementRef:
                return TryResolve(elementRef, select, level, valuePreservingOnly: true, out var target, out var targetLevel)
                    ? Find(target, select, targetLevel)
                    : null;

            // g.Select(x => x.D).Distinct().Max(): $max/$min over an $addToSet array returns one of the set's elements.
            case MongoArrayReduceExpression { OperatorName: "$min" or "$max" } reduce:
                return TryResolveArrayReduce(reduce, select, level, out var setOperand, out var setLevel)
                    ? Find(setOperand, select, setLevel)
                    : null;

            default:
                return null;
        }
    }

    private static IReadOnlyProperty? FindCommon(MongoExpression left, MongoExpression right, MongoSelectDefinition select, Level level)
    {
        var leftProperty = IsNeutral(left) ? null : Find(left, select, level);
        var rightProperty = IsNeutral(right) ? null : Find(right, select, level);

        if (leftProperty == null && !IsNeutral(left))
            return null;
        if (rightProperty == null && !IsNeutral(right))
            return null;

        return leftProperty ?? rightProperty;
    }

    // Whether a DateTime-valued position of `value` derives from a kind-sensitive property. Walks only the nodes that
    // carry a DateTime through (date arithmetic, date truncation, branches, casts, element references, array reduces);
    // an operand of another type (a date part compared in a ternary's test) doesn't make the result's kind depend on it.
    //
    // Fails closed: a DateTime-typed node this walk doesn't know (a new node kind, or an element reference it can't
    // trace) counts as kind-sensitive, so an unrecognized read-back declines rather than silently reading Kind=Utc.
    private static bool ReferencesKindSensitiveDate(MongoExpression value, MongoSelectDefinition select, Level level)
        => value switch
        {
            MongoFieldExpression field => IsKindSensitive(field.Property),
            // Only reached for an outer field inside a computed date (Find resolves a bare one).
            MongoOuterFieldExpression outerField => IsKindSensitive(outerField.Property),
            MongoDateAddExpression dateAdd => ReferencesKindSensitiveDate(dateAdd.StartDate, select, level),
            MongoDatePartExpression { Part: MongoDatePart.Date } datePart
                => ReferencesKindSensitiveDate(datePart.Operand, select, level),
            MongoConvertExpression convert => ReferencesKindSensitiveDate(convert.Operand, select, level),
            MongoConditionalExpression conditional
                => ReferencesKindSensitiveDate(conditional.IfTrue, select, level)
                   || ReferencesKindSensitiveDate(conditional.IfFalse, select, level),
            MongoCoalesceExpression coalesce
                => ReferencesKindSensitiveDate(coalesce.Left, select, level)
                   || ReferencesKindSensitiveDate(coalesce.Right, select, level),
            MongoElementRefExpression { Path: MongoElementRefExpression.RemoveSentinelPath } => false,
            MongoElementRefExpression { ValueProperty: { } valueProperty } => IsKindSensitive(valueProperty),
            MongoElementRefExpression elementRef
                => TryResolve(elementRef, select, level, valuePreservingOnly: false, out var target, out var targetLevel)
                    ? ReferencesKindSensitiveDate(target, select, targetLevel)
                    : IsDateTimeTyped(elementRef),
            MongoArrayReduceExpression reduce
                => TryResolveArrayReduce(reduce, select, level, out var setOperand, out var setLevel)
                    ? ReferencesKindSensitiveDate(setOperand, select, setLevel)
                    : IsDateTimeTyped(reduce),
            MongoConstantExpression or MongoParameterExpression => false,
            // DateTimeOffset.DateTime/LocalDateTime over a DateTimeOffset field: HasDateTimeKind applies only to DateTime
            // properties, so no configured kind is involved.
            MongoDateTimeOffsetLocalExpression => false,
            _ => IsDateTimeTyped(value)
        };

    private static bool IsDateTimeTyped(MongoExpression value)
        => value.Type.UnwrapNullableType() == typeof(DateTime);

    // Resolves a flattening $min/$max/$size/... over an $addToSet accumulator array to that accumulator's element operand.
    private static bool TryResolveArrayReduce(
        MongoArrayReduceExpression reduce, MongoSelectDefinition select, Level level,
        out MongoExpression operand, out Level operandLevel)
    {
        operand = null!;
        operandLevel = default;

        var (grouping, inputLevel) = GroupingAt(select, level);

        if (grouping is null)
            return false;

        foreach (var accumulator in grouping.Accumulators)
        {
            if (accumulator.OutputField == reduce.FieldName && accumulator.Operator == "$addToSet"
                && accumulator.Operand is { } setOperand)
            {
                operand = setOperand;
                operandLevel = inputLevel;
                return true;
            }
        }

        return false;
    }

    // Resolves an element reference to the expression that produced it and the level that expression is evaluated at.
    // With valuePreservingOnly, only accumulators that return one of their input values ($min/$max/$first/$last)
    // resolve; a computing accumulator ($sum/$avg) has no single source value.
    private static bool TryResolve(
        MongoElementRefExpression elementRef,
        MongoSelectDefinition select,
        Level level,
        bool valuePreservingOnly,
        out MongoExpression target,
        out Level targetLevel)
    {
        target = null!;
        targetLevel = default;

        switch (level)
        {
            case Level.ProjectionOutput:
                if (!TryFindProjection(select, elementRef.Path, out var projection))
                    return false;
                target = projection.Expression;
                targetLevel = ProjectionLevel(select);
                return true;

            case Level.GroupOutput when select.Grouping is { } grouping:
                if (!TryResolveGroupOutput(grouping, elementRef.Path, valuePreservingOnly, out target))
                    return false;
                targetLevel = GroupInputLevel(select);
                return true;

            case Level.GroupInput:
                foreach (var priorProjection in select.PriorGroupingProjection)
                {
                    if (priorProjection.Alias == elementRef.Path)
                    {
                        target = priorProjection.Expression;
                        targetLevel = Level.PriorGroupOutput;
                        return true;
                    }
                }

                return false;

            case Level.PriorGroupOutput when select.PriorGrouping is { } priorGrouping:
                if (!TryResolveGroupOutput(priorGrouping, elementRef.Path, valuePreservingOnly, out target))
                    return false;
                targetLevel = Level.Document;
                return true;

            default:
                return false;
        }
    }

    private static bool TryResolveGroupOutput(
        MongoGrouping grouping, string path, bool valuePreservingOnly, out MongoExpression target)
    {
        target = null!;

        if (path == "_id")
        {
            if (grouping.IsCompositeKey)
                return false;
            target = grouping.Key[0].FieldRef;
            return true;
        }

        if (path.StartsWith("_id.", StringComparison.Ordinal))
        {
            var name = path["_id.".Length..];
            foreach (var part in grouping.Key)
            {
                if (part.Name == name)
                {
                    target = part.FieldRef;
                    return true;
                }
            }

            return false;
        }

        foreach (var accumulator in grouping.Accumulators)
        {
            if (accumulator.OutputField == path && accumulator.Operand is { } operand
                && (!valuePreservingOnly || accumulator.Operator is "$min" or "$max" or "$first" or "$last"))
            {
                target = operand;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindProjection(MongoSelectDefinition select, string alias, out MongoProjection projection)
    {
        foreach (var candidate in select.Projection)
        {
            if (candidate.Alias == alias)
            {
                projection = candidate;
                return true;
            }
        }

        projection = default;
        return false;
    }

    // A branch that contributes no value of its own: a null constant, or the $$REMOVE sentinel a filtered accumulator
    // uses to skip an element.
    private static bool IsNeutral(MongoExpression value)
        => value is MongoConstantExpression { Value: null }
            or MongoElementRefExpression { Path: MongoElementRefExpression.RemoveSentinelPath };
}
