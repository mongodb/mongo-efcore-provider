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
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;           // QueryableMethods
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.EntityFrameworkCore.Extensions;        // GetDocumentPath(), IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates an EF Core predicate, key-selector, or value lambda body into a dialect-agnostic
/// <see cref="MongoExpression"/> tree. Query parameters become <see cref="MongoParameterExpression"/> placeholders
/// resolved per execution. Returns <see langword="false"/> (never throws) for any unsupported shape so callers can fall
/// back to driver-LINQ.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    // selfParam is used only to detect a single-level correlated owned-collection element predicate whose free
    // parameter is this one, so the call site can build a two-scope child translator instead of declining.
    public MongoExpressionTranslator(IEntityType entityType, ParameterExpression? selfParam = null)
    {
        _entityType = entityType;
        SelfParam = selfParam;
    }

    // Two-scope translator (correlated SelectMany inner filter, or owned-collection element predicate via $filter).
    // Members rooted on outerParam (by reference, never name) resolve against outerEntityType at document root; the rest
    // against innerEntityType under innerPrefix. Pass null, not "", for a $filter element scope: "" renders a
    // leading-dot path ("$$e..x") that MongoDB rejects.
    public MongoExpressionTranslator(
        IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string? innerPrefix)
    {
        _entityType = innerEntityType;
        _outerParam = outerParam;
        _outerEntityType = outerEntityType;
        _innerPrefix = innerPrefix;
    }

    // Settable because NativeSlotPopulator.PopulateNativeSlots builds the translator before the lambda is known.
    // Always null on a two-scope translator, so correlation two or more scopes deep keeps declining.
    internal ParameterExpression? SelfParam { get; set; }

    // Path of a staged (not unwound) $lookup output array, and its element entity type.
    internal readonly record struct ReferencedCollectionSource(string ArrayPath, IEntityType ElementType);

    // Resolves a correlated subquery over another collection (Queryable.Where(DbSet<Order>(), corr)) to the array a staged
    // $lookup produces, or null unless it is exactly a collection navigation's FK correlation. Set only by the projection
    // binder on its root translator. The quantifier/Count/filtered-count arms consume the result through
    // TryResolveOwnedCollectionPath. Staging goes to the binder's pending lists (committed only if the whole projection is
    // accepted, deduplicated by alias), so resolving the same source twice is harmless.
    internal Func<Expression, ReferencedCollectionSource?>? ReferencedCollectionResolver { get; set; }

    // When set, a single-hop member on SelfParam resolves against these grouping key parts (a projected Distinct()'s
    // output schema) instead of the entity. A Distinct member name can coincide with an unrelated entity property
    // (Select(o => new { Country = o.City })), so resolving by entity-property name would filter the wrong field. A name
    // matching no key part declines.
    internal MongoGrouping? DistinctAliasScope { get; set; }

    // When set, the lambda runs over a keyed GroupBy(key).Select(...)'s flattened output: a single-hop member resolves to
    // the Select's output alias of that name, the bare parameter to the sole SyntheticBareProjectionAlias of a bare Select
    // (see TryResolveFlattenedAlias). Unlike DistinctAliasScope it never consults key-part names: output aliases can
    // shadow a key part with a different value (Select(g => new { Country = g.Count(), C = g.Key.Country })). Nothing
    // resolves against the entity either, so an unmatched name declines. Never set together with DistinctAliasScope.
    internal MongoProjectedAliasScope? ProjectedAliasScope { get; set; }

    // Whether node is SelfParam standing for the root entity itself; gates every arm treating it as the entity ($$ROOT
    // checks, GetType()/is folds, key equality, entity-list Contains). After a projected Distinct or keyed GroupBy.Select
    // the parameter is a projected row, and folding x.GetType() == typeof(Order) over it would answer true for every
    // grouped row. A subtype isn't TPH-safe on its own: each site declines any entity with a base or derived type.
    private bool IsSelfParamTheEntity(Expression node)
        => SelfParam is not null
           && ReferenceEquals(node, SelfParam)
           && ProjectedAliasScope is null
           && _entityType.ClrType.IsAssignableFrom(SelfParam.Type);

    public bool TryTranslate(Expression efBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = TranslateNode(Unwrap(efBody));
        return result is not null;
    }

    // OrderBy/ThenBy key: unlike TryTranslate, accepts any mapped scalar property, not just booleans.
    public bool TryTranslateField(Expression keySelectorBody, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        // Inner-only: sort keys are never inside a $filter/$map scope; guards against a future two-scope caller.
        return TryResolveInnerField(UnwrapOrderPreserving(keySelectorBody), out result);
    }

    // Numeric value expression (projection/computed leaf). Declines operands that aren't default-serialized (a converted
    // stored form would diverge from CLR arithmetic). Integer division truncates like C# (MongoBinaryOperator.IntegerDivide).
    public bool TryTranslateValue(Expression valueBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // Don't Unwrap the top-level node: Unwrap strips any Convert, silently dropping a narrowing cast such as
        // (int)o.Weight. TranslateOperand's narrowing-aware Convert handling must see it and reject it.
        var translated = TranslateOperand(valueBody, allowNumericWidening: true);
        if (translated is null)
            return false;

        if (!AllFieldsDefaultSerialized(translated))
            return false;

        result = translated;
        return true;
    }

    // A bare stored field Sum/Min/Max/Average can reduce server-side even if value-converted or non-default-represented
    // (where TryTranslateValue declines). The reduction reads the STORED value, so the property must preserve the CLR
    // answer (StoredOrdering); Min/Max results are read back through the property's serializer (NativeAggregateReadBack).
    public bool TryTranslateStoredAggregateField(
        Expression body, MongoAggregateOperator op, [NotNullWhen(true)] out MongoFieldExpression? field)
    {
        field = null;
        if (!TryResolveInnerField(UnwrapOrderPreserving(body), out var candidate))
            return false;

        var property = candidate.Property;
        var admitted = op is MongoAggregateOperator.Sum or MongoAggregateOperator.Average
            ? IsNumericType(property.ClrType) && StoredOrdering.PreservesClrValueForNumericReduction(property)
            : StoredOrdering.PreservesClrOrderingForAggregateAndSort(property);
        if (!admitted)
            return false;

        field = candidate;
        return true;
    }

    // Never a bare field or element ref (those resolve earlier), so the regex it feeds always renders via
    // $expr/$regexMatch (see MongoQueryLanguageRenderer.IsQueryDialectRegex).
    private MongoExpression? TryTranslateComputedRegexReceiver(Expression receiver)
        => TryTranslateValue(receiver, out var computed)
           && computed.Type == typeof(string)
           && MongoAggregationExpressionRenderer.CanRender(computed)
            ? computed
            : null;

    // DateTime/DateTimeOffset member chain (x.Dto.Value.DateTime.Date composes by recursing into the receiver). The
    // innermost receiver must be a plain stored field: a computed DateTimeOffset receiver has no MQL sub-field form.
    private bool TryTranslateDateTimeMember(Expression node, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (node is not MemberExpression { Expression: { } receiver } member)
            return false;

        var receiverType = receiver.Type.UnwrapNullableType();
        if (receiverType != typeof(DateTime) && receiverType != typeof(DateTimeOffset))
            return false;

        MongoExpression receiverExpr;
        if (TryResolveMember(receiver, out var property, out var fieldPath, out var dateTimeIsOuter))
        {
            // Correlated outer-scoped date chains aren't supported; decline rather than bind to the inner scope.
            if (dateTimeIsOuter)
                return false;

            receiverExpr = new MongoFieldExpression(property, fieldPath!);
        }
        else if (TryTranslateDateTimeMember(receiver, out var nestedDateTimeExpr))
        {
            receiverExpr = nestedDateTimeExpr;
        }
        else if (receiverType == typeof(DateTime) && TryTranslateDateAdd(receiver, allowNumericWidening: false, out var dateAddExpr))
        {
            // x.When.AddYears(-1).Year: a part of a computed instant (a DateTimeOffset receiver still needs a field).
            receiverExpr = dateAddExpr;
        }
        else
        {
            return false;
        }

        if (receiverType == typeof(DateTimeOffset))
        {
            switch (member.Member.Name)
            {
                case nameof(DateTimeOffset.UtcDateTime):
                    if (receiverExpr is not MongoFieldExpression utcField)
                        return false;
                    // The DateTimeOffset property backs the value; it carries no DateTimeKind, so the kind-aware read-back
                    // (NativeDateTimeKindReadBack) classifies it as kind-free rather than untraceable.
                    result = new MongoElementRefExpression(
                        utcField.ElementName + ".DateTime", typeof(DateTime), valueProperty: utcField.Property);
                    return true;

                case nameof(DateTimeOffset.DateTime):
                case nameof(DateTimeOffset.LocalDateTime):
                    if (receiverExpr is not MongoFieldExpression localField)
                        return false;
                    result = new MongoDateTimeOffsetLocalExpression(localField);
                    return true;

                default:
                    if (!DatePartsByMemberName.TryGetValue(member.Member.Name, out var offsetPart))
                        return false;
                    if (receiverExpr is not MongoFieldExpression partField)
                        return false;
                    result = new MongoDatePartExpression(new MongoDateTimeOffsetLocalExpression(partField), offsetPart);
                    return true;
            }
        }

        // Plain DateTime receiver: no offset reconstruction, extract directly.
        if (!DatePartsByMemberName.TryGetValue(member.Member.Name, out var plainPart))
            return false;

        // The server extracts the part from the stored UTC instant, but a Local-kind property materializes as local
        // time, so Hour/Date/Year... would silently disagree with the C# value (EF-459). Decline; driver-LINQ throws.
        if (NativeDateTimeKindReadBack.ReferencesKindSensitiveProperty(receiverExpr))
            return false;

        result = new MongoDatePartExpression(receiverExpr, plainPart);
        return true;
    }

    // A projection leaf that is exactly <DateTime field>.TimeOfDay. Deliberately not reachable from TranslateOperand
    // (filters, group keys, accumulators): the value is milliseconds since midnight, not a TimeSpan's default stored
    // form. The projection reader uses BsonSerializerFactory.TimeOfDayMillisecondsSerializer, and every operator reading
    // the projected alias afterwards must recognise it (MongoDatePartExpression.IsTimeOfDay: AllFieldsDefaultSerialized,
    // projected Distinct, set-op operand matching) or decline (see MongoDatePart). Declines a computed receiver, a
    // non-default-serialized field, and a HasDateTimeKind(Local) field (server-side time of day is UTC; EF-459).
    public bool TryTranslateTimeOfDayLeaf(Expression leaf, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (leaf is not MemberExpression { Member.Name: nameof(DateTime.TimeOfDay), Expression: { } receiver }
            || (receiver.Type != typeof(DateTime) && receiver.Type != typeof(DateTimeOffset))
            || !TryResolveInnerField(receiver, out var field)
            || field.Property.GetDateTimeKind() == DateTimeKind.Local)
        {
            return false;
        }

        if (!AllFieldsDefaultSerialized(field))
            return false;

        // A DateTimeOffset's time of day is that of its stored local (offset) time, as for its other date parts.
        MongoExpression operand = receiver.Type == typeof(DateTimeOffset) ? new MongoDateTimeOffsetLocalExpression(field) : field;
        result = new MongoDatePartExpression(operand, MongoDatePart.TimeOfDay);
        return true;
    }

    // An embedded collection navigation chain (b.Home.Notes) as an element ref for the array itself. Inherits
    // TryResolveOwnedCollectionPath's guards (final hop must be a collection navigation; two-scope mode declines); safe
    // from by-name retargeting because NativeProjectionBinder builds this translator on the query root.
    public bool TryTranslateOwnedCollectionArray(
        Expression expression,
        [NotNullWhen(true)] out MongoElementRefExpression? result)
    {
        var source = UnwrapAsQueryable(expression);
        if (TryResolveOwnedCollectionPath(source, out var arrayPath, out _, out var arrayIsOuter) && !arrayIsOuter)
        {
            // Use the unwrapped source's type (the navigation's own collection type), not the AsQueryable()
            // wrapper's IQueryable<T> — nothing renders from it, but misreporting the CLR type is a trap.
            result = new MongoElementRefExpression(arrayPath, source.Type);
            return true;
        }

        result = null;
        return false;
    }

    // Only the overloads recognised by TryGetDateAddShape map onto $dateAdd; Add(TimeSpan) falls through to driver-LINQ.
    private bool TryTranslateDateAdd(Expression node, bool allowNumericWidening, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is not MethodCallExpression { Object: { } receiver } call
            || !TryGetDateAddShape(call, out var unit, out var constantAmount))
        {
            return false;
        }

        // A literal receiver such as `new DateTime(1900, 1, 1)` can arrive un-evaluated: EF's parameter extraction
        // only folds maximal evaluatable subtrees, and the enclosing `.AddMinutes(o.OrderID % 25)` isn't one.
        var startDate = TranslateOperand(receiver, allowNumericWidening);
        if (startDate is null && !TryEvaluateClosedSubtree(receiver, out startDate))
            return false;

        if (startDate is null)
            return false;

        // Calendar arithmetic (day/month/year) over a Local-kind property differs from UTC arithmetic across a DST
        // change (EF-459); hour and smaller units are exact in both and stay native.
        if (NativeDateTimeKindReadBack.IsCalendarUnit(unit)
            && NativeDateTimeKindReadBack.ReferencesKindSensitiveProperty(startDate))
            return false;

        var amount = constantAmount ?? TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (amount is null)
            return false;

        result = new MongoDateAddExpression(startDate, unit, amount);
        return true;
    }

    // Evaluates a closed subtree (no lambda parameter, query parameter or extension node) to a constant; covers literals
    // EF's parameter extraction leaves un-folded because a sibling operand references the query source. Declines a
    // non-deterministic call (NonDeterministicCalls): EF leaves Guid.NewGuid() in the tree, and evaluating it once would
    // bake one value into every row (NonDeterministicFunctionTests.Guid_NewGuid_in_closed_date_add_receiver_throws).
    private static bool TryEvaluateClosedSubtree(Expression node, out MongoExpression? result)
    {
        result = null;

        // A clock member must never be baked into the cached template: it is a per-execution value
        // (TryCreateRuntimeClockParameter); a clock subtree that isn't admitted there declines.
        if (ContainsParameterOrExtensionNode(node) || NonDeterministicCalls.ContainsNonDeterministicCall(node) || RuntimeClock.ContainsClock(node))
            return false;

        object? value;
        try
        {
            value = Expression.Lambda<Func<object>>(Expression.Convert(node, typeof(object))).Compile().Invoke();
        }
        catch
        {
            return false; // the subtree throws when evaluated (e.g. an invalid DateTime); decline, don't crash
        }

        // Relabel (not convert) a non-Utc DateTime literal as Utc. Otherwise BsonValue.Create calls ToUniversalTime,
        // which depends on the host time zone and can skew the rendered $date (e.g. Europe/Dublin's pre-1916
        // -00:25 LMT offset), diverging from the tick value the in-memory comparison uses. Only literals reach here:
        // a clock (Local `Now`/`Today`) is declined above, since relabelling it would shift the instant by the host's
        // UTC offset.
        if (value is DateTime { Kind: not DateTimeKind.Utc } dateTimeValue)
            value = DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Utc);

        result = new MongoConstantExpression(value, forSerialization: null);
        return true;
    }

    private static bool ContainsParameterOrExtensionNode(Expression node)
    {
        var finder = new ParameterOrExtensionNodeFinder();
        finder.Visit(node);
        return finder.Found;
    }

    private sealed class ParameterOrExtensionNodeFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (node is null || Found)
                return node;

            if (node is ParameterExpression || node.NodeType == ExpressionType.Extension)
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    // Shared with MongoProjectionBindingExpressionVisitor so projection leaf registration and this translator gate on
    // the same method set.
    public static bool IsDateAddMethod(MethodCallExpression call)
        => TryGetDateAddShape(call, out _, out _);

    // The one recogniser behind both the emit side and IsDateAddMethod, so they cannot disagree. AddTicks is admitted
    // only with a constant that is a whole number of milliseconds: BSON dates are millisecond-precise and $dateAdd
    // rejects a fractional amount, so sub-millisecond and non-constant ticks decline.
    private static bool TryGetDateAddShape(MethodCallExpression call, out MongoDateAddUnit unit, out MongoExpression? constantAmount)
    {
        unit = default;
        constantAmount = null;
        if ((call.Method.DeclaringType != typeof(DateTime) && call.Method.DeclaringType != typeof(DateTimeOffset))
            || call.Arguments.Count != 1)
        {
            return false;
        }

        if (DateAddUnitsByMethodName.TryGetValue(call.Method.Name, out unit))
            return true;

        if (call.Method.Name == nameof(DateTime.AddTicks)
            && Unwrap(call.Arguments[0]) is ConstantExpression { Value: long ticks }
            && ticks % TimeSpan.TicksPerMillisecond == 0)
        {
            unit = MongoDateAddUnit.Millisecond;
            constantAmount = new MongoConstantExpression((double)(ticks / TimeSpan.TicksPerMillisecond), forSerialization: null);
            return true;
        }

        return false;
    }

    // The AddXxx overloads that map 1:1 onto a $dateAdd unit (AddTicks is handled by TryGetDateAddShape; Add(TimeSpan)
    // is absent, see MongoDateAddUnit). DateTime and DateTimeOffset declare identically-named members.
    private static readonly Dictionary<string, MongoDateAddUnit> DateAddUnitsByMethodName = new()
    {
        [nameof(DateTime.AddYears)] = MongoDateAddUnit.Year,
        [nameof(DateTime.AddMonths)] = MongoDateAddUnit.Month,
        [nameof(DateTime.AddDays)] = MongoDateAddUnit.Day,
        [nameof(DateTime.AddHours)] = MongoDateAddUnit.Hour,
        [nameof(DateTime.AddMinutes)] = MongoDateAddUnit.Minute,
        [nameof(DateTime.AddSeconds)] = MongoDateAddUnit.Second,
        [nameof(DateTime.AddMilliseconds)] = MongoDateAddUnit.Millisecond
    };

    // Mirrors MongoEFToLinqTranslatingExpressionVisitor.DateTimeOffsetComponentMembers minus TimeOfDay, which is only
    // a projection leaf (TryTranslateTimeOfDayLeaf; see MongoDatePart); also applies to plain DateTime receivers.
    private static readonly Dictionary<string, MongoDatePart> DatePartsByMemberName = new()
    {
        [nameof(DateTime.Year)] = MongoDatePart.Year,
        [nameof(DateTime.Month)] = MongoDatePart.Month,
        [nameof(DateTime.Day)] = MongoDatePart.Day,
        [nameof(DateTime.Hour)] = MongoDatePart.Hour,
        [nameof(DateTime.Minute)] = MongoDatePart.Minute,
        [nameof(DateTime.Second)] = MongoDatePart.Second,
        [nameof(DateTime.Millisecond)] = MongoDatePart.Millisecond,
        [nameof(DateTime.DayOfWeek)] = MongoDatePart.DayOfWeek,
        [nameof(DateTime.DayOfYear)] = MongoDatePart.DayOfYear,
        [nameof(DateTime.Date)] = MongoDatePart.Date
    };

    // C#'s implicit numeric conversions (spec §10.2.1). MongoDB arithmetic operates on the raw numeric BSON value,
    // so these compiler-inserted widenings are safe to unwrap.
    private static readonly HashSet<(Type From, Type To)> WideningNumericConversions =
    [
        (typeof(sbyte), typeof(short)), (typeof(sbyte), typeof(int)), (typeof(sbyte), typeof(long)),
        (typeof(sbyte), typeof(float)), (typeof(sbyte), typeof(double)), (typeof(sbyte), typeof(decimal)),
        (typeof(byte), typeof(short)), (typeof(byte), typeof(ushort)), (typeof(byte), typeof(int)),
        (typeof(byte), typeof(uint)), (typeof(byte), typeof(long)), (typeof(byte), typeof(ulong)),
        (typeof(byte), typeof(float)), (typeof(byte), typeof(double)), (typeof(byte), typeof(decimal)),
        (typeof(short), typeof(int)), (typeof(short), typeof(long)), (typeof(short), typeof(float)),
        (typeof(short), typeof(double)), (typeof(short), typeof(decimal)),
        (typeof(ushort), typeof(int)), (typeof(ushort), typeof(uint)), (typeof(ushort), typeof(long)),
        (typeof(ushort), typeof(ulong)), (typeof(ushort), typeof(float)), (typeof(ushort), typeof(double)),
        (typeof(ushort), typeof(decimal)),
        (typeof(int), typeof(long)), (typeof(int), typeof(float)), (typeof(int), typeof(double)), (typeof(int), typeof(decimal)),
        (typeof(uint), typeof(long)), (typeof(uint), typeof(ulong)), (typeof(uint), typeof(float)),
        (typeof(uint), typeof(double)), (typeof(uint), typeof(decimal)),
        (typeof(long), typeof(float)), (typeof(long), typeof(double)), (typeof(long), typeof(decimal)),
        (typeof(ulong), typeof(float)), (typeof(ulong), typeof(double)), (typeof(ulong), typeof(decimal)),
        (typeof(float), typeof(double))
    ];

    internal static bool IsWideningNumericConvert(Type from, Type to)
        => WideningNumericConversions.Contains((from, to));

    private static bool IsIntegerType(Type type)
    {
        var t = type.UnwrapNullableType();
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
    }

    // Matches MongoFieldExpression or MongoOuterFieldExpression; use instead of `is MongoFieldExpression` so bare-value
    // checks never miss the outer-scoped kind.
    internal static bool TryGetBareFieldProperty(MongoExpression node, [NotNullWhen(true)] out IProperty? property)
    {
        switch (node)
        {
            case MongoFieldExpression f:
                property = f.Property;
                return true;
            case MongoOuterFieldExpression o:
                property = o.Property;
                return true;
            default:
                property = null;
                return false;
        }
    }

    // A bare stored field whose conversion/non-default BsonRepresentation makes a truthiness test ($not, $and/$or
    // operand, bare boolean root) unsafe: only false/null/0/undefined are falsy in MQL, so a HasConversion<string>() bool
    // stored as "True"/"False" is always truthy. The single definition for all classify/throw sites in both renderers.
    internal static bool IsUnsafeTruthinessRoot(MongoExpression node, [NotNullWhen(true)] out IProperty? property)
        => TryGetBareFieldProperty(node, out property) && !AllFieldsDefaultSerialized(node);

    // Whether every reachable field uses its property's default BSON serialization (no converter, no non-default
    // BsonRepresentation); the shared predicate behind the truthiness and raw-operator guards in both renderers. Don't use
    // it to reject at translate time in TranslateOperand's filtered-count arm: that surfaces as an
    // InvalidOperationException from NativeProjectionBinder in every mode, whereas the render-time
    // NativeTranslationNotSupportedException lets TryBuildPipeline fall back.
    internal static bool AllFieldsDefaultSerialized(MongoExpression expr)
        => expr switch
        {
            MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
            MongoOuterFieldExpression outerField => NativeGroupByBinder.HasDefaultKeySerialization(outerField.Property),
            MongoBinaryExpression b => AllFieldsDefaultSerialized(b.Left) && AllFieldsDefaultSerialized(b.Right),
            MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand),
            MongoConcatExpression concat => concat.Operands.All(AllFieldsDefaultSerialized),
            // Raw-field $not is truthiness-based; see IsUnsafeTruthinessRoot.
            MongoUnaryExpression u => AllFieldsDefaultSerialized(u.Operand),
            MongoConditionalExpression cond => AllFieldsDefaultSerialized(cond.Test)
                && AllFieldsDefaultSerialized(cond.IfTrue)
                && AllFieldsDefaultSerialized(cond.IfFalse),
            MongoCoalesceExpression coalesce
                => AllFieldsDefaultSerialized(coalesce.Left) && AllFieldsDefaultSerialized(coalesce.Right),
            // Date operators run against the operand's raw BSON; a converted field would be a server type error or
            // wrong values.
            // TimeOfDay is milliseconds, not a TimeSpan's default (string) form: a later operator reducing or comparing
            // it (a terminal Max over the bare Select, a set op against a TimeSpan property) would read it wrongly.
            _ when MongoDatePartExpression.IsTimeOfDay(expr) => false,
            MongoDatePartExpression datePart => AllFieldsDefaultSerialized(datePart.Operand),
            MongoDateTimeOffsetLocalExpression local => AllFieldsDefaultSerialized(local.Operand),
            MongoDateAddExpression dateAdd
                => AllFieldsDefaultSerialized(dateAdd.StartDate) && AllFieldsDefaultSerialized(dateAdd.Amount),
            MongoStringIndexOfExpression indexOf
                => AllFieldsDefaultSerialized(indexOf.Haystack) && AllFieldsDefaultSerialized(indexOf.Needle)
                    && (indexOf.Start is null || AllFieldsDefaultSerialized(indexOf.Start)),
            MongoStringLengthExpression length => AllFieldsDefaultSerialized(length.Operand),
            MongoMathExpression math => math.Operands.All(AllFieldsDefaultSerialized),
            MongoTrimExpression trim => AllFieldsDefaultSerialized(trim.Source)
                && (trim.Chars is null || AllFieldsDefaultSerialized(trim.Chars)),
            MongoSubstringExpression substring => AllFieldsDefaultSerialized(substring.Source)
                && AllFieldsDefaultSerialized(substring.Start)
                && (substring.Length is null || AllFieldsDefaultSerialized(substring.Length)),
            MongoReplaceExpression replace => AllFieldsDefaultSerialized(replace.Input)
                && AllFieldsDefaultSerialized(replace.Find) && AllFieldsDefaultSerialized(replace.Replacement),
            MongoStringCompareExpression compare => AllFieldsDefaultSerialized(compare.Left)
                && AllFieldsDefaultSerialized(compare.Right),
            MongoStringFirstOrLastExpression firstOrLast => AllFieldsDefaultSerialized(firstOrLast.Source),
            // As a value (projection/sort key) a regex renders as $regexMatch/$indexOfCP over the raw stored value,
            // which errors for a non-string stored representation.
            MongoRegexExpression regex => AllFieldsDefaultSerialized(regex.Field) && AllFieldsDefaultSerialized(regex.Term),
            MongoTupleExpression tuple => tuple.Elements.All(AllFieldsDefaultSerialized),
            MongoMappedArrayReduceExpression mappedReduce => AllFieldsDefaultSerialized(mappedReduce.ElementExpression),
            // No arm needed (catch-all): MongoInExpression serializes candidates through the field's own serializer,
            // so it compares like-for-like; MongoSizeExpression has no property serialization; and
            // MongoArrayContainsExpression's Value is pre-rendered via the element serializer, with whole-collection
            // converters declined at translate time.
            _ => true
        };

    // Strips redundant Convert/ConvertChecked wrappers (EF sometimes adds a nullable-widening convert). Not
    // interchangeable with UnwrapOrderPreserving.
    internal static Expression Unwrap(Expression e)
        => e.RemoveConvert();

    // For a sort key: Unwrap would silently discard a narrowing or signed/unsigned cast and $sort by the raw stored
    // value. This strips only nullable <-> underlying, boxing and widening numeric converts (monotone, not always
    // value-preserving, so not sufficient where equality matters, e.g. a GroupBy key). Anything else stays so
    // TryResolveMember declines. See NativeCastTests.
    private static Expression UnwrapOrderPreserving(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var fromType = u.Operand.Type.UnwrapNullableType();
            var toType = u.Type.UnwrapNullableType();

            if (fromType != toType
                && toType != typeof(object)
                && !IsWideningNumericConvert(fromType, toType))
            {
                return e; // order-changing: leave it in place so the caller declines and falls through
            }

            e = u.Operand;
        }

        return e;
    }

    // `&` / `|` over two non-nullable bools is the logical and/or with both sides evaluated; server-side evaluation has
    // no side effects, so it is exact. bool? operands (three-valued `&`/`|`) are excluded and decline.
    private static bool IsNonNullableBoolLogical(BinaryExpression node)
        => node is { NodeType: ExpressionType.And or ExpressionType.Or }
           && node.Type == typeof(bool) && node.Left.Type == typeof(bool) && node.Right.Type == typeof(bool);

    private static bool IsLogicalAnd(BinaryExpression node)
        => node.NodeType == ExpressionType.AndAlso
           || (node.NodeType == ExpressionType.And && IsNonNullableBoolLogical(node));

    private static bool IsLogicalOr(BinaryExpression node)
        => node.NodeType == ExpressionType.OrElse
           || (node.NodeType == ExpressionType.Or && IsNonNullableBoolLogical(node));

    // Returns null for any unsupported node (the caller propagates null → false return).
    private MongoExpression? TranslateNode(Expression node)
    {
        // A closed boolean holding a clock (`DateTime.Now != myDate`) is evaluated per execution; renders as
        // `{ $expr: <placeholder> }` like a bare boolean query parameter.
        if (TryCreateRuntimeClockParameter(node, forSerialization: null) is { } runtimeClock)
            return runtimeClock;

        switch (node)
        {
            case BinaryExpression andAlso when IsLogicalAnd(andAlso):
            {
                var left = TranslateNode(Unwrap(andAlso.Left));
                if (left is null) return null;
                var right = TranslateNode(Unwrap(andAlso.Right));
                if (right is null) return null;
                return new MongoBinaryExpression(MongoBinaryOperator.AndAlso, left, right);
            }

            case BinaryExpression orElse when IsLogicalOr(orElse):
            {
                var left = TranslateNode(Unwrap(orElse.Left));
                if (left is null) return null;
                var right = TranslateNode(Unwrap(orElse.Right));
                if (right is null) return null;
                return new MongoBinaryExpression(MongoBinaryOperator.OrElse, left, right);
            }

            // An owned entity against a captured/constructed value of its type (`p.Home == x`, `l == x` in an element
            // scope): a per-property conjunction. See MongoExpressionTranslator.OwnedEquality.cs.
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } ownedEq
                when TryTranslateOwnedEntityEquality(
                    ownedEq.Left, ownedEq.Right, ownedEq.NodeType == ExpressionType.NotEqual, out var ownedEquality):
                return ownedEquality;

            // Root-entity structural equality (`c == local`, `c == null`); must precede the ordinary comparison
            // dispatch, which has no coverage for it.
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } eq
                when TryTranslateEntityEquality(eq, out var entityEquality):
                return entityEquality;

            // Constructed-tuple comparison; must precede ordinary comparison for the same reason. See
            // MongoExpressionTranslator.TupleEquality.cs.
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } tupleEq
                when TryTranslateTupleEquality(tupleEq, out var tupleEquality):
                return tupleEquality;

            // `root.GetType() == typeof(T)` against a non-hierarchy root folds to a constant. See
            // MongoExpressionTranslator.EntityType.cs.
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } getTypeEq
                when TryTranslateGetTypeComparison(getTypeEq, out var getTypeComparison):
                return getTypeComparison;

            // `root is T` against a non-hierarchy root folds to a constant. See MongoExpressionTranslator.TypeIs.cs.
            case TypeBinaryExpression { NodeType: ExpressionType.TypeIs } typeIs
                when TryTranslateTypeIs(typeIs, out var typeIsResult):
                return typeIsResult;

            // The `.Equals(...)` spelling, the only one composite-key entity types support (no synthesized `==`).
            case MethodCallExpression callEq when TryTranslateEntityEqualityCall(callEq, out var entityEqualityCall):
                return entityEqualityCall;

            // A predicate compared to a bool literal (`c.CustomerID.EndsWith("KI") == ((bool?)true)`). Must precede
            // ordinary comparison, whose operand resolution only recognizes members and values.
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } boolPredEq
                when TryTranslateBooleanPredicateComparison(boolPredEq, out var boolPredicateComparison):
                return boolPredicateComparison;

            // a.CompareTo(b) / string.Compare(a, b) against a constant; see MongoExpressionTranslator.StringCompare.cs.
            case BinaryExpression stringCompare when IsComparison(stringCompare.NodeType)
                                                     && TryTranslateStringCompare(stringCompare, out var stringCompareResult):
                return stringCompareResult;

            // x.S.ToLower()/ToUpper() == constant; see MongoExpressionTranslator.CaseMapping.cs.
            case BinaryExpression caseMapping when TryTranslateCaseMappingComparison(caseMapping, out var caseMappingResult):
                return caseMappingResult;

            case BinaryExpression be when IsComparison(be.NodeType):
                return TranslateComparison(be);

            // string.Equals with an explicit StringComparison; see MongoExpressionTranslator.StringEquals.cs.
            case MethodCallExpression stringEqualsCall
                when TryTranslateStringEqualsWithComparison(stringEqualsCall, out var stringEquals):
                return stringEquals;

            // Instance Equals(T): e.Field.Equals(value) ≡ e.Field == value. Equals(object) is excluded: the boxed argument would be stripped by Unwrap, turning a mismatched-type
            // call such as ((int)21).Equals((object)(ulong)21) (false in C#) into a $eq MongoDB matches by numeric
            // value. The mismatched-type case is folded to false below.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } equalsCall
                when equalsCall.Method.GetParameters()[0].ParameterType != typeof(object):
                return TranslateComparisonCore(equalsCall.Object!, equalsCall.Arguments[0], ExpressionType.Equal);

            // Nullable receiver (`c.NullableAge.Equals(21)`): Nullable<T> only has Equals(object). Peel one boxing layer and require matching nullable-stripped
            // types; a mismatch falls through.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } nullableEqualsCall
                when nullableEqualsCall.Object!.Type.UnwrapNullableType()
                     == nullableEqualsCall.Arguments[0].RemoveObjectConvert().Type.UnwrapNullableType():
                return TranslateComparisonCore(
                    nullableEqualsCall.Object!, nullableEqualsCall.Arguments[0].RemoveObjectConvert(), ExpressionType.Equal);

            // Instance Equals(object) across mismatched types (`nullableLongPrm.Equals(e.ReportsTo)`): always false in C#; folds to a constant, mirroring the driver-LINQ bridge
            // (ExpressionExtensionMethods.IsAlwaysFalseAcrossTypeMismatch). Scoped to exact-equality primitives since
            // an arbitrary Equals(object) override may legitimately compare across types.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } mismatchedEqualsCall
                when ExpressionExtensionMethods.IsAlwaysFalseAcrossTypeMismatch(
                    mismatchedEqualsCall.Object!, mismatchedEqualsCall.Arguments[0]):
                return new MongoConstantExpression(false, forSerialization: null);

            // Static object.Equals(a, b): both arguments are always boxed. Peel exactly one Convert-to-object layer each (RemoveObjectConvert,
            // unlike Unwrap, leaves widenings intact) and require matching types; a mismatched exact-equality pair
            // folds to false, anything else declines. Mirrors the driver-LINQ bridge's static-Equals case.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2
                } staticEqualsCall:
            {
                var leftArg = staticEqualsCall.Arguments[0].RemoveObjectConvert();
                var rightArg = staticEqualsCall.Arguments[1].RemoveObjectConvert();
                // Nullability alone doesn't make the pair mismatched: Equals(DateTime?, DateTime) is a lifted ==.
                var leftUnderlying = leftArg.Type.UnwrapNullableType();
                var rightUnderlying = rightArg.Type.UnwrapNullableType();
                if (leftUnderlying != rightUnderlying)
                {
                    return ExpressionExtensionMethods.AreMismatchedExactEqualityTypes(leftUnderlying, rightUnderlying)
                        ? new MongoConstantExpression(false, forSerialization: null)
                        : null;
                }

                return TranslateComparisonCore(leftArg, rightArg, ExpressionType.Equal);
            }

            case UnaryExpression { NodeType: ExpressionType.Not } not:
            {
                var operand = TranslateNode(Unwrap(not.Operand));
                if (operand is null) return null;
                // Self-negating nodes (Contains, StartsWith, Any/All, ...) flip their own Negated flag; they have no
                // query-dialect "not" wrapper. See MongoExpressionNegator.TryFlipNegatedFlag.
                if (MongoExpressionNegator.TryFlipNegatedFlag(operand, out var selfNegated))
                    return selfNegated;
                // A correlated quantifier: flip via the negator's quantifier case rather than wrapping in Not, which
                // has no query-dialect form. Needed so NativeCardinalityBinder's All(pred) can re-negate cleanly.
                if (operand is MongoQuantifierExpression quantifierOperand)
                    return MongoExpressionNegator.TryNegate(quantifierOperand, out var quantifierComplement)
                        ? quantifierComplement
                        : null;
                // !(collection.Count > n): invert rather than wrap (the array-index form has no $not); see
                // MongoExpressionNegator. A parameterized threshold declines here (coverage gap, not correctness).
                if (operand is MongoBinaryExpression { Left: MongoSizeExpression })
                    return MongoExpressionNegator.TryNegate(operand, out var countComplement)
                        ? countComplement
                        : null;
                // !(a || b) / !(a && b): exact De Morgan via the negator, or decline — never the generic $not wrap
                // below, which renders in the aggregation dialect and differs from the query dialect in three ways:
                // $eq doesn't equate missing with null ({f: null} does), relational operators order null/missing
                // below every value (the query dialect never matches them), and a field path doesn't match array
                // elements implicitly. Each would silently return wrong rows.
                // Exception: the wrap is exact when every leaf answers the same in both dialects — a numeric type
                // bracket ({$type: "number"} ≡ $isNumber) or a leaf that already renders only in $expr — which covers
                // a negated bracketed relational cast (see MongoNumericTypeBracketExpression).
                if (operand is MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse })
                {
                    if (MongoExpressionNegator.TryNegate(operand, out var conjunctionComplement))
                        return conjunctionComplement;

                    return IsDialectInvariantForNegation(operand)
                        ? new MongoUnaryExpression(MongoUnaryOperator.Not, operand)
                        : null;
                }
                // Nullable bools (field or outer-scoped field) decline: Not could diverge from driver rendering.
                if (TryGetBareFieldProperty(operand, out var notOperandProperty) && notOperandProperty.IsNullable)
                    return null;
                return new MongoUnaryExpression(MongoUnaryOperator.Not, operand);
            }

            // Boolean ternary predicate root. Branches recurse through TranslateNode (predicates, not values). A branch that is a bare
            // non-default-serialized bool declines: $cond evaluates by truthiness. MongoConditionalExpression has
            // no query-dialect form, so it renders via $expr, and IsQueryDialectRenderable keeps it out of
            // $elemMatch (where $expr is a server error).
            case ConditionalExpression conditional when conditional.Type == typeof(bool):
            {
                if (!TryTranslate(conditional.Test, out var test))
                    return null;

                var ifTrue = TranslateNode(Unwrap(conditional.IfTrue));
                if (ifTrue is null || IsUnsafeTruthinessRoot(ifTrue, out _))
                    return null;

                var ifFalse = TranslateNode(Unwrap(conditional.IfFalse));
                if (ifFalse is null || IsUnsafeTruthinessRoot(ifFalse, out _))
                    return null;

                return new MongoConditionalExpression(test, ifTrue, ifFalse);
            }

            // arrayField.Contains(constant) → { field: value }: mirror of the $in arm below, with a stored array receiver.
            // MongoDB's implicit element match gives the semantics; see MongoArrayContainsExpression for why it isn't a
            // MongoBinaryOperator.Equal (which would mean whole-array equality in the aggregation dialect).
            // Constant items only: a parameterized item would need a placeholder resolved through the element
            // serializer, so it falls through to the $in arm and declines there.
            case MethodCallExpression call
                when TryMatchContainsMethod(call, out var arrayReceiver, out var containsItem)
                     && Unwrap(containsItem) is ConstantExpression
                     && TryResolveMember(Unwrap(arrayReceiver), out var arrayProperty, out var arrayFieldPath, out var arrayIsOuter)
                     && !arrayIsOuter
                     && arrayProperty.ClrType.TryGetEnumerableElementType() is not null:
            {
                var itemNode = TranslateArrayContainsItem(Unwrap(containsItem), arrayProperty);
                if (itemNode is null)
                    return null; // e.g. a value-converted array, or a CLR/element type mismatch — decline.

                var arrayFieldExpr = new MongoFieldExpression(arrayProperty, arrayFieldPath);
                return new MongoArrayContainsExpression(arrayFieldExpr, itemNode, negated: false);
            }

            // Entity-list membership (customers.Contains(c)); must precede the general membership arm, which requires a
            // member-access item. See MongoExpressionTranslator.EntityEquality.cs.
            case MethodCallExpression ownedContainsCall when TryTranslateOwnedCollectionContains(ownedContainsCall, out var ownedContains):
                return ownedContains;

            case MethodCallExpression entityContainsCall when TryTranslateEntityListContains(entityContainsCall, out var entityListContains):
                return entityListContains;

            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath, out var itemIsOuter))
                {
                    if (itemIsOuter)
                        return null;

                    var valuesNode = TranslateInValues(collectionExpr, property);
                    if (valuesNode is null)
                        return null;

                    var fieldExpr2 = new MongoFieldExpression(property, fieldPath);
                    return new MongoInExpression(fieldExpr2, valuesNode, negated: false);
                }

                // A constructed Tuple/ValueTuple needle over entity fields (`ids.Contains(new Tuple<int, int>(o.OrderID,
                // o.ProductID))`) renders as an MQL array, like the tuple-equality operand. Only a query-PARAMETER haystack
                // is admitted: its elements serialize through the driver's tuple serializer (one BSON array per tuple, see
                // MongoValueRenderer.RenderInValues); a constant haystack would go through BsonValue.Create,
                // which cannot serialize a tuple.
                if (IsTupleType(itemExpr.Type)
                    && TryDecomposeTupleOperand(Unwrap(itemExpr), out var tupleElements)
                    && new MongoTupleExpression(tupleElements) is var tupleNeedle
                    && AllFieldsDefaultSerialized(tupleNeedle)
                    && MongoAggregationExpressionRenderer.CanRender(tupleNeedle))
                {
                    return TranslateInValuesRaw(collectionExpr, itemExpr.Type) is MongoParameterExpression tupleValues
                        ? new MongoComputedInExpression(tupleNeedle, tupleValues, negated: false)
                        : null;
                }

                // A computed needle (`data.Contains(c.CustomerID + "x")`, `dates.Contains(o.OrderDate!.Value.Date)`,
                // or an anonymous-type tuple of entity fields) has no query-dialect form, so it uses $expr's array
                // $in via MongoComputedInExpression. Limited to types TranslateInValuesRaw can serialize without a
                // backing IProperty, and gated by CanRender so it declines instead of throwing at render time.
                if (TryTranslateValue(itemExpr, out var needleNode)
                    && (needleNode.Type == typeof(string) || needleNode.Type == typeof(DateTime)
                        || needleNode is MongoDocumentConstructionExpression)
                    && MongoAggregationExpressionRenderer.CanRender(needleNode))
                {
                    var rawValuesNode = TranslateInValuesRaw(collectionExpr, needleNode.Type);
                    if (rawValuesNode is null)
                        return null;

                    return new MongoComputedInExpression(needleNode, rawValuesNode, negated: false);
                }

                return null;
            }

            // IsNullOrEmpty/IsNullOrWhiteSpace translate as `s == null || s == ""` (`s.Trim() == ""` for whitespace) so the
            // existing null/missing semantics apply.
            case MethodCallExpression { Method.IsStatic: true, Arguments: [var nullOrArg] } nullOrCall
                when nullOrCall.Method.DeclaringType == typeof(string)
                     && nullOrCall.Method.Name is nameof(string.IsNullOrEmpty) or nameof(string.IsNullOrWhiteSpace):
            {
                var tested = nullOrCall.Method.Name == nameof(string.IsNullOrWhiteSpace)
                    ? (Expression)Expression.Call(nullOrArg, typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!)
                    : nullOrArg;

                return TranslateNode(Expression.OrElse(
                    Expression.Equal(nullOrArg, Expression.Constant(null, typeof(string))),
                    Expression.Equal(tested, Expression.Constant(string.Empty))));
            }

            case MethodCallExpression call when TryMatchRegexMethod(call, out var kind, out var receiver, out var termExpr, out var caseInsensitive):
            {
                MongoExpression fieldNode;
                IProperty? property = null;
                var receiverIsOuter = false;

                if (TryResolveMember(Unwrap(receiver), out var receiverProperty, out var fieldPath, out receiverIsOuter))
                {
                    // Element-scope translators (innerPrefix null) decline an outer receiver: MongoRegexExpression
                    // needs a MongoFieldExpression, which would render element-relative inside $filter/$map. The
                    // SelectMany/join two-scope translators (non-null innerPrefix) already resolve it to the correct
                    // root path, so it must stay translatable (e.g. x.Outer.Name.StartsWith(...) in a join Where).
                    if (receiverIsOuter && _innerPrefix is null)
                        return null;

                    if (!IsRegexSearchableString(receiverProperty))
                        return null;

                    property = receiverProperty;
                    fieldNode = new MongoFieldExpression(receiverProperty, fieldPath!);
                }
                else if (TryResolveFlattenedAlias(Unwrap(receiver), out var aliasFieldRef)
                         && aliasFieldRef.Type == typeof(string))
                {
                    // A flattened alias with no backing IProperty (a computed Distinct key part, or a keyed GroupBy
                    // output alias). RenderRegex only reads the document path, so the flattened alias works. See
                    // TryResolveFlattenedAlias.
                    fieldNode = aliasFieldRef;
                }
                else if (TryTranslateComputedRegexReceiver(Unwrap(receiver)) is { } computedReceiver)
                {
                    // A computed string receiver (`(c.A + "").Contains("1")`, `(c.S ?? "z").StartsWith("a")`,
                    // `c.U.ToString().Contains("7")`). Aggregation dialect only: MongoQueryLanguageRenderer's
                    // IsQueryDialectRegex keeps it out of the { path: /re/ } form, so it renders via
                    // $expr/$regexMatch. The field-to-field term arm below requires `property`, so it declines here.
                    fieldNode = computedReceiver;
                }
                else
                {
                    return null;
                }

                var termNode = termExpr.Type == typeof(char)
                    ? TranslateCharAsString(Unwrap(termExpr))
                    : TranslateValue(Unwrap(termExpr), property);
                if (termNode is null && termExpr.Type == typeof(char))
                    return null; // a computed char (e.g. c.Text[0]) has no regex-term form

                if (termNode is null)
                {
                    // Field-to-field (`c.ContactName.StartsWith(c.ContactName)`, as EF's All_top_level_column
                    // produces): a $regularExpression pattern must be a literal, so this renders only in the
                    // aggregation dialect ($indexOfCP/$strLenCP). Cross-scope pairs aren't supported.
                    if (property is null || receiverIsOuter
                        || !TryResolveInnerStringField(Unwrap(termExpr), out var termField))
                    {
                        return null;
                    }

                    termNode = termField;

                    // Case-insensitive field-to-field declines: RenderRegexAsExpr would fold via $toLower, which is
                    // ASCII-only (leaves É, Б, Ω unchanged), and no $expr operator does Unicode case folding, so
                    // non-ASCII OrdinalIgnoreCase would silently answer wrong. Constant/parameter terms are
                    // unaffected; they use the regex "i" option.
                    if (caseInsensitive)
                    {
                        return null;
                    }
                }

                return new MongoRegexExpression(fieldNode, kind, termNode, negated: false, caseInsensitive);
            }

            case MethodCallExpression likeCall when TryTranslateLike(likeCall, out var likeResult):
                return likeResult;

            // Regex.IsMatch(constantInput, fieldPattern): reversed-argument shape.
            case MethodCallExpression regexIsMatchCall when TryTranslateRegexIsMatch(regexIsMatchCall, out var regexIsMatchResult):
                return regexIsMatchResult;

            case MethodCallExpression { Method.DeclaringType: var mqlDeclaring } presenceCall
                when mqlDeclaring == typeof(MongoDB.Driver.Mql)
                     && TryTranslateFieldPresence(presenceCall, out var fieldPresence):
                return fieldPresence;

            case MethodCallExpression { Method.Name: "ContainsKey" } containsKeyCall
                when TryTranslateDictionaryContainsKey(containsKeyCall, out var containsKeyResult):
                return containsKeyResult;

            // Quantifiers over an owned (embedded) collection.
            case MethodCallExpression call
                when TryMatchQuantifierMethod(call, out var quantifier, out var quantifierSource, out var elementLambda):
            {
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType, out var sourceIsOuter))
                {
                    // A bare Any() over a primitive collection property is the same "count >= 1" node as the owned form.
                    // Any(pred)/All(pred) over a primitive collection stay declined.
                    if (elementLambda is null
                        && quantifier == MongoQuantifierKind.Any
                        && TryResolvePrimitiveCollectionPath(quantifierSource, out var primitiveAnyPath, out var primitiveAnyOuter)
                        && !primitiveAnyOuter)
                    {
                        return new MongoBinaryExpression(
                            MongoBinaryOperator.GreaterThanOrEqual,
                            new MongoSizeExpression(primitiveAnyPath, typeof(int), nullSafe: true),
                            new MongoConstantExpression(1, forSerialization: null));
                    }

                    return null;
                }

                if (sourceIsOuter)
                    return null;

                if (elementLambda is null)
                {
                    // A bare Any() is Count >= 1, rendered as the array-index existence form. Keeping one
                    // representation for cardinality lets !Any() fall out of the negator's inversion.
                    return new MongoBinaryExpression(
                        MongoBinaryOperator.GreaterThanOrEqual,
                        new MongoSizeExpression(arrayPath, typeof(int), nullSafe: true),
                        new MongoConstantExpression(1, forSerialization: null));
                }

                if (!TryCreateElementPredicateTranslator(
                        elementLambda, elementType, out var elementTranslator, out var isCorrelated))
                {
                    return null;
                }

                // A correlated element predicate (references the enclosing entity). $elemMatch can't reference the
                // enclosing document, so a correlation on SelfParam gets a two-scope child and emits a
                // MongoQuantifierExpression ($anyElementTrue/$allElementsTrue over $map). Correlation reaching further
                // out has already declined.
                if (isCorrelated)
                {
                    if (!elementTranslator.TryTranslate(elementLambda.Body, out var correlatedPredicate))
                        return null;

                    // No negation for All here: $allElementsTrue is itself "for all".
                    if (!MongoAggregationExpressionRenderer.CanRender(correlatedPredicate))
                        return null;

                    // CanRender admits any bare field unconditionally, so a bare converted bool root
                    // (`b.Posts.Any(p => b.Flag)`) would pass and be truthiness-tested, silently answering wrong.
                    // Decline here; RenderQuantifier also throws as defense in depth.
                    if (TryGetBareFieldProperty(correlatedPredicate, out _)
                        && !AllFieldsDefaultSerialized(correlatedPredicate))
                        return null;

                    return new MongoQuantifierExpression(
                        new MongoElementRefExpression(arrayPath, quantifierSource.Type), correlatedPredicate, quantifier);
                }

                // An element-scoped translator yields element-relative paths, as $elemMatch requires (mirror of
                // NativeSelectManyBinder.TryBuildOwnedInnerFilter, which then prefixes them).
                elementTranslator.ElementParam = elementLambda.Parameters[0];
                if (!elementTranslator.TryTranslate(elementLambda.Body, out var translated))
                    return null;

                MongoExpression child = translated;
                var negated = false;

                if (quantifier is MongoQuantifierKind.All)
                {
                    // All(pred) ≡ no element satisfies ¬pred: a negated $elemMatch over the exact complement. Also
                    // true for an empty, missing, or null array, matching LINQ. No exact complement → decline;
                    // an approximation would return wrong rows.
                    if (!MongoExpressionNegator.TryNegate(child, out var complement))
                        return null;

                    child = complement;
                    negated = true;
                }

                // Load-bearing: $expr inside $elemMatch is a server error, and RenderNode's catch-all would wrap a
                // non-query-dialect child in $expr. TryNegate can return a quantifier or field-to-field regex
                // without checking query-dialect renderability, and the un-negated Any child is unchecked, so this
                // is the only guard.
                if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(child))
                    return null;

                return new MongoElemMatchExpression(arrayPath, child, negated);
            }

            // Nullable<T>.HasValue is built as `x.A != null` ($ne selects null and missing, matching LINQ). Must precede the
            // bare-boolean-member default, which would fail to resolve "HasValue".
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath, out var hasValueIsOuter)
                    || hasValueIsOuter)
                    return null;

                return new MongoBinaryExpression(
                    MongoBinaryOperator.NotEqual,
                    new MongoFieldExpression(nullableProperty, nullablePath),
                    new MongoConstantExpression(null, nullableProperty));
            }

            // Literal boolean root (e.g. a constant-folded `All(x => true)`); renders via RenderNode's catch-all as
            // `{ $expr: true }`. Must precede the bare-boolean-member default.
            case ConstantExpression { Value: bool literalBool }:
                return new MongoConstantExpression(literalBool, forSerialization: null);

            // EF hoists a predicate that doesn't reference the query source (`c => data.Contains(v + "x")`) into a single
            // bool parameter before translation. Renders as `{ $expr: <placeholder> }`.
            case var parameterNode when node.Type == typeof(bool) && NativeQueryParameter.TryGetQueryParameterName(parameterNode, out var boolParamName):
                return new MongoParameterExpression(boolParamName, forSerialization: null);

            default: // bare boolean member access (c.Active)
                if (TryResolveMember(node, out var boolProp, out var boolPath, out var boolIsOuter))
                {
                    // Only non-nullable bools; a nullable bare access could diverge.
                    if (boolProp!.ClrType != typeof(bool) || boolProp.IsNullable)
                        return null;
                    // Only element-scope translators (innerPrefix null) emit MongoOuterFieldExpression. The
                    // SelectMany two-scope translator keeps a plain root MongoFieldExpression: an outer field isn't
                    // query-dialect-renderable and would be truthiness-tested via $expr instead of serialized.
                    return boolIsOuter && _innerPrefix is null
                        ? new MongoOuterFieldExpression(boolProp, boolPath!)
                        : new MongoFieldExpression(boolProp, boolPath!);
                }

                return null;
        }
    }

    // Query-native: bare member vs constant/parameter, with the field always on Left (mirroring the operator) so the
    // renderer keeps routing it to the indexable $match dialect. Anything else goes via TranslateOperand to $expr,
    // preserving operand order. A cast the query-native branch can't absorb (HasNumericConvert) falls through to $expr
    // as an explicit MongoConvertExpression, gated by CanFallThroughToExpr; a relational comparison there also gets a
    // MongoNumericTypeBracketExpression conjunct (NeedsNumericTypeBracket), since bare $expr doesn't type-bracket
    // null/missing the way the query dialect does. By design a narrowing cast against a constant ((int)x.D > 0) returns
    // the CLR answer, which differs from driver-LINQ (it drops the cast); pinned by NativeCastTests.
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
        => TranslateComparisonCore(be.Left, be.Right, be.NodeType);

    // `predicate == literalBool` / `!=` where the predicate side isn't a stored member, as the predicate or its negation.
    // Declines nullable-bool predicates (three-valued semantics aren't preserved).
    private bool TryTranslateBooleanPredicateComparison(BinaryExpression be, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        var left = Unwrap(be.Left);
        var right = Unwrap(be.Right);

        Expression predicateSide;
        bool literalValue;
        if (right is ConstantExpression { Value: bool rightBool })
        {
            literalValue = rightBool;
            predicateSide = left;
        }
        else if (left is ConstantExpression { Value: bool leftBool })
        {
            literalValue = leftBool;
            predicateSide = right;
        }
        else
        {
            return false;
        }

        if (predicateSide.Type != typeof(bool) || predicateSide is ConstantExpression)
            return false;

        if (TryResolveMember(predicateSide, out _, out _, out _))
            return false; // the query-native comparison owns this shape

        if (!TryTranslate(predicateSide, out var predicate))
            return false;

        var negate = be.NodeType == ExpressionType.Equal ? !literalValue : literalValue;
        if (!negate)
        {
            result = predicate;
            return true;
        }

        return MongoExpressionNegator.TryNegate(predicate, out result);
    }

    // Shared core of TranslateComparison; also serves instance Equals(T) (its argument carries at most a non-boxing
    // widening, as with ==).
    private MongoBinaryExpression? TranslateComparisonCore(Expression left, Expression right, ExpressionType nodeType)
    {
        var leftUnwrapped = Unwrap(left);
        var rightUnwrapped = Unwrap(right);

        // Entity-typed operand vs literal null (`c == null`, owned `b.Address == null`). Only a literal null: a captured
        // entity instance has no BsonValue mapping and must decline to driver-LINQ. Both sides are resolved
        // unconditionally so both out locals are assigned.
        var isLeftEntityTyped = TryResolveEntityTypedOperand(leftUnwrapped, out var leftEntityRef);
        var isRightEntityTyped = TryResolveEntityTypedOperand(rightUnwrapped, out var rightEntityRef);
        if (isLeftEntityTyped && rightUnwrapped is ConstantExpression { Value: null }
            || isRightEntityTyped && leftUnwrapped is ConstantExpression { Value: null })
        {
            var nullCheckOp = MapComparisonOperator(nodeType);
            if (nullCheckOp is null)
                return null;

            return new MongoBinaryExpression(
                nullCheckOp.Value,
                (leftEntityRef ?? rightEntityRef)!,
                new MongoConstantExpression(null, forSerialization: null));
        }

        // `c == c`: both sides are SelfParam (by reference), so trivially true/false. Two different entity-typed
        // operands are not admitted.
        if (IsSelfParamTheEntity(leftUnwrapped) && IsSelfParamTheEntity(rightUnwrapped))
        {
            var selfOp = MapComparisonOperator(nodeType);
            if (selfOp is null)
                return null;

            var rootRef = new MongoElementRefExpression(MongoElementRefExpression.WholeRootDocumentPath, _entityType.ClrType);
            return new MongoBinaryExpression(selfOp.Value, rootRef, rootRef);
        }

        if (TryTranslateDictionaryEntryComparison(left, right, nodeType, out var dictionaryComparison))
            return dictionaryComparison;

        // `x.P.ToString() == value` over a property stored as the string ToString() would produce.
        if (nodeType is ExpressionType.Equal or ExpressionType.NotEqual
            && TryTranslateStoredToStringComparison(leftUnwrapped, rightUnwrapped, nodeType) is { } toStringComparison)
        {
            return toStringComparison;
        }

        if (nodeType is ExpressionType.Equal or ExpressionType.NotEqual
            && TryTranslateStoredToStringComparison(rightUnwrapped, leftUnwrapped, nodeType) is { } mirroredToStringComparison)
        {
            return mirroredToStringComparison;
        }

        // A bare flattened alias (`.Select(g => g.Sum(o => o.OrderID)).All(v => v >= 0)`) and a named one
        // (`x.Total > 100`) need no arm here: under ProjectedAliasScope TryResolveMember declines, so both fall
        // through to the $expr path, where TranslateOperand resolves them via TryResolveFlattenedAlias.

        // Set when a numeric-cast relational comparison falls through to $expr and needs
        // a type-bracket conjunct (see CanFallThroughToExpr). Not built for outer-scoped fields, which decline.
        MongoFieldExpression? numericTypeBracket = null;

        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath, out var leftIsOuter)
            && IsSimpleValue(rightUnwrapped))
        {
            // A relational operator runs on the stored form; decline unless it orders like the CLR value (EF-337).
            // Equality is exact whatever the storage.
            if (!ComparisonPreservesClrOrdering(nodeType, leftProperty!))
                return null;

            // A widening or identity-like cast on the member side is absorbed; any other cast falls through to the
            // $expr path as an explicit $toX (see HasNumericConvert), gated by CanFallThroughToExpr, with a type
            // bracket for a relational comparison.
            if (HasNumericConvert(left, leftProperty!.ClrType, out var leftWideningTarget, out var leftIdentityLike))
            {
                if (!CanFallThroughToExpr(leftProperty))
                    return null;

                if (!leftIsOuter && NeedsNumericTypeBracket(nodeType))
                    numericTypeBracket = new MongoFieldExpression(leftProperty, leftPath!);
            }
            else
            {
                var mongoOp = MapComparisonOperator(nodeType);
                if (mongoOp is null)
                    return null;

                var valueExpr = TranslateValue(
                    rightUnwrapped, ConstantSerializationContext(leftProperty, leftWideningTarget, leftIdentityLike));
                if (valueExpr is null)
                    return null;

                // Only element-scope translators (innerPrefix null) emit MongoOuterFieldExpression. The SelectMany
                // translator keeps a root MongoFieldExpression so the comparison stays in the query dialect; via
                // $expr a relational operator over a nullable outer field would admit null/missing rows.
                //
                // NullSafe for the two-scope Inner side vs a literal null under ==/!=: an unmatched left-outer row's
                // Inner sub-document is missing, and $expr's $eq/$ne treat missing differently from null (see
                // MongoFieldExpression.NullSafe). A no-op when the Inner side is present.
                var leftNullSafe = _innerPrefix is not null && !leftIsOuter
                    && rightUnwrapped is ConstantExpression { Value: null }
                    && nodeType is ExpressionType.Equal or ExpressionType.NotEqual;
                MongoExpression leftField = leftIsOuter && _innerPrefix is null
                    ? new MongoOuterFieldExpression(leftProperty, leftPath!)
                    : new MongoFieldExpression(leftProperty, leftPath!, leftNullSafe);
                return new MongoBinaryExpression(mongoOp.Value, leftField, valueExpr);
            }
        }

        // The mirrored shape, same fall-through rule. The $expr path keeps original operand order (no mirroring).
        else if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath, out var rightIsOuter)
                 && IsSimpleValue(leftUnwrapped))
        {
            if (!ComparisonPreservesClrOrdering(nodeType, rightProperty!))
                return null;

            if (HasNumericConvert(right, rightProperty!.ClrType, out var rightWideningTarget, out var rightIdentityLike))
            {
                if (!CanFallThroughToExpr(rightProperty))
                    return null;

                // nodeType, not the mirrored operator: relational operators are closed under Mirror.
                if (!rightIsOuter && NeedsNumericTypeBracket(nodeType))
                    numericTypeBracket = new MongoFieldExpression(rightProperty, rightPath!);
            }
            else
            {
                // The member must render on the Left, so mirror the operator.
                var mongoOp = MapComparisonOperator(Mirror(nodeType));
                if (mongoOp is null)
                    return null;

                var valueExpr = TranslateValue(
                    leftUnwrapped, ConstantSerializationContext(rightProperty, rightWideningTarget, rightIdentityLike));
                if (valueExpr is null)
                    return null;

                // Same outer-field confinement and NullSafe rule as the branch above.
                var rightNullSafe = _innerPrefix is not null && !rightIsOuter
                    && leftUnwrapped is ConstantExpression { Value: null }
                    && nodeType is ExpressionType.Equal or ExpressionType.NotEqual;
                MongoExpression rightField = rightIsOuter && _innerPrefix is null
                    ? new MongoOuterFieldExpression(rightProperty, rightPath!)
                    : new MongoFieldExpression(rightProperty, rightPath!, rightNullSafe);
                return new MongoBinaryExpression(mongoOp.Value, rightField, valueExpr);
            }
        }

        // Constructed anonymous-type/DTO NotEqual declines: EF's own lowering of it is buggy (dotnet/efcore#36412;
        // see Where_compare_constructed_multi_value_not_equal), so native would reproduce the wrong answer.
        if (nodeType == ExpressionType.NotEqual
            && leftUnwrapped.TryGetProjectionMembers(out _) && rightUnwrapped.TryGetProjectionMembers(out _))
            return null;

        // Field-to-field / arithmetic-operand shape: always routes to $expr.
        var generalOp = MapComparisonOperator(nodeType);
        if (generalOp is null)
            return null;

        var leftOperand = TranslateOperand(left);
        if (leftOperand is null)
            return null;

        var rightOperand = TranslateOperand(right);
        if (rightOperand is null)
            return null;

        // Char comparison (`x.FirstOrDefault() == 'S'`): C# widens both sides to int, so our side arrives as
        // $toInt over $substrCP, which throws on a non-numeral string. Unwrap it and re-express the constant side
        // as a one-character string (see CharCodeAsFirstOrLastValue). A parameterized other side declines.
        if (TryUnwrapCharFirstOrLast(leftOperand, out var leftFirstOrLast))
        {
            if (rightOperand is not MongoConstantExpression { Value: int rightCharCode })
                return null;

            leftOperand = leftFirstOrLast;
            rightOperand = new MongoConstantExpression(CharCodeAsFirstOrLastValue(rightCharCode), forSerialization: null);
        }
        else if (TryUnwrapCharFirstOrLast(rightOperand, out var rightFirstOrLast))
        {
            if (leftOperand is not MongoConstantExpression { Value: int leftCharCode })
                return null;

            rightOperand = rightFirstOrLast;
            leftOperand = new MongoConstantExpression(CharCodeAsFirstOrLastValue(leftCharCode), forSerialization: null);
        }

        // $expr field refs read the raw stored value with no serializer, so a converted or non-default-represented
        // operand would compare in stored form (silently wrong rows, or cross-BSON-type ordering). Both operands
        // must be default-serialized; TranslateOperand's Convert guard only covers casts.
        if (!AllFieldsDefaultSerialized(leftOperand) || !AllFieldsDefaultSerialized(rightOperand))
            return null;

        // Put a size node on the Left (mirroring the operator): the query renderer's array-index form only
        // recognizes that orientation. Other $expr comparisons keep their order.
        if (rightOperand is MongoSizeExpression
            && leftOperand is MongoConstantExpression or MongoParameterExpression)
        {
            var mirroredOp = MapComparisonOperator(Mirror(nodeType));
            if (mirroredOp is null)
                return null;

            return new MongoBinaryExpression(mirroredOp.Value, rightOperand, leftOperand);
        }

        var comparisonResult = new MongoBinaryExpression(generalOp.Value, leftOperand, rightOperand);

        // Conjoin the type bracket, if any; see NeedsNumericTypeBracket.
        return numericTypeBracket is null
            ? comparisonResult
            : new MongoBinaryExpression(
                MongoBinaryOperator.AndAlso, new MongoNumericTypeBracketExpression(numericTypeBracket), comparisonResult);
    }

    // Subsumed by TranslateOperand's own MongoConvertExpression guard, but kept as a cheap early-out. Nullable
    // relational comparisons don't decline here; they get a bracket via NeedsNumericTypeBracket.
    private static bool CanFallThroughToExpr(IProperty property)
        => NativeGroupByBinder.HasDefaultKeySerialization(property);

    // The Convert(_, Int32) that C#'s char-comparison lowering wraps around a MongoStringFirstOrLastExpression.
    private static bool TryUnwrapCharFirstOrLast(
        MongoExpression operand, [NotNullWhen(true)] out MongoStringFirstOrLastExpression? source)
    {
        source = operand is MongoConvertExpression { Type: var toType, Operand: MongoStringFirstOrLastExpression inner }
            && toType == typeof(int)
                ? inner
                : null;
        return source is not null;
    }

    // A char comparison's constant int code point as the one-character string RenderStringFirstOrLast always produces,
    // including '\0' (an Int32 zero would cross BSON type brackets against an embedded-NUL string).
    private static object CharCodeAsFirstOrLastValue(int code) => ((char)code).ToString();

    // Whether a numeric-cast comparison's $expr fall-through needs a MongoNumericTypeBracketExpression: every relational
    // comparison. {UnitPrice: {$lt: 100}} excludes null/missing (type bracketing), but $expr compares by BSON total
    // order, where null (and $toInt of a missing element) sorts below every number. All four operators get it so the
    // result is exact, not coincidentally correct for >/>=. Non-nullable properties too: a missing element violates the
    // model, but the query dialect (and so driver-LINQ) still excludes that row, and a projection or Count never
    // materializes the entity to reject it. Only removes non-numeric rows, so it can't change the CLR answer over
    // well-formed documents. Equality never needs it.
    private static bool NeedsNumericTypeBracket(ExpressionType comparisonNodeType)
        => IsRelationalComparison(comparisonNodeType);

    // Query-native member-vs-value arm of TranslateComparisonCore; see StoredOrdering. The field-to-field/$expr arm is
    // stricter (AllFieldsDefaultSerialized).
    private static bool ComparisonPreservesClrOrdering(ExpressionType nodeType, IProperty property)
        => !IsRelationalComparison(nodeType) || StoredOrdering.PreservesClrOrderingForNativeComparison(property);

    // The four type-bracketed operators. Equality is absent: $eq/$ne partition every value, including null/missing.
    private static bool IsRelationalComparison(ExpressionType nodeType)
        => nodeType is ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    // Serialization context for the constant side of a query-native comparison: the property's own serializer, or null
    // (BsonValue.Create in the comparison's CLR type). Identity-like converts (enum <-> underlying, char -> int, boxing)
    // keep the property's serializer, else an enum-as-string constant would render as a number and match nothing. A
    // widening cast uses the comparison's type (the stored type could truncate a fractional constant), but only when the
    // property is default-serialized; a converted property keeps its serializer or type bracketing returns zero rows.
    // See NativeCastTests.Widening_cast_comparison_over_a_value_converted_property_*.
    private static IProperty? ConstantSerializationContext(
        IProperty property, Type? toleratedWideningTarget, bool isIdentityLikeConvert)
        => isIdentityLikeConvert
            ? property
            : (toleratedWideningTarget is not null && NativeGroupByBinder.HasDefaultKeySerialization(property))
              || StoredOrdering.ConstantsSerializeByClrType(property)
                ? null
                : property;

    // `x.P.ToString() ==/!= value` where P is stored as the string its ToString() produces: a non-nullable property with
    // EF's built-in Guid/Number/EnumToStringConverter and no BsonRepresentation. A floating-point number is formatted by
    // the converter with the invariant culture where ToString() uses the current one; the stored form is what the driver
    // compared too, so the culture is not replicated (as for ordinal string comparisons).
    private MongoBinaryExpression? TryTranslateStoredToStringComparison(Expression call, Expression value, ExpressionType nodeType)
    {
        if (call is not MethodCallExpression { Method.Name: nameof(ToString), Object: { } receiver, Arguments.Count: 0 }
            || value.Type != typeof(string)
            || !IsSimpleValue(value)
            || !TryResolveMember(Unwrap(receiver), out var property, out var path, out var isOuter)
            || isOuter
            || property.ClrType.IsNullableValueType()
            || property.GetBsonRepresentation() != null)
        {
            return null;
        }

        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        if (converter is null || converter.ProviderClrType != typeof(string))
            return null;

        var converterType = converter.GetType();
        var isBuiltInToString = converterType == typeof(GuidToStringConverter)
            || (converterType.IsGenericType
                && converterType.GetGenericTypeDefinition() is var definition
                && (definition == typeof(NumberToStringConverter<>) || definition == typeof(EnumToStringConverter<>)));
        if (!isBuiltInToString)
            return null;

        var operand = TranslateValue(value, forSerialization: null);
        return operand is null
            ? null
            : new MongoBinaryExpression(MapComparisonOperator(nodeType)!.Value, new MongoFieldExpression(property, path), operand);
    }

    // A bare constant or query parameter (including a constant-index `args[0]` into a parameter array), or a closed
    // clock subtree evaluated per execution (RuntimeClock.IsRuntimeEvaluable, the predicate TranslateValue's
    // TryCreateRuntimeClockParameter admits by), not a member or arithmetic. Identifies the query-native
    // (member vs. value) shape.
    private static bool IsSimpleValue(Expression node)
        => node is ConstantExpression
           || NativeQueryParameter.TryGetQueryParameterName(node, out _)
           || NativeQueryParameter.TryGetParameterArrayElementIndex(node, out _, out _)
           || RuntimeClock.IsRuntimeEvaluable(node);

    /// <summary>int/long (or nullable): the integer types whose C# bitwise result matches the server's. short/byte promote to
    /// int through a Convert; uint/ulong are excluded (the server's results are signed).</summary>
    internal static bool IsBitwiseInteger(Type type)
        => type.UnwrapNullableType() == typeof(int) || type.UnwrapNullableType() == typeof(long);

    /// <summary>Maps an integer <c>&amp;</c>/<c>|</c>/<c>^</c> to its bitwise operator; kept apart from
    /// <see cref="MapArithmeticOperator"/>, which other binders share.</summary>
    internal static MongoBinaryOperator? MapBitwiseOperator(BinaryExpression node)
        => IsBitwiseInteger(node.Type) && IsBitwiseInteger(node.Left.Type) && IsBitwiseInteger(node.Right.Type)
            ? node.NodeType switch
            {
                ExpressionType.And => MongoBinaryOperator.BitAnd,
                ExpressionType.Or => MongoBinaryOperator.BitOr,
                ExpressionType.ExclusiveOr => MongoBinaryOperator.BitXor,
                _ => null
            }
            : null;

    internal static MongoBinaryOperator? MapComparisonOperator(ExpressionType nodeType)
        => nodeType switch
        {
            ExpressionType.Equal => MongoBinaryOperator.Equal,
            ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
            ExpressionType.LessThan => MongoBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
            ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
            _ => null
        };

    // Takes the whole node because division depends on the result type (integral maps to IntegerDivide). The renderer
    // can't decide this: widening converts on operands ((double)a / b) have already been unwrapped.
    internal static MongoBinaryOperator? MapArithmeticOperator(BinaryExpression node)
        => node.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => MongoBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => MongoBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => MongoBinaryOperator.Multiply,
            ExpressionType.Divide => IsIntegerType(node.Type)
                ? MongoBinaryOperator.IntegerDivide
                : MongoBinaryOperator.Divide,
            ExpressionType.Modulo => MongoBinaryOperator.Modulo,
            _ => null
        };

    // One operand of a field-to-field/arithmetic comparison ($expr) or a computed value. By default a numeric cast
    // becomes an explicit $toX, matching the driver; TryTranslateValue passes allowNumericWidening: MongoDB arithmetic
    // acts on the raw numeric value, so unwrapping a widening (never narrowing) conversion reproduces C#'s promotion.
    private MongoExpression? TranslateOperand(Expression node, bool allowNumericWidening = false)
    {
        // A maximal closed subtree holding a clock (`DateTime.Now.AddMonths(-1)`) is evaluated whole, in .NET, per
        // execution: exact C# semantics, and never baked into the cached template.
        if (TryCreateRuntimeClockParameter(node, forSerialization: null) is { } runtimeClock)
            return runtimeClock;

        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            var fromType = unary.Operand.Type.UnwrapNullableType();
            var toType = unary.Type.UnwrapNullableType();

            // Boxing to object never changes the value; unwrap it rather than decline (there's no $toX for object).
            if (fromType != toType && toType != typeof(object)
                && !(allowNumericWidening && IsWideningNumericConvert(fromType, toType)))
            {
                // A type-changing cast becomes $toX, as the driver emits; an unrenderable target declines.
                if (MongoConvertExpression.ToOperatorFor(toType) is null)
                    return null;

                var converted = TranslateOperand(unary.Operand, allowNumericWidening);
                if (converted is null)
                    return null;

                // $toX applies to the raw stored value, so a converted field underneath would convert the wrong
                // value.
                if (!AllFieldsDefaultSerialized(converted))
                    return null;

                // An enum field's cast to its underlying type relabels the stored integer (identity-like): stage the
                // field bare, as the driver does, so a MISSING field stays MISSING (driver-LINQ read default) rather
                // than $toInt's null. A computed enum (`(int)o.OrderDate.DayOfWeek`) keeps its $toInt.
                return converted is MongoFieldExpression or MongoOuterFieldExpression && IsEnumUnderlyingRelabel(fromType, toType)
                    ? converted
                    : new MongoConvertExpression(converted, toType);
            }

            return TranslateOperand(unary.Operand, allowNumericWidening);
        }

        if (node is ConditionalExpression conditional)
        {
            if (!TryTranslate(conditional.Test, out var test))
                return null;

            var ifTrue = TranslateConditionalBranch(conditional.IfTrue, allowNumericWidening);
            if (ifTrue is null)
                return null;

            var ifFalse = TranslateConditionalBranch(conditional.IfFalse, allowNumericWidening);
            if (ifFalse is null)
                return null;

            return new MongoConditionalExpression(test, ifTrue, ifFalse);
        }

        if (node is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            var left = TranslateOperand(coalesce.Left, allowNumericWidening);
            if (left is null)
                return null;

            var right = TranslateOperand(coalesce.Right, allowNumericWidening);
            if (right is null)
                return null;

            return new MongoCoalesceExpression(left, right);
        }

        if (TryTranslateDateTimeMember(node, out var dateTimeMember))
            return dateTimeMember;

        if (TryTranslateDateAdd(node, allowNumericWidening, out var dateAdd))
            return dateAdd;

        // These method-call/computed-member arms precede TryResolveMember, which never matches them. A char term
        // becomes a one-char string.
        if (TryMatchIndexOfMethod(node, out var indexOfReceiver, out var indexOfTerm, out var indexOfStart))
        {
            var haystack = TranslateOperand(indexOfReceiver, allowNumericWidening);
            var needle = haystack is null ? null
                : indexOfTerm.Type == typeof(char) ? TranslateCharAsString(indexOfTerm)
                : TranslateOperand(indexOfTerm, allowNumericWidening);
            MongoExpression? start = null;
            if (needle is not null && indexOfStart is not null)
            {
                start = TranslateOperand(indexOfStart, allowNumericWidening);
                if (start is null)
                    return null;
            }

            return haystack is not null && needle is not null
                ? new MongoStringIndexOfExpression(haystack, needle, start)
                : null;
        }

        if (TryMatchSubstring(node, out var substringReceiver, out var substringStart, out var substringLength))
        {
            var source = TranslateOperand(substringReceiver, allowNumericWidening);
            var start = source is null ? null : TranslateOperand(substringStart, allowNumericWidening);
            if (start is null)
                return null;

            MongoExpression? length = null;
            if (substringLength is not null && (length = TranslateOperand(substringLength, allowNumericWidening)) is null)
                return null;

            return new MongoSubstringExpression(source!, start, length);
        }

        if (TryMatchReplace(node, out var replaceReceiver, out var replaceFind, out var replaceWith))
        {
            MongoExpression? Operand(Expression e)
                => e.Type == typeof(char) ? TranslateCharAsString(e) : TranslateOperand(e, allowNumericWidening);

            var input = TranslateOperand(replaceReceiver, allowNumericWidening);
            var find = input is null ? null : Operand(replaceFind);
            var with = find is null ? null : Operand(replaceWith);
            return with is null ? null : new MongoReplaceExpression(input!, find!, with);
        }

        // string.Length has no backing IProperty, so TryResolveMember wouldn't match.
        if (TryMatchStringLength(node, out var lengthReceiver))
        {
            var lengthOperand = TranslateOperand(lengthReceiver, allowNumericWidening);
            return lengthOperand is null ? null : new MongoStringLengthExpression(lengthOperand);
        }

        if (TryTranslateMath(node, out var math))
            return math;

        if (node is MethodCallExpression trimCall && TryTranslateTrim(trimCall, out var trim))
            return trim;

        // The FirstOrDefault()/LastOrDefault() receiver may itself be computed (e.g. a Trim()).
        if (node is MethodCallExpression firstLastCall
            && TryMatchStringFirstOrLastMethod(firstLastCall, out var firstOrLastKind, out var firstOrLastReceiver))
        {
            var firstOrLastSource = TranslateOperand(firstOrLastReceiver, allowNumericWidening);
            return firstOrLastSource is null
                ? null
                : new MongoStringFirstOrLastExpression(firstOrLastSource, firstOrLastKind);
        }

        if (TryTranslateStringJoin(node, out var joinResult))
            return joinResult;

        // Integral x.ToString() → $toString (see TryMatchIntegralToString for the admissible receivers).
        if (TryMatchIntegralToString(node, out var toStringReceiver))
        {
            var toStringOperand = TranslateOperand(toStringReceiver, allowNumericWidening);
            return toStringOperand is null || !AllFieldsDefaultSerialized(toStringOperand)
                ? null
                : new MongoConvertExpression(toStringOperand, typeof(string));
        }

        if (TryTranslateStringConcatMethod(node, allowNumericWidening) is { } concatMethod)
            return concatMethod;

        if (TryResolveMember(node, out var property, out var fieldPath, out var operandIsOuter))
        {
            // Not confined to _innerPrefix is null (unlike the comparison and bare-bool arms): this operand path
            // never reaches the query dialect, so an outer field renders the same as a plain one. Asserted by
            // NativeSelectManyBinderTests.
            return operandIsOuter
                ? new MongoOuterFieldExpression(property, fieldPath!)
                : new MongoFieldExpression(property, fieldPath!);
        }

        // A flattened alias with no backing IProperty (a post-Distinct computed key part, or any output alias of a
        // keyed GroupBy.Select) resolves to its top-level output path, so every operator bottoming out here accepts
        // it. See TryResolveFlattenedAlias.
        if (TryResolveFlattenedAlias(node, out var aliasFieldRef))
            return aliasFieldRef;

        // Owned-collection element count (b.Posts.Count / .Count() / .LongCount()); the renderer picks the dialect.
        // Runs after TryResolveMember so a mapped scalar named "Count" wins. TryResolveOwnedCollectionPath matches scope
        // roots by reference only, so every non-root single-scope translator must be built after an identity guard
        // (ReferencesEnclosingScope / NativeSelectManyBinder.ReferencesParameter); otherwise a shared property name could
        // retarget an enclosing member to the inner scope (wrong rows). First arm: the same count over a primitive
        // collection property (string[] / List<int>), unfiltered only.
        if (TryMatchCountExpression(node, out var primitiveCountSource, out var primitiveCountPredicate)
            && primitiveCountPredicate is null
            && TryResolvePrimitiveCollectionPath(primitiveCountSource, out var primitiveCountPath, out var primitiveCountOuter)
            && !primitiveCountOuter)
        {
            return new MongoSizeExpression(primitiveCountPath, node.Type, nullSafe: true);
        }

        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType, out var countSourceIsOuter)
            && !countSourceIsOuter)
        {
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // Filtered count: the predicate translates like a quantifier's, with the same correlated guard.
            if (!TryCreateElementPredicateTranslator(countPredicate, countElementType, out var countElementTranslator, out _)
                || !countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
            {
                return null;
            }

            // Deliberately no renderability or AllFieldsDefaultSerialized gate: a translate-time decline sends a
            // projection leaf (Select(b => new { N = b.Posts.Count(pred) })) to the generic fall-through, which throws
            // InvalidOperationException in every mode including DriverLinq. Admitting it lets an unrenderable
            // predicate throw NativeTranslationNotSupportedException at render time, which TryBuildPipeline turns into
            // a driver-LINQ fallback. No alias hazard: a computed count leaf registers no alias override.
            return new MongoFilteredSizeExpression(arrayPath, elementPredicate, node.Type);
        }

        // Sum/Min/Max/Average of one element property over a referenced collection (binder-provided resolver).
        if (node is MethodCallExpression aggregateValueCall
            && ReferencedCollectionResolver is not null
            && TryMatchElementAggregateMethod(aggregateValueCall, out var aggregateOperator, out var aggregateSource, out var aggregateSelector))
        {
            return TranslateReferencedArrayAggregate(aggregateValueCall, aggregateOperator, aggregateSource, aggregateSelector);
        }

        // Any()/Any(pred)/All(pred) over a referenced collection as a projected VALUE. The predicate arm renders
        // $elemMatch (query dialect only), so a projection uses counts: Any() is size >= 1, Any(pred) is Count(pred) >= 1,
        // All(pred) is Count(not pred) == 0 (true for the empty group, as LINQ). Owned collections keep their behavior.
        if (node is MethodCallExpression { Method.Name: nameof(Enumerable.Any) or nameof(Enumerable.All) } quantifierValueCall
            && ReferencedCollectionResolver is not null
            && TryMatchQuantifierMethod(quantifierValueCall, out var valueQuantifier, out var valueQuantifierSource, out var valueQuantifierLambda)
            && TryResolveOwnedCollectionPath(valueQuantifierSource, out var valueQuantifierPath, out var valueQuantifierElementType, out var valueQuantifierOuter)
            && !valueQuantifierOuter)
        {
            if (valueQuantifierLambda is null)
            {
                return new MongoBinaryExpression(
                    MongoBinaryOperator.GreaterThanOrEqual,
                    new MongoSizeExpression(valueQuantifierPath, typeof(int), nullSafe: true),
                    new MongoConstantExpression(1, forSerialization: null));
            }

            if (!TryCreateElementPredicateTranslator(valueQuantifierLambda, valueQuantifierElementType, out var valueQuantifierTranslator, out _)
                || !valueQuantifierTranslator.TryTranslate(valueQuantifierLambda.Body, out var valueQuantifierPredicate))
            {
                return null;
            }

            var isAny = valueQuantifier == MongoQuantifierKind.Any;
            var counted = isAny
                ? valueQuantifierPredicate
                : new MongoUnaryExpression(MongoUnaryOperator.Not, valueQuantifierPredicate);

            // The aggregation dialect's boolean operators answer true/false for any operand, so Not is the exact complement.
            if (!MongoAggregationExpressionRenderer.CanRender(counted))
                return null;

            return new MongoBinaryExpression(
                isAny ? MongoBinaryOperator.GreaterThanOrEqual : MongoBinaryOperator.Equal,
                new MongoFilteredSizeExpression(valueQuantifierPath, counted, typeof(int)),
                new MongoConstantExpression(isAny ? 1 : 0, forSerialization: null));
        }

        // String + is concatenation, not arithmetic; $add rejects strings, so use $concat.
        if (node is BinaryExpression { NodeType: ExpressionType.Add } stringAdd && stringAdd.Type == typeof(string))
            return TranslateStringConcat(stringAdd, allowNumericWidening);

        if (node is BinaryExpression arith && MapArithmeticOperator(arith) is { } arithOp && IsNumericType(arith.Type))
        {
            var left = TranslateOperand(arith.Left, allowNumericWidening);
            if (left is null)
                return null;

            var right = TranslateOperand(arith.Right, allowNumericWidening);
            if (right is null)
                return null;

            return new MongoBinaryExpression(arithOp, left, right);
        }

        // Integer & | ^ ~ → $bitAnd/$bitOr/$bitXor/$bitNot, which null-propagate like the lifted C# operators. Ahead of
        // the generic bool Not/&/| hand-offs below, which assume a boolean predicate.
        if (node is BinaryExpression bitwise && MapBitwiseOperator(bitwise) is { } bitwiseOp)
        {
            var left = TranslateOperand(bitwise.Left, allowNumericWidening);
            if (left is null)
                return null;

            var right = TranslateOperand(bitwise.Right, allowNumericWidening);
            if (right is null)
                return null;

            return new MongoBinaryExpression(bitwiseOp, left, right);
        }

        if (node is UnaryExpression { NodeType: ExpressionType.Not } bitwiseNot && IsBitwiseInteger(bitwiseNot.Type))
        {
            var complemented = TranslateOperand(bitwiseNot.Operand, allowNumericWidening);
            return complemented is null
                ? null
                : new MongoUnaryExpression(MongoUnaryOperator.BitNot, complemented, bitwiseNot.Type);
        }

        // Unary minus → $subtract:[0, x], matching the driver's shape. Numeric only, so e.g. TimeSpan negation
        // still declines.
        if (node is UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate
            && IsNumericType(negate.Type))
        {
            var negatedOperand = TranslateOperand(negate.Operand, allowNumericWidening);
            if (negatedOperand is null)
                return null;

            return new MongoBinaryExpression(MongoBinaryOperator.Subtract, new MongoConstantExpression(0, forSerialization: null), negatedOperand);
        }

        // A predicate used as a value (OrderBy(x => !x.Flag), Select(x => x.Name.StartsWith("A"))) hands off to
        // TranslateNode; aggregation renderability is decided by CanRender.
        if (node is MethodCallExpression containsCall && TryMatchContainsMethod(containsCall, out _, out _))
            return TranslateNode(node);

        if (node is MethodCallExpression { Type: var predicateType } predicateCall && predicateType == typeof(bool)
            && (TryMatchRegexMethod(predicateCall, out _, out _, out _, out _)
                || predicateCall.Method.DeclaringType == typeof(string)
                || predicateCall.Method.DeclaringType == typeof(System.Text.RegularExpressions.Regex)))
            return TranslateNode(node);

        if (node is UnaryExpression { NodeType: ExpressionType.Not }
            or BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse })
            return TranslateNode(node);

        if (node is BinaryExpression bitwiseLogical && IsNonNullableBoolLogical(bitwiseLogical))
            return TranslateNode(node);

        // A comparison used as a value (`p.Discontinued == ((p.ProductID > 50) != prm)`).
        if (node is BinaryExpression comparisonOperand && IsComparison(comparisonOperand.NodeType))
            return TranslateNode(node);

        // A constructed anonymous-type/DTO value (`new { x = c.City } == new { x = "London" }`). Each member must be
        // default-serialized: AllFieldsDefaultSerialized's catch-all assumes MongoDocumentConstructionExpression members
        // already are.
        if (node.TryGetProjectionMembers(out var constructionMembers))
        {
            var translatedMembers = new List<(string, MongoExpression)>();
            foreach (var (memberName, memberValue) in constructionMembers)
            {
                var translated = TranslateOperand(memberValue, allowNumericWidening);
                if (translated is null || !AllFieldsDefaultSerialized(translated))
                    return null;

                translatedMembers.Add((memberName, translated));
            }

            return new MongoDocumentConstructionExpression(node, translatedMembers);
        }

        // Bare constants/parameters here are $expr operands, not stored values, so they use BsonValue.Create.
        return TranslateValue(node, forSerialization: null);
    }

    // Chains flatten into one $concat (for brevity only; nesting would also work).
    private MongoExpression? TranslateStringConcat(BinaryExpression node, bool allowNumericWidening)
        => TranslateConcatParts([node.Left, node.Right], allowNumericWidening);

    private MongoExpression? TranslateConcatParts(IEnumerable<Expression> parts, bool allowNumericWidening)
    {
        var operands = new List<MongoExpression>();
        foreach (var part in parts)
        {
            var operand = TranslateConcatOperand(part, allowNumericWidening);
            if (operand is null)
                return null;

            operands.AddRange(operand is MongoConcatExpression nested ? nested.Operands : [operand]);
        }

        return new MongoConcatExpression(operands);
    }

    // Non-string operands are wrapped in $toString (after stripping the boxing convert of C#'s string + object overload).
    // $toString diverges from .NET for bool ("true" vs "True") and dates (ISO-8601), but driver-LINQ renders identically,
    // so native introduces no new divergence.
    private MongoExpression? TranslateConcatOperand(Expression operand, bool allowNumericWidening)
    {
        // $toString over a stored DateTimeOffset renders its {DateTime, Ticks, Offset} sub-document as BSON JSON
        // (EF-217). Shared with the driver-LINQ bridge, which throws for the same operand.
        if (Visitors.MongoProjectionBindingExpressionVisitor.IsDateTimeOffsetValue(operand))
            return null;

        if (operand is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Type: var boxedType } boxing
            && boxedType == typeof(object))
            operand = boxing.Operand;

        if (operand is BinaryExpression { NodeType: ExpressionType.Add } nestedAdd && nestedAdd.Type == typeof(string))
            return TranslateStringConcat(nestedAdd, allowNumericWidening);

        var value = TranslateOperand(operand, allowNumericWidening);
        if (value is null)
            return null;

        // Decide from the original expression's CLR type: an untyped constant/parameter node reports object and
        // would get a needless $toString.
        return operand.Type == typeof(string) ? value : new MongoConvertExpression(value, typeof(string));
    }

    // A `(T?)null` branch compiles to Convert(Constant(null, object), T?), which TranslateOperand's Convert handling
    // declines. It's always null, so accept it here, only in conditional-branch position, rather than loosening the
    // shared Convert branch.
    private MongoExpression? TranslateConditionalBranch(Expression branch, bool allowNumericWidening)
    {
        if (branch is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            && unary.Operand is ConstantExpression { Value: null } nullConstant)
            return TranslateValue(nullConstant, forSerialization: null);

        return TranslateOperand(branch, allowNumericWidening);
    }

    // Numeric CLR types accepted by $add/$subtract/$multiply/$divide/$mod.
    internal static bool IsNumericType(Type type)
    {
        if (IsIntegerType(type))
            return true;

        var underlying = type.UnwrapNullableType();
        return underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal);
    }

    private static MongoExpression? TranslateValue(Expression node, IProperty? forSerialization)
    {
        if (node is ConstantExpression constant)
            return new MongoConstantExpression(constant.Value, forSerialization);

        if (NativeQueryParameter.TryGetQueryParameterName(node, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization, valueType: node.Type);

        if (NativeQueryParameter.TryGetParameterArrayElementIndex(node, out var arrayParameterName, out var index))
            return new MongoParameterExpression(arrayParameterName, forSerialization, arrayElementIndex: index, valueType: node.Type);

        return TryCreateRuntimeClockParameter(node, forSerialization);
    }

    private static int _runtimeParameterSeed;

    // Per-execution parameter (with a RuntimeEvaluator) for a closed subtree holding a clock member. Serialized like a
    // query parameter: through forSerialization's serializer when compared to a property (so
    // o.Created < DateTime.UtcNow.AddDays(-1) stays query-dialect), else BsonValue.Create. Both convert a Local DateTime
    // to its UTC instant, as driver-LINQ does.
    internal static MongoParameterExpression? TryCreateRuntimeClockParameter(Expression node, IProperty? forSerialization)
    {
        if (!RuntimeClock.IsRuntimeEvaluable(node))
            return null;

        var name = "__mongoef_runtime_" + Interlocked.Increment(ref _runtimeParameterSeed);
        return new MongoParameterExpression(
            name, forSerialization, valueType: node.Type, runtimeEvaluator: RuntimeClock.CompileEvaluator(node));
    }

    // Classifies the Convert layers on the member side of a `member <op> constant` comparison as widening numeric,
    // identity-like, or decline (returns true to decline). toleratedWideningTarget is the outermost absorbed widening
    // target; isIdentityLikeConvert is set only when no widening layer was absorbed (widening wins, e.g.
    // (object)(double)x.I), since the two demand opposite constant serialization (see ConstantSerializationContext).
    // Absorbing a widening cast matches the driver, which drops it on the query-dialect path. It isn't always
    // value-preserving (long/ulong -> double above 2^53), an accepted divergence pinned by
    // NativeCastTests.Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq.
    private static bool HasNumericConvert(
        Expression operand, Type propertyClrType, out Type? toleratedWideningTarget, out bool isIdentityLikeConvert)
    {
        toleratedWideningTarget = null;
        var sawIdentityLike = false;
        var underlying = propertyClrType.UnwrapNullableType();
        while (operand is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var to = u.Type.UnwrapNullableType();
            var from = u.Operand.Type.UnwrapNullableType();

            // Skip nullability-only layers (long -> long?): from == to would otherwise decline. EF lowers a lifted
            // uint -> long widening as `Convert(Convert(e.EmployeeID, Int64), Nullable<Int64>)`.
            if (from != to && to != underlying)
            {
                if (IsWideningNumericConvert(from, to) && MongoConvertExpression.ToOperatorFor(to) is not null)
                {
                    // Widening: record the outermost target, e.g. (double)(long)x.I compares in double.
                    toleratedWideningTarget ??= to;
                }
                else if (IsIdentityLikeConvert(from, to))
                {
                    // Identity-like: same stored value. Reported after the loop so a widening layer wins.
                    sawIdentityLike = true;
                }
                else
                {
                    toleratedWideningTarget = null;
                    isIdentityLikeConvert = false;
                    return true; // narrowing, signed/unsigned, or an unrenderable target
                }
            }

            operand = u.Operand;
        }

        // Widening wins over identity-like: ConstantSerializationContext checks isIdentityLikeConvert first, so
        // reporting both would drop the widening arm's truncation protection.
        isIdentityLikeConvert = sawIdentityLike && toleratedWideningTarget is null;
        return false;
    }

    // Identity-like converts leave the stored value unchanged (no $toX; the constant keeps the property's serializer).
    // Admitted: enum <-> its underlying type or an integral widening of it (C# promotes sub-int enums to int); enum ->
    // another enum with the same underlying type (each layer classified separately); char -> int; T -> object. Other
    // shapes (including char -> long) decline, which is conservative.
    private static bool IsIdentityLikeConvert(Type fromType, Type toType)
    {
        if (fromType.IsEnum && toType.IsEnum)
        {
            return Enum.GetUnderlyingType(fromType) == Enum.GetUnderlyingType(toType);
        }

        if (fromType.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(fromType);
            // Integral targets only: an int-backed enum cast to double would otherwise be admitted, and then
            // Enum.ToObject would throw on a non-integral constant.
            return toType == underlying || (IsIntegerType(toType) && IsWideningNumericConvert(underlying, toType));
        }

        if (toType.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(toType);
            return fromType == underlying || (IsIntegerType(fromType) && IsWideningNumericConvert(fromType, underlying));
        }

        return (fromType == typeof(char) && toType == typeof(int)) || toType == typeof(object);
    }

    // The IsIdentityLikeConvert case of an enum to its exact underlying type ((int)x.Status): a default-serialized enum
    // stores that integer, so the driver doesn't render $toX. Shared by the translator (stages the cast bare), the
    // projection read side (reads it like an identity cast) and the malformed-value classifier.
    internal static bool IsEnumUnderlyingRelabel(Type fromType, Type toType)
    {
        fromType = Nullable.GetUnderlyingType(fromType) ?? fromType;
        toType = Nullable.GetUnderlyingType(toType) ?? toType;
        return fromType.IsEnum && toType == Enum.GetUnderlyingType(fromType) && IsIdentityLikeConvert(fromType, toType);
    }

    // Whether an aggregation $not over this AndAlso/OrElse tree is its exact complement: every leaf is either a numeric
    // type bracket (same answer as $isNumber) or already aggregation-only, so no leaf changes dialect when wrapped.
    private static bool IsDialectInvariantForNegation(MongoExpression node)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } logical
                => IsDialectInvariantForNegation(logical.Left) && IsDialectInvariantForNegation(logical.Right),
            MongoNumericTypeBracketExpression => true,
            _ => !MongoQueryLanguageRenderer.IsQueryDialectRenderable(node)
                 && MongoAggregationExpressionRenderer.CanRender(node)
        };

    // Mirrors a relational operator when the member is on the right-hand side.
    internal static ExpressionType Mirror(ExpressionType nodeType)
        => nodeType switch
        {
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            _ => nodeType
        };

    private static bool IsComparison(ExpressionType t)
        => t is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
}
