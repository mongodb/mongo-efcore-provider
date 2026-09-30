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
    /// Reads a wrapped projection body (anonymous type / DTO construction) into (member name, value) pairs:
    /// a <see cref="NewExpression"/> with <see cref="NewExpression.Members"/>, or a
    /// <see cref="MemberInitExpression"/> over a parameterless constructor. Returns <see langword="false"/> (with
    /// empty <paramref name="members"/>) for anything else, an empty construction, or a nested/list binding.
    /// </summary>
    /// <param name="body">The projection body.</param>
    /// <param name="members">The pairs read, or empty on <see langword="false"/>.</param>
    /// <param name="allowPositionalConstructorArguments">
    /// Also admit <c>new SomeNamedClass(args)</c>, whose <see cref="NewExpression.Members"/> is always
    /// <see langword="null"/>, naming arguments <see cref="PositionalConstructorArgumentAliasPrefix"/> + index.
    /// Only for callers that address values by index (the <c>GroupBy</c>/<c>SelectMany</c> result selectors);
    /// <c>NativeProjectionBinder</c>/<c>NativeJoinScopeProjectionBinder</c> must not, since they resolve aliases
    /// by real <c>MemberInfo</c> and would never find a synthetic name.
    /// </param>
    /// <param name="rowIndependentConstructorArgumentsOver">
    /// When non-null, also admit a <see cref="MemberInitExpression"/> whose constructor arguments are all
    /// row-independent over this selector parameter (<c>NativeProjectionBinder.IsRowIndependentLeaf</c>):
    /// <c>new Dto(param) { A = x.A }</c>. Only the bindings are returned; the constructor arguments stay on the
    /// shaper, which evaluates them client-side. Only for <c>NativeProjectionBinder</c>, whose read side evaluates
    /// row-independent subtrees in place.
    /// </param>
    internal static bool TryGetProjectionMembers(
        this Expression body, out IReadOnlyList<(string MemberName, Expression Value)> members,
        bool allowPositionalConstructorArguments = false,
        ParameterExpression? rowIndependentConstructorArgumentsOver = null)
    {
        switch (body)
        {
            case NewExpression
            {
                Members: { } newMembers, Arguments: { Count: > 0 } arguments
            } when newMembers.Count == arguments.Count:
            {
                var pairs = new List<(string, Expression)>(arguments.Count);
                for (var i = 0; i < arguments.Count; i++)
                {
                    pairs.Add((newMembers[i].Name, arguments[i]));
                }

                members = pairs;
                return true;
            }

            // Constructor-only DTO; see allowPositionalConstructorArguments.
            case NewExpression
            {
                Members: null, Arguments: { Count: > 0 } positionalArguments
            } when allowPositionalConstructorArguments:
            {
                var pairs = new List<(string, Expression)>(positionalArguments.Count);
                for (var i = 0; i < positionalArguments.Count; i++)
                {
                    pairs.Add((PositionalConstructorArgumentAlias(i), positionalArguments[i]));
                }

                members = pairs;
                return true;
            }

            case MemberInitExpression { NewExpression.Arguments.Count: 0, Bindings.Count: > 0 }:
            case MemberInitExpression { NewExpression.Arguments.Count: > 0, Bindings.Count: > 0 } memberInitWithArguments
                when rowIndependentConstructorArgumentsOver is not null
                     && memberInitWithArguments.NewExpression.Arguments.All(
                         a => Query.NativeTranslation.NativeProjectionBinder.IsRowIndependentLeaf(
                             a, rowIndependentConstructorArgumentsOver)):
            {
                var memberInit = (MemberInitExpression)body;
                var pairs = new List<(string, Expression)>(memberInit.Bindings.Count);
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        members = [];
                        return false;
                    }

                    pairs.Add((assignment.Member.Name, assignment.Expression));
                }

                members = pairs;
                return true;
            }

            default:
                members = [];
                return false;
        }
    }

    /// <summary>
    /// Pseudo-member-name prefix for a constructor-only DTO's positional arguments (argument <c>i</c> becomes
    /// <c>"_ctorArg" + i</c>); see <see cref="TryGetProjectionMembers"/>.
    /// </summary>
    internal const string PositionalConstructorArgumentAliasPrefix = "_ctorArg";

    private static string PositionalConstructorArgumentAlias(int index)
        => PositionalConstructorArgumentAliasPrefix + index;

    /// <summary>
    /// Inverse of <see cref="TryGetProjectionMembers"/>: rebuilds the construction with each member's value
    /// replaced by the matching entry of <paramref name="values"/>.
    /// </summary>
    /// <remarks>
    /// Only valid on a body <see cref="TryGetProjectionMembers"/> accepted, with <paramref name="values"/> in its
    /// order; that is what makes the <see cref="MemberAssignment"/> cast safe.
    /// </remarks>
    internal static Expression RebuildProjectionMembers(this Expression body, IReadOnlyList<Expression> values)
        => body switch
        {
            NewExpression newExpression => newExpression.Update(values),
            MemberInitExpression memberInit => memberInit.Update(
                memberInit.NewExpression,
                memberInit.Bindings.Select(
                    (binding, i) => (MemberBinding)((MemberAssignment)binding).Update(values[i]))),
            _ => throw new InvalidOperationException(
                $"'{body.GetType().Name}' is not a wrapped projection construction; "
                + $"{nameof(RebuildProjectionMembers)} may only be used on a body "
                + $"{nameof(TryGetProjectionMembers)} accepted.")
        };

    /// <summary>
    /// Matches a single access hop — a <see cref="MemberExpression"/> or EF Core's
    /// <c>EF.Property&lt;T&gt;(root, "Name")</c> — yielding the receiver and name, so both spellings resolve
    /// identically wherever a member chain is walked.
    /// </summary>
    internal static bool TryGetMemberOrEFProperty(this Expression expression, out Expression receiver, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } inner } member:
                receiver = inner;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var root, ConstantExpression { Value: string propertyName }]:
                receiver = root;
                name = propertyName;
                return true;

            default:
                receiver = null!;
                name = null!;
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="expression"/> anywhere references <paramref name="parameter"/> (by identity).
    /// </summary>
    /// <remarks>
    /// Scope is decided by parameter identity, never member name (see <c>Query/AGENTS.md</c>): two scopes
    /// commonly share a member such as <c>Id</c>.
    /// </remarks>
    internal static bool ReferencesParameter(this Expression expression, ParameterExpression parameter)
    {
        var visitor = new ParameterReferenceVisitor(parameter);
        visitor.Visit(expression);
        return visitor.Found;
    }

    private sealed class ParameterReferenceVisitor(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, parameter))
            {
                Found = true;
            }

            return node;
        }
    }

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
    /// Whether <paramref name="type"/> is EF Core's <c>TransparentIdentifier&lt;TOuter, TInner&gt;</c>, the type
    /// a join's result selector wraps its outer/inner pair in.
    /// </summary>
    /// <remarks>
    /// Check an <c>Outer</c>/<c>Inner</c> member's declaring type with this, not the name alone, or a user type
    /// with such a member is mistaken for join plumbing.
    /// </remarks>
    internal static bool IsTransparentIdentifierType(this Type? type)
        => type is { IsGenericType: true }
           && type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal);

    // Types whose Equals(object) requires an exact runtime-type match. Both the driver-LINQ bridge and the native
    // translator fold a mismatched-type Equals(...) to `false` using this set, so they agree.
    private static readonly HashSet<Type> ExactTypeEqualityTypes =
    [
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
        typeof(decimal), typeof(char), typeof(string), typeof(Guid), typeof(DateTime),
        typeof(DateTimeOffset), typeof(TimeSpan)
    ];

    /// <summary>
    /// True when <paramref name="left"/> and <paramref name="right"/> are different members of
    /// <see cref="ExactTypeEqualityTypes"/>, so they are never equal. Limited to that set because an arbitrary
    /// type's <c>Equals(object)</c> could compare across types.
    /// </summary>
    internal static bool AreMismatchedExactEqualityTypes(Type left, Type right)
        => left != right && ExactTypeEqualityTypes.Contains(left) && ExactTypeEqualityTypes.Contains(right);

    /// <summary>
    /// True when <paramref name="receiver"/>.Equals(<paramref name="argument"/>) always returns
    /// <see langword="false"/> because of a type mismatch (e.g. <c>((int?)1).Equals((ulong)2)</c>).
    /// </summary>
    internal static bool IsAlwaysFalseAcrossTypeMismatch(Expression receiver, Expression argument)
    {
        var receiverType = Nullable.GetUnderlyingType(receiver.Type) ?? receiver.Type;
        var argumentType = argument.RemoveObjectConvert().Type;
        argumentType = Nullable.GetUnderlyingType(argumentType) ?? argumentType;

        return AreMismatchedExactEqualityTypes(receiverType, argumentType);
    }

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

    /// <summary>
    /// Whether <paramref name="member"/> is the <c>Outer</c>/<c>Inner</c> field of an EF-generated
    /// <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> (the wrapper nav-expansion adds per join). Checked by
    /// declaring type, not name, so a joined entity with its own <c>Outer</c>/<c>Inner</c> property isn't mistaken
    /// for join plumbing.
    /// </summary>
    internal static bool IsTransparentIdentifierOuterOrInnerAccess(this MemberExpression member)
        => member.Member.Name is "Outer" or "Inner"
           && member.Member.DeclaringType is { IsGenericType: true } declaringType
           && declaringType.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal);
}
