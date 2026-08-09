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
    public MongoExpressionTranslator(IEntityType entityType)
    {
        _entityType = entityType;
    }

    /// <summary>
    /// Creates a two-scope translator for a CORRELATED reference-<c>SelectMany</c> inner filter: a member access
    /// rooted on <paramref name="outerParam"/> resolves against <paramref name="outerEntityType"/> at document
    /// root (no prefix); any other member resolves against <paramref name="innerEntityType"/> and is prefixed
    /// with <paramref name="innerPrefix"/> (the <c>_lookup_&lt;Nav&gt;</c> unwind scope). Outer members are
    /// identified by reference identity, never by name, so a member name shared between the two scopes never
    /// conflates them.
    /// </summary>
    public MongoExpressionTranslator(
        IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string innerPrefix)
    {
        _entityType = innerEntityType;
        _outerParam = outerParam;
        _outerEntityType = outerEntityType;
        _innerPrefix = innerPrefix;
    }

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
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path))
            return false;

        result = new MongoFieldExpression(property, path);
        return true;
    }

    /// <summary>
    /// Attempts to translate a numeric VALUE expression (a projection/computed leaf) to a
    /// <see cref="MongoExpression"/> — a member field-ref, constant/parameter, or arithmetic
    /// (<c>+ - * / %</c>) over numeric operands — reusing the same operand-resolution shapes a
    /// comparison's operands use (member / constant / parameter / nested arithmetic). Unlike
    /// <see cref="TryTranslate"/> (predicate/boolean shapes), this accepts a bare value. Returns
    /// <see langword="false"/> for a non-numeric/non-value shape, an integer-result division (MongoDB
    /// <c>$divide</c> is non-truncating, diverging from C#), or an operand whose property is not
    /// default-serialized (a computed value over a converted/represented stored form would diverge
    /// from CLR arithmetic).
    /// </summary>
    public bool TryTranslateValue(Expression valueBody, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // Do NOT Unwrap the top-level node here: the shared Unwrap strips ANY Convert/ConvertChecked
        // unconditionally (no widening/narrowing check), so unwrapping first would silently drop a top-level
        // narrowing/value-changing cast — e.g. (int)o.Weight, or (int)(o.A + o.B) — and return the raw wider
        // value (a silent wrong-data bug). Pass the RAW body so TranslateOperand's narrowing-aware Convert
        // branch (below, under allowNumericWidening) sees the top-level cast and rejects it. Guards A and B
        // both recurse through Unary/Binary nodes, so they operate correctly on the raw body too.
        // Guard A: reject any integer-result division in the subtree (spike-confirmed divergence).
        if (ContainsIntegerDivision(valueBody))
            return false;

        var translated = TranslateOperand(valueBody, allowNumericWidening: true);
        if (translated is null)
            return false;

        // Guard B: reject a value-converted / non-default-BsonRepresentation operand.
        if (!AllFieldsDefaultSerialized(translated))
            return false;

        result = translated;
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
    /// <para>
    /// Delegates to <see cref="TryResolveOwnedCollectionPath"/>, so it inherits that resolver's guards: the chain
    /// must be rooted at a <see cref="ParameterExpression"/> with at least one hop, every non-final hop must be an
    /// embedded single-reference navigation, and the FINAL hop must be an embedded collection NAVIGATION. That last
    /// requirement is the structural protection against a mapped scalar property that happens to share a
    /// navigation's name — a scalar's receiver is an entity, never a collection. Two-scope (cross-scope
    /// <c>SelectMany</c>) mode is declined outright by the same resolver.
    /// </para>
    /// <para>
    /// The <c>AsQueryable()</c> layer EF's nav-expansion wraps the navigation in is stripped here, exactly as the
    /// quantifier and count entry points do — the resolver itself walks only member / <c>EF.Property</c> hops.
    /// </para>
    /// <para>
    /// This is the ONLY caller of <see cref="TryResolveOwnedCollectionPath"/> that is reached from a
    /// PROJECTION rather than a predicate, so it inherits the by-name-retarget invariant recorded on
    /// <see cref="TranslateOperand"/>'s count branch: this translator is built by
    /// <see cref="NativeProjectionBinder"/> on the query root, where the only parameter in scope IS the query
    /// parameter, so there is no enclosing scope for a member access to be silently retargeted from.
    /// </para>
    /// </remarks>
    public bool TryTranslateOwnedCollectionArray(
        Expression expression,
        [NotNullWhen(true)] out MongoElementRefExpression? result)
    {
        var source = UnwrapAsQueryable(expression);
        if (TryResolveOwnedCollectionPath(source, out var arrayPath, out _))
        {
            // The UNWRAPPED source's type (the navigation's own collection type) — not the AsQueryable()
            // wrapper's IQueryable<T>. Nothing renders from it (an element ref emits "$" + Path), but a node
            // that misreports its own CLR type is a trap for a future reader.
            result = new MongoElementRefExpression(arrayPath, source.Type);
            return true;
        }

        result = null;
        return false;
    }

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

    private static bool ContainsIntegerDivision(Expression node)
        => node switch
        {
            BinaryExpression { NodeType: ExpressionType.Divide } d
                => IsIntegerType(d.Type) || ContainsIntegerDivision(d.Left) || ContainsIntegerDivision(d.Right),
            BinaryExpression b => ContainsIntegerDivision(b.Left) || ContainsIntegerDivision(b.Right),
            UnaryExpression u => ContainsIntegerDivision(u.Operand),
            _ => false
        };

    private static bool IsIntegerType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
    }

    private static bool AllFieldsDefaultSerialized(MongoExpression expr)
        => expr switch
        {
            MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
            MongoBinaryExpression b => AllFieldsDefaultSerialized(b.Left) && AllFieldsDefaultSerialized(b.Right),
            MongoConvertExpression c => AllFieldsDefaultSerialized(c.Operand),
            // A MongoSizeExpression falls into the catch-all below, deliberately: an array COUNT carries no
            // property serialization, so there is no converter / BsonRepresentation for it to diverge on.
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
    /// <para>
    /// <b>Why a sort key cannot use the general <see cref="Unwrap"/>.</b> That helper strips any
    /// <c>Convert</c>/<c>ConvertChecked</c> unconditionally, so a narrowing or signed/unsigned cast in a sort
    /// key was silently discarded and <c>$sort</c> ordered by the RAW STORED VALUE. MEASURED:
    /// <c>OrderBy(x =&gt; (int)x.D)</c> over a <c>double</c> returned a different order from BOTH in-memory
    /// LINQ and explicit <c>DriverLinq</c>, and <c>(uint)x.I</c> / <c>(short)x.I</c> were genuine order
    /// REVERSALS — wrong rows in the wrong order, with no exception, under the default mode.
    /// </para>
    /// <para>
    /// <b>What is ORDER-preserving (not necessarily value-preserving), and why each one is enough.</b> A
    /// nullable ↔ underlying convert (which EF inserts freely) changes no value; a boxing convert to
    /// <see cref="object"/> changes no value; and a WIDENING numeric convert is monotonic — <b>not</b> because
    /// every entry of <see cref="WideningNumericConversions"/> is value-preserving (it is not: <c>(int, float)</c>,
    /// <c>(long, float)</c>, <c>(long, double)</c>, <c>(ulong, float)</c> and <c>(ulong, double)</c> can collapse
    /// two distinct integers onto the same float/double, e.g. <c>(float)16777217L == (float)16777216L</c>) — but
    /// because IEEE round-to-nearest is monotone non-decreasing, so two source values that tie after rounding
    /// were already adjacent in source order and their relative order among themselves is exactly what
    /// "ties" means. Order preservation is the only property a sort key needs, so the weaker guarantee is
    /// sufficient here even though it would not be for, say, a `GroupBy` key (grouping needs VALUE equality,
    /// not order — see the caution below). Everything else stays in the tree, so <see cref="TryResolveMember"/>
    /// declines it and the caller falls through to <see cref="TryTranslateValue"/>, which can render an
    /// explicit <c>$toX</c> (or decline in turn).
    /// </para>
    /// <para>
    /// <b><see cref="WideningNumericConversions"/> tracks the C# implicit-conversion table closely but not
    /// exactly</b> — it omits <c>char</c>'s own widening conversions (<c>char</c> → <c>ushort</c>/<c>int</c>/
    /// <c>uint</c>/<c>long</c>/<c>ulong</c>/<c>float</c>/<c>double</c>/<c>decimal</c>). That omission only makes
    /// this method MORE conservative: an unrecognized widening still declines rather than mis-sorting, so a
    /// <c>char</c> cast in a sort key falls through to <see cref="TryTranslateValue"/> instead of going native
    /// here, which is safe, just not optimal.
    /// </para>
    /// <para>
    /// <b>Every other <see cref="TryTranslateField"/> caller is unaffected by this helper's weaker (order-only,
    /// not value) guarantee.</b> <see cref="TryTranslateField"/> has call sites beyond the sort key this helper
    /// guards — <c>NativeGroupByBinder</c>, <c>NativeCardinalityBinder</c>, <c>NativeProjectionBinder</c>, and
    /// <c>NativeSelectManyBinder</c> — but every one of them pre-filters its input to a <see cref="MemberExpression"/>
    /// (or an <c>EF.Property</c> call) before calling in, so the <c>while</c> loop above never executes for
    /// them and their behavior is byte-identical to the old blanket <see cref="Unwrap"/>. A FUTURE caller that
    /// does NOT pre-filter would inherit this ORDER-preservation rule where VALUE preservation is what its own
    /// semantics need — e.g. a <c>GroupBy</c> key over <c>(float)x.LongVal</c> would group by the raw
    /// <c>long</c> values where in-memory LINQ collapses two of them onto the same <c>float</c> key.
    /// </para>
    /// <para>
    /// <b>Do not "simplify" this back to <see cref="Unwrap"/>.</b> Doing so reintroduces the defect above; the
    /// functional pins in <c>NativeCastTests</c> are what catch it.
    /// </para>
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
                if (operand is MongoFieldExpression fieldExpr && fieldExpr.Property.IsNullable)
                    return null; // conservative: nullable bool Not could diverge from driver rendering
                return new MongoUnaryExpression(MongoUnaryOperator.Not, operand);
            }

            // --- Collection membership: Enumerable.Contains / List<T>.Contains / ICollection<T>.Contains ---

            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (!TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath))
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
                if (!TryResolveMember(Unwrap(receiver), out var property, out var fieldPath))
                    return null; // receiver must resolve to a bare string field

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
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType))
                    return null; // not an owned-collection source rooted at the query parameter

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

                // A CORRELATED element predicate — one reaching outside the element into the enclosing entity —
                // must be declined BEFORE the element-scoped translator ever sees it. See the helper's remarks:
                // the element-scoped translator resolves a member by NAME alone, so an enclosing-scoped access
                // whose name also exists on the element would be silently retargeted at the element. This
                // applies to All exactly as it does to Any.
                if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0]))
                    return null;

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

            // --- Nullable<T>.HasValue (EF-322 slice A5 / EF-400) ---
            //
            // Deliberately built as the SAME node an explicit `x.A != null` produces, rather than a new node
            // kind: MongoQueryLanguageRenderer already renders it, MongoExpressionNegator already inverts it
            // exactly ($eq/$ne partition every BSON value INCLUDING missing and null — see the negator's own
            // remarks for why that is true of equality and false of the four relational operators), so `!HasValue`
            // needs no code of its own. The rendered form selects null AND missing, which is what LINQ's
            // HasValue means for a stored element that is absent.
            //
            // This arm must sit BEFORE the bare-boolean-member default below: HasValue is a bool-typed
            // MemberExpression, so the default would reach TryResolveMember, fail to find a "HasValue"
            // property and decline the whole predicate.
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath))
                    return null;

                return new MongoBinaryExpression(
                    MongoBinaryOperator.NotEqual,
                    new MongoFieldExpression(nullableProperty, nullablePath),
                    new MongoConstantExpression(null, nullableProperty));
            }

            // --- Bare boolean member access (c.Active) ---

            default:
                if (TryResolveMember(node, out var boolProp, out var boolPath))
                {
                    // Accept only non-nullable bools; a nullable bool bare access could diverge.
                    if (boolProp!.ClrType != typeof(bool) || boolProp.IsNullable)
                        return null;
                    return new MongoFieldExpression(boolProp, boolPath!);
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
    /// <b>Query-native (SP1 parity, preserved exactly):</b> a bare member on exactly one side and a
    /// constant/parameter value on the other. Always produces the field on <see cref="MongoBinaryExpression.Left"/>
    /// — mirroring the operator when the member was originally on the right — so the renderer's
    /// <c>IsQueryNativeComparison</c> check keeps routing this shape to the indexable <c>$match</c> dialect.
    /// </item>
    /// <item>
    /// <b>Field-to-field / arithmetic-operand (EF-329, always <c>$expr</c>):</b> anything else — a member on
    /// both sides, or an arithmetic sub-expression on either side — translated via <see cref="TranslateOperand"/>
    /// without the "field on exactly one side" restriction, preserving operand order (no mirroring, since
    /// non-commutative comparisons need their original left/right order inside <c>$expr</c>).
    /// </item>
    /// </list>
    /// <para>
    /// <b>EF-403 (slice A1, Task 7) — the site-B fall-through.</b> The convert classification that
    /// <see cref="HasNumericConvert"/> performs for the query-native branch used to be a VETO on the whole
    /// comparison (<c>return null</c>). It is now a ROUTE: a cast the query-native branch cannot absorb no
    /// longer declines the comparison, it declines only that BRANCH, and control falls through to the
    /// <c>$expr</c> path below, where Task 3's <see cref="MongoConvertExpression"/> renders it as an explicit
    /// <c>$toX</c> over the field ref. The three-outcome classification itself (widening numeric /
    /// identity-like / decline) is UNCHANGED — this only changes what "decline" LANDS ON, and it is reached
    /// from both the member-left and the mirrored member-right branch.
    /// </para>
    /// <para>
    /// <b>THIS CHANGES RESULTS FOR A NARROWING CAST AGAINST A CONSTANT, DELIBERATELY, BY OWNER RULING. Do not
    /// "correct" it toward the driver.</b> MEASURED: for <c>(int)x.D &gt; 0</c> the driver's own LINQ provider
    /// DROPS the cast on this shape and answers as though the comparison were <c>x.D &gt; 0</c>; the native
    /// rendering emits <c>{$expr: {$gt: [{$toInt: "$D"}, 0]}}</c> and answers what C# answers. The owner ruled:
    /// take the CLR-correct answer and document the divergence. Two consequences follow, and both are recorded
    /// rather than merely implemented — (1) "query results are unchanged" is no longer a blanket argument for
    /// making the native path the default, and (2) <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> no longer
    /// restores the same answer for this shape. This is the OPPOSITE of the EF-359 accepted-divergence family
    /// (where native and driver-LINQ agree with each other and both differ from the CLR): here native is
    /// deliberately MORE correct than driver-LINQ. Pinned, three legs in one test, by
    /// <c>NativeCastTests.Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq</c>.
    /// </para>
    /// </remarks>
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
    {
        var leftUnwrapped = Unwrap(be.Left);
        var rightUnwrapped = Unwrap(be.Right);

        // --- Query-native shape: member on exactly one side, value on the other ---

        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath) && IsSimpleValue(rightUnwrapped))
        {
            // A WIDENING numeric cast or an IDENTITY-LIKE cast on the member side is absorbed (the field ref
            // is the stored field exactly as for a bare member); anything else still changes comparison
            // semantics, so it declines THIS BRANCH and — as of Task 7 — falls THROUGH to the general $expr
            // path below instead of declining the whole comparison, where the cast renders as an explicit
            // MongoConvertExpression ($toX). See HasNumericConvert for the three-outcome classification; this
            // site only consumes it, it does not alter it.
            if (HasNumericConvert(be.Left, leftProperty!.ClrType, out var leftWideningTarget, out var leftIdentityLike))
            {
                if (!CanFallThroughToExpr(leftProperty))
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

                return new MongoBinaryExpression(
                    mongoOp.Value, new MongoFieldExpression(leftProperty, leftPath!), valueExpr);
            }
        }

        // The mirrored shape, and the SAME fall-through rule. Note the $expr path does NOT mirror the operator
        // — it keeps the original left/right order, which is what a non-commutative comparison inside $expr
        // needs, so this is a genuinely different emission from the branch above rather than a spelling of it.
        else if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath)
                 && IsSimpleValue(leftUnwrapped))
        {
            if (HasNumericConvert(be.Right, rightProperty!.ClrType, out var rightWideningTarget, out var rightIdentityLike))
            {
                if (!CanFallThroughToExpr(rightProperty))
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

                return new MongoBinaryExpression(
                    mongoOp.Value, new MongoFieldExpression(rightProperty, rightPath!), valueExpr);
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
    /// EF-403 (slice A1, Task 7). Gates the site-B fall-through: <see langword="true"/> when a comparison whose
    /// cast the query-native branch could not absorb may fall through to the general <c>$expr</c> path,
    /// <see langword="false"/> when it must keep DECLINING the whole comparison as it did before Task 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is Guard B, reached from a third site — the same
    /// <see cref="NativeGroupByBinder.HasDefaultKeySerialization"/> predicate the GroupBy key, the
    /// <c>OfType</c> discriminator, and <see cref="AllFieldsDefaultSerialized"/> (Task 6's projection-leaf
    /// gate) already use — and it is LOAD-BEARING, not defence-in-depth. It was added because the
    /// fall-through MEASURABLY produced silently wrong rows without it.</b>
    /// </para>
    /// <para>
    /// <b>MEASURED, on the tree with the fall-through in place and this guard removed:</b> a <c>double</c>
    /// property carrying a value-TRANSFORMING converter (<c>v =&gt; v * 2</c>), CLR value <c>3.5</c>, stored
    /// <c>7.0</c>, under <c>Where(x =&gt; (int)x.Weight &gt; 3)</c>. The correct answer is ZERO rows
    /// (<c>(int)3.5 == 3</c>, and <c>3 &gt; 3</c> is false). The fall-through emits
    /// <c>{$expr: {$gt: [{$toInt: "$Weight"}, 3]}}</c> over the RAW STORED value, so <c>(int)7.0 == 7 &gt; 3</c>
    /// matched and it returned ONE row — silently, under the DEFAULT <c>Native</c> mode, for a shape that
    /// declined cleanly before this task. That is the exact failure class this codebase has now hit three
    /// times in this area (see the "GOVERNING HAZARD" note in <c>Query/AGENTS.md</c>), so it is closed here
    /// rather than recorded as a residual.
    /// </para>
    /// <para>
    /// <b>Why the member's own property is enough, rather than a walk of the translated tree.</b> This site is
    /// reached only from the query-native (member-vs-value) shape, so there is exactly ONE field in the
    /// comparison — the member this method is handed. The constant side carries no property at all on the
    /// <c>$expr</c> path (<see cref="TranslateOperand"/> serializes it via <c>BsonValue.Create</c>).
    /// <see cref="AllFieldsDefaultSerialized"/> over the translated operand would give the same answer for this
    /// shape; the property check is used because it can run BEFORE translation, so a declining comparison never
    /// builds a node it then discards.
    /// </para>
    /// <para>
    /// <b>PROVENANCE CORRECTION (fix round 1) — the earlier wording here was wrong, and the wrong kind of
    /// wrong.</b> This remark used to say the general <c>$expr</c> path below was left unguarded because it
    /// "has been reachable since EF-329 … a shipped path". That is true of a PLAIN field-to-field comparison
    /// and FALSE of the cast subset: <see cref="TranslateOperand"/>'s <see cref="MongoConvertExpression"/>
    /// branch was introduced by Task 3 of THIS slice (<c>94101da5</c>, unreleased), so
    /// <c>Where(o =&gt; (int)o.EncWeight &gt; o.Other)</c> was a WITHIN-SLICE regression, not an inherited
    /// exposure — and "don't touch a shipped path" never applied to it. It was MEASURED against the slice base
    /// and CLOSED at <see cref="TranslateOperand"/>'s own convert branch (see the guard there for the figures).
    /// A wrong provenance is what makes a future editor defer the wrong thing, so it is corrected here rather
    /// than quietly replaced.
    /// </para>
    /// <para>
    /// <b>Relationship to that deeper guard, measured rather than assumed.</b> Once
    /// <see cref="TranslateOperand"/> refuses to build a <see cref="MongoConvertExpression"/> over a
    /// non-default-serialized field, this early check is SUBSUMED for every shape reachable here: a member-side
    /// convert that <see cref="HasNumericConvert"/> declines is exactly a convert
    /// <see cref="TranslateOperand"/> would then build (or already refuse, for an unrenderable target).
    /// <b>MEASURED, and the number is stated rather than softened: forcing this method to return
    /// <see langword="true"/> unconditionally turns ZERO tests red — 0 of 33 in <c>NativeCastTests</c> and 0 of
    /// 121 in <c>MongoExpressionTranslatorTests</c>.</b> The same mutation applied to the deeper guard turns 1
    /// red (<c>NativeCastTests.Field_to_field_cast_over_a_value_converted_property_still_declines</c>, with
    /// <c>returned p</c> — the wrong row — in the assertion message), which is what shows the two are not
    /// symmetric: the deeper guard is netted, this one is not.
    /// </para>
    /// <para>
    /// <b>It is kept anyway, as an EARLY-OUT, and that word is load-bearing: it is NOT an independent
    /// protection and must not be cited as a second line of defence.</b> Two reasons to keep it: it declines
    /// BEFORE translation, so a doomed comparison never builds a node it discards; and it states the site-B
    /// fall-through's own precondition AT the fall-through, rather than leaving it to a guard two call levels
    /// away that a future edit to <see cref="TranslateOperand"/> could move. If a later change makes that
    /// double statement a liability rather than an aid, DELETE this method — do not add a test to "cover" it,
    /// which would only pin the redundancy in place.
    /// </para>
    /// <para>
    /// <b>What is still unguarded, stated narrowly:</b> a PLAIN field-to-field / arithmetic comparison with no
    /// cast (<c>o.EncWeight &gt; o.Other</c>). That path predates this slice, has shipped, and its own question
    /// needs its own measurement. Neither this method nor the guard in <see cref="TranslateOperand"/> is a
    /// general statement about the <c>$expr</c> dialect's safety.
    /// </para>
    /// </remarks>
    private static bool CanFallThroughToExpr(IProperty property)
        => NativeGroupByBinder.HasDefaultKeySerialization(property);

    /// <summary>
    /// Chooses the serialization context for the CONSTANT side of a query-native comparison: the property's
    /// own serializer (<paramref name="property"/>), or none at all (<see langword="null"/> ⇒
    /// <c>BsonValue.Create</c> over the constant's own CLR value, i.e. the COMPARISON's type).
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF-403 (slice A1, Task 5). <paramref name="isIdentityLikeConvert"/> is checked FIRST and
    /// unconditionally overrides the widening rule below it: an identity-like convert (enum ↔ underlying,
    /// <c>char</c> → <c>int</c>, boxing to <c>object</c>) does not move the comparison to a different STORED
    /// representation — it is the exact same stored value under a different declared CLR type — so the
    /// constant must go through the SAME serializer the property itself uses. This is NOT the same rule as
    /// the widening arm below, and collapsing the two is exactly the blanket mistake Task 4 measured wrong
    /// (see <see cref="HasNumericConvert"/>): an enum-as-string property's constant would render as a raw
    /// number instead of the mapped string, and the comparison would silently match nothing.
    /// </para>
    /// <para>
    /// <b>THIS RULE IS NOT OPTIONAL, AND IT IS NOT BLANKET. Both halves are MEASURED (spike §6.2).</b>
    /// </para>
    /// <para>
    /// <b>Why the comparison type is needed at all.</b> Absorbing a widening cast moves the comparison from the
    /// STORED type to the CAST's type. Serializing the constant through the stored property COERCES it to the
    /// stored CLR type — so a fractional constant is TRUNCATED to an integral stored type and the query returns
    /// WRONG ROWS, silently, under the default <c>Native</c> mode. MEASURED: <c>(double)x.I &gt;= 2.5</c> emitted
    /// <c>{"I": {"$gte": 2}}</c> and returned <c>b,c,d,e</c> where <c>b,c,e</c> is correct (both the CLR and
    /// driver-LINQ). With the comparison type it emits <c>{"I": {"$gte": 2.5}}</c>, returns the correct rows,
    /// and the emitted MQL matches the driver byte for byte.
    /// </para>
    /// <para>
    /// <b>Why it must NOT be applied unconditionally.</b> The spike's blanket variant (re-serialize EVERY
    /// convert layer's constant in the comparison type) cost 5 specification and 23 functional failures, all
    /// enum-as-string or value-converted properties. RE-MEASURED here, on this tree, by forcing this method to
    /// return <see langword="null"/> unconditionally: <b>5 specification failures — the identical set,
    /// <c>BuiltInDataTypesMongoTest.Can_query_using_any_data_type{,_shadow,_nullable_shadow}</c> +
    /// <c>Can_query_using_any_nullable_data_type{,_as_literal}</c> — and 47 functional</b>, essentially all
    /// <c>ValueConverterTests.*_can_deserialize_and_query_from_*</c> plus
    /// <c>NativeGateRoutingTests.A_string_as_objectId_where_equals_parity</c>. (47 rather than 23 because that
    /// mutation is BROADER than the spike's: it drops the property serializer for every query-native
    /// comparison, cast or not, where the spike's only touched convert layers.)
    /// </para>
    /// <para>
    /// <b>Which guard actually holds the ENUM arm, corrected against the spike's framing:</b> not this
    /// conjunct — <see cref="HasNumericConvert"/> is. An <c>enum -&gt; underlying</c> convert is not in
    /// <c>WideningNumericConversions</c> (that table holds primitive pairs only), so as of Task 4 it still
    /// DECLINED and never reached this method with a tolerated target at all. MEASURED (Task 4): neither
    /// <c>ValueConverterTests.Enum_can_deserialize_and_query_from_string</c> nor
    /// <c>NativeGateRoutingTests.A_enum_as_string_where_equals_parity</c> — the two the spike named — was among
    /// the 47 above.
    /// </para>
    /// <para>
    /// <b>Superseded by Task 5, and stated precisely because the sentence above is now historical, not
    /// current.</b> An enum ↔ underlying convert now DOES reach this method — via
    /// <paramref name="isIdentityLikeConvert"/>, a THIRD, separately-tracked signal, never via
    /// <paramref name="toleratedWideningTarget"/>. <see cref="HasNumericConvert"/> still never puts an
    /// enum/underlying pair into <c>WideningNumericConversions</c>'s bucket; it now has a second bucket for it
    /// instead. The two buckets exist because they demand OPPOSITE constant treatment (see this method's own
    /// remarks above), so merging them back into one flag is exactly the mistake this task's brief warns
    /// against repeating.
    /// </para>
    /// <para>
    /// <b>So what THIS conjunct holds is the value-converted / non-default-represented property reached through
    /// a genuinely widening numeric cast — and that is also the fixture that SEPARATES
    /// <see cref="NativeGroupByBinder.HasDefaultKeySerialization"/> from the cheaper "the property's CLR type is
    /// not an enum", which the spike left UNVERIFIED (§6.2, §11).</b> A plain <c>int</c> carried through a value
    /// converter to a string satisfies "not an enum" but NOT <c>HasDefaultKeySerialization</c>, and the two
    /// rules disagree observably: with the shipped conjunct <c>(long)x.Coded &gt;= 2L</c> emits
    /// <c>{"Coded": {"$gte": "2"}}</c> and returns rows, while "not an enum" would emit the raw number <c>2</c>,
    /// which MongoDB type-brackets against a string-stored field, returning NO rows. MEASURED: dropping ONLY
    /// this conjunct (leaving the tolerated-target one) turns exactly THREE tests red across the whole EF10
    /// solution, and zero specification tests — the two functional
    /// <c>NativeCastTests.Widening_cast_comparison_over_a_value_converted_property_keeps_the_property_serializer</c>
    /// and <c>…over_a_value_transforming_converter_returns_the_right_rows</c>, plus the unit
    /// <c>MongoExpressionTranslatorTests.Widening_cast_over_a_value_converted_property_keeps_the_property_serializer</c>.
    /// Those three are the conjunct's ONLY nets in the tree, so do not delete it as untested.
    /// </para>
    /// <para>
    /// <b>Two sub-families are reachable, and only one of them fails loudly</b> — which is why this conjunct is
    /// load-bearing rather than defence-in-depth. A <b>value-TRANSFORMING</b> numeric converter (e.g.
    /// <c>v =&gt; v * 2</c>) would emit the UNCONVERTED constant against converted stored values and return
    /// silently WRONG ROWS; a <b>re-encoding</b> converter or <c>BsonRepresentation</c> (int stored as a string)
    /// would emit a raw number against a string field, which MongoDB type-brackets to ZERO rows. Both are
    /// covered end to end — the transforming case by
    /// <c>NativeCastTests.Widening_cast_comparison_over_a_value_transforming_converter_returns_the_right_rows</c>.
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

    private static MongoBinaryOperator? MapArithmeticOperator(ExpressionType nodeType)
        => nodeType switch
        {
            ExpressionType.Add => MongoBinaryOperator.Add,
            ExpressionType.Subtract => MongoBinaryOperator.Subtract,
            ExpressionType.Multiply => MongoBinaryOperator.Multiply,
            ExpressionType.Divide => MongoBinaryOperator.Divide,
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
    /// <para>
    /// A numeric-widening <see cref="UnaryExpression"/> (e.g. <c>(double)c.Age</c>) is rejected here rather than
    /// silently stripped <b>on the comparison-operand path</b> (<paramref name="allowNumericWidening"/> is
    /// <see langword="false"/>, the default): empirically, the driver's own LINQ translator renders a numeric
    /// cast on a bare field-to-field comparison operand as an explicit <c>$toDouble</c> conversion, but renders
    /// the very same cast on an arithmetic operand (e.g. <c>(double)c.Age + c.Score</c>) by simply dropping it —
    /// an inconsistent, shape-dependent rule. Reproducing it exactly would require re-deriving the driver's
    /// numeric-promotion logic; falling back to driver-LINQ for any numeric cast inside this operand position
    /// avoids silently diverging from it. A benign (non-numeric, e.g. nullable-widening) convert is still
    /// unwrapped, matching <see cref="Unwrap"/> elsewhere in this class.
    /// </para>
    /// <para>
    /// The two <see cref="TranslateComparison"/> call sites keep the default (<paramref name="allowNumericWidening"/>
    /// = <see langword="false"/>), so the EF-329 comparison path is byte-identical. <see cref="TryTranslateValue"/>
    /// (a computed VALUE leaf) passes <see langword="true"/>: a value leaf has none of the driver-rendering
    /// inconsistency above to avoid, and MongoDB's <c>$add</c>/<c>$subtract</c>/<c>$multiply</c>/<c>$divide</c>/<c>$mod</c>
    /// operate on the raw BSON numeric value regardless of declared CLR width, so unwrapping a WIDENING conversion
    /// (never a narrowing one — a narrowing/truncating cast like <c>(int)o.Weight</c> is still rejected) reproduces
    /// C#'s own implicit-numeric-promotion semantics exactly (e.g. the compiler-inserted <c>int -&gt; double</c> in
    /// <c>o.Weight / o.Count</c>).
    /// </para>
    /// </remarks>
    private MongoExpression? TranslateOperand(Expression node, bool allowNumericWidening = false)
    {
        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            var fromType = Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type;
            var toType = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
            if (fromType != toType && !(allowNumericWidening && IsWideningNumericConvert(fromType, toType)))
            {
                // A type-changing cast MQL can express becomes an explicit $toX over the translated operand —
                // which is what the driver's own LINQ provider emits in this position (MEASURED, spike §3.1).
                // An unrenderable target still declines, matching the driver, which throws for those.
                if (MongoConvertExpression.ToOperatorFor(toType) is null)
                    return null;

                var converted = TranslateOperand(unary.Operand, allowNumericWidening);
                if (converted is null)
                    return null;

                // GUARD B, ON THE CAST SUBSET ONLY (EF-403 slice A1, Task 7 fix round 1). A $toX renders over
                // the RAW STORED value, so a value-converted / non-default-represented field underneath it is
                // converted at the WRONG point: the cast is applied to the provider value instead of the model
                // value. MEASURED, on this branch as originally shipped by Task 3 (94101da5) and again at the
                // slice base for comparison — `Where(o => (int)o.EncWeight > o.Other)` over a double carrying
                // `v => v * 2`, CLR 3.5 / stored 7.0, against Other = 5:
                //
                //   slice base fd6bd8ba : NativeOnly threw, default Native THREW (declined, then the driver's
                //                         own ValueConverterSerializer limitation), in-memory LINQ []
                //   with this branch    : NativeOnly returned [p], default Native RETURNED [p]  <-- WRONG
                //   correct answer      : [] ((int)3.5 == 3, and 3 > 5 is false)
                //
                // So this was a WITHIN-SLICE regression (Task 3 is unreleased), not a pre-existing EF-329-era
                // exposure and not an EF-359-family accepted divergence — the driver does not agree with
                // native here, it cannot answer this shape at all. Closed at the branch that originates the
                // node rather than at any one call site, so every caller inherits it.
                //
                // SCOPE: this guards the CAST subset only. A PLAIN field-to-field comparison
                // (`o.EncWeight > o.Other`, no cast) still goes native unguarded — that path predates the
                // slice, has shipped, and its own question (does comparing two converted fields against each
                // other need the same treatment?) needs its own measurement. Tracked separately; do not read
                // this guard as a claim about that.
                if (!AllFieldsDefaultSerialized(converted))
                    return null;

                return new MongoConvertExpression(converted, toType);
            }

            return TranslateOperand(unary.Operand, allowNumericWidening); // benign or widening convert — unwrap and recurse
        }

        if (TryResolveMember(node, out var property, out var fieldPath))
            return new MongoFieldExpression(property, fieldPath!);

        // An OWNED-collection element count — b.Posts.Count / .Count() / .LongCount(). The renderer decides the
        // dialect: a comparison against an admissible integer constant becomes an array-index existence test,
        // anything else routes to $expr with a null-safe $size (see MongoQueryLanguageRenderer).
        //
        // Ordering note: this runs AFTER the TryResolveMember attempt above, which means an entity with a real
        // mapped scalar property called "Count" resolves as that FIELD. That ordering is defence-in-depth and an
        // efficiency guard, NOT the safety property — measured by moving this block ahead of TryResolveMember,
        // which turned no test red. The actual protection is structural, in TryResolveOwnedCollectionPath: it
        // requires the chain to be rooted at the query parameter with at least one hop, every non-final hop to be
        // an embedded single reference, and the FINAL hop to be an embedded collection NAVIGATION. A mapped
        // scalar's receiver is an entity, never a collection, so a name collision can never resolve — `o.Count`
        // declines on the zero-hop check, `b.Address.Count` on the final-hop check. The resolver also declines
        // outright in two-scope mode, so a cross-scope count stays out of scope for free.
        //
        // INHERITED INVARIANT, worth knowing before adding another caller: TryResolveOwnedCollectionPath does not
        // check WHICH ParameterExpression roots the chain (see its own remarks), so its safety depends on every
        // non-root single-scope translator being constructed only after an identity guard has run — the quantifier
        // arm's ReferencesEnclosingScope, or NativeSelectManyBinder's ReferencesParameter, which routes an
        // outer-referencing layer to the two-scope constructor where this resolver declines outright. A future
        // caller that builds a non-root single-scope translator WITHOUT such a guard reopens a by-name retarget
        // (an enclosing member resolved against the inner scope because the two types share a property name),
        // which is a wrong-rows failure, not a decline.
        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType))
        {
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // A FILTERED count (EF-359). The element predicate is translated exactly as a quantifier's is — same
            // correlated-scope guard, same element-scoped child translator — so it inherits both invariants rather
            // than re-deriving them.
            //
            // The correlated guard is LOAD-BEARING, not defensive: single-scope TryResolveMember resolves a member
            // by NAME with no parameter-identity check, so an enclosing-scoped access whose name also exists on the
            // element would be silently retargeted AT THE ELEMENT — wrong rows under the default Native mode, where
            // the pre-slice fallback was correct. Note a $filter cond CAN legally reference the enclosing document
            // (unlike $elemMatch, which cannot at all), so correlated support is a deferrable capability here rather
            // than an impossibility — it needs a two-scope element translator.
            if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0]))
                return null;

            var countElementTranslator = new MongoExpressionTranslator(countElementType);
            if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
                return null;

            // Decline at TRANSLATE time for anything the aggregation renderer cannot express. As measured (fix
            // round 1 of this task), this check is DEFENCE-IN-DEPTH rather than the thing that currently changes
            // observable behaviour for the PREDICATE spelling this method builds: MongoAggregationExpressionRenderer
            // .Render already has its own catch-all that throws the SAME NativeTranslationNotSupportedException for
            // a node kind CanRender declines, and MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline's
            // TYPED catch (NativeTranslationNotSupportedException) when (mode != NativeOnly) — wrapping
            // MongoSelectLowerer.Lower + MongoPipelineFactory.Create — already converts that render-time throw into
            // a graceful driver-LINQ fallback under Native. (Corrected citation, fix round 1: this used to say
            // "MongoShapedQueryCompilingExpressionVisitor's broad catch (Exception) ... around native-pipeline
            // construction" — wrong on two counts. The ONLY broad catch (Exception) in that visitor wraps
            // STREAMING-shaper construction only, and on catching falls back to the native DOM shaper, not to
            // driver-LINQ; it has no bearing here. The catch that actually matters is the TYPED one in
            // TryBuildPipeline, cited above.) So removing this check does not
            // flip Element_predicate_outside_the_renderable_set_declines (the functional decline test), mutation-
            // verified. What removing it DOES break is a translator-level unit assertion with no such safety net:
            // TryTranslateBlogPredicate(b => b.Posts.Count(p => p.Heading!.StartsWith("h")) > 0) returns a
            // MongoBinaryExpression instead of null (MongoExpressionTranslatorTests
            // .Element_predicate_outside_the_renderable_set_declines_at_translate_time) — CanRender is what keeps
            // TryTranslate's own contract (null for an unsupported shape) intact independent of what a particular
            // caller's exception-handling happens to paper over.
            //
            // SETTLED (EF-359 fix round 2 — the repo owner has ruled; this replaces the fix-round-1 neutral
            // placeholder, which itself replaced an earlier claim that a follow-up re-measurement had
            // contradicted). This check has NO CORRECTNESS role: the one place a wrong admission would be
            // dangerous — a filtered count's element predicate reaching $expr while nested inside $elemMatch (a
            // hard server error there) — is independently prevented by
            // MongoQueryLanguageRenderer.IsQueryDialectRenderable, not by this check (see "the nested-in-
            // quantifier row" in MongoFilteredSizeExpression's own remarks). On the PREDICATE spelling this method
            // also builds, both routes (check present vs. removed) end in a graceful driver-LINQ fallback — see
            // the paragraph above — so only the PROJECTION spelling (Task 3, reusing this exact code path via
            // NativeProjectionBinder's TryTranslateValue call) is materially affected by whether this check exists.
            //
            // MEASURED, precisely: with this check PRESENT (shipped), an element predicate with no aggregation-
            // dialect rendering reaching a projection leaf — e.g.
            // Select(b => new { b.Title, N = b.Posts.Count(p => p.Heading!.StartsWith("h")) }) — throws the
            // pre-existing EF-359 InvalidOperationException ("could not be translated") identically under Native,
            // DriverLinq AND NativeOnly. With the check REMOVED, the same query returns the correct value under
            // Native and DriverLinq, and declines cleanly (NativeTranslationNotSupportedException) under
            // NativeOnly — because the driver's own LINQ rendering of the fallback emits the SAME alias the
            // shaper reads, so the fallback genuinely works for this shape; it does not "fall back onto the
            // pre-existing crash" the way the design doc's original justification assumed for this check.
            // THAT JUSTIFICATION IS MEASURED FALSE and must not be repeated as a reason to keep this check.
            //
            // The check is kept anyway, DELIBERATELY, per the owner's explicit ruling, on SCOPE grounds rather
            // than correctness ones: EF-359 fixes the renderable filtered-count cases; widening admissibility
            // further by deleting a guard is exactly the direction that produced two live silent-wrong-data bugs
            // in the owned array-valued projection slice (EF-322 owned-data slice 8 — see the "GOVERNING HAZARD"
            // and "alias-agreement rule" notes in Query/AGENTS.md). The improvement this leaves on the table is
            // filed as EF-365 ("A non-renderable element predicate in a filtered Count(pred) projection hard-
            // fails where a graceful fallback is available") — its description carries the two measurement
            // tables and this mechanism, and records the scope of the eventual fix (delete this call site, then
            // delete the now-callerless CanRender classifier and its unit tests, and re-baseline the StartsWith
            // residual-decline test). EF-365 also records the breadth that still needs verifying before that fix
            // ships: only StartsWith was measured here — regex-family predicates (Contains → $in), unary Not, a
            // bare nullable bool, and a MIXED projection (a declining leaf alongside an admitted one) are
            // UNVERIFIED and must be checked, not assumed to behave the same way.
            if (!MongoAggregationExpressionRenderer.CanRender(elementPredicate))
                return null;

            return new MongoFilteredSizeExpression(arrayPath, elementPredicate, node.Type);
        }

        // Restrict to numeric operand types: ExpressionType.Add on strings is compiler-generated
        // concatenation (string.Concat), not arithmetic — it has no $add equivalent (confirmed empirically:
        // the driver server rejects "$add" on strings with "$add only supports numeric or date types").
        if (node is BinaryExpression arith && MapArithmeticOperator(arith.NodeType) is { } arithOp && IsNumericType(arith.Type))
        {
            var left = TranslateOperand(arith.Left, allowNumericWidening);
            if (left is null)
                return null;

            var right = TranslateOperand(arith.Right, allowNumericWidening);
            if (right is null)
                return null;

            return new MongoBinaryExpression(arithOp, left, right);
        }

        // A bare constant/parameter operand has no associated property for serialization context — these
        // are pure numeric $expr operands, not stored field values, so they serialize via BsonValue.Create.
        return TranslateValue(node, forSerialization: null);
    }

    // True for the numeric CLR types $add/$subtract/$multiply/$divide/$mod accept — used to keep
    // TranslateOperand's arithmetic-operand handling scoped to genuine arithmetic (excluding e.g. string
    // concatenation, which also compiles to ExpressionType.Add but is not representable as "$add").
    private static bool IsNumericType(Type type)
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
    /// EF-403 (slice A1, Task 5). On a <see langword="false"/> return: <see langword="true"/> when at least one
    /// absorbed layer was IDENTITY-LIKE (enum ↔ its own underlying type or a WIDENING of it, <c>char</c> →
    /// <c>int</c>, or boxing to <c>object</c>) rather than a widening numeric conversion. This is a SEPARATE,
    /// THIRD outcome from <paramref name="toleratedWideningTarget"/> — both are "tolerate", but they demand
    /// OPPOSITE constant treatment (see <see cref="ConstantSerializationContext"/>), so they are tracked
    /// independently rather than folded into one flag. <b>PRECEDENCE (fix round 1):</b> if the SAME chain also
    /// sets <paramref name="toleratedWideningTarget"/> (e.g. <c>(object)(double)x.I</c> — boxing wrapping a
    /// widening numeric cast), this parameter comes back <see langword="false"/> — the widening arm wins,
    /// because it determines the type the comparison actually happens in, and an identity-like wrapper around
    /// it changes nothing about that. See the precedence comment at this method's final assignment.
    /// </param>
    /// <remarks>
    /// <para>
    /// EF-403 (slice A1, Task 4). This method used to be a pure VETO — any layer whose target differed from the
    /// property's own CLR type declined the whole comparison, with no fall-through to the <c>$expr</c> path
    /// below it. MEASURED (spike §7.2): it is the single highest-volume cast decline site in the whole
    /// specification suite (140 declining occurrences, against 40 at <c>TranslateOperand</c>'s <c>Convert</c>
    /// branch, and almost none of those 40 numeric) — so relaxing it, not <c>TranslateOperand</c>, is where
    /// A1's yield lives. It now CLASSIFIES instead: a WIDENING numeric layer is tolerated (absorbed, i.e. the
    /// field ref is the stored field exactly as for a bare member), anything else still declines.
    /// </para>
    /// <para>
    /// EF-403 (slice A1, Task 5) widens the tolerated set a second time, with a THIRD outcome rather than
    /// folding into the widening one: an IDENTITY-LIKE layer (see <see cref="IsIdentityLikeConvert"/>) is ALSO
    /// tolerated — the comparison stays on the SAME stored value, so the field ref is again the stored field
    /// unchanged — but it is reported via <paramref name="isIdentityLikeConvert"/>, never via
    /// <paramref name="toleratedWideningTarget"/>, because the two outcomes need opposite constant serialization
    /// (see <see cref="ConstantSerializationContext"/>). Keep the three outcomes — widening / identity-like /
    /// decline — visibly distinct in this method's body; collapsing the first two into one "tolerate" bucket is
    /// the exact blanket rule Task 4 measured to be wrong for a DIFFERENT pair of arms, and doing it again here
    /// would reintroduce the same class of defect for enum-as-string constants.
    /// </para>
    /// <para>
    /// <b>Absorbing rather than rendering a <c>$toX</c> is justified by DRIVER PARITY, not by value
    /// preservation — the value-preservation half of that argument is FALSE for two admitted pairs and must not
    /// be restated.</b> What holds unconditionally: on the query-dialect (member-vs-constant) path the driver's
    /// own LINQ provider drops a widening cast entirely (spike §3.3, P10), so absorbing it keeps native and
    /// driver-LINQ — this branch's oracle — in agreement, which is the whole basis for the relaxation.
    /// </para>
    /// <para>
    /// What does NOT hold: <c>(long, double)</c> and <c>(ulong, double)</c> are in
    /// <see cref="WideningNumericConversions"/> and <see cref="MongoConvertExpression.ToOperatorFor"/> admits
    /// <see cref="double"/>, so <c>(double)x.SomeLong == 9007199254740992.0</c> is absorbed — and those pairs are
    /// <b>not</b> value-preserving above 2^53 (see <see cref="UnwrapOrderPreserving"/>'s own remarks, which
    /// record the same family for the sort path: IEEE round-to-nearest collapses distinct integers onto one
    /// double). Native then compares the RAW stored <see cref="long"/> against the double, where in-memory LINQ
    /// would have rounded the operand first, so the two can disagree. Native still equals driver-LINQ (the
    /// driver drops the cast identically), so this joins the EF-359 <b>accepted-divergence</b> family — native
    /// and driver-LINQ agree with each other and both differ from the CLR — rather than being wrong data.
    /// MEASURED end-to-end by
    /// <c>NativeCastTests.Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq</c>; UNVERIFIED
    /// at any other boundary (no probe crossed <see cref="ulong"/>'s range, or fed a NaN/Infinity).
    /// </para>
    /// <para>
    /// The admissible TARGET set is <see cref="MongoConvertExpression.ToOperatorFor"/>'s — the single definition
    /// of what MQL can express — rather than a second hand-rolled list. It costs nothing here: MEASURED (spike
    /// §7.2), every widening pair in the suite's declining population targets <c>Int32</c>, <c>Int64</c> or
    /// <c>Double</c>, all of which it admits. What it buys is that a tolerated comparison type is always one
    /// whose constant <c>BsonValue.Create</c> renders unambiguously, which the constant rule below relies on.
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
            // classified: `from == to`, so the widening test below would answer false and decline it. This is
            // not hypothetical — MEASURED, it is the ONLY thing separating the 16 specification cases this arm
            // converts from the spike's predicted 18: EF lowers
            // `Where_method_call_nullable_type_reverse_closure_via_query_cache` to
            // `Convert(Convert(e.EmployeeID, Int64), Nullable<Int64>) > <param>`, i.e. a genuine uint -> long
            // widening wearing a nullable lift on the outside. The pre-existing `to != underlying` test below
            // does not catch it either, because it compares against the PROPERTY's type, not this layer's own
            // operand type.
            if (from != to && to != underlying)
            {
                // Three outcomes, kept VISIBLY DISTINCT — collapsing the first two ("tolerate") loses the
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
                    // precedence (see the final assignment below) — fix round 1 found this reachable via
                    // (object)(double)x.I, where the outer boxing layer must NOT mask the inner widening one.
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

        // PRECEDENCE, fix round 1: a widening target found ANYWHERE in the chain wins over an identity-like
        // layer found anywhere else in it. Both flags can be set by a single chain — (object)(double)x.I sets
        // toleratedWideningTarget=double (from the inner cast) AND sawIdentityLike=true (from the outer boxing
        // cast) — and ConstantSerializationContext checks isIdentityLikeConvert FIRST, so reporting both would
        // let the identity-like arm's "always keep the property serializer" rule mask the widening arm's
        // truncation protection, reopening case 9's silent-wrong-rows defect for a comparison that used to
        // decline outright before this task. Widening must win because it is what determines the type the
        // comparison ACTUALLY happens in; an identity-like layer merely wrapping it changes nothing about that.
        isIdentityLikeConvert = sawIdentityLike && toleratedWideningTarget is null;
        return false;
    }

    /// <summary>
    /// Identity-like converts: the comparison happens on the SAME stored value, so the member side needs no
    /// <c>$toX</c> and the constant KEEPS the property's serializer (that is how an enum-as-string constant
    /// renders at all — see <see cref="ConstantSerializationContext"/>'s remarks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF-403 (slice A1, Task 5). Four shapes, all identity-like:
    /// <list type="bullet">
    /// <item>enum → its own <see cref="Enum.GetUnderlyingType(Type)"/> OR a WIDENING of it, and back (see the
    /// promotion correction below)</item>
    /// <item><see cref="char"/> → <see cref="int"/></item>
    /// <item><c>T</c> → <see cref="object"/> (boxing for a value type; a plain reference upcast for a reference
    /// type — see the naming note below)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Fix round 1 correction — an enum backed by a SUB-<c>int</c> underlying type (<c>short</c>,
    /// <c>byte</c>, <c>ushort</c>, <c>sbyte</c>) reaches this method with a Convert target WIDER than its own
    /// underlying type, and the exact-match rule alone declined it.</b> C# applies its ordinary binary numeric
    /// promotion to an enum equality/relational comparison exactly as it would to any other sub-<c>int</c>
    /// integral operand: the enum unwraps to its underlying type first, and if THAT is narrower than
    /// <see cref="int"/> the comparison promotes to <see cref="int"/> — so a <c>short</c>-backed enum's member
    /// side arrives as <c>Convert(member, Int32)</c>, not <c>Convert(member, Int16)</c>. MEASURED by a compiled
    /// probe (not merely reasoned): <c>Enum64</c>/<c>Enum32</c>-backed comparisons arrive as
    /// <c>Convert(m, Int64)</c>/<c>Convert(m, Int32)</c> (their own underlying type, exact match, unaffected by
    /// this fix); <c>Enum16</c>/<c>Enum8</c>/<c>EnumU16</c>/<c>EnumS8</c>-backed comparisons ALL arrive as
    /// <c>Convert(m, Int32)</c> — a genuine widening of the underlying type, not an exact match. The exact-match
    /// rule alone declined all four of the narrower-than-<c>int</c> backings; this is why the shipped arm
    /// admits <c>toType == underlying OR IsWideningNumericConvert(underlying, toType)</c> for the enum
    /// direction, checked against the enum's OWN underlying type, not the outer <c>int</c>. This is exactly the
    /// gap that cost this task its whole <c>BuiltInDataTypesMongoTest.Can_query_using_any_(nullable_)data_type*</c>
    /// family on first delivery (6 specification cases) — every enum fixture added before this fix was
    /// <c>int</c>-backed and so could never expose a promotion the rule already handled by exact match.
    /// </para>
    /// <para>
    /// <b>The reverse direction (<c>underlying → enum</c>) is widened symmetrically, for consistency with the
    /// forward direction's own "and back" contract, though no failing case forced it.</b> A member whose CLR
    /// type is NARROWER than an enum's own underlying type, converted up to that enum type, is the same shape
    /// in reverse — e.g. a <c>byte</c> member compared against a <see cref="int"/>-backed enum constant.
    /// </para>
    /// <para>
    /// <b>What this fix deliberately still declines, and why that is correct, not incomplete:</b> a
    /// LONG-backed enum narrowed to <see cref="int"/> (<c>(int)someLongBackedEnum == 5</c>) is a genuine
    /// narrowing of the underlying type — <c>IsWideningNumericConvert(Int64, Int32)</c> is false — and stays
    /// declined. This is the shape the design review named as needing to stay closed, and it does: the widening
    /// check is directional (narrower-underlying → wider-target only), never the reverse.
    /// </para>
    /// <para>
    /// <b>Known edge, recorded rather than fixed:</b> <c>char</c> → <c>long</c>/<c>double</c> still declines —
    /// only <c>char</c> → <c>int</c> is admitted, per the brief. This is conservative (a decline still falls
    /// back correctly), and it matches this same file's <c>WideningNumericConversions</c> table, which
    /// similarly omits <c>char</c>'s own widening conversions and is documented there as making callers "MORE
    /// conservative", not less.
    /// </para>
    /// <para>
    /// <b>Naming, made precise:</b> <c>toType == typeof(object)</c> is broader than "boxing" — it also admits a
    /// plain REFERENCE upcast for a reference-typed mapped member (e.g. <c>(object)x.SomeString</c>), which
    /// involves no boxing at all. Both are identity-like for the same reason: the stored value does not change,
    /// only its declared CLR type does. The method name is kept for its "widened over Task 4's numeric/enum
    /// arms" resonance, not as a literal claim that every admitted shape boxes.
    /// </para>
    /// </remarks>
    private static bool IsIdentityLikeConvert(Type fromType, Type toType)
    {
        if (fromType.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(fromType);
            // Fix round 2: the widening disjunct is restricted to an INTEGRAL target. Without this, an
            // int-backed enum cast to double/float/decimal (e.g. `(double)x.EnumProp >= 2.5`) also satisfies
            // IsWideningNumericConvert(Int32, Double) and was wrongly admitted as identity-like — the constant
            // then kept the ENUM property's serializer, and BsonValueSerializer.Coerce's Enum.ToObject throws
            // ArgumentException for a non-integral value, an UNCAUGHT crash under the default Native mode (see
            // this method's remarks). Every genuine C# enum-comparison PROMOTION this arm exists for
            // (short/byte/ushort/sbyte -> Int32) is itself integral, so this restriction costs nothing there;
            // it only closes the floating-point hole. See IsIntegerType.
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
