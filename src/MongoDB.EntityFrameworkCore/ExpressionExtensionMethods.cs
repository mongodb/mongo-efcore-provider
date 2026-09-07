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
    /// Reads a WRAPPED projection body — an anonymous type / DTO construction — into its
    /// (member name, value expression) pairs, in both spellings the C# compiler produces: a
    /// <see cref="NewExpression"/> carrying <see cref="NewExpression.Members"/> (anonymous types and
    /// positional constructors) or a <see cref="MemberInitExpression"/> over a parameterless constructor
    /// (object-initializer syntax).
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — leaving <paramref name="members"/> empty — for a body that is not a wrapped
    /// construction, has no members, or uses a member binding this cannot express (a nested or list binding,
    /// or a <see cref="MemberInitExpression"/> whose constructor itself takes arguments). Callers treat that as
    /// "not a wrapped projection" and fall through to their bare-body handling.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Eight sites read this same shape — two arms inside <c>NativeProjectionBinder</c>'s own switch, plus
    /// <c>NativeProjectionBinder</c>'s document-construction leaf, <c>NativeJoinScopeProjectionBinder</c>,
    /// <c>NativeSelectManyBinder</c>, <c>NativeGroupByBinder</c>, and the QMTEV's two shaper builders
    /// (<c>TryBuildGroupResultShaper</c>, <c>BuildSelectManyResultShaper</c>, which additionally REBUILD the
    /// construction — see <see cref="RebuildProjectionMembers"/>). They had **already drifted**: the
    /// <c>GroupBy</c> copy omitted both the <c>Members.Count == Arguments.Count</c> pairing check and the
    /// non-empty checks its five siblings carry, so it accepted a degenerate empty construction. This is the
    /// strict form; a degenerate body now declines and falls back, which is the safe direction.
    /// </para>
    /// </remarks>
    internal static bool TryGetProjectionMembers(
        this Expression body, out IReadOnlyList<(string MemberName, Expression Value)> members)
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

            case MemberInitExpression { NewExpression.Arguments.Count: 0, Bindings.Count: > 0 } memberInit:
            {
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
    /// Rebuilds a wrapped projection body — the counterpart of <see cref="TryGetProjectionMembers"/> — with each
    /// member's value replaced by the matching entry of <paramref name="values"/>, preserving the original
    /// construction spelling (<see cref="NewExpression"/> or <see cref="MemberInitExpression"/>).
    /// </summary>
    /// <remarks>
    /// Only valid on a <paramref name="body"/> that <see cref="TryGetProjectionMembers"/> accepted, and
    /// <paramref name="values"/> must be in that method's own order. The <see cref="MemberAssignment"/> cast is
    /// safe for exactly that reason: the reader rejects any other binding kind, so a body it admitted has none.
    /// That coupling is the point — the shaper-building callers used to restate the admissibility switch in
    /// order to rebuild, which meant deciding admissibility WHILE already mutating the projection (see
    /// <c>TryBuildGroupResultShaper</c>).
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
    /// Matches a SINGLE access hop in either spelling EF Core produces — a plain
    /// <see cref="MemberExpression"/> (an ordinary scalar/navigation access) or the shadow-safe
    /// <c>EF.Property&lt;T&gt;(root, "Name")</c> call its navigation expansion emits — yielding the receiver and
    /// the accessed name.
    /// </summary>
    /// <remarks>
    /// Both spellings must resolve identically wherever a member chain is walked, which is why this is shared:
    /// <c>MongoExpressionTranslator</c> and <c>NativeSelectManyBinder</c> held byte-identical private copies
    /// (the former's comment even read "Mirrors NativeSelectManyBinder.TryGetMemberAccess"). Note the
    /// SPECIALIZED matchers elsewhere in the native binders — "is this a member access on THIS parameter", "give
    /// me the root parameter of this chain" — are deliberately not folded in here: they answer different
    /// questions and do not all agree on details, so collapsing them would change what each accepts.
    /// </remarks>
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
    /// Whether <paramref name="expression"/> anywhere references <paramref name="parameter"/>.
    /// </summary>
    /// <remarks>
    /// Scope questions in the native translators are decided by parameter IDENTITY, never by member name — see
    /// the standing invariant in <c>Query/AGENTS.md</c>; a name-based test conflates a member two scopes happen
    /// to share (both typically have an <c>Id</c>). This lives here because <c>NativeSelectManyBinder</c> and
    /// <c>NativeProjectionBinder</c> each held an identical private wrapper-plus-visitor pair for it. The
    /// scope-CLASSIFYING visitors elsewhere (<c>NativeJoinScopeTranslator</c>'s inner-access detector,
    /// <c>MongoExpressionTranslator</c>'s free-parameter collector) are deliberately NOT this: they answer which
    /// scope, or which parameters, not merely whether one appears.
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
    /// Whether <paramref name="type"/> is one of EF Core's compiler-generated
    /// <c>TransparentIdentifier&lt;TOuter, TInner&gt;</c> constructions — the anonymous result type a join's
    /// result selector wraps its outer/inner pair in.
    /// </summary>
    /// <remarks>
    /// Callers should check an <c>"Outer"</c>/<c>"Inner"</c> member's DECLARING TYPE with this helper rather
    /// than matching the member name alone — otherwise any user type exposing a member called <c>Outer</c> or
    /// <c>Inner</c> would be mistaken for join plumbing. Shared by
    /// <c>MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs</c> and
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.ClassifyJoinHop</c>, which must stay in agreement.
    /// </remarks>
    internal static bool IsTransparentIdentifierType(this Type? type)
        => type is { IsGenericType: true }
           && type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal);

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
    /// Whether <paramref name="member"/> is an access to the <c>Outer</c>/<c>Inner</c> field of an EF-
    /// generated <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> — the wrapper nav-expansion introduces
    /// for each join in a chain. Checked by declaring type (not just member name) so a joined entity that
    /// happens to declare its own real <c>Outer</c>/<c>Inner</c> property isn't mistaken for join-chain
    /// plumbing.
    /// </summary>
    internal static bool IsTransparentIdentifierOuterOrInnerAccess(this MemberExpression member)
        => member.Member.Name is "Outer" or "Inner"
           && member.Member.DeclaringType is { IsGenericType: true } declaringType
           && declaringType.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal);
}
