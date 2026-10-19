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
using System.Linq;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Resolves which logical scope a transparent-identifier member chain (e.g. <c>ti.Outer.Inner</c>) refers to,
/// for <c>SelectMany</c>'s <c>TransparentIdentifier</c> and <c>Join</c>'s (possibly user-named) result selector.
/// Resolution is by parameter identity, never member name alone (see <c>Query/AGENTS.md</c>).
/// </summary>
internal static class MongoTransparentScopeResolver
{
    /// <summary>
    /// The hop names of EF Core's <c>TransparentIdentifier&lt;TOuter, TInner&gt;</c> (<c>[outerHop, innerHop]</c>),
    /// which every <c>SelectMany</c>/<c>Join</c> scope chain in the native translator is built from.
    /// </summary>
    internal static readonly IReadOnlyList<string> TransparentIdentifierHops = ["Outer", "Inner"];

    /// <summary>
    /// Peels a chain of member accesses named from <paramref name="hopNames"/> down to the bare
    /// <paramref name="rootParam"/> parameter, and resolves which scope it refers to. Given
    /// <paramref name="sourceCount"/> chained scopes, the <c>k</c>-th level's own element is reached via
    /// <c>(sourceCount - k)</c> leading <c>hopNames[0]</c> hops followed by exactly one trailing
    /// <c>hopNames[1]</c> hop; the root scope is reached via exactly <paramref name="sourceCount"/>
    /// <c>hopNames[0]</c> hops. <paramref name="scopeIndex"/> is <c>0</c> for the root, or <c>k</c> (1-based).
    /// Returns <see langword="false"/> for any other shape. <paramref name="hopNames"/> is exactly
    /// <c>[outerHop, innerHop]</c>.
    /// </summary>
    internal static bool TryResolveScopeDepth(
        Expression? scopeAccess, ParameterExpression rootParam, IReadOnlyList<string> hopNames, int sourceCount,
        out int scopeIndex)
    {
        scopeIndex = -1;
        var outerHop = hopNames[0];
        var innerHop = hopNames[1];

        var path = new List<string>();
        var current = scopeAccess;
        while (current is MemberExpression { Member.Name: { } name } hop && (name == outerHop || name == innerHop))
        {
            path.Add(name);
            current = hop.Expression;
        }

        if (current != rootParam || path.Count == 0 || path.Count > sourceCount)
            return false;

        path.Reverse(); // now ordered outward-from-root: path[0] is the first hop off rootParam.

        if (path[^1] == innerHop && path.Take(path.Count - 1).All(h => h == outerHop))
        {
            scopeIndex = sourceCount - path.Count + 1;
            return true;
        }

        if (path.Count == sourceCount && path.All(h => h == outerHop))
        {
            scopeIndex = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rewrites every scope-rooted member access in an expression onto the matching per-scope synthetic
    /// parameter, recording the single scope it resolves to (or flagging <see cref="CrossScope"/> if operands
    /// span more than one). A non-scope-rooted member is left untouched.
    /// </summary>
    internal sealed class ScopeRerootingVisitor(
        ParameterExpression rootParam, IReadOnlyList<string> hopNames, int sourceCount, ParameterExpression[] scopeParams)
        : ExpressionVisitor
    {
        public int? ResolvedScope { get; private set; }
        public bool CrossScope { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryResolveScopeDepth(node.Expression, rootParam, hopNames, sourceCount, out var scope))
            {
                // If the recorded scope type doesn't match the member's declaring type, MakeMemberAccess would
                // throw. Leave the subtree unrewritten instead; the caller's outside-hop-chain guard then declines.
                if (node.Member.DeclaringType?.IsAssignableFrom(scopeParams[scope].Type) != true)
                {
                    return base.VisitMember(node);
                }

                if (ResolvedScope is { } prior && prior != scope)
                    CrossScope = true;
                ResolvedScope = scope;
                return Expression.MakeMemberAccess(scopeParams[scope], node.Member);
            }

            return base.VisitMember(node);
        }
    }
}
