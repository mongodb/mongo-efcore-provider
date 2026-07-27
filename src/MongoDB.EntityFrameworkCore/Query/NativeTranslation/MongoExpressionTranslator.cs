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
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
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
internal sealed class MongoExpressionTranslator
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
    /// Attempts to resolve a simple member-access expression to its <see cref="IProperty"/> and
    /// the MongoDB document element name. Returns <see langword="false"/> for any property that
    /// cannot be natively addressed, including composite-PK components whose storage path is
    /// <c>_id.&lt;element&gt;</c> — those fall back to driver-LINQ.
    /// </summary>
    private bool TryResolveMember(Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;

        // Fast path: a bare top-level member on the query parameter (p.Foo). Everything else — a member
        // rooted on another hop, or an EF.Property(...) call produced by owned-nav expansion — is delegated
        // to the owned single-reference dotted-path resolver (single-scope only), which declines cleanly
        // (returns false) for any shape that is not a valid owned chain.
        if (node is not MemberExpression { Expression: ParameterExpression param } me)
            return TryResolveOwnedFieldPath(node, out property, out fieldPath);

        // Two-scope mode: a member rooted on the outer param resolves against the outer entity type at document
        // root; every other member is inner-scoped. Identity (ReferenceEquals), never name — so a member name
        // shared between the two scopes cannot be mis-routed.
        var isOuter = _outerParam is not null && ReferenceEquals(param, _outerParam);
        var scopeType = isOuter ? _outerEntityType! : _entityType;

        var resolved = scopeType.FindProperty(me.Member.Name);
        if (resolved is null)
            return false;

        // A component of a composite primary key is stored nested under "_id" (e.g. { _id: { Key1, Key2 } }),
        // so its top-level element name does not address the stored field. The native translator does not resolve
        // the dotted "_id.<name>" path, so refuse it here and let the query fall back rather than emit a $match
        // against a non-existent top-level field (which silently returns nothing).
        if (resolved.IsPrimaryKey() && resolved.FindContainingPrimaryKey()!.Properties.Count > 1)
            return false;

        property = resolved;
        fieldPath = resolved.GetElementName();

        // Inner-scope fields are prefixed with the unwind scope in two-scope mode; outer-scope fields (and every
        // field in single-scope mode, where _innerPrefix is null) stay at their resolved element name.
        if (!isOuter && _innerPrefix is not null)
            fieldPath = _innerPrefix + "." + fieldPath;

        return true;
    }

    /// <summary>
    /// Resolves a nested member/navigation access chain into an owned single-reference (OwnsOne) dotted
    /// document path, e.g. <c>p.Address.City</c> → element path <c>"Address.City"</c> and the <c>City</c>
    /// property. Each hop may be a <see cref="MemberExpression"/> (scalar access) or an
    /// <c>EF.Property(root, "Nav")</c> call (the shadow-nav-safe form EF's nav-expansion rewrites owned-nav
    /// access into); every non-leaf hop must resolve to an embedded single-reference navigation, and the chain
    /// must be rooted at the query parameter with a mapped scalar leaf. Returns <see langword="false"/> (caller
    /// falls back to driver-LINQ) for any other shape. Engaged only in single-scope mode — a two-scope
    /// SelectMany-unwind translator declines, because <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>
    /// yields a root-relative path that would not compose with the unwind-scope prefixing. Also declines when
    /// <c>_entityType</c> is not itself a document root — <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>
    /// always yields a TRUE-document-root-relative path, so a single-scope translator built on a non-root scope
    /// (e.g. an owned-collection-element inner filter translator whose result the CALLER separately prefixes
    /// with the unwind path) would otherwise have that prefix applied twice.
    /// </summary>
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: owned dotted paths are out of scope (declined, falls back)

        // GetDocumentPath() below returns a path rooted at the TRUE document root. That composes correctly
        // only when _entityType IS the document root (the query-parameter's own entity type) — which is what
        // every predicate/sort/projection surface builds this translator against (CollectionExpression.EntityType).
        // A single-scope translator built on a NON-root entity type (e.g. NativeSelectManyBinder.
        // TryBuildOwnedInnerFilter's owned-inner-filter translator, built on the owned collection ELEMENT type)
        // would already have its result prefixed with the unwind path by its caller — composing that with a
        // root-relative GetDocumentPath() double-prefixes the path and silently matches nothing. Decline here so
        // that shape falls back to driver-LINQ instead of emitting a wrong (empty) native $match.
        if (!_entityType.IsDocumentRoot())
            return false;

        // Collect hop names from the outer (leaf) hop inward; the root must be the query parameter.
        var names = new List<string>();
        var current = node;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression)
            return false;

        // A single top-level member is handled by TryResolveMember's fast path, never here.
        if (names.Count < 2)
            return false;

        names.Reverse(); // now root-first: [firstNav, ..., leaf]

        var scopeType = _entityType;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded() || navigation.IsCollection)
                return false; // cross-collection or owned-collection intermediate → fall back
            scopeType = navigation.TargetEntityType;
        }

        var leaf = scopeType.FindProperty(names[^1]);
        if (leaf is null)
            return false;

        // Composite-PK components are stored under "_id" and are not addressable by their top-level element
        // name (mirrors the single-member guard in TryResolveMember).
        if (leaf.IsPrimaryKey() && leaf.FindContainingPrimaryKey()!.Properties.Count > 1)
            return false;

        property = leaf;
        // GetDocumentPath() gives the ordered containing element names from the document root down to the leaf's
        // declaring owned entity type (scopeType, after the loop above); append the leaf's own element name. This
        // is the exact dotted path the shapers and pipeline use, so the emitted $match/$project/$sort addresses
        // the stored field correctly. (Reads scopeType rather than the obsolete IProperty.DeclaringEntityType.)
        fieldPath = string.Join(".", scopeType.GetDocumentPath().Append(leaf.GetElementName()));
        return true;
    }

    // A single access hop in either shape EF produces: a plain MemberExpression (scalar access) or an
    // EF.Property(root, "Name") call (owned-nav expansion). Mirrors NativeSelectManyBinder.TryGetMemberAccess.
    private static bool TryGetMemberOrEFProperty(Expression expression, out Expression inner, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } e } member:
                inner = e;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var root, ConstantExpression { Value: string propName }]:
                inner = root;
                name = propName;
                return true;

            default:
                inner = null!;
                name = null!;
                return false;
        }
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

    /// <summary>
    /// Recognizes a <c>Contains</c> call over a collection: either the static
    /// <c>Enumerable.Contains(source, item)</c> form, or an instance <c>Contains(item)</c> call whose
    /// declaring type is (or implements) the generic <c>ICollection&lt;T&gt;</c> contract
    /// (<c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>).
    /// Matches by <see cref="System.Reflection.MethodInfo"/> shape, not by name string alone.
    /// </summary>
    private static bool TryMatchContainsMethod(
        MethodCallExpression call,
        [NotNullWhen(true)] out Expression? collection,
        [NotNullWhen(true)] out Expression? item)
    {
        collection = null;
        item = null;

        if (call.Method.Name != nameof(Enumerable.Contains))
            return false;

        // Static Enumerable.Contains<TSource>(IEnumerable<TSource>, TSource) — the two-argument form only;
        // the three-argument IEqualityComparer<TSource> overload has no query-dialect equivalent.
        if (call.Method.IsStatic && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            item = call.Arguments[1];
            return true;
        }

        // Instance List<T>.Contains(item) / ICollection<T>.Contains(item) / HashSet<T>.Contains(item) /
        // IList<T>.Contains(item).
        if (!call.Method.IsStatic && call.Object is not null && call.Arguments.Count == 1)
        {
            var declaringType = call.Method.DeclaringType;
            if (declaringType is { IsGenericType: true })
            {
                var def = declaringType.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(IList<>) || def == typeof(ICollection<>))
                {
                    collection = call.Object;
                    item = call.Arguments[0];
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Recognizes the single-argument, ordinal-equivalent overload of
    /// <c>string.StartsWith(string)</c>/<c>EndsWith(string)</c>/<c>Contains(string)</c> — the only
    /// overloads the driver-LINQ v3 provider translates for these methods (it throws
    /// <c>ExpressionNotSupportedException</c> for the <c>StringComparison</c>-taking overloads, confirmed
    /// empirically under <c>MongoQueryMode.DriverLinq</c>). Matching only this overload
    /// keeps native and fallback behavior identical: anything else (a <see cref="StringComparison"/> arg,
    /// or a receiver that isn't <see cref="string"/>) is left unmatched here and falls through to the
    /// driver-LINQ path unchanged.
    /// </summary>
    private static bool TryMatchRegexMethod(
        MethodCallExpression call,
        out MongoRegexKind kind,
        [NotNullWhen(true)] out Expression? receiver,
        [NotNullWhen(true)] out Expression? term)
    {
        kind = default;
        receiver = null;
        term = null;

        if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
            return false;

        if (call.Arguments.Count != 1 || call.Arguments[0].Type != typeof(string))
            return false;

        switch (call.Method.Name)
        {
            case nameof(string.StartsWith):
                kind = MongoRegexKind.StartsWith;
                break;
            case nameof(string.EndsWith):
                kind = MongoRegexKind.EndsWith;
                break;
            case nameof(string.Contains):
                kind = MongoRegexKind.Contains;
                break;
            default:
                return false;
        }

        receiver = call.Object;
        term = call.Arguments[0];
        return true;
    }

    /// <summary>
    /// Translates the collection side of a <c>Contains</c> call into a <see cref="MongoConstantExpression"/>
    /// (a captured/inline collection) or <see cref="MongoParameterExpression"/> (a query-parameter
    /// collection), using <paramref name="property"/> as the element serialization context. Returns
    /// <see langword="null"/> for any other shape, or when the collection's element type does not match
    /// the property's CLR type.
    /// </summary>
    private static MongoExpression? TranslateInValues(Expression collectionExpr, IProperty property)
    {
        var unwrapped = Unwrap(collectionExpr);

        var elementType = GetEnumerableElementType(unwrapped.Type);
        if (elementType is null)
            return null;

        var propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var underlyingElementType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (underlyingElementType != propertyType)
            return null; // collection element type mismatches the property — not supported

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, property);

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, property);

        // EF8 hands an inline array literal (`new[] { .. }.Contains(..)`) as a NewArrayExpression
        // rather than a pre-folded ConstantExpression (the constant-folding that produces the latter
        // only happens on EF9/net9+). Recognize this shape too, but only when every element is itself
        // a constant — anything else (a captured variable reference, a computed element, etc.) is left
        // unmatched here and falls back to driver-LINQ rather than attempting to evaluate arbitrary
        // sub-expressions.
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray)
        {
            var values = Array.CreateInstance(elementType, newArray.Expressions.Count);
            for (var i = 0; i < newArray.Expressions.Count; i++)
            {
                if (Unwrap(newArray.Expressions[i]) is not ConstantExpression elementConstant)
                    return null; // non-constant element — not supported

                values.SetValue(elementConstant.Value, i);
            }

            return new MongoConstantExpression(values, property);
        }

        return null; // any other node shape (method call, sub-expression, etc.) is not supported
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private static bool IsComparison(ExpressionType t)
        => t is ExpressionType.Equal or ExpressionType.NotEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
}
