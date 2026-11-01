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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Rewrites the lambda of an element <c>Select</c> over an owned collection (<c>l =&gt; l.Sku</c>, <c>l =&gt; new { l.Sku, Twice = l.Qty * 2 }</c>) so it
/// can run on locals the cursor filled, with no element instance: each read of a scalar member of the element becomes its local. The lambda
/// runs while the driver deserializes the batch, before any row is returned and for rows the caller may never read, so it must be free of side
/// effects: only member reads, operators, conditionals, anonymous-type construction and a few framework methods are accepted. Anything else (the
/// element itself, a nested owned member, a method or constructor of the user's types, a nested lambda) throws
/// <see cref="NativeTranslationNotSupportedException"/>, and the collection is read whole and the <c>Select</c> run over it as before.
/// </summary>
internal sealed class MongoElementSelectorRewriter(
    IEntityType elementType,
    IReadOnlyDictionary<IProperty, ParameterExpression> locals,
    ParameterExpression element) : System.Linq.Expressions.ExpressionVisitor
{
    private static readonly HashSet<ExpressionType> AllowedNodeTypes =
    [
        ExpressionType.Add, ExpressionType.AddChecked, ExpressionType.And, ExpressionType.AndAlso, ExpressionType.Coalesce,
        ExpressionType.Conditional, ExpressionType.Constant, ExpressionType.Convert, ExpressionType.ConvertChecked, ExpressionType.Default,
        ExpressionType.Divide, ExpressionType.Equal, ExpressionType.ExclusiveOr, ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual,
        ExpressionType.LessThan, ExpressionType.LessThanOrEqual, ExpressionType.MemberAccess, ExpressionType.Modulo, ExpressionType.Multiply,
        ExpressionType.MultiplyChecked, ExpressionType.Negate, ExpressionType.NegateChecked, ExpressionType.Not, ExpressionType.NotEqual,
        ExpressionType.OnesComplement, ExpressionType.Or, ExpressionType.OrElse, ExpressionType.Parameter, ExpressionType.Subtract,
        ExpressionType.SubtractChecked, ExpressionType.Call, ExpressionType.New, ExpressionType.TypeIs, ExpressionType.UnaryPlus
    ];

    /// <summary>The element properties the selector reads.</summary>
    public HashSet<IProperty> Used { get; } = [];

    public override Expression? Visit(Expression? node)
    {
        if (node != null && !AllowedNodeTypes.Contains(node.NodeType))
        {
            throw Unsupported(node);
        }

        return base.Visit(node);
    }

    // Only reached for a use of the element other than a member read of it: the element is not constructed.
    protected override Expression VisitParameter(ParameterExpression node)
        => throw Unsupported(node);

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == element)
        {
            if (elementType.FindProperty(node.Member.Name) is not { } property
                || property.ClrType != node.Type
                || !locals.TryGetValue(property, out var local))
            {
                throw Unsupported(node);
            }

            Used.Add(property);
            return local;
        }

        // A static member (`DateTime.Now`, `Guid.Empty`) is not a read of the element; only `string.Empty` is known to be pure.
        if (node.Expression == null)
        {
            return node.Member.DeclaringType == typeof(string) ? node : throw Unsupported(node);
        }

        return base.VisitMember(node);
    }

    protected override Expression VisitConstant(ConstantExpression node)
        => node.Value is null or string || node.Type.IsValueType ? node : throw Unsupported(node);

    protected override Expression VisitMethodCall(MethodCallExpression node)
        => IsPureFrameworkType(node.Method.DeclaringType) ? base.VisitMethodCall(node) : throw Unsupported(node);

    protected override Expression VisitNew(NewExpression node)
        => IsAnonymousType(node.Type)
           || node.Type.IsGenericType && (node.Type.GetGenericTypeDefinition() == typeof(Nullable<>)
                                          || node.Type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            ? base.VisitNew(node)
            : throw Unsupported(node);

    protected override Expression VisitBinary(BinaryExpression node)
        => IsFrameworkOperator(node.Method) ? base.VisitBinary(node) : throw Unsupported(node);

    protected override Expression VisitUnary(UnaryExpression node)
        => IsFrameworkOperator(node.Method) ? base.VisitUnary(node) : throw Unsupported(node);

    private static bool IsFrameworkOperator(System.Reflection.MethodInfo? method)
        => method?.DeclaringType == null || method.DeclaringType.Assembly == typeof(object).Assembly;

    private static bool IsPureFrameworkType(Type? type)
        => type == typeof(string) || type == typeof(Math) || type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(Nullable<>);

    private static bool IsAnonymousType(Type type)
        => type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);

    private static NativeTranslationNotSupportedException Unsupported(Expression node)
        => new($"The element Select reads '{node}', which the cursor cannot evaluate without constructing the element.");
}
