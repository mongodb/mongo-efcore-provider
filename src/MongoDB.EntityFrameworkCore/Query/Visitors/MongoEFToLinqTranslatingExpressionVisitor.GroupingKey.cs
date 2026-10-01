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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Grouping-key references inside element lambdas. The driver folds an aggregate over a group
// (g.Count(o => g.Key.Country == "US")) into a $group accumulator, which is evaluated against each *input*
// document, yet still renders g.Key there as "$_id" — the input document's own _id, not the group key — so the
// answer is silently wrong. Every element of a group has that group's key, so inside a lambda over the group's
// elements g.Key is replaced by the key selector applied to the element; the driver then renders per-document
// fields, which is equally correct when it keeps the $push-then-$project form instead of folding.
//
// Only GroupBy(source, key) is handled. EF Core's navigation expansion inlines an element selector into the
// element lambdas before this visitor runs; the comparer overloads are left alone (keys within a group are then
// only comparer-equal, so one element's key is not the group key), as is any other shape.
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor
{
    // Queryable operators between a GroupBy and the operator whose lambdas see the groupings.
    private static readonly HashSet<string> GroupingPreservingQueryableMethods =
    [
        nameof(Queryable.Where),
        nameof(Queryable.OrderBy),
        nameof(Queryable.OrderByDescending),
        nameof(Queryable.ThenBy),
        nameof(Queryable.ThenByDescending),
        nameof(Queryable.Skip),
        nameof(Queryable.Take)
    ];

    // Operators over a group's element sequence that yield the same elements.
    private static readonly HashSet<string> ElementPreservingMethods =
    [
        nameof(Enumerable.Where),
        nameof(Enumerable.OrderBy),
        nameof(Enumerable.OrderByDescending),
        nameof(Enumerable.ThenBy),
        nameof(Enumerable.ThenByDescending),
        nameof(Enumerable.Skip),
        nameof(Enumerable.Take),
        nameof(Enumerable.SkipWhile),
        nameof(Enumerable.TakeWhile),
        nameof(Enumerable.Distinct),
        nameof(Enumerable.AsEnumerable),
        nameof(Queryable.AsQueryable),
        nameof(Enumerable.ToList),
        nameof(Enumerable.ToArray)
    ];

    // Operators over a group's element sequence whose argument 1 is a lambda over one element.
    private static readonly HashSet<string> ElementLambdaMethods =
    [
        nameof(Enumerable.Where),
        nameof(Enumerable.Select),
        nameof(Enumerable.SelectMany),
        nameof(Enumerable.Count),
        nameof(Enumerable.LongCount),
        nameof(Enumerable.Sum),
        nameof(Enumerable.Average),
        nameof(Enumerable.Min),
        nameof(Enumerable.Max),
        nameof(Enumerable.MinBy),
        nameof(Enumerable.MaxBy),
        nameof(Enumerable.Any),
        nameof(Enumerable.All),
        nameof(Enumerable.First),
        nameof(Enumerable.FirstOrDefault),
        nameof(Enumerable.Single),
        nameof(Enumerable.SingleOrDefault),
        nameof(Enumerable.Last),
        nameof(Enumerable.LastOrDefault),
        nameof(Enumerable.OrderBy),
        nameof(Enumerable.OrderByDescending),
        nameof(Enumerable.ThenBy),
        nameof(Enumerable.ThenByDescending),
        nameof(Enumerable.SkipWhile),
        nameof(Enumerable.TakeWhile),
        nameof(Enumerable.DistinctBy)
    ];

    /// <summary>
    /// For a <c>Queryable</c> operator over a <c>GroupBy(source, key)</c> result, rewrites grouping-key references
    /// inside element lambdas of its grouping lambdas (see the file header). Returns <paramref name="node"/>
    /// unchanged when nothing applies.
    /// </summary>
    private static MethodCallExpression RewriteGroupingKeyReferencesInElementLambdas(MethodCallExpression node)
    {
        if (node.Method.DeclaringType != typeof(Queryable) || node.Arguments.Count < 2)
        {
            return node;
        }

        if (!TryFindGroupByKeySelector(node, out var keySelector))
        {
            return node;
        }

        var sourceRow = keySelector.Parameters[0];
        var arguments = node.Arguments.ToArray();
        var changed = false;
        for (var i = 1; i < arguments.Length; i++)
        {
            if (UnwrapLambda(arguments[i]) is not { Parameters.Count: >= 1 } lambda
                || !IsGroupingOf(lambda.Parameters[0].Type, keySelector.ReturnType, sourceRow.Type))
            {
                continue;
            }

            var grouping = lambda.Parameters[0];
            var body = new ElementLambdaKeyRewriter(grouping, sourceRow, keySelector.Body).Visit(lambda.Body);
            if (body != lambda.Body)
            {
                arguments[i] = RequoteLike(arguments[i], Expression.Lambda(lambda.Type, body, lambda.Parameters));
                changed = true;
            }
        }

        return changed ? node.Update(node.Object, arguments) : node;
    }

    // The key selector of the GroupBy(source, key) that `node`'s grouping lambdas range over.
    private static bool TryFindGroupByKeySelector(MethodCallExpression node, [NotNullWhen(true)] out LambdaExpression? keySelector)
    {
        keySelector = null;
        if (node.Method.DeclaringType != typeof(Queryable) || node.Arguments.Count < 2)
        {
            return false;
        }

        var source = node.Arguments[0];
        while (source is MethodCallExpression { Method.Name: var name } sourceCall
               && sourceCall.Method.DeclaringType == typeof(Queryable)
               && GroupingPreservingQueryableMethods.Contains(name))
        {
            source = sourceCall.Arguments[0];
        }

        if (source is not MethodCallExpression { Method.Name: nameof(Queryable.GroupBy), Arguments.Count: 2 } groupBy
            || groupBy.Method.DeclaringType != typeof(Queryable)
            || UnwrapLambda(groupBy.Arguments[1]) is not { Parameters.Count: 1 } selector)
        {
            return false;
        }

        keySelector = selector;
        return true;
    }

    // g.Key.Hour / g.Key.Date.AddDays(..) in a grouping lambda, where the GroupBy key is a Local-kind DateTime property:
    // the group key is a Local-kind value too, so the date operation is refused like it is on the property itself.
    private void ThrowIfLocalKindGroupKeyDateOperation(MethodCallExpression node)
    {
        if (!TryFindGroupByKeySelector(node, out var keySelector))
        {
            return;
        }

        for (var i = 1; i < node.Arguments.Count; i++)
        {
            if (UnwrapLambda(node.Arguments[i]) is { Parameters.Count: >= 1 } lambda
                && IsGroupingOf(lambda.Parameters[0].Type, keySelector.ReturnType, keySelector.Parameters[0].Type))
            {
                new GroupKeyDateOperationVisitor(
                    lambda.Parameters[0],
                    keySelector.Body,
                    (receiver, operation) => ThrowIfLocalKindDateTime(receiver, operation)).Visit(lambda.Body);
            }
        }
    }

    private sealed class GroupKeyDateOperationVisitor(
        ParameterExpression grouping, Expression keyBody, Action<Expression, string> check)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } receiver && node.Member.DeclaringType == typeof(DateTime) && node.Member.Name != nameof(DateTime.Kind))
            {
                CheckResolved(receiver, node.Member.Name);
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Object is { } receiver
                && node.Method.DeclaringType == typeof(DateTime)
                && node.Method.Name is nameof(DateTime.AddYears) or nameof(DateTime.AddMonths) or nameof(DateTime.AddDays))
            {
                CheckResolved(receiver, node.Method.Name);
            }

            return base.VisitMethodCall(node);
        }

        // Writes the receiver's g.Key references in terms of the key selector's body, then asks whether that is Local-kind.
        private void CheckResolved(Expression receiver, string operation)
        {
            var resolved = new KeySubstitutor(grouping, keyBody).Visit(receiver);
            if (resolved != receiver)
            {
                check(resolved, operation);
            }
        }
    }

    private static LambdaExpression? UnwrapLambda(Expression expression)
        => expression switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression quoted } => quoted,
            LambdaExpression lambda => lambda,
            _ => null
        };

    private static Expression RequoteLike(Expression original, LambdaExpression rewritten)
        => original is UnaryExpression { NodeType: ExpressionType.Quote } ? Expression.Quote(rewritten) : rewritten;

    private static bool IsGroupingOf(Type type, Type keyType, Type elementType)
        => type.IsGenericType
           && type.GetGenericTypeDefinition() == typeof(IGrouping<,>)
           && type.GenericTypeArguments[0] == keyType
           && type.GenericTypeArguments[1] == elementType;

    /// <summary>
    /// Walks a grouping lambda body; for each operator over the group's element sequence (rooted at
    /// <paramref name="grouping"/>), substitutes <c>grouping.Key</c> references inside its element lambda with
    /// <paramref name="keyBody"/> written over that lambda's element.
    /// </summary>
    private sealed class ElementLambdaKeyRewriter(
        ParameterExpression grouping,
        ParameterExpression sourceRow,
        Expression keyBody)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Object != null
                || node.Arguments.Count < 2
                || (node.Method.DeclaringType != typeof(Enumerable) && node.Method.DeclaringType != typeof(Queryable))
                || !ElementLambdaMethods.Contains(node.Method.Name)
                || !IsElementSequence(node.Arguments[0])
                || UnwrapLambda(node.Arguments[1]) is not { Parameters.Count: 1 } lambda
                || lambda.Parameters[0].Type != sourceRow.Type)
            {
                return base.VisitMethodCall(node);
            }

            var key = ReplacingExpressionVisitor.Replace(sourceRow, lambda.Parameters[0], keyBody);
            var body = new KeySubstitutor(grouping, key).Visit(lambda.Body);

            var arguments = node.Arguments.ToArray();
            arguments[0] = Visit(arguments[0]);
            if (body != lambda.Body)
            {
                arguments[1] = RequoteLike(arguments[1], Expression.Lambda(lambda.Type, body, lambda.Parameters));
            }

            for (var i = 2; i < arguments.Length; i++)
            {
                arguments[i] = Visit(arguments[i]);
            }

            return node.Update(null, arguments);
        }

        private bool IsElementSequence(Expression expression)
        {
            while (true)
            {
                if (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert)
                {
                    expression = convert.Operand;
                    continue;
                }

                if (expression == grouping)
                {
                    return true;
                }

                if (expression is MethodCallExpression { Object: null, Arguments.Count: >= 1 } call
                    && (call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(Queryable))
                    && ElementPreservingMethods.Contains(call.Method.Name))
                {
                    expression = call.Arguments[0];
                    continue;
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Replaces <c>grouping.Key</c> (and member chains over it) with <paramref name="key"/>, the key written over
    /// the current element, resolving members through an anonymous/member-init key construction. A reference that
    /// resolves to a whole composite key (<c>new { ... }</c>), or to a member the construction doesn't expose
    /// (e.g. a constructor-call key), is left unchanged.
    /// </summary>
    private sealed class KeySubstitutor(ParameterExpression grouping, Expression key)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => Resolve(node) is { } resolved and not (NewExpression or MemberInitExpression)
                ? resolved.Type == node.Type ? resolved : Expression.Convert(resolved, node.Type)
                : base.VisitMember(node);

        private Expression? Resolve(Expression expression)
        {
            if (expression is not MemberExpression { Expression: { } inner } member)
            {
                return null;
            }

            if (inner == grouping)
            {
                return member.Member.Name == nameof(IGrouping<object, object>.Key) ? key : null;
            }

            switch (Resolve(inner))
            {
                case null:
                    return null;

                case NewExpression { Members: { } members } newExpression:
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (members[i].Name == member.Member.Name)
                        {
                            return newExpression.Arguments[i];
                        }
                    }

                    return null;

                case NewExpression:
                    return null;

                case MemberInitExpression memberInit:
                    return memberInit.Bindings
                        .OfType<MemberAssignment>()
                        .FirstOrDefault(b => b.Member.Name == member.Member.Name)?.Expression;

                case var resolvedInner:
                    return Expression.MakeMemberAccess(resolvedInner, member.Member);
            }
        }
    }
}
