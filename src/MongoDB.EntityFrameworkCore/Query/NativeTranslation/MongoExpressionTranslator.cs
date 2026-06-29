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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates an EF Core predicate or key-selector lambda body into a dialect-agnostic
/// <see cref="MongoExpression"/> tree, suitable for later rendering to a MongoDB filter
/// or sort document.
/// </summary>
/// <remarks>
/// This is a compile-time-only translator: it produces a <em>template</em> tree where
/// captured constants become <see cref="MongoConstantExpression"/> nodes baked into the
/// template, and query parameters become <see cref="MongoParameterExpression"/> placeholder
/// nodes that are resolved per execution (the B2 binding step).
///
/// Returns <see langword="false"/> for any shape outside the parity acceptance set rather
/// than throwing, so callers can fall back to the driver-LINQ path gracefully.
/// </remarks>
internal sealed class MongoExpressionTranslator
{
    private readonly IEntityType _entityType;

    /// <summary>
    /// Creates a <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    public MongoExpressionTranslator(IEntityType entityType)
    {
        _entityType = entityType;
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
                // Only allow Not over a field or further translated expression; nullable bools fall back.
                if (operand is MongoFieldExpression fieldExpr && fieldExpr.Property.IsNullable)
                    return null; // conservative: nullable bool Not could diverge from driver rendering
                return new MongoUnaryExpression(MongoUnaryOperator.Not, operand);
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
    /// Translate a comparison <see cref="BinaryExpression"/> into a <see cref="MongoBinaryExpression"/>
    /// with <see cref="MongoFieldExpression"/> on the field side and a value node
    /// (<see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/>) on the other.
    /// </summary>
    private MongoBinaryExpression? TranslateComparison(BinaryExpression be)
    {
        IProperty? property;
        string? fieldPath;
        Expression valueNode;
        bool memberOnLeft;

        var leftUnwrapped = Unwrap(be.Left);
        var rightUnwrapped = Unwrap(be.Right);

        if (TryResolveMember(leftUnwrapped, out property, out fieldPath))
        {
            valueNode = rightUnwrapped;
            memberOnLeft = true;
        }
        else if (TryResolveMember(rightUnwrapped, out property, out fieldPath))
        {
            valueNode = leftUnwrapped;
            memberOnLeft = false;
        }
        else
        {
            return null; // no member on either side — not supported
        }

        // Numeric cast on the member side changes comparison semantics — fall back.
        if (HasNumericConvert(memberOnLeft ? be.Left : be.Right, property!.ClrType))
            return null;

        // Equality/inequality on nullable properties can diverge from driver rendering — fall back.
        if (be.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && property.IsNullable)
            return null;

        // Mirror the operator when the member is on the right-hand side.
        var effectiveNodeType = memberOnLeft ? be.NodeType : Mirror(be.NodeType);

        var mongoOp = effectiveNodeType switch
        {
            ExpressionType.Equal => MongoBinaryOperator.Equal,
            ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
            ExpressionType.LessThan => MongoBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
            ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
            _ => (MongoBinaryOperator?)null
        };

        if (mongoOp is null)
            return null;

        var valueExpr = TranslateValue(valueNode, property);
        if (valueExpr is null)
            return null;

        var fieldExpr = new MongoFieldExpression(property, fieldPath!);
        return new MongoBinaryExpression(mongoOp.Value, fieldExpr, valueExpr);
    }

    /// <summary>
    /// Translates a value node (the non-field operand of a comparison) to either a
    /// <see cref="MongoConstantExpression"/> (baked-in literal) or a
    /// <see cref="MongoParameterExpression"/> (B2 placeholder for a query parameter).
    /// Returns <see langword="null"/> for any node that cannot be represented.
    /// </summary>
    private static MongoExpression? TranslateValue(Expression node, IProperty forSerialization)
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

        if (node is not MemberExpression { Expression: ParameterExpression } me)
            return false;

        property = _entityType.FindProperty(me.Member.Name);
        if (property is null)
            return false;

        // A component of a composite primary key is stored nested under "_id" (e.g. { _id: { Key1, Key2 } }),
        // so its top-level element name does not address the stored field. The driver-LINQ path resolves the
        // dotted "_id.<name>" path; the native translator does not, so refuse it here and let the query fall
        // back rather than emit a $match against a non-existent top-level field (which silently returns nothing).
        if (property.IsPrimaryKey() && property.FindContainingPrimaryKey()!.Properties.Count > 1)
            return false;

        fieldPath = property.GetElementName();
        return true;
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
