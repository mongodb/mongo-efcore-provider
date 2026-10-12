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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The structure of a projected element leaf over a cross-document collection, as nav-expansion lowers it:
/// <c>ToList(Where(DbSet&lt;T&gt;, correlation) [.Where(f)] [.OrderBy(k).ThenBy(k)])</c> or the same chain under
/// <c>FirstOrDefault</c> (a predicate argument is hoisted into a <c>Where</c> by nav-expansion).
/// </summary>
internal sealed class ReferencedElementShape
{
    /// <summary>Whether the leaf is <c>FirstOrDefault</c> (one element or <see langword="null"/>) rather than <c>ToList</c>.</summary>
    public required bool IsFirst { get; init; }

    /// <summary>The query root of the subquery's entity type.</summary>
    public required EntityQueryRootExpression Root { get; init; }

    /// <summary>The <c>Where</c> directly over <see cref="Root"/>: the correlation with the outer entity.</summary>
    public required LambdaExpression Correlation { get; init; }

    /// <summary>The uncorrelated element filters, innermost first. Moving one ahead of the ordering does not change the result.</summary>
    public required IReadOnlyList<LambdaExpression> Filters { get; init; }

    /// <summary>The ordering keys, primary first.</summary>
    public required IReadOnlyList<(LambdaExpression Key, bool Ascending)> Sorts { get; init; }

    /// <summary>
    /// The element selector of <c>Select(o =&gt; o.Total).ToList()</c>, or <see langword="null"/> for a list of the entities themselves.
    /// Only a <c>ToList</c> carries one: a scalar <c>FirstOrDefault().Member</c> is the correlated-reducer leaf.
    /// </summary>
    public LambdaExpression? Selector { get; init; }
}

/// <summary>
/// Recognizes <see cref="ReferencedElementShape"/>. Purely structural and side-effect free, so the emit side
/// (<see cref="NativeProjectionBinder"/>) and the read side (<c>MongoProjectionBindingExpressionVisitor</c>) can both call it; the
/// emit side alone decides admissibility and records the leaf on the lookup it staged.
/// </summary>
internal static class NativeReferencedElementMatcher
{
    internal static bool TryMatch(Expression leaf, [NotNullWhen(true)] out ReferencedElementShape? shape)
    {
        shape = null;

        bool isFirst;
        Expression source;
        switch (leaf)
        {
            case MethodCallExpression
            {
                Method: { Name: nameof(Enumerable.ToList), DeclaringType: var toListDeclaring },
                Arguments: [var toListSource]
            } when toListDeclaring == typeof(Enumerable):
                isFirst = false;
                source = toListSource;
                break;

            case MethodCallExpression
            {
                Method: { Name: nameof(Queryable.FirstOrDefault), DeclaringType: var firstDeclaring },
                Arguments: [var firstSource]
            } when firstDeclaring == typeof(Queryable):
                isFirst = true;
                source = firstSource;
                break;

            default:
                return false;
        }

        // `Select(o => o.Total).ToList()`: the selector is applied to the materialized entities. An identity selector (what a ThenInclude
        // leaves behind) is none.
        LambdaExpression? selector = null;
        if (!isFirst
            && source is MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring },
                Arguments: [var selectSource, var selectorArgument]
            }
            && selectDeclaring == typeof(Queryable)
            && selectorArgument is UnaryExpression { NodeType: ExpressionType.Quote } or LambdaExpression
            && selectorArgument.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } selectLambda)
        {
            source = selectSource;
            if (selectLambda.Body != selectLambda.Parameters[0])
                selector = selectLambda;
        }

        var wheres = new List<LambdaExpression>();
        var sorts = new List<(LambdaExpression Key, bool Ascending)>();
        var primarySorts = 0;
        var previousWasWhere = false;
        var current = source;

        while (current is not EntityQueryRootExpression)
        {
            // Only the operators below take a lambda second argument; Concat/Union/Except/... take a query, which must not be unwrapped as one.
            if (current is not MethodCallExpression
                {
                    Method: { DeclaringType: var declaring, Name: nameof(Queryable.Where) or nameof(Queryable.OrderBy)
                        or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) } method
                } call
                || declaring != typeof(Queryable)
                || call.Arguments is not [var next, var lambdaArgument]
                || lambdaArgument is not (UnaryExpression { NodeType: ExpressionType.Quote } or LambdaExpression)
                || lambdaArgument.UnwrapLambdaFromQuote() is not { Parameters.Count: 1 } lambda)
            {
                return false;
            }

            switch (method.Name)
            {
                case nameof(Queryable.Where):
                    wheres.Add(lambda);
                    previousWasWhere = true;
                    break;
                case nameof(Queryable.OrderBy):
                    primarySorts++;
                    sorts.Add((lambda, true));
                    previousWasWhere = false;
                    break;
                case nameof(Queryable.OrderByDescending):
                    primarySorts++;
                    sorts.Add((lambda, false));
                    previousWasWhere = false;
                    break;
                case nameof(Queryable.ThenBy):
                    sorts.Add((lambda, true));
                    previousWasWhere = false;
                    break;
                case nameof(Queryable.ThenByDescending):
                    sorts.Add((lambda, false));
                    previousWasWhere = false;
                    break;
                default:
                    // Select, Skip, Take, Distinct, ...: not an element leaf.
                    return false;
            }

            current = next;
        }

        // The correlation is the Where the subquery starts with; a second OrderBy would re-sort from scratch.
        if (!previousWasWhere || wheres.Count == 0 || primarySorts > 1)
            return false;

        // Collected outermost first.
        sorts.Reverse();
        wheres.Reverse();

        shape = new ReferencedElementShape
        {
            IsFirst = isFirst,
            Root = (EntityQueryRootExpression)current,
            Correlation = wheres[0],
            Filters = wheres.Skip(1).ToList(),
            Sorts = sorts,
            Selector = selector
        };
        return true;
    }
}
