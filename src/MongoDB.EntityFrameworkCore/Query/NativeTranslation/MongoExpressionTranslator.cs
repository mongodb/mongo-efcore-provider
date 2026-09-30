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
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;           // QueryableMethods
using MongoDB.EntityFrameworkCore.Extensions;        // GetDocumentPath(), IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates an EF Core predicate, key-selector, or value lambda body into a dialect-agnostic
/// <see cref="MongoExpression"/> tree for later rendering to a MongoDB filter, sort, or projection.
/// </summary>
/// <remarks>
/// Produces a compile-time template: captured constants become <see cref="MongoConstantExpression"/> nodes and
/// query parameters become <see cref="MongoParameterExpression"/> placeholders resolved per execution. Returns
/// <see langword="false"/> (never throws) for any shape outside its acceptance set so callers can fall back to
/// driver-LINQ.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    /// <summary>
    /// Creates a single-scope translator for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    /// <param name="selfParam">
    /// The root lambda parameter this translator was built for, or <see langword="null"/>. Used only to detect a
    /// single-level correlated owned-collection element predicate (<c>Count(pred)</c>/<c>Any</c>/<c>All</c>) whose
    /// free parameter is this one, so the call site can build a two-scope child translator instead of declining.
    /// </param>
    public MongoExpressionTranslator(IEntityType entityType, ParameterExpression? selfParam = null)
    {
        _entityType = entityType;
        SelfParam = selfParam;
    }

    /// <summary>
    /// Creates a two-scope translator for a correlated reference-<c>SelectMany</c> inner filter or a correlated
    /// owned-collection element predicate rendered via <c>$filter</c>. Members rooted on
    /// <paramref name="outerParam"/> (matched by reference identity, never by name) resolve against
    /// <paramref name="outerEntityType"/> at document root; all others resolve against
    /// <paramref name="innerEntityType"/>, prefixed with <paramref name="innerPrefix"/> when non-null. Pass
    /// <see langword="null"/>, not <c>""</c>, for a <c>$filter</c> element scope: <c>""</c> renders a leading-dot
    /// path (<c>"$$e..x"</c>), which MongoDB rejects.
    /// </summary>
    public MongoExpressionTranslator(
        IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string? innerPrefix)
    {
        _entityType = innerEntityType;
        _outerParam = outerParam;
        _outerEntityType = outerEntityType;
        _innerPrefix = innerPrefix;
    }

    /// <summary>
    /// The root lambda parameter of a single-scope translator (see the constructor). Settable because
    /// <see cref="NativeSlotPopulator.PopulateNativeSlots"/> builds the translator before the lambda is known.
    /// Always <see langword="null"/> on a two-scope translator, so correlation two or more scopes deep keeps declining.
    /// </summary>
    internal ParameterExpression? SelfParam { get; set; }

    /// <summary>
    /// When set, a single-hop member access on <see cref="SelfParam"/> resolves against these grouping key parts
    /// (a projected <c>Distinct()</c>'s output schema) instead of the entity type. Needed because a Distinct
    /// member name can coincide with an unrelated entity property (<c>Select(o => new { Country = o.City })</c>),
    /// and resolving by entity-property name would silently filter the wrong field. A name matching no key part
    /// declines rather than falling through to the entity.
    /// </summary>
    internal MongoGrouping? DistinctAliasScope { get; set; }

    /// <summary>
    /// When set, the lambda runs over a keyed <c>GroupBy(key).Select(...)</c>'s flattened output: a single-hop
    /// member on the scope's parameter resolves to the <c>Select</c>'s output alias of that name (a top-level
    /// <see cref="MongoElementRefExpression"/> typed from the projection's read), and the bare parameter to the sole
    /// <see cref="NativeProjectionBinder.SyntheticBareProjectionAlias"/> output of a bare <c>Select</c>. See
    /// <see cref="TryResolveFlattenedAlias"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DistinctAliasScope"/>, never consults key-part names: for a keyed GroupBy the output aliases
    /// are the <c>Select</c>'s member names, which can shadow a key part of the same name with a different value
    /// (<c>Select(g =&gt; new { Country = g.Count(), C = g.Key.Country })</c>). Nothing resolves against the entity
    /// either (<see cref="TryResolveMember"/> and the owned-path walk decline), so an unmatched name declines.
    /// Never set together with <see cref="DistinctAliasScope"/>.
    /// </remarks>
    internal MongoProjectedAliasScope? ProjectedAliasScope { get; set; }

    /// <summary>
    /// Whether <paramref name="node"/> is <see cref="SelfParam"/> standing for the root entity itself. Gates every arm
    /// that treats the parameter as the entity (<c>$$ROOT</c> null/self checks, <c>GetType()</c>/<c>is</c> folds,
    /// primary-key equality, entity-list <c>Contains</c>).
    /// </summary>
    /// <remarks>
    /// After a projected Distinct or a keyed GroupBy.Select, a lambda's parameter is a projected row or scalar alias,
    /// not the entity; folding <c>x.GetType() == typeof(Order)</c> over it would answer true for every grouped row.
    /// Structural (the parameter's CLR type must be the entity type or a subtype), plus the scope flag as a second
    /// line. A subtype isn't TPH-safe on its own: that comes from each site's own hierarchy decline (the
    /// <c>GetType()</c>/<c>is</c> folds decline any entity with a base or derived type; key equality and
    /// <c>Contains</c> require the other side's type to be exactly the entity's).
    /// </remarks>
    private bool IsSelfParamTheEntity(Expression node)
        => SelfParam is not null
           && ReferenceEquals(node, SelfParam)
           && ProjectedAliasScope is null
           && _entityType.ClrType.IsAssignableFrom(SelfParam.Type);

    /// <summary>
    /// Attempts to translate a predicate or key-selector body. Returns <see langword="false"/> if the shape is not
    /// natively representable; the caller should fall back to driver-LINQ.
    /// </summary>
    public bool TryTranslate(Expression efBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = TranslateNode(Unwrap(efBody));
        return result is not null;
    }

    /// <summary>
    /// Attempts to translate an <c>OrderBy</c>/<c>ThenBy</c> key-selector body to a sort-key field reference.
    /// Unlike <see cref="TryTranslate"/>, accepts any mapped scalar property, not just booleans.
    /// </summary>
    public bool TryTranslateField(Expression keySelectorBody, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path, out var isOuter)
            || isOuter) // Sort keys are never inside a $filter/$map scope; guards against a future two-scope caller.
            return false;

        result = new MongoFieldExpression(property, path);
        return true;
    }

    /// <summary>
    /// Attempts to translate a numeric value expression (projection/computed leaf): a field ref, constant/parameter,
    /// or <c>+ - * / %</c> arithmetic over numeric operands. Declines for non-numeric shapes and for operands that
    /// aren't default-serialized (a converted stored form would diverge from CLR arithmetic). Integer division
    /// renders with C#'s truncating semantics (<see cref="MongoBinaryOperator.IntegerDivide"/>).
    /// </summary>
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

    /// <summary>
    /// Translates a computed string receiver for <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>, or returns
    /// <see langword="null"/>. The result is never a bare field or element ref (those resolve earlier), so the
    /// regex it feeds always renders via <c>$expr</c>/<c>$regexMatch</c> (see
    /// <see cref="MongoQueryLanguageRenderer.IsQueryDialectRegex"/>). Pure: nothing is recorded on a decline.
    /// </summary>
    private MongoExpression? TryTranslateComputedRegexReceiver(Expression receiver)
        => TryTranslateValue(receiver, out var computed)
           && computed.Type == typeof(string)
           && MongoAggregationExpressionRenderer.CanRender(computed)
            ? computed
            : null;

    /// <summary>
    /// Resolves a <c>DateTime</c>/<c>DateTimeOffset</c> member-access chain (<c>.Year</c>, <c>.Date</c>,
    /// <c>.DateTime</c>, <c>.UtcDateTime</c>, ...) to a native date expression, recursing into the receiver so
    /// multi-hop chains such as <c>x.Dto.Value.DateTime.Date</c> compose.
    /// </summary>
    /// <remarks>
    /// The innermost receiver must be a plain stored field; a computed receiver has no MQL sub-field form for the
    /// <c>DateTimeOffset</c> reconstruction case, so it declines.
    /// </remarks>
    private bool TryTranslateDateTimeMember(Expression node, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (node is not MemberExpression { Expression: { } receiver } member)
            return false;

        var receiverType = Nullable.GetUnderlyingType(receiver.Type) ?? receiver.Type;
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

        result = new MongoDatePartExpression(receiverExpr, plainPart);
        return true;
    }

    /// <summary>
    /// Resolves a rooted member chain ending in an embedded collection navigation to an element reference for the
    /// array itself (<c>b.Home.Notes</c> → <c>Home.Notes</c>), for use as a native projection leaf.
    /// </summary>
    /// <remarks>
    /// Inherits <see cref="TryResolveOwnedCollectionPath"/>'s guards: the final hop must be a collection
    /// navigation (so a same-named scalar can't match) and two-scope mode declines. Safe from by-name retargeting
    /// because <see cref="NativeProjectionBinder"/> builds this translator on the query root.
    /// </remarks>
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

    /// <summary>
    /// Resolves a <c>DateTime</c>/<c>DateTimeOffset</c> <c>.AddXxx(amount)</c> call to a
    /// <see cref="MongoDateAddExpression"/>.
    /// </summary>
    /// <remarks>
    /// Only the overloads in <see cref="DateAddUnitsByMethodName"/> map onto a <c>$dateAdd</c> unit;
    /// <c>AddTicks</c> and <c>Add(TimeSpan)</c> fall through to driver-LINQ.
    /// </remarks>
    private bool TryTranslateDateAdd(Expression node, bool allowNumericWidening, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is not MethodCallExpression { Object: { } receiver } call
            || call.Arguments.Count != 1
            || (call.Method.DeclaringType != typeof(DateTime) && call.Method.DeclaringType != typeof(DateTimeOffset))
            || !DateAddUnitsByMethodName.TryGetValue(call.Method.Name, out var unit))
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

        var amount = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (amount is null)
            return false;

        result = new MongoDateAddExpression(startDate, unit, amount);
        return true;
    }

    /// <summary>
    /// Evaluates a closed subtree (no lambda parameter, EF query parameter, or other extension node) to a
    /// <see cref="MongoConstantExpression"/>. Covers literals EF's parameter extraction leaves un-folded because a
    /// sibling operand references the query source. Declines for anything that can't be compiled in isolation.
    /// </summary>
    private static bool TryEvaluateClosedSubtree(Expression node, out MongoExpression? result)
    {
        result = null;

        if (ContainsParameterOrExtensionNode(node))
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

        // Relabel (not convert) a non-Utc DateTime as Utc. Otherwise BsonValue.Create calls ToUniversalTime,
        // which depends on the host time zone and can skew the rendered $date (e.g. Europe/Dublin's pre-1916
        // -00:25 LMT offset), diverging from the tick value the in-memory comparison uses.
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

    /// <summary>
    /// Whether <paramref name="call"/> is an <c>.AddXxx(amount)</c> call <see cref="TryTranslateDateAdd"/> can
    /// translate. Shared with <see cref="Visitors.MongoProjectionBindingExpressionVisitor"/> so the projection
    /// leaf registration and this translator gate on the same method set.
    /// </summary>
    public static bool IsDateAddMethod(MethodCallExpression call)
        => (call.Method.DeclaringType == typeof(DateTime) || call.Method.DeclaringType == typeof(DateTimeOffset))
            && call.Arguments.Count == 1
            && DateAddUnitsByMethodName.ContainsKey(call.Method.Name);

    // The AddXxx overloads that map 1:1 onto a $dateAdd unit (see MongoDateAddUnit for why AddTicks/Add(TimeSpan)
    // are absent). DateTime and DateTimeOffset declare identically-named members.
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

    // Mirrors MongoEFToLinqTranslatingExpressionVisitor.DateTimeOffsetComponentMembers minus TimeOfDay (see
    // MongoDatePart); also applies to plain DateTime receivers.
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
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
    }

    /// <summary>
    /// Matches a bare document-root field reference of either sibling kind (<see cref="MongoFieldExpression"/> or
    /// the outer-scoped <see cref="MongoOuterFieldExpression"/>) and yields its <see cref="IProperty"/>. Use this
    /// rather than <c>is MongoFieldExpression</c> so bare-value checks never miss the outer-scoped kind.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="node"/> is a bare stored field whose value conversion or non-default
    /// <c>BsonRepresentation</c> makes a truthiness test unsafe in a position MongoDB evaluates by truthiness
    /// (<c>$not</c>, an <c>$and</c>/<c>$or</c> operand, a bare boolean predicate root).
    /// </summary>
    /// <remarks>
    /// Only <c>false</c>/<c>null</c>/<c>0</c>/<c>undefined</c> are falsy in MQL, so a
    /// <c>HasConversion&lt;string&gt;()</c> bool stored as <c>"True"</c>/<c>"False"</c> is always truthy and
    /// answers the wrong boolean. Callers decline at
    /// classify time or throw at render time; <paramref name="property"/> is reported for the error message. The
    /// single shared definition for all classify/throw sites in both renderers.
    /// </remarks>
    internal static bool IsUnsafeTruthinessRoot(MongoExpression node, [NotNullWhen(true)] out IProperty? property)
        => TryGetBareFieldProperty(node, out property) && !AllFieldsDefaultSerialized(node);

    /// <summary>
    /// Whether every field reachable from <paramref name="expr"/> uses its property's default BSON serialization
    /// (no value converter, no non-default <c>BsonRepresentation</c>).
    /// </summary>
    /// <remarks>
    /// The shared correctness predicate behind the truthiness and raw-operator guards in
    /// <see cref="MongoAggregationExpressionRenderer"/> and <see cref="MongoQueryLanguageRenderer"/> (see
    /// <see cref="IsUnsafeTruthinessRoot"/>). Don't call it to reject at translate time in
    /// <c>TranslateOperand</c>'s filtered-count arm: a translate-time decline there surfaces as an
    /// <see cref="InvalidOperationException"/> from <c>NativeProjectionBinder</c> in every mode, whereas the
    /// render-time <c>NativeTranslationNotSupportedException</c> lets <c>TryBuildPipeline</c> fall back.
    /// </remarks>
    internal static bool AllFieldsDefaultSerialized(MongoExpression expr)
        => expr switch
        {
            MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
            MongoOuterFieldExpression outerField => NativeGroupByBinder.HasDefaultKeySerialization(outerField.Property),
            MongoBinaryExpression b => AllFieldsDefaultSerialized(b.Left) && AllFieldsDefaultSerialized(b.Right),
            MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand),
            MongoConcatExpression concat => concat.Operands.All(AllFieldsDefaultSerialized),
            // Raw-field $not is truthiness-based; see remarks.
            MongoUnaryExpression u => AllFieldsDefaultSerialized(u.Operand),
            MongoConditionalExpression cond => AllFieldsDefaultSerialized(cond.Test)
                && AllFieldsDefaultSerialized(cond.IfTrue)
                && AllFieldsDefaultSerialized(cond.IfFalse),
            MongoCoalesceExpression coalesce
                => AllFieldsDefaultSerialized(coalesce.Left) && AllFieldsDefaultSerialized(coalesce.Right),
            // Date operators run against the operand's raw BSON; a converted field would be a server type error or
            // wrong values.
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
            // No arm needed (catch-all): MongoInExpression serializes candidates through the field's own serializer,
            // so it compares like-for-like; MongoSizeExpression has no property serialization; and
            // MongoArrayContainsExpression's Value is pre-rendered via the element serializer, with whole-collection
            // converters declined at translate time.
            _ => true
        };

    // Strips redundant Convert/ConvertChecked wrappers (EF sometimes adds a nullable-widening convert). Not
    // interchangeable with UnwrapOrderPreserving; see its remarks.
    internal static Expression Unwrap(Expression e)
        => e.RemoveConvert();

    /// <summary>
    /// Strips only the order-preserving <see cref="ExpressionType.Convert"/> layers, for a sort key.
    /// </summary>
    /// <remarks>
    /// <see cref="Unwrap"/> would silently discard a narrowing or signed/unsigned cast and <c>$sort</c> by the raw
    /// stored value. This unwraps only nullable ↔ underlying, boxing, and widening numeric conversions (not always
    /// value-preserving, but monotone). Not sufficient where value equality matters, e.g. a <c>GroupBy</c> key.
    /// Anything else stays so <see cref="TryResolveMember"/> declines. See <c>NativeCastTests</c>.
    /// </remarks>
    private static Expression UnwrapOrderPreserving(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var fromType = Nullable.GetUnderlyingType(u.Operand.Type) ?? u.Operand.Type;
            var toType = Nullable.GetUnderlyingType(u.Type) ?? u.Type;

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
        switch (node)
        {
            // --- Logical binary operators ---

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

            // --- Comparison binary operators ---

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

            // --- string.Equals with an explicit StringComparison; see MongoExpressionTranslator.StringEquals.cs ---
            case MethodCallExpression stringEqualsCall
                when TryTranslateStringEqualsWithComparison(stringEqualsCall, out var stringEquals):
                return stringEquals;

            // --- Instance Equals(T): e.Field.Equals(value) ≡ e.Field == value ---
            //
            // Equals(object) is excluded: the boxed argument would be stripped by Unwrap, turning a mismatched-type
            // call such as ((int)21).Equals((object)(ulong)21) (false in C#) into a $eq MongoDB matches by numeric
            // value. The mismatched-type case is folded to false below.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } equalsCall
                when equalsCall.Method.GetParameters()[0].ParameterType != typeof(object):
                return TranslateComparisonCore(equalsCall.Object!, equalsCall.Arguments[0], ExpressionType.Equal);

            // --- Nullable-receiver Equals(object): `c.NullableAge.Equals(21)` ---
            //
            // Nullable<T> only has Equals(object). Peel one boxing layer and require matching nullable-stripped
            // types; a mismatch falls through.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } nullableEqualsCall
                when (Nullable.GetUnderlyingType(nullableEqualsCall.Object!.Type) ?? nullableEqualsCall.Object.Type)
                     == (Nullable.GetUnderlyingType(nullableEqualsCall.Arguments[0].RemoveObjectConvert().Type)
                         ?? nullableEqualsCall.Arguments[0].RemoveObjectConvert().Type):
                return TranslateComparisonCore(
                    nullableEqualsCall.Object!, nullableEqualsCall.Arguments[0].RemoveObjectConvert(), ExpressionType.Equal);

            // --- Instance Equals(object) across mismatched types: `nullableLongPrm.Equals(e.ReportsTo)` ---
            //
            // Always false in C#; folds to a constant, mirroring the driver-LINQ bridge
            // (ExpressionExtensionMethods.IsAlwaysFalseAcrossTypeMismatch). Scoped to exact-equality primitives since
            // an arbitrary Equals(object) override may legitimately compare across types.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1
                } mismatchedEqualsCall
                when ExpressionExtensionMethods.IsAlwaysFalseAcrossTypeMismatch(
                    mismatchedEqualsCall.Object!, mismatchedEqualsCall.Arguments[0]):
                return new MongoConstantExpression(false, forSerialization: null);

            // --- Static object.Equals(a, b) ---
            //
            // Both arguments are always boxed. Peel exactly one Convert-to-object layer each (RemoveObjectConvert,
            // unlike Unwrap, leaves widenings intact) and require matching types; a mismatched exact-equality pair
            // folds to false, anything else declines. Mirrors the driver-LINQ bridge's static-Equals case.
            case MethodCallExpression
                {
                    Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2
                } staticEqualsCall:
            {
                var leftArg = staticEqualsCall.Arguments[0].RemoveObjectConvert();
                var rightArg = staticEqualsCall.Arguments[1].RemoveObjectConvert();
                if (leftArg.Type != rightArg.Type)
                {
                    return ExpressionExtensionMethods.AreMismatchedExactEqualityTypes(
                        Nullable.GetUnderlyingType(leftArg.Type) ?? leftArg.Type,
                        Nullable.GetUnderlyingType(rightArg.Type) ?? rightArg.Type)
                        ? new MongoConstantExpression(false, forSerialization: null)
                        : null;
                }

                return TranslateComparisonCore(leftArg, rightArg, ExpressionType.Equal);
            }

            // --- Negation of a boolean field ---

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
                    return null; // conservative: nullable bool Not could diverge from driver rendering
                return new MongoUnaryExpression(MongoUnaryOperator.Not, operand);
            }

            // --- Boolean ternary as a predicate root: test ? ifTrue : ifFalse ---
            //
            // Branches recurse through TranslateNode (they're predicates, not values). A branch that is a bare
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

            // --- Array field contains value: arrayField.Contains(constant) → { field: value } ---
            //
            // The mirror of the $in arm below: here the receiver is a stored array field. MongoDB's implicit
            // element match gives the semantics; see MongoArrayContainsExpression for why it isn't a
            // MongoBinaryOperator.Equal (which would mean whole-array equality in the aggregation dialect).
            // Constant items only: a parameterized item would need a placeholder resolved through the element
            // serializer, so it falls through to the $in arm and declines there.
            case MethodCallExpression call
                when TryMatchContainsMethod(call, out var arrayReceiver, out var containsItem)
                     && Unwrap(containsItem) is ConstantExpression
                     && TryResolveMember(Unwrap(arrayReceiver), out var arrayProperty, out var arrayFieldPath, out var arrayIsOuter)
                     && !arrayIsOuter // outer-scoped array receivers aren't supported
                     && GetEnumerableElementType(arrayProperty.ClrType) is not null:
            {
                var itemNode = TranslateArrayContainsItem(Unwrap(containsItem), arrayProperty);
                if (itemNode is null)
                    return null; // e.g. a value-converted array, or a CLR/element type mismatch — decline.

                var arrayFieldExpr = new MongoFieldExpression(arrayProperty, arrayFieldPath);
                return new MongoArrayContainsExpression(arrayFieldExpr, itemNode, negated: false);
            }

            // --- Entity-list membership: customers.Contains(c) ---
            // Must precede the general membership arm, which requires a member-access item. See
            // MongoExpressionTranslator.EntityEquality.cs.
            case MethodCallExpression entityContainsCall when TryTranslateEntityListContains(entityContainsCall, out var entityListContains):
                return entityListContains;

            // --- Collection membership: Enumerable.Contains / List<T>.Contains / ICollection<T>.Contains ---

            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath, out var itemIsOuter))
                {
                    if (itemIsOuter) // outer-scoped items aren't supported
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
                // MongoAggregationExpressionRenderer.RenderInValues); a constant haystack would go through BsonValue.Create,
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

            // --- string.IsNullOrEmpty(s) / IsNullOrWhiteSpace(s): translate the equivalent `s == null || s == ""`
            // (`s.Trim() == ""` for whitespace) so the existing null/missing semantics apply.
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

            // --- String prefix/suffix/substring: string.StartsWith/EndsWith/Contains(string) ---

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

                    if (receiverProperty.ClrType != typeof(string))
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
                    return null; // receiver must be a bare string field, a computed Distinct alias, or a computed string
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
                        || !TryResolveMember(Unwrap(termExpr), out var termProperty, out var termFieldPath, out var termIsOuter)
                        || termIsOuter
                        || termProperty.ClrType != typeof(string))
                    {
                        return null;
                    }

                    termNode = new MongoFieldExpression(termProperty, termFieldPath);

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

            // --- EF.Functions.Like(matchExpression, pattern) ---

            case MethodCallExpression likeCall when TryTranslateLike(likeCall, out var likeResult):
                return likeResult;

            // --- Regex.IsMatch(constantInput, fieldPattern) — reversed-argument shape ---

            case MethodCallExpression regexIsMatchCall when TryTranslateRegexIsMatch(regexIsMatchCall, out var regexIsMatchResult):
                return regexIsMatchResult;

            // --- Quantifiers over an owned (embedded) collection: source.Any() / Any(pred) / All(pred) ---

            case MethodCallExpression call
                when TryMatchQuantifierMethod(call, out var quantifier, out var quantifierSource, out var elementLambda):
            {
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType, out var sourceIsOuter))
                    return null; // not an owned-collection source rooted at the query parameter

                if (sourceIsOuter)
                    return null; // a quantifier over an outer-scoped collection isn't supported

                if (elementLambda is null)
                {
                    // A bare Any() is Count >= 1, rendered as the array-index existence form. Keeping one
                    // representation for cardinality lets !Any() fall out of the negator's inversion.
                    return new MongoBinaryExpression(
                        MongoBinaryOperator.GreaterThanOrEqual,
                        new MongoSizeExpression(arrayPath, typeof(int), nullSafe: true),
                        new MongoConstantExpression(1, forSerialization: null));
                }

                // A correlated element predicate (references the enclosing entity). $elemMatch can't reference the
                // enclosing document, so when the free parameter is this translator's own SelfParam (by reference),
                // build a two-scope child and emit a MongoQuantifierExpression ($anyElementTrue/$allElementsTrue
                // over $map). Correlation reaching further out declines.
                if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0], out var quantifierFreeParam))
                {
                    if (SelfParam is null || !ReferenceEquals(quantifierFreeParam, SelfParam))
                        return null;

                    var correlatedElementTranslator = new MongoExpressionTranslator(
                        elementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: null);
                    if (!correlatedElementTranslator.TryTranslate(elementLambda.Body, out var correlatedPredicate))
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
                var elementTranslator = new MongoExpressionTranslator(elementType);
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

            // --- Nullable<T>.HasValue ---
            //
            // Built as `x.A != null`, so the renderer and negator need nothing new; $ne selects null and missing,
            // matching LINQ. Must precede the bare-boolean-member default, which would fail to resolve "HasValue".
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath, out var hasValueIsOuter)
                    || hasValueIsOuter) // outer-scoped HasValue isn't supported
                    return null;

                return new MongoBinaryExpression(
                    MongoBinaryOperator.NotEqual,
                    new MongoFieldExpression(nullableProperty, nullablePath),
                    new MongoConstantExpression(null, nullableProperty));
            }

            // --- Literal boolean predicate root (e.g. a constant-folded `All(x => true)`) ---
            //
            // Renders via RenderNode's catch-all as `{ $expr: true }`/`{ $expr: false }`. Must precede the
            // bare-boolean-member default.
            case ConstantExpression { Value: bool literalBool }:
                return new MongoConstantExpression(literalBool, forSerialization: null);

            // --- Bare boolean query-parameter predicate root ---
            //
            // EF hoists a predicate that doesn't reference the query source (`c => data.Contains(v + "x")`) into a
            // single bool parameter before translation. Renders as `{ $expr: <placeholder> }`.
            case var parameterNode when node.Type == typeof(bool) && NativeQueryParameter.TryGetQueryParameterName(parameterNode, out var boolParamName):
                return new MongoParameterExpression(boolParamName, forSerialization: null);

            // --- Bare boolean member access (c.Active) ---

            default:
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

    /// <summary>
    /// Translates a comparison <see cref="BinaryExpression"/> into a <see cref="MongoBinaryExpression"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <b>Query-native:</b> a bare member on one side, a constant/parameter on the other. The field is always put
    /// on <see cref="MongoBinaryExpression.Left"/> (mirroring the operator) so the renderer keeps routing it to the
    /// indexable <c>$match</c> dialect.
    /// </item>
    /// <item>
    /// <b>Field-to-field / arithmetic (<c>$expr</c>):</b> anything else, via <see cref="TranslateOperand"/>,
    /// preserving operand order for non-commutative comparisons.
    /// </item>
    /// </list>
    /// <para>
    /// A cast the query-native branch can't absorb (<see cref="HasNumericConvert"/>) falls through to <c>$expr</c>
    /// as an explicit <see cref="MongoConvertExpression"/>, gated by <see cref="CanFallThroughToExpr"/>. A
    /// relational comparison there also gets a <see cref="MongoNumericTypeBracketExpression"/>
    /// conjunct (<see cref="NeedsNumericTypeBracket"/>), since bare <c>$expr</c> doesn't type-bracket null/missing
    /// the way the query dialect does.
    /// </para>
    /// <para>
    /// By design, a narrowing cast against a constant (<c>(int)x.D &gt; 0</c>) returns the CLR answer, which differs
    /// from driver-LINQ (it drops the cast). Pinned by <c>NativeCastTests</c>.
    /// </para>
    /// </remarks>
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
        => TranslateComparisonCore(be.Left, be.Right, be.NodeType);

    /// <summary>
    /// Recognizes <c>predicate == literalBool</c> / <c>!=</c> (e.g.
    /// <c>c.CustomerID.EndsWith("KI") == ((bool?)true)</c>) where the predicate side isn't a stored member,
    /// translating it as the predicate or its negation.
    /// </summary>
    /// <remarks>
    /// Declines when the non-literal side resolves via <see cref="TryResolveMember"/> (the query-native comparison
    /// owns that) and for nullable-bool predicates, whose three-valued semantics aren't preserved.
    /// </remarks>
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
            return false; // TranslateComparisonCore's query-native branch owns this shape

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

    /// <summary>
    /// The shared core of <see cref="TranslateComparison"/>, taking operands directly so an instance
    /// <c>Equals(T)</c> call can reuse it (its argument carries at most a non-boxing widening, as with <c>==</c>).
    /// </summary>
    private MongoBinaryExpression? TranslateComparisonCore(Expression left, Expression right, ExpressionType nodeType)
    {
        var leftUnwrapped = Unwrap(left);
        var rightUnwrapped = Unwrap(right);

        // --- Entity-typed operand vs literal null (`c == null`, owned `b.Address == null`) ---
        // Only a literal null: a captured entity instance has no BsonValue mapping and must decline to driver-LINQ.
        // Renders as "$$ROOT" or the sub-document path. Both sides are resolved unconditionally so both out locals
        // are assigned.
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

        // --- Entity_equality_self (`c == c`): both sides are SelfParam (by reference), so trivially true/false.
        // Comparing two different entity-typed operands is not admitted.
        if (IsSelfParamTheEntity(leftUnwrapped) && IsSelfParamTheEntity(rightUnwrapped))
        {
            var selfOp = MapComparisonOperator(nodeType);
            if (selfOp is null)
                return null;

            var rootRef = new MongoElementRefExpression(MongoElementRefExpression.WholeRootDocumentPath, _entityType.ClrType);
            return new MongoBinaryExpression(selfOp.Value, rootRef, rootRef);
        }

        // A bare flattened alias (`.Select(g => g.Sum(o => o.OrderID)).All(v => v >= 0)`) and a named one
        // (`x.Total > 100`) need no arm here: under ProjectedAliasScope TryResolveMember declines, so both fall
        // through to the $expr path, where TranslateOperand resolves them via TryResolveFlattenedAlias.

        // --- Query-native shape: member on exactly one side, value on the other ---

        // Set when a numeric-cast relational comparison falls through to $expr and needs
        // a type-bracket conjunct (see CanFallThroughToExpr). Not built for outer-scoped fields, which decline.
        MongoFieldExpression? numericTypeBracket = null;

        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath, out var leftIsOuter)
            && IsSimpleValue(rightUnwrapped))
        {
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

        // --- Field-to-field / arithmetic-operand shape: always routes to $expr ---

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

    /// <summary>
    /// Whether a comparison whose cast the query-native branch couldn't absorb may fall through to <c>$expr</c>
    /// (otherwise the whole comparison declines).
    /// </summary>
    /// <remarks>
    /// Requires default serialization. Subsumed by <see cref="TranslateOperand"/>'s own
    /// <see cref="MongoConvertExpression"/> guard, but kept as a cheap early-out. Nullable relational comparisons
    /// don't decline here; they get a bracket via <see cref="NeedsNumericTypeBracket"/>.
    /// </remarks>
    private static bool CanFallThroughToExpr(IProperty property)
        => NativeGroupByBinder.HasDefaultKeySerialization(property);

    /// <summary>
    /// Recognizes a <see cref="MongoStringFirstOrLastExpression"/> wrapped in the <c>Convert(_, Int32)</c> that C#'s
    /// char-comparison lowering adds. See <see cref="TranslateComparisonCore"/>.
    /// </summary>
    private static bool TryUnwrapCharFirstOrLast(
        MongoExpression operand, [NotNullWhen(true)] out MongoStringFirstOrLastExpression? source)
    {
        source = operand is MongoConvertExpression { Type: var toType, Operand: MongoStringFirstOrLastExpression inner }
            && toType == typeof(int)
                ? inner
                : null;
        return source is not null;
    }

    /// <summary>
    /// Re-expresses a char comparison's constant int code point as the one-character string that
    /// <c>RenderStringFirstOrLast</c> always produces, including for <c>'\0'</c> (an Int32 zero would cross BSON
    /// type brackets against an embedded-NUL string). See <see cref="MongoStringFirstOrLastExpression"/>.
    /// </summary>
    private static object CharCodeAsFirstOrLastValue(int code) => ((char)code).ToString();

    /// <summary>
    /// Whether a numeric-cast comparison's <c>$expr</c> fall-through needs a
    /// <see cref="MongoNumericTypeBracketExpression"/> over the compared field to match the query dialect.
    /// </summary>
    /// <remarks>
    /// Every relational comparison. <c>{UnitPrice: {$lt: 100}}</c> excludes null and missing (type bracketing),
    /// but <c>$expr</c> compares by BSON total order, where null (and <c>$toInt</c> of a missing element) sorts
    /// below every number. All four relational operators get the bracket so the result is exact, not
    /// coincidentally correct for <c>&gt;</c>/<c>&gt;=</c>. Non-nullable properties get it too: a missing element
    /// there violates the model, but the query dialect (and so driver-LINQ) still excludes that row, and a
    /// projection or <c>Count</c> over it never materializes the entity to reject it. The bracket only removes
    /// non-numeric rows, so it can't change the CLR answer over well-formed documents. Equality never needs it.
    /// </remarks>
    private static bool NeedsNumericTypeBracket(ExpressionType comparisonNodeType)
        => IsRelationalComparison(comparisonNodeType);

    // The four type-bracketed operators. Equality is absent: $eq/$ne partition every value, including null/missing.
    private static bool IsRelationalComparison(ExpressionType nodeType)
        => nodeType is ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    /// <summary>
    /// Chooses the serialization context for the constant side of a query-native comparison: the property's own
    /// serializer, or <see langword="null"/> (<c>BsonValue.Create</c> in the comparison's CLR type).
    /// </summary>
    /// <remarks>
    /// Identity-like converts (enum ↔ underlying, <c>char</c> → <c>int</c>, boxing) keep the property's serializer:
    /// otherwise an enum-as-string constant would render as a number and match nothing. A widening cast uses the
    /// comparison's type (serializing through the stored type could truncate a fractional constant), but only
    /// when the property is default-serialized; a converted property keeps its serializer or type bracketing
    /// returns zero rows. See <c>NativeCastTests.Widening_cast_comparison_over_a_value_converted_property_*</c>.
    /// </remarks>
    private static IProperty? ConstantSerializationContext(
        IProperty property, Type? toleratedWideningTarget, bool isIdentityLikeConvert)
        => isIdentityLikeConvert
            ? property
            : toleratedWideningTarget is not null && NativeGroupByBinder.HasDefaultKeySerialization(property)
                ? null
                : property;

    // A bare constant or query parameter (including a constant-index `args[0]` into a parameter array), not a member
    // or arithmetic. Identifies the query-native (member vs. value) shape.
    private static bool IsSimpleValue(Expression node)
        => node is ConstantExpression
           || NativeQueryParameter.TryGetQueryParameterName(node, out _)
           || NativeQueryParameter.TryGetParameterArrayElementIndex(node, out _, out _);

    private static MongoBinaryOperator? MapComparisonOperator(ExpressionType nodeType)
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

    /// <summary>
    /// Maps an arithmetic <see cref="BinaryExpression"/> to its dialect-neutral operator.
    /// </summary>
    /// <remarks>
    /// Takes the whole node because division depends on the result type: integral division maps to
    /// <see cref="MongoBinaryOperator.IntegerDivide"/> (<c>$trunc</c> of <c>$divide</c>). The renderer can't decide
    /// this because widening converts on operands (<c>(double)a / b</c>) have already been unwrapped.
    /// </remarks>
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

    /// <summary>
    /// Translates one operand of a field-to-field / arithmetic comparison (always <c>$expr</c>) or a computed value:
    /// fields, constants/parameters, arithmetic, casts, ternaries, coalesce, and supported date/string/math calls.
    /// </summary>
    /// <remarks>
    /// By default (<paramref name="allowNumericWidening"/> <see langword="false"/>) a numeric cast becomes an explicit
    /// <c>$toX</c>, matching the driver. <see cref="TryTranslateValue"/> passes <see langword="true"/>: MongoDB
    /// arithmetic acts on the raw numeric value, so unwrapping a widening (never narrowing) conversion reproduces
    /// C#'s implicit promotion.
    /// </remarks>
    private MongoExpression? TranslateOperand(Expression node, bool allowNumericWidening = false)
    {
        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            var fromType = Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type;
            var toType = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;

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

                return new MongoConvertExpression(converted, toType);
            }

            return TranslateOperand(unary.Operand, allowNumericWidening); // benign or widening convert
        }

        // A ternary. Test uses the ordinary predicate translator; branches recurse here. Any declining part
        // declines the whole $cond.
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

        // `left ?? right` → $ifNull. Both sides recurse here (chained `a ?? b ?? c` nests on the right); either
        // declining declines the whole node.
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

        // DateTime/DateTimeOffset member or date-part chain (.Year, .Date, .DateTime, ...).
        if (TryTranslateDateTimeMember(node, out var dateTimeMember))
            return dateTimeMember;

        // DateTime/DateTimeOffset .AddXxx(amount).
        if (TryTranslateDateAdd(node, allowNumericWidening, out var dateAdd))
            return dateAdd;

        // string.IndexOf(term[, start]); operands recurse here. A char term becomes a one-char string. These
        // method-call/computed-member arms precede TryResolveMember, which never matches them.
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

        // string.Substring(start[, length]); operands recurse here.
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

        // string.Replace(find, replacement); a char operand becomes a one-char string.
        if (TryMatchReplace(node, out var replaceReceiver, out var replaceFind, out var replaceWith))
        {
            MongoExpression? Operand(Expression e)
                => e.Type == typeof(char) ? TranslateCharAsString(e) : TranslateOperand(e, allowNumericWidening);

            var input = TranslateOperand(replaceReceiver, allowNumericWidening);
            var find = input is null ? null : Operand(replaceFind);
            var with = find is null ? null : Operand(replaceWith);
            return with is null ? null : new MongoReplaceExpression(input!, find!, with);
        }

        // string.Length (no backing IProperty, so TryResolveMember wouldn't match).
        if (TryMatchStringLength(node, out var lengthReceiver))
        {
            var lengthOperand = TranslateOperand(lengthReceiver, allowNumericWidening);
            return lengthOperand is null ? null : new MongoStringLengthExpression(lengthOperand);
        }

        // Math/MathF function calls.
        if (TryTranslateMath(node, allowNumericWidening, out var math))
            return math;

        // string.Trim()/TrimStart()/TrimEnd() and their char/char[] overloads.
        if (node is MethodCallExpression trimCall && TryTranslateTrim(trimCall, out var trim))
            return trim;

        // string.FirstOrDefault()/LastOrDefault(); the receiver may itself be computed (e.g. a Trim()).
        if (node is MethodCallExpression firstLastCall
            && TryMatchStringFirstOrLastMethod(firstLastCall, out var firstOrLastKind, out var firstOrLastReceiver))
        {
            var firstOrLastSource = TranslateOperand(firstOrLastReceiver, allowNumericWidening);
            return firstOrLastSource is null
                ? null
                : new MongoStringFirstOrLastExpression(firstOrLastSource, firstOrLastKind);
        }

        // string.Join(separator, elements) over a fixed-arity array literal.
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

        // string.Concat(a, b[, c[, d]]) — the method spelling of `+`.
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

        // An owned-collection element count (b.Posts.Count / .Count() / .LongCount()). The renderer picks the
        // dialect: array-index existence test for an admissible integer constant, else null-safe $size in $expr.
        //
        // Runs after TryResolveMember so a mapped scalar named "Count" wins. TryResolveOwnedCollectionPath requires
        // the final hop to be a collection navigation, so a name collision can't resolve. It matches scope roots
        // by reference only, so every non-root single-scope translator must be built after an identity guard
        // (ReferencesEnclosingScope / NativeSelectManyBinder.ReferencesParameter); otherwise a shared property
        // name could retarget an enclosing member to the inner scope (wrong rows).
        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType, out var countSourceIsOuter)
            && !countSourceIsOuter) // same out-of-scope note as the quantifier arm above
        {
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // A filtered count: the element predicate translates like a quantifier's, with the same correlated
            // guard. Single-scope TryResolveMember resolves by name, so without it an enclosing member sharing a
            // name with an element member would silently retarget to the element. A correlation on SelfParam (by
            // reference) uses a two-scope child; anything else declines. countFreeParam is null when there are two
            // or more distinct free parameters, which the identity check then declines.
            MongoExpressionTranslator countElementTranslator;
            if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0], out var countFreeParam))
            {
                if (SelfParam is null || countFreeParam is null || !ReferenceEquals(countFreeParam, SelfParam))
                    return null;

                // innerPrefix null, not "": the renderer prepends "$$e." via $filter's "as" variable (see the
                // two-scope constructor).
                countElementTranslator = new MongoExpressionTranslator(
                    countElementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: null);
            }
            else
            {
                countElementTranslator = new MongoExpressionTranslator(countElementType);
            }

            if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
                return null;

            // Deliberately no renderability or AllFieldsDefaultSerialized gate: a translate-time decline sends a
            // projection leaf (Select(b => new { N = b.Posts.Count(pred) })) to the generic fall-through, which throws
            // InvalidOperationException in every mode including DriverLinq. Admitting it lets an unrenderable
            // predicate throw NativeTranslationNotSupportedException at render time, which TryBuildPipeline turns into
            // a driver-LINQ fallback. No alias hazard: a computed count leaf registers no alias override.
            return new MongoFilteredSizeExpression(arrayPath, elementPredicate, node.Type);
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

        // A predicate used as a value (a computed sort key OrderBy(x => !x.Flag), or a bare boolean projection
        // Select(x => x.Name.StartsWith("A"))): hand off to TranslateNode. Limited to Contains, Not, string
        // predicate methods, logical and/or, and comparisons; aggregation renderability is decided by CanRender.
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

        // A comparison used as a value (`p.Discontinued == ((p.ProductID > 50) != prm)`); same hand-off.
        if (node is BinaryExpression comparisonOperand && IsComparison(comparisonOperand.NodeType))
            return TranslateNode(node);

        // A constructed anonymous-type/DTO value (`new { x = c.City } == new { x = "London" }`). Members recurse
        // here; any declining member declines the whole construction. Each member must be default-serialized:
        // AllFieldsDefaultSerialized's catch-all assumes MongoDocumentConstructionExpression members already are.
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

    /// <summary>
    /// Translates string <c>a + b</c> to a <see cref="MongoConcatExpression"/>, flattening chains into one
    /// <c>$concat</c> (for brevity only; nesting would also work).
    /// </summary>
    private MongoExpression? TranslateStringConcat(BinaryExpression node, bool allowNumericWidening)
    {
        var left = TranslateConcatOperand(node.Left, allowNumericWidening);
        if (left is null)
            return null;

        var right = TranslateConcatOperand(node.Right, allowNumericWidening);
        if (right is null)
            return null;

        var operands = new List<MongoExpression>();
        operands.AddRange(left is MongoConcatExpression leftConcat ? leftConcat.Operands : [left]);
        operands.AddRange(right is MongoConcatExpression rightConcat ? rightConcat.Operands : [right]);
        return new MongoConcatExpression(operands);
    }

    /// <summary>
    /// Translates one string-concatenation operand: nested concats flatten; anything else is translated as a value
    /// and, if not a <see cref="string"/>, wrapped in <c>$toString</c>. The boxing convert from C#'s
    /// <c>string + object</c> overload is stripped first.
    /// </summary>
    /// <remarks>
    /// <c>$toString</c> diverges from .NET for <c>bool</c> (<c>"true"</c> vs <c>"True"</c>) and dates (ISO-8601), but
    /// driver-LINQ renders identically, so native introduces no new divergence.
    /// </remarks>
    private MongoExpression? TranslateConcatOperand(Expression operand, bool allowNumericWidening)
    {
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
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(byte) || underlying == typeof(sbyte) || underlying == typeof(uint)
            || underlying == typeof(ulong) || underlying == typeof(ushort)
            || underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal);
    }

    /// <summary>
    /// Translates a comparison's value side to a <see cref="MongoConstantExpression"/> (baked-in literal) or a
    /// <see cref="MongoParameterExpression"/> (per-execution placeholder); <see langword="null"/> otherwise.
    /// </summary>
    private static MongoExpression? TranslateValue(Expression node, IProperty? forSerialization)
    {
        if (node is ConstantExpression constant)
            return new MongoConstantExpression(constant.Value, forSerialization);

        if (NativeQueryParameter.TryGetQueryParameterName(node, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization);

        if (NativeQueryParameter.TryGetParameterArrayElementIndex(node, out var arrayParameterName, out var index))
            return new MongoParameterExpression(arrayParameterName, forSerialization, arrayElementIndex: index);

        return null;
    }

    /// <summary>
    /// Classifies the <c>Convert</c> layers on the member side of a <c>member &lt;op&gt; constant</c> comparison as
    /// widening numeric, identity-like, or decline. Returns <see langword="true"/> to decline.
    /// </summary>
    /// <param name="operand">The raw (un-unwrapped) member-side operand.</param>
    /// <param name="propertyClrType">The resolved property's CLR type.</param>
    /// <param name="toleratedWideningTarget">
    /// The outermost absorbed widening target (the type the comparison happens in), or <see langword="null"/>.
    /// </param>
    /// <param name="isIdentityLikeConvert">
    /// Whether an identity-like layer was absorbed and no widening one was; widening wins when both occur (e.g.
    /// <c>(object)(double)x.I</c>). The two demand opposite constant serialization; see
    /// <see cref="ConstantSerializationContext"/>.
    /// </param>
    /// <remarks>
    /// Absorbing a widening cast matches the driver, which drops it on the query-dialect path. It isn't always
    /// value-preserving (<c>long</c>/<c>ulong</c> → <c>double</c> above 2^53), an accepted CLR divergence pinned by
    /// <c>NativeCastTests.Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq</c>. Admissible targets
    /// come from <see cref="MongoConvertExpression.ToOperatorFor"/>.
    /// </remarks>
    private static bool HasNumericConvert(
        Expression operand, Type propertyClrType, out Type? toleratedWideningTarget, out bool isIdentityLikeConvert)
    {
        toleratedWideningTarget = null;
        var sawIdentityLike = false;
        var underlying = Nullable.GetUnderlyingType(propertyClrType) ?? propertyClrType;
        while (operand is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var to = Nullable.GetUnderlyingType(u.Type) ?? u.Type;
            var from = Nullable.GetUnderlyingType(u.Operand.Type) ?? u.Operand.Type;

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

    /// <summary>
    /// Identity-like converts leave the stored value unchanged, so the member side needs no <c>$toX</c> and the
    /// constant keeps the property's serializer (see <see cref="ConstantSerializationContext"/>).
    /// </summary>
    /// <remarks>
    /// Admitted: enum ↔ its underlying type or an integral widening of it (C# promotes sub-<c>int</c> enums to
    /// <c>int</c> for comparison); enum → another enum with the exact same underlying type (e.g.
    /// <c>(int)(DtoEnum)entity.SourceEnum</c>, each layer classified separately); <c>char</c> → <c>int</c>; and
    /// <c>T</c> → <c>object</c>. Other shapes (including <c>char</c> → <c>long</c>) decline, which is conservative.
    /// </remarks>
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
    private static ExpressionType Mirror(ExpressionType nodeType)
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
