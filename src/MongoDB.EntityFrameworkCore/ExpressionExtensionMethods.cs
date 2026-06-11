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
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore;

internal static class ExpressionExtensionMethods
{
    internal static T GetConstantValue<T>(this Expression expression)
        => expression is ConstantExpression constantExpression
            ? (T)constantExpression.Value!
            : throw new InvalidOperationException();

    /// <summary>
    /// Strips any <see cref="ExpressionType.Quote"/> wrappers, returning the quoted operand (typically a
    /// <see cref="LambdaExpression"/>). Returns the expression unchanged when it is not quoted.
    /// </summary>
    internal static Expression UnwrapQuote(this Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }

        return expression;
    }

    internal static LambdaExpression UnwrapLambdaFromQuote(this Expression expression)
        => (LambdaExpression)expression.UnwrapQuote();

    internal static IReadOnlyList<TMemberInfo> GetMemberAccess<TMemberInfo>(this LambdaExpression memberAccessExpression)
        where TMemberInfo : MemberInfo
    {
        var members = memberAccessExpression.Parameters[0].MatchMemberAccess<TMemberInfo>(memberAccessExpression.Body);

        if (members is null)
        {
            throw new ArgumentException(
                $"The expression '{memberAccessExpression}' is not a valid member access expression. The expression should represent a simple property or field access: 't => t.MyProperty'.");
        }
        return members;
    }

    internal static IReadOnlyList<TMemberInfo>? MatchMemberAccess<TMemberInfo>(
        this Expression parameterExpression,
        Expression memberAccessExpression)
        where TMemberInfo : MemberInfo
    {
        var memberInfos = new List<TMemberInfo>();
        var unwrappedExpression = RemoveTypeAs(RemoveConvert(memberAccessExpression));
        do
        {
            var memberExpression = unwrappedExpression as MemberExpression;
            if (!(memberExpression?.Member is TMemberInfo memberInfo))
            {
                return null;
            }
            memberInfos.Insert(0, memberInfo);
            unwrappedExpression = RemoveTypeAs(RemoveConvert(memberExpression.Expression));
        }
        while (unwrappedExpression != parameterExpression);

        return memberInfos;
    }

    [return: NotNullIfNotNull(nameof(expression))]
    internal static Expression? RemoveTypeAs(this Expression? expression)
    {
        while (expression?.NodeType == ExpressionType.TypeAs)
        {
            expression = ((UnaryExpression)RemoveConvert(expression)).Operand;
        }

        return expression;
    }

    internal static Expression ConvertIfRequired(this Expression expression, Type targetType) =>
        expression.Type == targetType ? expression : Expression.Convert(expression, targetType);

    [return: NotNullIfNotNull(nameof(expression))]
    internal static Expression? RemoveConvert(this Expression? expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
            ? RemoveConvert(unaryExpression.Operand)
            : expression;

    /// <summary>
    /// Removes a single boxing conversion to <see cref="object"/> if present. Unlike
    /// <see cref="RemoveConvert"/>, this strips only one level and only an <see cref="object"/>-typed
    /// <see cref="ExpressionType.Convert"/> / <see cref="ExpressionType.ConvertChecked"/>, leaving numeric
    /// or widening conversions intact.
    /// </summary>
    internal static Expression RemoveObjectConvert(this Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
           && unaryExpression.Type == typeof(object)
            ? unaryExpression.Operand
            : expression;

    /// <summary>
    /// Extracts the simple member/property name from a key-selector-style expression — a member access
    /// (<c>o.CustomerId</c>) or an <c>EF.Property(o, "CustomerId")</c> call — after stripping any
    /// <see cref="ExpressionType.Convert"/> / <see cref="ExpressionType.ConvertChecked"/> wrappers. Returns
    /// <see langword="null"/> when the expression is not a simple member or <c>EF.Property</c> access.
    /// </summary>
    internal static string? TryGetSimplePropertyName(this Expression expression)
        => expression.RemoveConvert() switch
        {
            MemberExpression member => member.Member.Name,
            MethodCallExpression methodCall
                when methodCall.Method.IsEFPropertyMethod()
                     && methodCall.Arguments.Count == 2
                     && methodCall.Arguments[1] is ConstantExpression { Value: string name } => name,
            _ => null
        };
}
