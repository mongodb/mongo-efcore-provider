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

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// "Does this tree contain a node matching X?" searches. Walks with the default
/// <see cref="System.Linq.Expressions.ExpressionVisitor"/> traversal (extension nodes descend through their own
/// <c>VisitChildren</c>) and stops at the first match.
/// </summary>
internal static class ExpressionSearch
{
    /// <summary>Whether <paramref name="expression"/> or any node below it satisfies <paramref name="predicate"/>.</summary>
    public static bool Contains(Expression? expression, Func<Expression, bool> predicate)
    {
        var finder = new Finder(predicate);
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class Finder(Func<Expression, bool> predicate) : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (Found || node is null)
            {
                return node;
            }

            if (predicate(node))
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }
}
