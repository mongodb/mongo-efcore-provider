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
        if (!TryResolveMember(Unwrap(keySelectorBody), out var property, out var path))
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
            // A MongoSizeExpression falls into the catch-all below, deliberately: an array COUNT carries no
            // property serialization, so there is no converter / BsonRepresentation for it to diverge on.
            _ => true
        };

    // Strip redundant Convert/ConvertChecked wrappers (EF sometimes adds a nullable-widening convert).
    private static Expression Unwrap(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            ? Unwrap(u.Operand)
            : e;

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
    /// </remarks>
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
    {
        var leftUnwrapped = Unwrap(be.Left);
        var rightUnwrapped = Unwrap(be.Right);

        // --- Query-native shape: member on exactly one side, value on the other ---

        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath) && IsSimpleValue(rightUnwrapped))
        {
            // Numeric cast on the member side changes comparison semantics — fall back.
            if (HasNumericConvert(be.Left, leftProperty!.ClrType))
                return null;

            var mongoOp = MapComparisonOperator(be.NodeType);
            if (mongoOp is null)
                return null;

            var valueExpr = TranslateValue(rightUnwrapped, leftProperty);
            if (valueExpr is null)
                return null;

            return new MongoBinaryExpression(mongoOp.Value, new MongoFieldExpression(leftProperty, leftPath!), valueExpr);
        }

        if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath) && IsSimpleValue(leftUnwrapped))
        {
            // Numeric cast on the member side changes comparison semantics — fall back.
            if (HasNumericConvert(be.Right, rightProperty!.ClrType))
                return null;

            // Mirror the operator since the member is on the right-hand side but must render on the Left.
            var mongoOp = MapComparisonOperator(Mirror(be.NodeType));
            if (mongoOp is null)
                return null;

            var valueExpr = TranslateValue(leftUnwrapped, rightProperty);
            if (valueExpr is null)
                return null;

            return new MongoBinaryExpression(mongoOp.Value, new MongoFieldExpression(rightProperty, rightPath!), valueExpr);
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
                return null; // type-changing cast (narrowing, or any cast on the comparison path) — fall back
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

    // True when the operand wraps the member in a Convert/ConvertChecked to a semantically different
    // type — i.e. a numeric cast that changes the comparison semantics. A nullable<->underlying widening
    // convert is benign (EF adds it automatically) and is not treated as a cast.
    private static bool HasNumericConvert(Expression operand, Type propertyClrType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyClrType) ?? propertyClrType;
        while (operand is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            var to = Nullable.GetUnderlyingType(u.Type) ?? u.Type;
            if (to != underlying)
                return true;
            operand = u.Operand;
        }

        return false;
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
