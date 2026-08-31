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
/// Translates an EF Core predicate or key-selector lambda body into a dialect-agnostic
/// <see cref="MongoExpression"/> tree, suitable for later rendering to a MongoDB filter
/// or sort document.
/// </summary>
/// <remarks>
/// <para>
/// This is a compile-time-only translator: it produces a <em>template</em> tree where
/// captured constants become <see cref="MongoConstantExpression"/> nodes baked into the
/// template, and query parameters become <see cref="MongoParameterExpression"/> placeholder
/// nodes that are resolved per execution (the B2 binding step).
/// </para>
/// <para>
/// Returns <see langword="false"/> for any shape outside the parity acceptance set rather
/// than throwing, so callers can fall back to the driver-LINQ path gracefully.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    /// <summary>
    /// Creates a single-scope <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    /// <param name="selfParam">
    /// The root <see cref="ParameterExpression"/> this translator was built for (a <c>Where</c> predicate's,
    /// an <c>OrderBy</c> key selector's, a <c>Select</c> projection's, etc.), or <see langword="null"/> when
    /// none is available. Used ONLY to detect a single-level CORRELATED owned-collection element predicate
    /// (<c>Count(pred)</c>/<c>Any</c>/<c>All</c>) nested inside this translator's own tree: when such a
    /// predicate's free parameter is identical (by parameter identity) to <see cref="SelfParam"/>, the
    /// quantifier/count call sites build a two-scope child translator instead of declining. See EF-421.
    /// </param>
    public MongoExpressionTranslator(IEntityType entityType, ParameterExpression? selfParam = null)
    {
        _entityType = entityType;
        SelfParam = selfParam;
    }

    /// <summary>
    /// Creates a two-scope translator for a CORRELATED reference-<c>SelectMany</c> inner filter, or for a
    /// CORRELATED owned-collection element predicate rendered via <c>$filter</c>: a member access rooted on
    /// <paramref name="outerParam"/> resolves against <paramref name="outerEntityType"/> at document root (no
    /// prefix); any other member resolves against <paramref name="innerEntityType"/> and is prefixed with
    /// <paramref name="innerPrefix"/> (the <c>_lookup_&lt;Nav&gt;</c> unwind scope) when non-null, or left
    /// unprefixed when <paramref name="innerPrefix"/> is <see langword="null"/> — the correct choice for a
    /// <c>$filter</c>'s element scope, whose fields are addressed by the renderer via the <c>$filter</c>'s own
    /// <c>as</c> variable (<c>"$$e.&lt;path&gt;"</c>), never via a baked-in document-relative prefix. Passing
    /// <c>""</c> instead of <see langword="null"/> here would NOT mean "no prefix" — it would render as a
    /// leading-dot path segment (<c>"$$e..&lt;path&gt;"</c>), which MongoDB rejects as an empty field-path
    /// segment. Outer members are identified by reference identity, never by name, so a member name shared
    /// between the two scopes never conflates them.
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
    /// The root <see cref="ParameterExpression"/> this SINGLE-SCOPE translator was built for — see the
    /// constructor parameter of the same name. SETTABLE (not constructor-only) because
    /// <see cref="NativeSlotPopulator.PopulateNativeSlots"/> handles exactly ONE <see cref="MethodCallExpression"/>
    /// per call, but constructs its translator generically at the TOP of that method — before the `if`/`else if`
    /// dispatch has determined which of the seven slot operators (<c>Where</c>/<c>OrderBy</c>/<c>ThenBy</c>/etc.)
    /// this call is for, and therefore before that operator's own lambda parameter is known. Only the single
    /// arm that actually runs sets this field, to ITS OWN parameter, immediately before translating — a plain
    /// constructor argument would require constructing a separate translator inside each arm instead (final-
    /// review fix: corrected a factually wrong claim that this setter existed because ONE translator instance
    /// was reused ACROSS SEVERAL slot operators — it never is; each call to <c>PopulateNativeSlots</c> handles
    /// one operator and constructs one translator). Always <see langword="null"/> on a two-scope translator
    /// (the constructor below never sets it) — a correlation nested two-or-more scopes deep is out of EF-421's
    /// scope and must keep declining, which requires that a two-scope child never itself exposes a further
    /// <see cref="SelfParam"/>.
    /// </summary>
    internal ParameterExpression? SelfParam { get; set; }

    /// <summary>
    /// Attempts to translate an EF Core expression body into a <see cref="MongoExpression"/>.
    /// </summary>
    /// <param name="efBody">The expression body (from a predicate or key-selector lambda).</param>
    /// <param name="result">
    /// The translated <see cref="MongoExpression"/>, or <see langword="null"/> if the body
    /// is outside the parity acceptance set.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the body was translated successfully; <see langword="false"/> if
    /// the shape is not natively representable (the caller should fall back to driver-LINQ).
    /// </returns>
    public bool TryTranslate(Expression efBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = TranslateNode(Unwrap(efBody));
        return result is not null;
    }

    /// <summary>
    /// Attempts to translate a key-selector lambda body to a <see cref="MongoFieldExpression"/>
    /// suitable for use in an ordering clause.  Unlike <see cref="TryTranslate"/>, this path
    /// accepts any mapped scalar property — not just booleans — because the intent is to
    /// produce a sort-key reference, not a predicate.
    /// </summary>
    /// <param name="keySelectorBody">The lambda body (e.g. <c>c.Age</c>) from an <c>OrderBy</c> or <c>ThenBy</c> call.</param>
    /// <param name="result">The translated <see cref="MongoFieldExpression"/>, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the body was translated successfully.</returns>
    public bool TryTranslateField(Expression keySelectorBody, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path, out var isOuter)
            || isOuter) // a sort key is never inside a $filter/$map scope, so an outer-scoped one has no
                         // meaning here — this translator is always the ROOT translator for a sort key
                         // (never itself the two-scope child), so isOuter is always false in practice; the
                         // guard exists so a future caller cannot silently regress this.
            return false;

        result = new MongoFieldExpression(property, path);
        return true;
    }

    /// <summary>
    /// Attempts to translate a numeric VALUE expression (a projection/computed leaf) — a member field-ref,
    /// constant/parameter, or arithmetic (<c>+ - * / %</c>) over numeric operands — reusing the same
    /// operand-resolution shapes a comparison uses. Unlike <see cref="TryTranslate"/>, this accepts a bare
    /// value. Declines for a non-numeric shape, or an operand whose property is not default-serialized (a
    /// converted/non-default-represented stored form would diverge from CLR arithmetic). An integer-result
    /// division is accepted and rendered with C#'s truncating semantics — see
    /// <see cref="MongoBinaryOperator.IntegerDivide"/>.
    /// </summary>
    public bool TryTranslateValue(Expression valueBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // Do NOT Unwrap the top-level node here: Unwrap strips ANY Convert unconditionally, which would
        // silently drop a top-level narrowing/value-changing cast (e.g. (int)o.Weight) and return the raw
        // wider value. Pass the RAW body so TranslateOperand's narrowing-aware Convert branch (below, under
        // allowNumericWidening) sees the top-level cast and rejects it.
        //
        // An integer-result division used to be rejected outright here, because MongoDB's $divide does not
        // truncate. EF-434 replaced that decline with a translation: MapArithmeticOperator maps it to
        // MongoBinaryOperator.IntegerDivide, which renders as $trunc-of-$divide and so matches C#.
        var translated = TranslateOperand(valueBody, allowNumericWidening: true);
        if (translated is null)
            return false;

        // Reject a value-converted / non-default-BsonRepresentation operand.
        if (!AllFieldsDefaultSerialized(translated))
            return false;

        result = translated;
        return true;
    }

    /// <summary>
    /// Resolves a <c>DateTime</c>/<c>DateTimeOffset</c> member-access chain (<c>.Year</c>, <c>.Date</c>,
    /// <c>.DateTime</c>, <c>.UtcDateTime</c>, etc.) to a native date expression. Recurses into the receiver so
    /// a multi-hop chain composes correctly — e.g. <c>x.Dto.Value.DateTime.Date</c> resolves the field first
    /// (the <c>.Value</c> hop is peeled generically by <see cref="TryResolveMember"/>), wraps it in
    /// <see cref="MongoDateTimeOffsetLocalExpression"/> for <c>.DateTime</c>, then wraps THAT in
    /// <see cref="MongoDatePartExpression"/> for <c>.Date</c>.
    /// </summary>
    /// <remarks>
    /// The ULTIMATE receiver (the innermost hop) must resolve to a plain stored field via
    /// <see cref="TryResolveMember"/> — a receiver that is itself an arbitrary COMPUTED value (a conditional
    /// branch, arithmetic) has no MQL sub-field-access form for the <c>DateTimeOffset</c> reconstruction case
    /// and declines here rather than being mistranslated. This does not limit chains of date-member hops
    /// themselves (those recurse through this same method), only what the chain may ultimately be rooted on.
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
            // A correlated outer-scoped DateTime/DateTimeOffset chain is out of EF-421's scope — decline
            // rather than silently building an inner-scoped field reference for an outer property.
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
                    result = new MongoElementRefExpression(utcField.ElementName + ".DateTime", typeof(DateTime));
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
    /// Resolves a rooted member-access chain whose final hop is an embedded COLLECTION navigation into a raw
    /// element reference for the array ITSELF (e.g. <c>b.Posts</c> → <c>Posts</c>,
    /// <c>b.Home.Notes</c> → <c>Home.Notes</c>), for use as a native projection leaf — the array is emitted by
    /// <c>$project</c> as-is, rather than being reduced to a <c>$size</c> the way
    /// <see cref="TryMatchCountExpression"/>'s callers do.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="TryResolveOwnedCollectionPath"/>, so it inherits that resolver's guards: the chain
    /// must be rooted at a <see cref="ParameterExpression"/> with at least one hop, every non-final hop must be an
    /// embedded single-reference navigation, and the FINAL hop must be an embedded collection NAVIGATION — this
    /// is the structural protection against a mapped scalar property that happens to share a navigation's name,
    /// since a scalar's receiver is never a collection. Two-scope (cross-scope <c>SelectMany</c>) mode is declined
    /// outright by the same resolver. The <c>AsQueryable()</c> wrapper EF's nav-expansion adds is stripped here,
    /// same as the quantifier and count entry points. This is the only caller of
    /// <see cref="TryResolveOwnedCollectionPath"/> reached from a projection rather than a predicate; it's safe
    /// from by-name member retargeting because <see cref="NativeProjectionBinder"/> builds this translator on the
    /// query root, where the only parameter in scope is the query parameter itself.
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

    // Mirrors MongoEFToLinqTranslatingExpressionVisitor.DateTimeOffsetComponentMembers' member set, minus
    // TimeOfDay (see MongoDatePart's own remarks for why that one is excluded), and applies identically to a
    // plain DateTime receiver, which needs none of that visitor's offset-reconstruction dance.
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

    // The C# compiler's own implicit numeric conversion table (C# language spec §10.2.1) — exactly the set
    // of numeric widenings the compiler inserts automatically for mixed-numeric-type arithmetic (and that a
    // user could equally write explicitly). MongoDB's arithmetic operators need no explicit cast to reproduce
    // this promotion (they operate on the raw numeric BSON value), so these are safe to unwrap.
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

    private static bool IsWideningNumericConvert(Type from, Type to)
        => WideningNumericConversions.Contains((from, to));

    private static bool IsIntegerType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
    }

    /// <summary>
    /// Matches a BARE field reference at document root, regardless of which of the two sibling node kinds it
    /// arrived as — an ordinary/inner-scope <see cref="MongoFieldExpression"/> or a two-scope translator's
    /// outer-scoped <see cref="MongoOuterFieldExpression"/> — and yields its backing <see cref="IProperty"/>.
    /// </summary>
    /// <remarks>
    /// Exists so every call site that must recognize "this operand is a bare stored value, not a computed
    /// one" (the bare-bool-truthiness hazard <see cref="AllFieldsDefaultSerialized"/>'s remarks describe)
    /// checks BOTH node kinds identically, rather than each site restating its own
    /// <c>is MongoFieldExpression</c> pattern and silently missing the outer-scoped sibling — exactly the gap
    /// EF-421's final review found in three (really four) sites in <see cref="MongoAggregationExpressionRenderer"/>.
    /// A THIRD sibling node kind, if one is ever added, only needs a case here — every caller stays correct.
    /// </remarks>
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
    /// Returns whether every field reachable from <paramref name="expr"/> uses its property's DEFAULT BSON
    /// serialization (no value converter, no non-default <c>BsonRepresentation</c>).
    /// </summary>
    /// <remarks>
    /// <b>Internal, not private</b> — it is the single correctness predicate behind SIX separate guards: five
    /// in <see cref="MongoAggregationExpressionRenderer"/>, and one in <see cref="MongoQueryLanguageRenderer"/>.
    /// In <see cref="MongoAggregationExpressionRenderer"/>, split between classify time and render time:
    /// <c>CanRender</c>'s <c>Not</c> arm and <c>CanRenderLogicalOperand</c> (EF-396) answer "would Render
    /// succeed AND answer correctly?", while its own private <c>RenderUnary</c> (EF-413),
    /// <c>CheckLogicalOperandSerialization</c> (EF-396), and <c>CheckBooleanRootSerialization</c>
    /// (final-review fix — guards a BARE <c>ElementPredicate</c> root under a quantifier/filtered-count, the
    /// one shape none of the other four catch) re-check at render time for the callers that invoke
    /// <c>Render</c> without going through <c>CanRender</c> first. The sixth lives in a DIFFERENT class and
    /// method of the same name — <see cref="MongoQueryLanguageRenderer"/>'s own <c>RenderUnary</c>, in its
    /// fall-to-<c>$expr</c> branch (EF-421 final-review round 2): that branch calls
    /// <c>MongoAggregationExpressionRenderer.CanRender</c> on the Not's OPERAND directly, so a bare field
    /// operand hits <c>CanRender</c>'s unconditional top arm rather than <c>CanRender</c>'s own already-guarded
    /// <c>Not</c> arm, and must repeat the check itself. All six exist for the same reason
    /// <see cref="TryTranslateValue"/> calls this at translate time: a raw-field aggregation <c>$not</c> —
    /// and, identically, a bare-field <c>$and</c>/<c>$or</c> operand — is TRUTHINESS-based (only
    /// <c>false</c>/<c>null</c>/<c>0</c>/<c>undefined</c> are falsy), so a value-converted/non-default-
    /// represented bool would render successfully but answer the WRONG boolean — e.g. a
    /// <c>HasConversion&lt;string&gt;()</c> bool stored as <c>"True"</c>/<c>"False"</c>, BOTH of which are
    /// non-empty (truthy) strings regardless of the CLR value. Do NOT call this from
    /// <c>MongoExpressionTranslator</c>'s own filtered-count branch (<c>TranslateOperand</c>'s
    /// <see cref="TryMatchCountExpression"/> arm) to REJECT at translate time — that branch is deliberately
    /// gate-free (see its own remarks): a translate-time <see langword="null"/> there drops the whole leaf
    /// into <c>NativeProjectionBinder</c>'s generic fall-through, an <see cref="InvalidOperationException"/>
    /// in EVERY mode including <c>DriverLinq</c> (MEASURED — a prior attempt at exactly this produced that
    /// crash). The render-time throw is what preserves the correct "declines gracefully" disposition for that
    /// position: <c>MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline</c>'s
    /// <c>catch (NativeTranslationNotSupportedException) when (mode != NativeOnly)</c> only ever sees a throw
    /// from this method's caller, never a translate-time decline.
    /// </remarks>
    internal static bool AllFieldsDefaultSerialized(MongoExpression expr)
        => expr switch
        {
            MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
            // Same correctness check as the MongoFieldExpression arm above, over the OUTER property instead.
            MongoOuterFieldExpression outerField => NativeGroupByBinder.HasDefaultKeySerialization(outerField.Property),
            MongoBinaryExpression b => AllFieldsDefaultSerialized(b.Left) && AllFieldsDefaultSerialized(b.Right),
            MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand),
            // EF-413: MongoUnaryExpression{Not} needs an explicit arm, NOT the catch-all — see this method's
            // own remarks above for why a raw-field $not silently answers wrong for a non-default-serialized
            // bool. Recursing into the operand catches this the same way the MongoBinaryExpression arm above
            // does for a comparison operand.
            MongoUnaryExpression u => AllFieldsDefaultSerialized(u.Operand),
            // A $cond's test/branches can each independently reach a non-default-serialized field (e.g. a
            // branch that is itself a raw field read) — recurse into all three the same way the binary/unary
            // arms above do, rather than falling into the catch-all below.
            MongoConditionalExpression cond => AllFieldsDefaultSerialized(cond.Test)
                && AllFieldsDefaultSerialized(cond.IfTrue)
                && AllFieldsDefaultSerialized(cond.IfFalse),
            // A date-part extraction or DateTimeOffset local-time reconstruction renders a raw MQL date
            // operator directly against its operand's BSON representation — a non-default BsonRepresentation
            // or ValueConverter on the underlying field would make that operator run against a value that
            // isn't actually a raw BSON date, silently producing a server-side type error or wrong values.
            // Recurse into the operand so a non-default-serialized field underneath is caught here, the same
            // way MongoConvertExpression's arm above does for an explicit cast.
            MongoDatePartExpression datePart => AllFieldsDefaultSerialized(datePart.Operand),
            MongoDateTimeOffsetLocalExpression local => AllFieldsDefaultSerialized(local.Operand),
            // A MongoInExpression is deliberately NOT given its own arm — it is correct via the catch-all
            // below: RenderIn/RenderInValues serialize every candidate value through the field's own property
            // serializer (MongoConstantExpression.ForSerialization / the parameter's serializer), so a
            // value-converted/non-default-represented field compares like-for-like on both sides of $in,
            // unlike Not's raw truthiness test. Adding a redundant check here would decline a currently-correct
            // shape.
            //
            // A MongoSizeExpression falls into the catch-all below, deliberately: an array COUNT carries no
            // property serialization, so there is no converter / BsonRepresentation for it to diverge on.
            //
            // A MongoArrayContainsExpression is likewise deliberately NOT given its own arm: its Value is
            // already fully rendered at translate time through the array's own ELEMENT serializer (see
            // TranslateArrayContainsItem), and a whole-collection value converter on the array field is
            // declined at that same translate-time point (no per-element serializer to resolve safely) — so
            // by the time this node exists, there is nothing left for this method to catch.
            _ => true
        };

    // Strip redundant Convert/ConvertChecked wrappers (EF sometimes adds a nullable-widening convert).
    private static Expression Unwrap(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            ? Unwrap(u.Operand)
            : e;

    /// <summary>
    /// Strips only the <see cref="ExpressionType.Convert"/> layers that PRESERVE ORDER, for a sort key.
    /// </summary>
    /// <remarks>
    /// A sort key can't use the general <see cref="Unwrap"/>, which strips any <c>Convert</c> unconditionally:
    /// a narrowing or signed/unsigned cast would then be silently discarded and <c>$sort</c> would order by the
    /// raw stored value instead, producing wrong order with no exception. Order preservation (not value
    /// preservation) is all a sort key needs, so this only unwraps casts guaranteed not to change relative
    /// order: nullable ↔ underlying (no value change), boxing to <see cref="object"/> (no value change), and
    /// widening numeric conversions (not all value-preserving — e.g. <c>(float)16777217L == (float)16777216L</c>
    /// — but IEEE round-to-nearest is monotone non-decreasing, so ties after rounding were already adjacent in
    /// source order). This guarantee is NOT sufficient for anything needing value equality, e.g. a
    /// <c>GroupBy</c> key — do not reuse this helper there. Everything else stays in the tree so
    /// <see cref="TryResolveMember"/> declines and the caller falls through to <see cref="TryTranslateValue"/>.
    /// <see cref="WideningNumericConversions"/> omits <c>char</c>'s own widenings; that only makes this more
    /// conservative (falls through rather than mis-sorting). Every <see cref="TryTranslateField"/> caller other
    /// than the sort-key path pre-filters its input to a bare member/<c>EF.Property</c> access, so the loop
    /// below never runs for them. Do not simplify this back to <see cref="Unwrap"/> — see <c>NativeCastTests</c>.
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

    // Returns null for any unsupported node (the caller propagates null → false return).
    private MongoExpression? TranslateNode(Expression node)
    {
        switch (node)
        {
            // --- Logical binary operators ---

            case BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso:
            {
                var left = TranslateNode(Unwrap(andAlso.Left));
                if (left is null) return null;
                var right = TranslateNode(Unwrap(andAlso.Right));
                if (right is null) return null;
                return new MongoBinaryExpression(MongoBinaryOperator.AndAlso, left, right);
            }

            case BinaryExpression { NodeType: ExpressionType.OrElse } orElse:
            {
                var left = TranslateNode(Unwrap(orElse.Left));
                if (left is null) return null;
                var right = TranslateNode(Unwrap(orElse.Right));
                if (right is null) return null;
                return new MongoBinaryExpression(MongoBinaryOperator.OrElse, left, right);
            }

            // --- Comparison binary operators ---

            case BinaryExpression be when IsComparison(be.NodeType):
                return TranslateComparison(be);

            // --- Negation of a boolean field ---

            case UnaryExpression { NodeType: ExpressionType.Not } not:
            {
                var operand = TranslateNode(Unwrap(not.Operand));
                if (operand is null) return null;
                // !list.Contains(x.Field) → flip Negated on the MongoInExpression rather than
                // wrapping it in a generic Not node (there is no query-dialect "not $in" wrapper).
                if (operand is MongoInExpression inExpr)
                    return new MongoInExpression(inExpr.Field, inExpr.Values, negated: !inExpr.Negated);
                // !arrayField.Contains(constant) → flip Negated on the MongoArrayContainsExpression rather
                // than wrapping in a generic Not node — mirrors the MongoInExpression case immediately above;
                // { field: { $ne: value } } is the exact complement (see RenderArrayContains's remarks).
                if (operand is MongoArrayContainsExpression arrayContainsExpr)
                    return new MongoArrayContainsExpression(
                        arrayContainsExpr.Field, arrayContainsExpr.Value, negated: !arrayContainsExpr.Negated);
                // !s.StartsWith(...)/!s.EndsWith(...)/!s.Contains(...) → flip Negated on the
                // MongoRegexExpression rather than wrapping in a generic Not node (there is no
                // query-dialect "not $regularExpression" wrapper other than an enclosing $not,
                // which the renderer applies based on this flag).
                if (operand is MongoRegexExpression regexExpr)
                    return new MongoRegexExpression(regexExpr.Field, regexExpr.Kind, regexExpr.Term, negated: !regexExpr.Negated);
                // !collection.Any(...)/!collection.All(...) → flip Negated rather than wrapping in a generic
                // Not node: RenderUnary supports Not over a bare field only, and $elemMatch has direct
                // query-dialect negations ({ path: { $not: { $elemMatch: ... } } }, and $exists: false for the
                // bare Any() form).
                if (operand is MongoElemMatchExpression elemMatchExpr)
                    return new MongoElemMatchExpression(
                        elemMatchExpr.ArrayPath, elemMatchExpr.ElementPredicate, negated: !elemMatchExpr.Negated);
                // !collection.Any(pred)/.All(pred) where pred is CORRELATED (translated to a
                // MongoQuantifierExpression rather than a MongoElemMatchExpression, since $elemMatch cannot
                // reference the enclosing document) → recurse into MongoExpressionNegator's own quantifier
                // case (flips Kind, negates ElementPredicate) rather than falling through to the generic
                // Unary(Not) wrap below. This mirrors the MongoElemMatchExpression arm immediately above and
                // the MongoSizeExpression-comparison arm immediately below — and is load-bearing for
                // NativeCardinalityBinder's root-level All(pred) arm: a generic Unary(Not) wrapping a
                // quantifier has no query-dialect form (MongoQueryLanguageRenderer.IsQueryDialectRenderable
                // declines a Not over anything but a bare field or query-native comparison), so
                // MongoExpressionNegator.TryNegate's outer gate would decline it before ever reaching the
                // quantifier-aware case — flipping Kind HERE, at the one-level-shallower point where the node
                // is still a bare MongoQuantifierExpression, is what lets `All(b => !b.Posts.Any(...))` re-
                // negate cleanly when NativeCardinalityBinder negates the WHOLE predicate a second time.
                if (operand is MongoQuantifierExpression quantifierOperand)
                    return MongoExpressionNegator.TryNegate(quantifierOperand, out var quantifierComplement)
                        ? quantifierComplement
                        : null;
                // !(collection.Count > n) → INVERT the comparison rather than wrapping in a generic Not node:
                // the array-index form has no $not wrapper, and MongoExpressionNegator owns the (exact)
                // inversion rule for this family — see its remarks for why inversion is exact here and is NOT
                // for a relational comparison on a scalar field. A parameterized threshold declines here,
                // because the negator is gated on query-dialect renderability; that is an accepted coverage
                // gap, not a correctness one.
                if (operand is MongoBinaryExpression { Left: MongoSizeExpression })
                    return MongoExpressionNegator.TryNegate(operand, out var countComplement)
                        ? countComplement
                        : null;
                // Only allow Not over a field or further translated expression; nullable bools fall back.
                // TryGetBareFieldProperty covers BOTH bare-field sibling kinds (MongoFieldExpression and the
                // outer-scoped MongoOuterFieldExpression) — final-review fix: a NULLABLE outer bool reached
                // through a correlated SelectMany inner filter must decline here exactly as a nullable
                // MongoFieldExpression already did, not slip through as newly-translatable.
                if (TryGetBareFieldProperty(operand, out var notOperandProperty) && notOperandProperty.IsNullable)
                    return null; // conservative: nullable bool Not could diverge from driver rendering
                return new MongoUnaryExpression(MongoUnaryOperator.Not, operand);
            }

            // --- Array field contains value: arrayField.Contains(constant) → { field: value } ---
            //
            // The mirror image of the $in arm immediately below: there the ITEM resolves to a field and the
            // COLLECTION side is a client-side set of values; here the RECEIVER is a genuine stored array
            // FIELD and the item is a single candidate VALUE. MongoDB's implicit array-element match
            // ({ field: value }) already means "value is an element of field" when field's declared type is
            // an array/list, so this needs no special comparison operator — see
            // MongoArrayContainsExpression's remarks for why the result is NOT built by reusing
            // MongoBinaryOperator.Equal (that would silently mean whole-array equality if ever rendered
            // through the aggregation-expression dialect).
            //
            // Scoped to EF-382: a CONSTANT item only. A parameterized item would need this arm to resolve a
            // per-execution placeholder through the array's ELEMENT serializer, which
            // TranslateArrayContainsItem does not build; such a call falls through unmatched (the guard
            // below requires a ConstantExpression) to the $in arm, which declines it exactly as it always
            // has (the item does not resolve via TryResolveMember there either).
            case MethodCallExpression call
                when TryMatchContainsMethod(call, out var arrayReceiver, out var containsItem)
                     && Unwrap(containsItem) is ConstantExpression
                     && TryResolveMember(Unwrap(arrayReceiver), out var arrayProperty, out var arrayFieldPath, out var arrayIsOuter)
                     && !arrayIsOuter // an outer-scoped array receiver is out of EF-421's scope — decline
                     && GetEnumerableElementType(arrayProperty.ClrType) is not null:
            {
                var itemNode = TranslateArrayContainsItem(Unwrap(containsItem), arrayProperty);
                if (itemNode is null)
                    return null; // e.g. a value-converted array, or a CLR/element type mismatch — decline.

                var arrayFieldExpr = new MongoFieldExpression(arrayProperty, arrayFieldPath);
                return new MongoArrayContainsExpression(arrayFieldExpr, itemNode, negated: false);
            }

            // --- Collection membership: Enumerable.Contains / List<T>.Contains / ICollection<T>.Contains ---

            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (!TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath, out var itemIsOuter)
                    || itemIsOuter) // an outer-scoped item is out of EF-421's scope — decline
                    return null; // item must resolve to a bare field

                var valuesNode = TranslateInValues(collectionExpr, property);
                if (valuesNode is null)
                    return null;

                var fieldExpr2 = new MongoFieldExpression(property, fieldPath);
                return new MongoInExpression(fieldExpr2, valuesNode, negated: false);
            }

            // --- String prefix/suffix/substring: string.StartsWith/EndsWith/Contains(string) ---

            case MethodCallExpression call when TryMatchRegexMethod(call, out var kind, out var receiver, out var termExpr):
            {
                if (!TryResolveMember(Unwrap(receiver), out var property, out var fieldPath, out var receiverIsOuter))
                    return null; // receiver must resolve to a bare string field

                // An outer-scoped receiver reached from the NEW element-scope translators (Count(pred)/
                // quantifier, both built with innerPrefix: null) is still out of EF-421's scope — MongoRegexExpression
                // is strictly typed to MongoFieldExpression, and this shape would need MongoOuterFieldExpression's
                // document-root-regardless-of-elementVariable rendering to be correct inside a $filter/$map.
                // But an outer-scoped receiver reached from a PRE-EXISTING two-scope translator (NativeSelectManyBinder,
                // NativeJoinScopeTranslator — both built with a real non-null innerPrefix) is NOT a new shape this
                // plan needs to gate at all: TryResolveMember already resolves it to the correct, unprefixed
                // OUTER-relative path, exactly as it did before this plan ever touched this method, so it renders
                // correctly as a plain MongoFieldExpression (query-dialect $regularExpression, not $expr). Declining
                // it here regressed x.Outer.Name.StartsWith(...) inside a join-scope Where predicate.
                if (receiverIsOuter && _innerPrefix is null)
                    return null;

                if (property!.ClrType != typeof(string))
                    return null;

                var termNode = TranslateValue(Unwrap(termExpr), property);
                if (termNode is null)
                    return null;

                var fieldExpr3 = new MongoFieldExpression(property, fieldPath!);
                return new MongoRegexExpression(fieldExpr3, kind, termNode, negated: false);
            }

            // --- Quantifiers over an owned (embedded) collection: source.Any() / Any(pred) / All(pred) ---

            case MethodCallExpression call
                when TryMatchQuantifierMethod(call, out var quantifier, out var quantifierSource, out var elementLambda):
            {
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType, out var sourceIsOuter))
                    return null; // not an owned-collection source rooted at the query parameter

                if (sourceIsOuter)
                    return null; // the ARRAY itself being reached through the outer scope is a separate, not-yet-supported shape (e.g. a quantifier over an outer sibling collection) — out of EF-421's scope

                if (elementLambda is null)
                {
                    // A bare Any() IS "at least one element", i.e. Count >= 1 — the same predicate, rendered by
                    // the same array-index existence form ({ "path.0": { $exists: true } }). Representing it as
                    // a count comparison keeps ONE representation for array cardinality, and !Any() then falls
                    // out of the negator's inversion (Count < 1) with no dedicated code.
                    return new MongoBinaryExpression(
                        MongoBinaryOperator.GreaterThanOrEqual,
                        new MongoSizeExpression(arrayPath, typeof(int), nullSafe: true),
                        new MongoConstantExpression(1, forSerialization: null));
                }

                // A CORRELATED element predicate — one reaching outside the element into the enclosing entity.
                //
                // EF-421: no longer an outright decline. If the free parameter found is IDENTICAL
                // (ReferenceEquals) to THIS translator's own SelfParam, build a two-scope child translator and
                // emit a MongoQuantifierExpression ($anyElementTrue/$allElementsTrue over $map) instead of the
                // ordinary $elemMatch path below — $elemMatch cannot reference the enclosing document at all,
                // so that dialect is unusable here regardless of scope handling. Any OTHER free parameter
                // (correlation reaching past the immediate enclosing scope) still declines exactly as before.
                if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0], out var quantifierFreeParam))
                {
                    if (SelfParam is null || !ReferenceEquals(quantifierFreeParam, SelfParam))
                        return null;

                    var correlatedElementTranslator = new MongoExpressionTranslator(
                        elementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: null);
                    if (!correlatedElementTranslator.TryTranslate(elementLambda.Body, out var correlatedPredicate))
                        return null;

                    // No negation for All here, unlike the uncorrelated path below: $allElementsTrue is
                    // itself a "for all" operator, so the predicate translates DIRECTLY for both Any and All.
                    if (!MongoAggregationExpressionRenderer.CanRender(correlatedPredicate))
                        return null; // no equivalent graceful degrade exists for this node — decline cleanly

                    // Final-review fix (CRITICAL — silent wrong-data risk): CanRender's own top arm admits ANY
                    // bare field (MongoFieldExpression/MongoOuterFieldExpression) unconditionally — it answers
                    // "would Render throw", not "would Render answer correctly" — so a BARE correlatedPredicate
                    // root (e.g. `b.Posts.Any(p => b.Flag)`, no comparison/Not/&&/|| wrapping it) sails through
                    // the check above even when the field is value-converted/non-default-represented, and
                    // $anyElementTrue/$allElementsTrue would then truthiness-test its raw stored form and
                    // silently answer the wrong boolean. Decline cleanly here (translate time, matching this
                    // whole branch's existing disposition) rather than let it reach the render-time throw in
                    // MongoAggregationExpressionRenderer.RenderQuantifier — which exists too, as defense in
                    // depth, but this branch already has a decline path so there is no reason to hard-throw.
                    if (TryGetBareFieldProperty(correlatedPredicate, out _)
                        && !AllFieldsDefaultSerialized(correlatedPredicate))
                        return null;

                    return new MongoQuantifierExpression(
                        new MongoElementRefExpression(arrayPath, quantifierSource.Type), correlatedPredicate, quantifier);
                }

                // Translate the element predicate with an ELEMENT-SCOPED translator: its field paths come out
                // element-relative, which is what $elemMatch requires. This is the mirror image of
                // NativeSelectManyBinder.TryBuildOwnedInnerFilter, which translates the same way and then
                // PREFIXES the result with the unwind path.
                var elementTranslator = new MongoExpressionTranslator(elementType);
                if (!elementTranslator.TryTranslate(elementLambda.Body, out var translated))
                    return null;

                MongoExpression child = translated;
                var negated = false;

                if (quantifier is MongoQuantifierKind.All)
                {
                    // All(pred) is true exactly when NO element satisfies ¬pred, i.e. a negated $elemMatch
                    // over the EXACT complement. That form is also correct for an empty, missing, or
                    // explicitly-null array: nothing satisfies the $elemMatch, so the enclosing $not matches
                    // and All is true — which is what LINQ's All over an empty sequence returns.
                    //
                    // A predicate with no exact complement declines the whole quantifier (clean fallback to
                    // driver-LINQ) rather than emitting an approximation, which would return wrong rows.
                    if (!MongoExpressionNegator.TryNegate(child, out var complement))
                        return null;

                    child = complement;
                    negated = true;
                }

                // $expr is not usable inside $elemMatch, and RenderNode's catch-all would silently wrap a
                // non-query-dialect child in $expr. Decline here (translate time) so the query falls back to
                // driver-LINQ instead. For All this is belt-and-braces — the negator gates on the same
                // classifier — but it stays because it is the invariant the renderer's contract depends on.
                if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(child))
                    return null;

                return new MongoElemMatchExpression(arrayPath, child, negated);
            }

            // --- Nullable<T>.HasValue ---
            //
            // Built as the SAME node an explicit `x.A != null` produces (not a new node kind): the renderer
            // and negator already handle $ne, and $eq/$ne partition every BSON value including missing and
            // null, so `!HasValue` needs no code of its own. The rendered form selects null AND missing, which
            // matches LINQ's HasValue for a stored element that is absent.
            //
            // Must sit BEFORE the bare-boolean-member default below: HasValue is a bool-typed MemberExpression,
            // so the default would reach TryResolveMember, fail to find a "HasValue" property, and decline.
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath, out var hasValueIsOuter)
                    || hasValueIsOuter) // an outer-scoped nullable HasValue check is out of EF-421's scope — decline
                    return null;

                return new MongoBinaryExpression(
                    MongoBinaryOperator.NotEqual,
                    new MongoFieldExpression(nullableProperty, nullablePath),
                    new MongoConstantExpression(null, nullableProperty));
            }

            // --- Bare boolean member access (c.Active) ---

            default:
                if (TryResolveMember(node, out var boolProp, out var boolPath, out var boolIsOuter))
                {
                    // Accept only non-nullable bools; a nullable bool bare access could diverge.
                    if (boolProp!.ClrType != typeof(bool) || boolProp.IsNullable)
                        return null;
                    // Same confinement as TranslateComparison's two branches (final-review fix): only the NEW
                    // element-scope two-scope translators (Count(pred)/quantifier, innerPrefix: null) get a
                    // MongoOuterFieldExpression here. The PRE-EXISTING SelectMany two-scope translator
                    // (_innerPrefix non-null) must keep emitting a plain MongoFieldExpression at document root,
                    // exactly as it did before this plan — MongoOuterFieldExpression is not query-dialect-
                    // renderable, so it would route through $expr as raw truthiness instead of through the
                    // property's own serializer.
                    return boolIsOuter && _innerPrefix is null
                        ? new MongoOuterFieldExpression(boolProp, boolPath!)
                        : new MongoFieldExpression(boolProp, boolPath!);
                }

                return null;
        }
    }

    /// <summary>
    /// Translate a comparison <see cref="BinaryExpression"/> into a <see cref="MongoBinaryExpression"/>.
    /// </summary>
    /// <remarks>
    /// Two shapes are recognized:
    /// <list type="bullet">
    /// <item>
    /// <b>Query-native:</b> a bare member on exactly one side and a constant/parameter value on the other.
    /// Always produces the field on <see cref="MongoBinaryExpression.Left"/> — mirroring the operator when the
    /// member was originally on the right — so the renderer's <c>IsQueryNativeComparison</c> check keeps
    /// routing this shape to the indexable <c>$match</c> dialect.
    /// </item>
    /// <item>
    /// <b>Field-to-field / arithmetic-operand (always <c>$expr</c>):</b> anything else — a member on both
    /// sides, or an arithmetic sub-expression on either side — translated via <see cref="TranslateOperand"/>
    /// without the "field on exactly one side" restriction, preserving operand order (no mirroring, since
    /// non-commutative comparisons need their original left/right order inside <c>$expr</c>).
    /// </item>
    /// </list>
    /// <para>
    /// A cast the query-native branch can't absorb (<see cref="HasNumericConvert"/>'s three-outcome
    /// classification: widening numeric / identity-like / decline) falls through to the <c>$expr</c> path
    /// rather than declining the whole comparison outright, where <see cref="MongoConvertExpression"/> renders
    /// it as an explicit <c>$toX</c> over the field ref — gated by <see cref="CanFallThroughToExpr"/>, which
    /// still declines a RELATIONAL comparison (<c>&lt; &lt;= &gt; &gt;=</c>) over a NULLABLE property: the
    /// query dialect type-brackets such an operator (matches neither a stored <c>null</c> nor a missing
    /// element) while <c>$expr</c> does not, admitting extra rows. Equality always falls through in both
    /// directions ($eq/$ne partition every BSON value including null and missing).
    /// </para>
    /// <para>
    /// This deliberately changes results for a narrowing cast against a constant: the driver's own LINQ
    /// provider drops the cast for <c>(int)x.D &gt; 0</c> and answers as though the comparison were
    /// <c>x.D &gt; 0</c>, while native rendering emits <c>{$expr: {$gt: [{$toInt: "$D"}, 0]}}</c> and answers
    /// what C# answers — the CLR-correct result, by design, even though it means
    /// <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> does not restore the same answer for this shape. Pinned by
    /// <c>NativeCastTests.Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq</c>.
    /// </para>
    /// </remarks>
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
    {
        var leftUnwrapped = Unwrap(be.Left);
        var rightUnwrapped = Unwrap(be.Right);

        // --- Query-native shape: member on exactly one side, value on the other ---

        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath, out var leftIsOuter)
            && IsSimpleValue(rightUnwrapped))
        {
            // A WIDENING numeric cast or an IDENTITY-LIKE cast on the member side is absorbed (the field ref
            // is the stored field exactly as for a bare member); anything else still changes comparison
            // semantics, so it declines THIS BRANCH and falls through to the general $expr path below instead
            // of declining the whole comparison, where the cast renders as an explicit MongoConvertExpression
            // ($toX). See HasNumericConvert for the three-outcome classification. CanFallThroughToExpr carries
            // the fall-through's own two preconditions — default serialization, and NOT a relational operator
            // over a nullable property (which would un-type-bracket the comparison and admit null/missing rows).
            if (HasNumericConvert(be.Left, leftProperty!.ClrType, out var leftWideningTarget, out var leftIdentityLike))
            {
                if (!CanFallThroughToExpr(leftProperty, be.NodeType))
                    return null;
            }
            else
            {
                var mongoOp = MapComparisonOperator(be.NodeType);
                if (mongoOp is null)
                    return null;

                var valueExpr = TranslateValue(
                    rightUnwrapped, ConstantSerializationContext(leftProperty, leftWideningTarget, leftIdentityLike));
                if (valueExpr is null)
                    return null;

                // MongoOuterFieldExpression only for the NEW element-scope two-scope translators (Count(pred)/
                // quantifier, both constructed with innerPrefix: null) — final-review fix. The PRE-EXISTING
                // SelectMany two-scope translator (_innerPrefix non-null, a real "_lookup_<Nav>" document-path
                // string) must keep emitting a plain MongoFieldExpression at document root exactly as it did
                // before this plan, because MongoOuterFieldExpression is IsQueryDialectRenderable => false and
                // would route the comparison through the $expr/aggregation-expression dialect instead of the
                // query dialect — harmless for $eq/$ne, but for a RELATIONAL operator over a NULLABLE outer
                // property it un-type-brackets the comparison (BSON total ordering admits null under $lt/$lte/
                // $gt/$gte, where the query dialect would not), silently admitting null/missing rows a
                // correlated SelectMany inner filter previously excluded.
                MongoExpression leftField = leftIsOuter && _innerPrefix is null
                    ? new MongoOuterFieldExpression(leftProperty, leftPath!)
                    : new MongoFieldExpression(leftProperty, leftPath!);
                return new MongoBinaryExpression(mongoOp.Value, leftField, valueExpr);
            }
        }

        // The mirrored shape, and the SAME fall-through rule. Note the $expr path does NOT mirror the operator
        // — it keeps the original left/right order, which is what a non-commutative comparison inside $expr
        // needs, so this is a genuinely different emission from the branch above rather than a spelling of it.
        else if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath, out var rightIsOuter)
                 && IsSimpleValue(leftUnwrapped))
        {
            if (HasNumericConvert(be.Right, rightProperty!.ClrType, out var rightWideningTarget, out var rightIdentityLike))
            {
                // be.NodeType, NOT the mirrored operator: the four relational operators are closed under
                // Mirror, so which side the member sits on cannot change the guard's answer.
                if (!CanFallThroughToExpr(rightProperty, be.NodeType))
                    return null;
            }
            else
            {
                // Mirror the operator since the member is on the right-hand side but must render on the Left.
                var mongoOp = MapComparisonOperator(Mirror(be.NodeType));
                if (mongoOp is null)
                    return null;

                var valueExpr = TranslateValue(
                    leftUnwrapped, ConstantSerializationContext(rightProperty, rightWideningTarget, rightIdentityLike));
                if (valueExpr is null)
                    return null;

                // Same confinement as the mirrored branch above — see its comment for the full rationale.
                MongoExpression rightField = rightIsOuter && _innerPrefix is null
                    ? new MongoOuterFieldExpression(rightProperty, rightPath!)
                    : new MongoFieldExpression(rightProperty, rightPath!);
                return new MongoBinaryExpression(mongoOp.Value, rightField, valueExpr);
            }
        }

        // --- Field-to-field / arithmetic-operand shape: always routes to $expr ---

        var generalOp = MapComparisonOperator(be.NodeType);
        if (generalOp is null)
            return null;

        var leftOperand = TranslateOperand(be.Left);
        if (leftOperand is null)
            return null;

        var rightOperand = TranslateOperand(be.Right);
        if (rightOperand is null)
            return null;

        // An $expr field reference reads the RAW STORED value, with no serializer applied. A value-converted
        // or non-default-represented operand on EITHER side would therefore be compared in provider (stored)
        // form rather than model form -- a value-transforming converter compares scaled/re-encoded numbers
        // against each other or against an unconverted operand (silently wrong rows), and a re-encoding
        // converter or non-default BsonRepresentation compares across BSON types, which MongoDB type-brackets
        // by BSON total order rather than by value (e.g. a string-encoded int sorts after every number,
        // independent of its numeric value). MEASURED (EF-404): both operands default-serialized is required;
        // this mirrors TranslateOperand's own MongoConvertExpression guard, but that guard only fires for a
        // cast -- a PLAIN field-to-field/arithmetic comparison with no cast reached this path unchecked.
        if (!AllFieldsDefaultSerialized(leftOperand) || !AllFieldsDefaultSerialized(rightOperand))
            return null;

        // Normalize a count-vs-value comparison so the size node is on the LEFT, mirroring the operator: the
        // query renderer's array-index form recognizes only that orientation. Field-to-field and arithmetic
        // comparisons are deliberately NOT mirrored — they render inside $expr, where operand order matters.
        if (rightOperand is MongoSizeExpression
            && leftOperand is MongoConstantExpression or MongoParameterExpression)
        {
            var mirroredOp = MapComparisonOperator(Mirror(be.NodeType));
            if (mirroredOp is null)
                return null;

            return new MongoBinaryExpression(mirroredOp.Value, rightOperand, leftOperand);
        }

        return new MongoBinaryExpression(generalOp.Value, leftOperand, rightOperand);
    }

    /// <summary>
    /// Gates the cast fall-through: <see langword="true"/> when a comparison whose cast the query-native
    /// branch could not absorb may fall through to the general <c>$expr</c> path, <see langword="false"/> when
    /// it must decline the whole comparison instead.
    /// </summary>
    /// <remarks>
    /// Two conjuncts. (1) The operand's stored form must be default-serialized
    /// (<see cref="NativeGroupByBinder.HasDefaultKeySerialization"/>) — kept as a cheap early-out that declines
    /// before translation runs, even though it is subsumed by <see cref="TranslateOperand"/>'s own
    /// <see cref="MongoConvertExpression"/> guard for every shape reachable here; remove it only if the
    /// duplication becomes a liability, not to "simplify."
    /// (2) A RELATIONAL comparison (<c>&lt; &lt;= &gt; &gt;=</c>) over a NULLABLE property must not leave the
    /// type-bracketed query dialect. <c>{UnitPrice: {$lt: 100}}</c> matches neither a stored BSON <c>null</c>
    /// nor a missing element, because a relational operator only matches a comparable BSON type. The
    /// corresponding <c>$expr</c> form, e.g. <c>{$expr: {$lt: [{$toDouble: "$UnitPrice"}, 100.0]}}</c>,
    /// converts null and missing alike to <c>null</c> and compares by BSON total order, where <c>Null</c>
    /// sorts below every number — so those rows wrongly match. This mirrors <see cref="MongoExpressionNegator"/>'s
    /// invariant from the other direction: a relational operator is type-bracketed and admits neither missing
    /// nor null. All four relational operators are declined together — not just <c>&lt;</c>/<c>&lt;=</c>, which
    /// are the only ones that measurably differ, because <c>&gt;</c>/<c>&gt;=</c> happening to agree is an
    /// accident of BSON collation order, not a property of the rendering; the exact-complement-or-decline rule
    /// applies here too. Equality is unaffected either way and always falls through ($eq/$ne partition every
    /// BSON value including null and missing). For a NON-nullable property a missing/null element is already a
    /// schema violation the read path rejects, so nullability (not "every relational cast") is the correct key —
    /// gating more broadly would revoke the deliberate CLR-correct divergence this fall-through exists for.
    /// A plain field-to-field/arithmetic comparison with no cast also reaches the general <c>$expr</c> path, but
    /// (EF-404) it is now guarded there directly — <see cref="TranslateComparison"/>'s general path applies
    /// <see cref="AllFieldsDefaultSerialized"/> to both fully-translated operands before emitting the <c>$expr</c>
    /// node, declining (falling back to driver-LINQ, which has no working oracle for this shape either and
    /// itself throws) rather than comparing raw stored values in the wrong representation.
    /// </remarks>
    private static bool CanFallThroughToExpr(IProperty property, ExpressionType comparisonNodeType)
        => NativeGroupByBinder.HasDefaultKeySerialization(property)
           && !(property.IsNullable && IsRelationalComparison(comparisonNodeType));

    // The four TYPE-BRACKETED comparison operators. Equality is deliberately absent: $eq/$ne partition every
    // BSON value including null and missing, so moving one of those into $expr does not change which documents
    // match. Kept as its own method rather than inlined so the set is stated once, next to the reason.
    private static bool IsRelationalComparison(ExpressionType nodeType)
        => nodeType is ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    /// <summary>
    /// Chooses the serialization context for the CONSTANT side of a query-native comparison: the property's
    /// own serializer (<paramref name="property"/>), or none at all (<see langword="null"/> ⇒
    /// <c>BsonValue.Create</c> over the constant's own CLR value, i.e. the COMPARISON's type).
    /// </summary>
    /// <remarks>
    /// <paramref name="isIdentityLikeConvert"/> is checked first and overrides the widening rule below it: an
    /// identity-like convert (enum ↔ underlying, <c>char</c> → <c>int</c>, boxing to <c>object</c>) does not
    /// move the comparison to a different stored representation — it's the same stored value under a different
    /// declared CLR type — so the constant must go through the SAME serializer the property itself uses.
    /// Collapsing this with the widening rule is wrong: an enum-as-string property's constant would render as a
    /// raw number instead of the mapped string, and the comparison would silently match nothing.
    /// <para>
    /// For a genuinely widening numeric cast (<paramref name="toleratedWideningTarget"/> set), the constant is
    /// instead serialized in the COMPARISON's type (<see langword="null"/> context), not the stored property's
    /// type: absorbing the cast moves the comparison from the stored type to the cast's type, so serializing the
    /// constant through the stored property would coerce/truncate it (e.g. a fractional constant against an
    /// integral stored type) and silently return wrong rows. This must NOT apply unconditionally, though — only
    /// when the property is ALSO default-serialized (<see cref="NativeGroupByBinder.HasDefaultKeySerialization"/>):
    /// a value-converted or non-default-represented property under a widening cast must keep the property's own
    /// serializer, or the constant renders as a raw value against a converted (e.g. string-encoded) stored
    /// field and MongoDB's type bracketing returns zero rows.
    /// </para>
    /// <para>
    /// Two failure sub-families motivate keeping this conjunct: a value-TRANSFORMING converter would emit an
    /// unconverted constant against converted stored values (silently wrong rows); a re-encoding converter or
    /// non-default <c>BsonRepresentation</c> would emit a raw number against a string-stored field (zero rows,
    /// via type bracketing). See <c>NativeCastTests.Widening_cast_comparison_over_a_value_converted_property_*</c>
    /// for the pinned cases.
    /// </para>
    /// </remarks>
    private static IProperty? ConstantSerializationContext(
        IProperty property, Type? toleratedWideningTarget, bool isIdentityLikeConvert)
        => isIdentityLikeConvert
            ? property // identity-like: SAME stored value — the constant must use the property's own serializer
            : toleratedWideningTarget is not null && NativeGroupByBinder.HasDefaultKeySerialization(property)
                ? null
                : property;

    // A "simple value" comparison operand — a bare constant or query parameter, not a member and not an
    // arithmetic sub-expression. Used to detect the query-native (member vs. value) shape.
    private static bool IsSimpleValue(Expression node)
        => node is ConstantExpression || NativeQueryParameter.TryGetQueryParameterName(node, out _);

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
    /// Takes the whole node, not just its <see cref="ExpressionType"/>, for one reason: division splits on the
    /// node's RESULT type (EF-434). C# integer division truncates and MQL's <c>$divide</c> does not, so an
    /// integral-result division maps to <see cref="MongoBinaryOperator.IntegerDivide"/> (rendered
    /// <c>$trunc</c>-of-<c>$divide</c>) while a floating/decimal one stays a plain
    /// <see cref="MongoBinaryOperator.Divide"/>. The decision cannot be deferred to the renderer: a widening
    /// <c>Convert</c> on an operand (<c>(double)a / b</c>) is unwrapped by <see cref="TranslateOperand"/>, so
    /// the rendered operands look integral even when C# computed in <c>double</c>. Only <c>node.Type</c>
    /// distinguishes those two cases.
    /// </remarks>
    internal static MongoBinaryOperator? MapArithmeticOperator(BinaryExpression node)
        => node.NodeType switch
        {
            ExpressionType.Add => MongoBinaryOperator.Add,
            ExpressionType.Subtract => MongoBinaryOperator.Subtract,
            ExpressionType.Multiply => MongoBinaryOperator.Multiply,
            ExpressionType.Divide => IsIntegerType(node.Type)
                ? MongoBinaryOperator.IntegerDivide
                : MongoBinaryOperator.Divide,
            ExpressionType.Modulo => MongoBinaryOperator.Modulo,
            _ => null
        };

    /// <summary>
    /// Translates one operand of a field-to-field / arithmetic comparison (a shape that always routes to
    /// <c>$expr</c>) into a <see cref="MongoExpression"/>: a member becomes a <see cref="MongoFieldExpression"/>,
    /// a constant/parameter becomes a value node, and an arithmetic <see cref="BinaryExpression"/>
    /// (<c>+ - * / %</c>) becomes a <see cref="MongoBinaryExpression"/> with operands translated recursively.
    /// </summary>
    /// <remarks>
    /// A numeric-widening <see cref="UnaryExpression"/> (e.g. <c>(double)c.Age</c>) is rejected here rather than
    /// unwrapped on the comparison-operand path (<paramref name="allowNumericWidening"/> is
    /// <see langword="false"/>, the default): the driver's own LINQ translator renders a numeric cast on a
    /// bare field-to-field comparison operand as an explicit <c>$toDouble</c>, but drops the very same cast on
    /// an arithmetic operand — an inconsistent, shape-dependent rule that would need re-deriving to match
    /// exactly, so falling back to driver-LINQ for any numeric cast here avoids silently diverging from it. A
    /// benign (non-numeric, e.g. nullable-widening) convert is still unwrapped. <see cref="TryTranslateValue"/>
    /// passes <see langword="true"/> instead: a computed value leaf has none of that driver-rendering
    /// inconsistency to avoid, and MongoDB's arithmetic operators act on the raw BSON numeric value regardless
    /// of declared CLR width, so unwrapping a widening (never narrowing) conversion reproduces C#'s own
    /// implicit-numeric-promotion exactly.
    /// </remarks>
    private MongoExpression? TranslateOperand(Expression node, bool allowNumericWidening = false)
    {
        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            var fromType = Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type;
            var toType = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
            if (fromType != toType && !(allowNumericWidening && IsWideningNumericConvert(fromType, toType)))
            {
                // A type-changing cast MQL can express becomes an explicit $toX over the translated operand,
                // matching what the driver's own LINQ provider emits here. An unrenderable target still
                // declines, matching the driver, which throws for those.
                if (MongoConvertExpression.ToOperatorFor(toType) is null)
                    return null;

                var converted = TranslateOperand(unary.Operand, allowNumericWidening);
                if (converted is null)
                    return null;

                // A $toX renders over the RAW STORED value, so a value-converted / non-default-represented
                // field underneath it would be converted at the wrong point (applied to the provider value
                // instead of the model value) — reject that here rather than emit a comparison that answers
                // against the wrong value.
                if (!AllFieldsDefaultSerialized(converted))
                    return null;

                return new MongoConvertExpression(converted, toType);
            }

            return TranslateOperand(unary.Operand, allowNumericWidening); // benign or widening convert — unwrap and recurse
        }

        // A ternary. Test is translated via the ORDINARY predicate translator (TryTranslate — the same one
        // Where uses), so anything already supported there as a predicate (nullable equality/comparison,
        // field-to-field, etc.) works as a $cond condition too. Both branches recurse through this same
        // method, so a branch may itself be a cast, arithmetic, nested conditional, or date-part expression.
        // A branch that declines declines the WHOLE conditional — there is no partial/fallback rendering for
        // one branch of a $cond.
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

        // A DateTime/DateTimeOffset member-access or date-part chain (.Year, .Date, .DateTime, etc.). Must run
        // regardless of whether TryResolveMember below would also be tried — it never matches this shape
        // anyway (the member's OWN receiver is never the query parameter directly for a date-member chain),
        // but this is placed here to keep it visually adjacent to the ConditionalExpression case above.
        if (TryTranslateDateTimeMember(node, out var dateTimeMember))
            return dateTimeMember;

        if (TryResolveMember(node, out var property, out var fieldPath, out var operandIsOuter))
        {
            // Deliberately NOT confined to _innerPrefix is null here, unlike TranslateComparison's two
            // branches and the bare-bool arm: this is the field-to-field/arithmetic/computed-value operand
            // path, which never reaches the query-native dialect regardless of node type (IsQueryNativeComparison
            // requires a bare field vs. a simple value on the OTHER side, never two operands both resolved
            // through this method), so MongoOuterFieldExpression renders identically to MongoFieldExpression
            // here — "$" + ElementName, outside any element-variable scope. NativeSelectManyBinderTests'
            // correlated arithmetic/numeric-comparison shapes (established in the plan that added
            // MongoOuterFieldExpression) assert MongoOuterFieldExpression at this exact site; confining it
            // here would only break that type assertion for zero MQL benefit.
            return operandIsOuter
                ? new MongoOuterFieldExpression(property, fieldPath!)
                : new MongoFieldExpression(property, fieldPath!);
        }

        // An OWNED-collection element count — b.Posts.Count / .Count() / .LongCount(). The renderer decides the
        // dialect: a comparison against an admissible integer constant becomes an array-index existence test,
        // anything else routes to $expr with a null-safe $size (see MongoQueryLanguageRenderer).
        //
        // This runs AFTER TryResolveMember, so an entity with a real mapped scalar property called "Count"
        // resolves as that field. The actual safety is structural, in TryResolveOwnedCollectionPath: it
        // requires the chain to be rooted at a parameter with at least one hop, every non-final hop to
        // be an embedded single reference, and the FINAL hop to be an embedded collection NAVIGATION. A mapped
        // scalar's receiver is an entity, never a collection, so a name collision can never resolve. In
        // two-scope mode the resolver checks root identity (EF-421) — a chain rooted on the OUTER param
        // resolves against the outer entity type; the `!countSourceIsOuter` guard below declines the array
        // ITSELF being reached through the outer scope, which stays out of scope.
        //
        // TryResolveOwnedCollectionPath does not check WHICH ParameterExpression roots the chain by name — only
        // by ReferenceEquals against the known scope parameters — so its safety depends on every non-root
        // single-scope translator being constructed only after an identity guard has run — the quantifier arm's
        // ReferencesEnclosingScope, or NativeSelectManyBinder's ReferencesParameter, which routes an
        // outer-referencing layer to the two-scope constructor where this resolver checks identity rather than
        // guessing. A future non-root single-scope translator built without such a guard would reopen a
        // by-name retarget (an enclosing member resolved against the inner scope because the two types share a
        // property name) — a wrong-rows failure, not a decline.
        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType, out var countSourceIsOuter)
            && !countSourceIsOuter) // same out-of-scope note as the quantifier arm above
        {
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // A FILTERED count. The element predicate is translated exactly as a quantifier's is — same
            // correlated-scope guard, same element-scoped child translator.
            //
            // The correlated guard is load-bearing: single-scope TryResolveMember resolves a member by NAME
            // with no parameter-identity check, so an enclosing-scoped access whose name also exists on the
            // element would be silently retargeted at the element — wrong rows under the default Native mode.
            // A $filter cond CAN legally reference the enclosing document (unlike $elemMatch, which cannot at
            // all), so correlated support here is a deferrable capability, not an impossibility.
            //
            // EF-421: a correlation whose free parameter is IDENTICAL (ReferenceEquals) to THIS translator's
            // own SelfParam is no longer declined outright — it is translated with a two-scope child
            // translator instead, mirroring NativeSelectManyBinder's existing pattern. Any other free
            // parameter (correlation reaching past the immediate enclosing scope) still declines exactly as
            // before — SelfParam is null for a translator that is itself already a two-scope child, so a
            // two-level-deep correlation can never match here.
            //
            // countFreeParam is null both when there is NO free parameter (impossible here — we're inside
            // ReferencesEnclosingScope's true branch) and when there are TWO OR MORE DISTINCT free parameters
            // (FreeParameterVisitor.FoundParameter's contract) — a body like
            // `p => p.X == b.Y && p.Z == someOtherCapturedVar` must decline, not silently build a two-scope
            // translator keyed on whichever of the two happened to be found first: the SECOND free parameter's
            // members would then be resolved by NAME against the element scope with no identity check, which
            // is exactly the wrong-rows hazard this file's own constraint forbids. Because null can never
            // ReferenceEquals a real ParameterExpression, the ordinary identity check below already declines
            // this shape correctly with no extra condition needed.
            MongoExpressionTranslator countElementTranslator;
            if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0], out var countFreeParam))
            {
                if (SelfParam is null || countFreeParam is null || !ReferenceEquals(countFreeParam, SelfParam))
                    return null;

                // innerPrefix is deliberately null, not "": the element predicate here is rendered via
                // $filter's own "as" variable (RenderFilteredSize / MongoAggregationExpressionRenderer's
                // elementVariable threading), so an inner field must resolve UNPREFIXED — the renderer
                // itself prepends "$$e." at render time. Passing "" would bake in a leading-dot path segment
                // instead (see the two-scope constructor's remarks).
                countElementTranslator = new MongoExpressionTranslator(
                    countElementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: null);
            }
            else
            {
                countElementTranslator = new MongoExpressionTranslator(countElementType);
            }

            if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
                return null;

            // NO aggregation-renderability gate here (EF-365 removed one). Admitting a predicate the
            // aggregation renderer cannot express is DELIBERATE and is the better disposition on both
            // spellings this branch serves:
            //
            //   * PREDICATE spelling (Where(b => b.Posts.Count(pred) > n)): unchanged either way — the throw
            //     just moves from translate time to MongoPipelineFactory.Create, and
            //     MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline's typed
            //     `catch (NativeTranslationNotSupportedException) when (mode != NativeOnly)` lands on the same
            //     graceful driver-LINQ fallback. NativeOnly still throws the same exception type.
            //
            //   * PROJECTION spelling (Select(b => new { N = b.Posts.Count(pred) })): materially BETTER.
            //     Declining here left Route == Fallback, which skips the projection-member registration and
            //     drops the query into MongoProjectionBindingExpressionVisitor's generic fall-through — an
            //     InvalidOperationException ("could not be translated") in ALL THREE modes, including
            //     DriverLinq. Admitting the leaf keeps Route == Projection, so the render-time throw is caught
            //     and the driver-LINQ fallback — which renders this count perfectly well — runs instead.
            //
            // The alias/silent-wrong-data hazard the removed gate cited (Query/AGENTS.md's alias-agreement
            // invariant) does NOT reach this shape: a computed count leaf registers no alias override, so
            // ShouldStripBareProjectionOnFallback is false and the driver's own $project (whose member names
            // ARE the shaper's aliases) stays in place; and an ARRAY leaf sibling, the one case that would
            // strip, is already failed closed by NativeProjectionBinder.IsWholeDocumentReadableLeaf, which
            // rejects any computed sibling and declines the whole projection before mutating anything. Both
            // arms — plus Contains/$in, unary Not and a three-leaf mixed projection — are measured in
            // NativeOwnedCollectionFilteredCountTests' *_EF365 tests.
            //
            // EF-413 (review fix): deliberately NOT gated here with an AllFieldsDefaultSerialized(elementPredicate)
            // check (unlike TryTranslateValue's own top-level check). Tried once, MEASURED WRONG: a translate-time
            // null return for a NOT over a non-default-serialized field (e.g. `Count(p => !p.ConvertedFlag)`) drops
            // the WHOLE leaf into the InvalidOperationException hard-fail this method's own remarks describe two
            // paragraphs up — for EVERY query mode, including DriverLinq, which previously worked. The correctness
            // fix for this hazard lives at RENDER time instead: MongoAggregationExpressionRenderer.RenderUnary
            // (EF-413) itself throws NativeTranslationNotSupportedException when a Not's operand is a
            // non-default-serialized field, which TryBuildPipeline's typed catch turns into exactly the graceful
            // driver-LINQ fallback this branch's whole design already relies on — see NativeOwnedCollectionFilteredCountTests.
            // Filtered_count_element_predicate_using_Not_over_a_value_converted_bool_declines_instead_of_answering_wrong.
            return new MongoFilteredSizeExpression(arrayPath, elementPredicate, node.Type);
        }

        // Restrict to numeric operand types: ExpressionType.Add on strings is compiler-generated
        // concatenation (string.Concat), not arithmetic — it has no $add equivalent (confirmed empirically:
        // the driver server rejects "$add" on strings with "$add only supports numeric or date types").
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

        // EF-413: a client-collection Contains (→ MongoInExpression) or a Not (→ MongoUnaryExpression) used as
        // a VALUE operand — e.g. a computed sort key (OrderBy(x => list.Contains(x.Field)) or
        // OrderBy(x => !x.Flag)). These are boolean-typed but neither a field/arithmetic operand nor a bare
        // constant/parameter, so without this they'd fall straight through to TranslateValue below and decline.
        // TranslateNode already has the predicate-position translation for both (including Contains' negated-$in
        // collapse for !list.Contains(...) — see its Not case); hand off directly rather than duplicating it.
        // Deliberately narrow: only these two node shapes are routed here, not every predicate TranslateNode
        // admits (a comparison, AndAlso/OrElse, ElemMatch, …) — that stays a decline in value position, since
        // widening it further is outside this ticket's scope (EF-413 is scoped to A6/A13 only). Whether the
        // result is actually renderable in aggregation-expression position is decided downstream by
        // MongoAggregationExpressionRenderer.CanRender, not here.
        if (node is MethodCallExpression containsCall && TryMatchContainsMethod(containsCall, out _, out _))
            return TranslateNode(node);

        if (node is UnaryExpression { NodeType: ExpressionType.Not })
            return TranslateNode(node);

        // A bare constant/parameter operand has no associated property for serialization context — these
        // are pure numeric $expr operands, not stored field values, so they serialize via BsonValue.Create.
        return TranslateValue(node, forSerialization: null);
    }

    // A `(T?)null` conditional branch compiles to Convert(Constant(null, typeof(object)), typeof(T?)) — an
    // unboxing conversion from the untyped null literal, not a numeric widening and not a same-underlying-type
    // nullable wrap, so TranslateOperand's generic Convert-handling declines it (no $toX operator exists for
    // most target types, and even where one does, Object->T is not in the numeric-widening table). The value
    // is unconditionally null regardless of the declared conversion, so recognize it here — narrowly, only for
    // a conditional's own branch position — rather than loosening the shared Convert branch every other caller
    // (predicate/sort/arithmetic operands) also goes through.
    private MongoExpression? TranslateConditionalBranch(Expression branch, bool allowNumericWidening)
    {
        if (branch is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            && unary.Operand is ConstantExpression { Value: null } nullConstant)
            return TranslateValue(nullConstant, forSerialization: null);

        return TranslateOperand(branch, allowNumericWidening);
    }

    // True for the numeric CLR types $add/$subtract/$multiply/$divide/$mod accept — used to keep
    // TranslateOperand's arithmetic-operand handling scoped to genuine arithmetic (excluding e.g. string
    // concatenation, which also compiles to ExpressionType.Add but is not representable as "$add").
    internal static bool IsNumericType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(byte) || underlying == typeof(sbyte) || underlying == typeof(uint)
            || underlying == typeof(ulong) || underlying == typeof(ushort)
            || underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal);
    }

    /// <summary>
    /// Translates a value node (the non-field operand of a comparison) to either a
    /// <see cref="MongoConstantExpression"/> (baked-in literal) or a
    /// <see cref="MongoParameterExpression"/> (B2 placeholder for a query parameter).
    /// Returns <see langword="null"/> for any node that cannot be represented.
    /// </summary>
    private static MongoExpression? TranslateValue(Expression node, IProperty? forSerialization)
    {
        if (node is ConstantExpression constant)
            return new MongoConstantExpression(constant.Value, forSerialization);

        if (NativeQueryParameter.TryGetQueryParameterName(node, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization);

        return null; // any other node shape (method call, sub-expression, etc.) is not supported
    }

    /// <summary>
    /// Classifies the <c>Convert</c>/<c>ConvertChecked</c> layers wrapping the MEMBER side of a
    /// <c>member &lt;op&gt; constant</c> comparison. Returns <see langword="true"/> when the comparison must
    /// be declined, <see langword="false"/> when it may proceed.
    /// </summary>
    /// <param name="operand">The raw (un-unwrapped) member-side operand.</param>
    /// <param name="propertyClrType">The resolved property's CLR type.</param>
    /// <param name="toleratedWideningTarget">
    /// On a <see langword="false"/> return: the OUTERMOST tolerated WIDENING NUMERIC target — the type the
    /// comparison actually happens in — or <see langword="null"/> when no widening layer was absorbed (a bare
    /// member, only benign nullable&lt;-&gt;underlying converts, or an identity-like convert instead — see
    /// <paramref name="isIdentityLikeConvert"/>). The caller uses this to decide the constant's serialization
    /// context; see <see cref="TranslateComparison"/>.
    /// </param>
    /// <param name="isIdentityLikeConvert">
    /// On a <see langword="false"/> return: <see langword="true"/> when at least one absorbed layer was
    /// IDENTITY-LIKE (enum ↔ its own underlying type or a widening of it, <c>char</c> → <c>int</c>, or boxing to
    /// <c>object</c>) rather than a widening numeric conversion. Tracked separately from
    /// <paramref name="toleratedWideningTarget"/> because the two demand opposite constant serialization (see
    /// <see cref="ConstantSerializationContext"/>). If the same chain sets both (e.g.
    /// <c>(object)(double)x.I</c>), this comes back <see langword="false"/> — widening wins, since it determines
    /// the type the comparison actually happens in, and an identity-like wrapper around it changes nothing.
    /// </param>
    /// <remarks>
    /// Classifies a member-side convert chain into three outcomes — widening numeric / identity-like / decline —
    /// rather than vetoing any non-exact-match cast outright; a widening numeric layer is absorbed (the field
    /// ref stays the stored field, exactly as for a bare member) and an identity-like layer likewise leaves the
    /// stored value untouched, only its declared CLR type. Keep the two "tolerate" outcomes visibly distinct:
    /// collapsing them loses the signal <see cref="ConstantSerializationContext"/> needs to serialize the
    /// constant correctly (e.g. an enum-as-string constant must keep the property's own serializer).
    /// <para>
    /// Absorbing rather than rendering an explicit <c>$toX</c> is justified by driver parity, not value
    /// preservation: on the query-dialect (member-vs-constant) path the driver's own LINQ provider drops a
    /// widening cast entirely, so absorbing it keeps native and driver-LINQ in agreement. Value preservation is
    /// NOT guaranteed for every admitted pair — <c>(long, double)</c> and <c>(ulong, double)</c> are in
    /// <see cref="WideningNumericConversions"/> but are not value-preserving above 2^53 (IEEE rounding collapses
    /// distinct longs onto the same double — see <see cref="UnwrapOrderPreserving"/>'s remarks for the sort-path
    /// analog). Native then compares the raw stored value against the (possibly-rounded) constant, which can
    /// disagree with in-memory LINQ but still agrees with driver-LINQ (which drops the cast identically) — an
    /// accepted divergence from the CLR, not wrong data. Pinned by
    /// <c>NativeCastTests.Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq</c>.
    /// </para>
    /// <para>
    /// The admissible target set is <see cref="MongoConvertExpression.ToOperatorFor"/>'s (the single definition
    /// of what MQL can express) rather than a second hand-rolled list, so a tolerated comparison type is always
    /// one whose constant <c>BsonValue.Create</c> renders unambiguously.
    /// </para>
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

            // A layer that changes nothing but nullability (long -> long?) is benign and must be SKIPPED, not
            // classified: `from == to`, so the widening test below would wrongly decline it. The pre-existing
            // `to != underlying` test alone does not catch this either, since it compares against the
            // PROPERTY's type, not this layer's own operand type — e.g. EF lowers a nullable-lifted uint -> long
            // widening as `Convert(Convert(e.EmployeeID, Int64), Nullable<Int64>)`.
            if (from != to && to != underlying)
            {
                // Three outcomes, kept visibly distinct — collapsing the first two ("tolerate") loses the
                // signal ConstantSerializationContext needs to pick the right constant treatment.
                if (IsWideningNumericConvert(from, to) && MongoConvertExpression.ToOperatorFor(to) is not null)
                {
                    // WIDENING NUMERIC: tolerate, and record the outermost tolerated target — the comparison
                    // happens in the outermost type, so the first one found is the one to report (e.g.
                    // (double)(long)x.I compares in double).
                    toleratedWideningTarget ??= to;
                }
                else if (IsIdentityLikeConvert(from, to))
                {
                    // IDENTITY-LIKE: tolerate — the SAME stored value, only its declared CLR type changes. Not
                    // reported to the out param yet: a WIDENING layer elsewhere in the SAME chain must win
                    // precedence (see the final assignment below) — reachable via (object)(double)x.I, where
                    // the outer boxing layer must NOT mask the inner widening one.
                    sawIdentityLike = true;
                }
                else
                {
                    toleratedWideningTarget = null;
                    isIdentityLikeConvert = false;
                    return true; // narrowing, signed/unsigned, or an unrenderable target — decline
                }
            }

            operand = u.Operand;
        }

        // PRECEDENCE: a widening target found anywhere in the chain wins over an identity-like layer found
        // anywhere else in it. Both flags can be set by a single chain — (object)(double)x.I sets
        // toleratedWideningTarget=double (inner cast) AND sawIdentityLike=true (outer boxing) — and
        // ConstantSerializationContext checks isIdentityLikeConvert first, so reporting both would let the
        // identity-like arm's "always keep the property serializer" rule mask the widening arm's truncation
        // protection. Widening must win because it determines the type the comparison actually happens in; an
        // identity-like layer merely wrapping it changes nothing about that.
        isIdentityLikeConvert = sawIdentityLike && toleratedWideningTarget is null;
        return false;
    }

    /// <summary>
    /// Identity-like converts: the comparison happens on the SAME stored value, so the member side needs no
    /// <c>$toX</c> and the constant KEEPS the property's serializer (that is how an enum-as-string constant
    /// renders at all — see <see cref="ConstantSerializationContext"/>'s remarks).
    /// </summary>
    /// <remarks>
    /// Five shapes are identity-like: enum → its own underlying type or a WIDENING of it (and back); enum →
    /// an UNRELATED enum with the SAME underlying type (e.g. an entity enum reinterpreted as a DTO enum with
    /// matching members — the two CLR types disagree, but the stored value doesn't change); <see cref="char"/>
    /// → <see cref="int"/>; and <c>T</c> → <see cref="object"/> (boxing for a value type, or a plain reference
    /// upcast for a reference type — both leave the stored value unchanged, only its declared CLR type, so
    /// both count as identity-like despite the method name's "boxing" resonance).
    /// <para>
    /// The enum-to-enum arm requires an EXACT underlying-type match, not a widening one: composing it with a
    /// genuine cross-width conversion (e.g. a <c>byte</c>-backed enum reinterpreted as an <c>int</c>-backed
    /// one) would need an actual numeric conversion this arm does not attempt, so a mismatch declines rather
    /// than mis-serializing. A chain like <c>(int)(DtoEnum)entity.SourceEnum</c> (the shape a C# enum equality
    /// comparison against an unrelated enum constant lowers to, since both operands are promoted to their
    /// shared underlying type before comparing) is still admitted overall: the OUTER <c>Convert(DtoEnum, int)</c>
    /// layer is caught by the pre-existing enum-to-its-own-underlying-type arm below, and this arm catches the
    /// INNER <c>Convert(SourceEnum, DtoEnum)</c> layer — <see cref="HasNumericConvert"/> walks every layer, so
    /// each is classified independently.
    /// </para>
    /// <para>
    /// The enum arm checks against the enum's OWN underlying type, not against <see cref="int"/>: C# promotes a
    /// sub-<c>int</c> enum (<c>short</c>/<c>byte</c>/<c>ushort</c>/<c>sbyte</c>-backed) to <see cref="int"/> for
    /// an equality/relational comparison, so e.g. a <c>short</c>-backed enum's member side arrives as
    /// <c>Convert(member, Int32)</c>, a genuine widening of its own underlying type rather than an exact match.
    /// The reverse direction (<c>underlying → enum</c>) is widened symmetrically for consistency. A LONG-backed
    /// enum narrowed to <see cref="int"/> still correctly declines — the widening check is directional
    /// (narrower-underlying → wider-target only), never the reverse.
    /// </para>
    /// <para>
    /// Known conservative edge: <c>char</c> → <c>long</c>/<c>double</c> still declines; only <c>char</c> →
    /// <c>int</c> is admitted, matching <see cref="WideningNumericConversions"/>'s own omission of <c>char</c>'s
    /// widenings elsewhere in this file. A decline here still falls back correctly, just not optimally.
    /// </para>
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
            // The widening disjunct is restricted to an INTEGRAL target. Without this, an int-backed enum cast
            // to double/float/decimal also satisfies IsWideningNumericConvert(Int32, Double) and would be
            // wrongly admitted as identity-like — the constant would then keep the enum property's serializer,
            // and Enum.ToObject throws for a non-integral value, an uncaught crash. Every genuine C#
            // enum-comparison promotion this arm exists for is itself integral, so this restriction only closes
            // the floating-point hole.
            return toType == underlying || (IsIntegerType(toType) && IsWideningNumericConvert(underlying, toType));
        }

        if (toType.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(toType);
            return fromType == underlying || (IsIntegerType(fromType) && IsWideningNumericConvert(fromType, underlying));
        }

        return (fromType == typeof(char) && toType == typeof(int)) || toType == typeof(object);
    }

    // Mirror a relational operator for the case where the member is on the right-hand side.
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
