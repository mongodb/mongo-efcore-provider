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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>a.CompareTo(b) OP k</c> / <c>string.Compare(a, b) OP k</c>.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// Constant int <c>k</c>: folds to a plain comparison or constant where exact; otherwise compares <c>$cmp</c> to <c>k</c>.
    /// </summary>
    private bool TryTranslateStringCompare(BinaryExpression comparison, out MongoExpression? result)
    {
        result = null;
        var op = comparison.NodeType;
        Expression callSide = comparison.Left, constantSide = comparison.Right;
        if (!IsCompareCall(callSide, out _, out _, out _))
        {
            (callSide, constantSide) = (comparison.Right, comparison.Left);
            op = Mirror(op);
        }

        if (!IsCompareCall(callSide, out var a, out var b, out var isString)
            || Unwrap(constantSide) is not ConstantExpression { Value: int k })
            return false;

        bool? constantFold = (k, op) switch
        {
            (-1, ExpressionType.GreaterThanOrEqual) or (1, ExpressionType.LessThanOrEqual) => true,
            (-1, ExpressionType.LessThan) or (1, ExpressionType.GreaterThan) => false,
            (not (-1 or 0 or 1), ExpressionType.Equal) => false,
            (not (-1 or 0 or 1), ExpressionType.NotEqual) => true,
            (< -1, ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual) => true,
            (> 1, ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual) => false,
            (< -1, ExpressionType.LessThan or ExpressionType.LessThanOrEqual) => false,
            (> 1, ExpressionType.LessThan or ExpressionType.LessThanOrEqual) => true,
            _ => null
        };
        if (constantFold is { } folded)
        {
            result = new MongoConstantExpression(folded, forSerialization: null);
            return true;
        }

        ExpressionType? relational = (k, op) switch
        {
            (0, _) => op,
            (-1, ExpressionType.Equal) or (-1, ExpressionType.LessThanOrEqual) => ExpressionType.LessThan,
            (-1, ExpressionType.NotEqual) or (-1, ExpressionType.GreaterThan) => ExpressionType.GreaterThanOrEqual,
            (1, ExpressionType.Equal) or (1, ExpressionType.GreaterThanOrEqual) => ExpressionType.GreaterThan,
            (1, ExpressionType.NotEqual) or (1, ExpressionType.LessThan) => ExpressionType.LessThanOrEqual,
            _ => null
        };

        if (relational is { } relOp && (!isString || IsFoldSafe(a, b)))
        {
            result = TranslateComparisonCore(a, b, relOp);
            return result is not null;
        }

        // $cmp orders BSON values by type then value; only the string case is known to match the CLR ordering.
        if (!isString)
            return false;

        var left = TranslateOperand(a);
        var right = left is null ? null : TranslateOperand(b);
        if (right is null || !AllFieldsDefaultSerialized(left!) || !AllFieldsDefaultSerialized(right))
            return false;

        result = new MongoBinaryExpression(
            MapComparisonOperator(op)!.Value, new MongoStringCompareExpression(left!, right),
            new MongoConstantExpression(k, forSerialization: null));
        return true;
    }

    private static bool IsCompareCall(Expression node, out Expression a, out Expression b, out bool isString)
    {
        a = b = null!;
        isString = false;
        switch (Unwrap(node))
        {
            case MethodCallExpression { Method.Name: nameof(string.CompareTo), Object: { Type: var t } obj, Arguments: [var arg] }
                when IsOrderedCompareType(t) && arg.Type == t:
                (a, b) = (obj, arg);
                isString = t == typeof(string);
                return true;
            case MethodCallExpression { Method.Name: nameof(string.Compare), Object: null, Arguments: [var x, var y] } call
                when IsOrderedCompareType(call.Method.DeclaringType) && x.Type == call.Method.DeclaringType && y.Type == x.Type:
                (a, b) = (x, y);
                isString = x.Type == typeof(string);
                return true;
            default:
                return false;
        }
    }

    // Types whose BSON order equals the CLR order. Excludes unsigned (BSON has no unsigned types), enums, Guid
    // (binary order) and TimeSpan.
    private static bool IsOrderedCompareType(Type? type)
        => type == typeof(string) || type == typeof(int) || type == typeof(long) || type == typeof(short)
           || type == typeof(byte) || type == typeof(sbyte) || type == typeof(double) || type == typeof(float)
           || type == typeof(decimal) || type == typeof(DateTime);

    // A relational fold renders as query-dialect { f: { $op: "c" } }, which never matches a null field; .NET orders
    // null first. So fold only when neither side can be null, and the receiver is default-serialized (a folded
    // relational comparison against a converted field would compare converted BSON values, not CLR strings).
    private bool IsFoldSafe(Expression a, Expression b)
        => TryResolveMember(Unwrap(a), out var property, out _, out var isOuter)
           && !isOuter && !property.IsNullable && StoredSerialization.HasDefaultKeySerialization(property)
           && Unwrap(b) is ConstantExpression { Value: string };
}
