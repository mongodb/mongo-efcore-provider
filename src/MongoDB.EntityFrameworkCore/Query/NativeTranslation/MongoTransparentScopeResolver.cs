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
/// Shared parameter-identity scope resolution for a nested "transparent identifier"-shaped parameter — a
/// single lambda parameter whose member-access chain (e.g. <c>ti.Outer.Inner</c>) walks down through a fixed
/// set of named hops to one of several logical scopes. Originally built for <c>SelectMany</c>'s
/// <c>TransparentIdentifier(Outer, Inner)</c> shape (hop names always literally <c>"Outer"</c>/<c>"Inner"</c>);
/// generalized to accept a caller-supplied hop-names list so <c>Join</c>'s own (possibly
/// user-named) two-scope result selector can reuse the identical walk. See the scope-by-parameter-identity
/// invariant in <c>Query/AGENTS.md</c> — resolution is always by parameter identity, never by member name.
/// </summary>
internal static class MongoTransparentScopeResolver
{
    /// <summary>
    /// Peels a chain of member accesses named from <paramref name="hopNames"/> down to the bare
    /// <paramref name="rootParam"/> parameter, and resolves which scope it refers to. Given
    /// <paramref name="sourceCount"/> chained scopes, the <c>k</c>-th level's own element is reached via
    /// <c>(sourceCount - k)</c> leading <c>hopNames[0]</c> hops followed by exactly one trailing
    /// <c>hopNames[1]</c> hop; the root scope is reached via exactly <paramref name="sourceCount"/>
    /// <c>hopNames[0]</c> hops and no <c>hopNames[1]</c> at all. <paramref name="scopeIndex"/> is <c>0</c> for
    /// the root, or <c>k</c> (1-based) for the <c>k</c>-th nested scope. Returns <see langword="false"/> —
    /// declining cleanly — for any chain that does not terminate exactly at <paramref name="rootParam"/>, is
    /// empty, exceeds <paramref name="sourceCount"/> hops, or does not match either valid shape.
    /// <paramref name="hopNames"/> must contain exactly two elements: index 0 is the outer/root-ward hop name,
    /// index 1 is the inner/leaf-ward hop name.
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
                if (ResolvedScope is { } prior && prior != scope)
                    CrossScope = true;
                ResolvedScope = scope;
                return Expression.MakeMemberAccess(scopeParams[scope], node.Member);
            }

            return base.VisitMember(node);
        }
    }
}
