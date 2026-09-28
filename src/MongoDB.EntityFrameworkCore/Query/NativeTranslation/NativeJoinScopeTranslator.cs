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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Resolves member access over a native join's <c>TransparentIdentifier(Outer, Inner)</c> shape against a
/// <see cref="MongoJoinScope"/>, using the two-scope <see cref="MongoExpressionTranslator"/> constructor.
/// </summary>
internal static class NativeJoinScopeTranslator
{
    public static bool TryTranslatePredicate(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body,
        [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(scope, rootParam, body, valueMode: false, out result);

    public static bool TryTranslateValue(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body,
        [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(scope, rootParam, body, valueMode: true, out result);

    /// <summary>
    /// Whether <paramref name="body"/> references the join's Inner side. <c>NativeSlotPopulator</c>'s Outer-only
    /// <c>Where</c>/<c>OrderBy</c> arms reject such bodies: they record into <c>PipelineOps</c>, which lower before the
    /// <c>$lookup</c> that materializes Inner, so they would filter on a field that doesn't exist yet and silently
    /// match nothing. Inner-reaching bodies go to the separate Inner arms, which use the same general-purpose
    /// <see cref="TryTranslatePredicate"/>/<see cref="TryTranslateValue"/> but defer into <c>PostJoinOps</c>.
    /// </summary>
    public static bool ReferencesInnerScope(ParameterExpression rootParam, Expression body)
    {
        var detector = new InnerAccessDetector(rootParam);
        detector.Visit(body);
        return detector.Found;
    }

    /// <summary>
    /// Resolves member access over a chained join's nested <c>TransparentIdentifier</c> shape, restricted to the root
    /// scope (index 0); used when <paramref name="scope"/>.Levels.Count > 1. Delegates to
    /// <see cref="MongoTransparentScopeResolver"/> with hops <c>["Outer", "Inner"]</c> and
    /// <c>sourceCount = scope.Levels.Count</c>: index 0 is the root, index <c>k</c> is <c>scope.Levels[k-1]</c>'s Inner.
    /// </summary>
    public static bool TryTranslateRootScopeOnly(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten) || scopeIndex != 0)
        {
            return false;
        }

        var translator = new MongoExpressionTranslator(scope.OuterEntityType);
        return valueMode
            ? translator.TryTranslateValue(rewritten, out result)
            : translator.TryTranslate(rewritten, out result);
    }

    /// <summary>
    /// Resolves a scalar/computed leaf rooted at any single scope of a chained join (never one spanning scopes).
    /// Used by <see cref="NativeJoinScopeProjectionBinder"/> when <c>scope.Levels.Count &gt; 1</c>. Resolves by the
    /// member-name hop chain rather than by comparing CLR types, so it avoids the residual gap described in
    /// <c>TryTranslateCore</c>.
    /// </summary>
    public static bool TryTranslateSingleScope(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten))
        {
            return false;
        }

        MongoExpressionTranslator translator;
        if (scopeIndex == 0)
        {
            translator = new MongoExpressionTranslator(scope.OuterEntityType);
        }
        else
        {
            var level = scope.Levels[scopeIndex - 1];

            // The throwaway outer parameter never appears in `rewritten` (the body resolves to scopeIndex, not 0), so
            // every member resolves via the inner branch: scope.Levels[k-1].InnerEntityType with its InnerPrefix.
            var unusedOuterParam = Expression.Parameter(scope.OuterEntityType.ClrType, "unusedOuterScope");
            translator = new MongoExpressionTranslator(
                level.InnerEntityType, unusedOuterParam, scope.OuterEntityType, level.InnerPrefix);
        }

        return valueMode
            ? translator.TryTranslateValue(rewritten, out result)
            : translator.TryTranslate(rewritten, out result);
    }

    /// <summary>
    /// Rewrites <paramref name="body"/>'s <c>Outer</c>/<c>Inner</c> hop chain onto one synthetic parameter per scope
    /// level, via <see cref="MongoTransparentScopeResolver.ScopeRerootingVisitor"/>. Succeeds only when the whole
    /// body resolves to a single scope index and no other reference to <paramref name="rootParam"/> survives. The
    /// caller decides whether <paramref name="scopeIndex"/> is acceptable.
    /// </summary>
    private static bool TryRerootToSingleScope(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body,
        out int scopeIndex, [NotNullWhen(true)] out Expression? rewritten)
    {
        scopeIndex = -1;
        rewritten = null;

        // Guard: a parameter that merely exposes members named "Outer"/"Inner" must not be walked as a join chain.
        if (!rootParam.Type.IsTransparentIdentifierType())
        {
            return false;
        }

        var sourceCount = scope.Levels.Count;

        // Index 0 is the root scope's parameter; 1..sourceCount are each level's Inner.
        var scopeParams = new ParameterExpression[sourceCount + 1];
        scopeParams[0] = Expression.Parameter(scope.OuterEntityType.ClrType, "rootScope");
        for (var i = 0; i < sourceCount; i++)
        {
            scopeParams[i + 1] = Expression.Parameter(scope.Levels[i].InnerEntityType.ClrType, $"innerScope{i}");
        }

        var visitor = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
            rootParam, hopNames: ["Outer", "Inner"], sourceCount, scopeParams);
        var candidate = visitor.Visit(body);

        // ScopeRerootingVisitor only rewrites pure Outer*/Inner? hop chains; any other reference to rootParam survives
        // untouched and could resolve against the wrong entity downstream. Reject it.
        if (visitor.CrossScope
            || visitor.ResolvedScope is not { } resolved
            || ReferencesParameterOutsideHopChain(candidate, rootParam))
        {
            return false;
        }

        scopeIndex = resolved;
        rewritten = candidate;
        return true;
    }

    /// <summary>True if <paramref name="rootParam"/> still appears in <paramref name="rewritten"/>, i.e. some
    /// reference to it wasn't part of the Outer*/Inner? hop chain.</summary>
    private static bool ReferencesParameterOutsideHopChain(Expression rewritten, ParameterExpression rootParam)
    {
        var found = false;
        new ParameterPresenceVisitor(rootParam, () => found = true).Visit(rewritten);
        return found;
    }

    private sealed class ParameterPresenceVisitor(ParameterExpression target, Action onFound) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, target))
                onFound();

            return base.VisitParameter(node);
        }
    }

    /// <summary>
    /// Recognizes a top-level <c>rootParam.Inner == null</c> / <c>!= null</c> (either operand order): a
    /// reference-<c>Include</c>'s null check on its navigation (e.g. <c>Include(e =&gt; e.Manager).First(e =&gt;
    /// e.Manager == null)</c>) after nav-expansion.
    /// </summary>
    /// <remarks>
    /// Structural only; whether the join may be confirmed is <c>NativeSlotPopulator</c>'s call. Never matches under
    /// <c>Not</c> or a quantifier (see <see cref="MongoLookupNullCheckExpression"/> for why that's safe).
    /// </remarks>
    public static bool TryMatchInnerNullCheck(ParameterExpression rootParam, Expression body, out bool isNotNull)
    {
        isNotNull = false;

        if (body is not BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } binary)
            return false;

        var leftIsInner = IsBareInnerAccess(rootParam, binary.Left);
        var rightIsInner = IsBareInnerAccess(rootParam, binary.Right);

        // Exactly one side must be the bare Inner access; neither or both (self-compare) decline.
        if (leftIsInner == rightIsInner)
            return false;

        var otherSide = leftIsInner ? binary.Right : binary.Left;
        if (otherSide is not ConstantExpression { Value: null })
            return false;

        isNotNull = binary.NodeType == ExpressionType.NotEqual;
        return true;
    }

    /// <summary>
    /// Depth-agnostic <see cref="TryMatchInnerNullCheck"/>: <c>rootParam.«hop chain» == null</c> / <c>!= null</c> for
    /// any non-root scope level, resolved by member-name chain via <see cref="TryRerootToBareScope"/> rather than by
    /// CLR type. Structural only: callers must check the level is left-outer and non-collection, otherwise the null
    /// test is degenerate.
    /// </summary>
    public static bool TryMatchScopeNullCheck(
        MongoJoinScope scope, ParameterExpression rootParam, Expression test,
        out int scopeIndex, out bool isNotNull)
    {
        scopeIndex = -1;
        isNotNull = false;

        if (test is not BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } binary)
        {
            return false;
        }

        var leftIsBareScope = TryRerootToBareScope(scope, rootParam, binary.Left, out var leftIndex);
        var rightIsBareScope = TryRerootToBareScope(scope, rootParam, binary.Right, out var rightIndex);

        // Exactly one side must be a bare scope leaf; neither or both (self-compare) decline.
        if (leftIsBareScope == rightIsBareScope)
        {
            return false;
        }

        var (matchedIndex, otherSide) = leftIsBareScope ? (leftIndex, binary.Right) : (rightIndex, binary.Left);

        // The root scope (index 0) can never be missing; only an Inner side can.
        if (matchedIndex == 0 || otherSide is not ConstantExpression { Value: null })
        {
            return false;
        }

        scopeIndex = matchedIndex;
        isNotNull = binary.NodeType == ExpressionType.NotEqual;
        return true;
    }

    // Resolves bare scope leaves (`x.Inner`, `x.Outer.Inner`, no trailing member) via
    // MongoTransparentScopeResolver.TryResolveScopeDepth, by member-name chain rather than CLR type.
    private static bool TryRerootToBareScope(
        MongoJoinScope scope, ParameterExpression rootParam, Expression node, out int scopeIndex)
    {
        // Same TransparentIdentifier guard as TryRerootToSingleScope.
        if (!rootParam.Type.IsTransparentIdentifierType())
        {
            scopeIndex = -1;
            return false;
        }

        return MongoTransparentScopeResolver.TryResolveScopeDepth(
            node, rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count, out scopeIndex);
    }

    private static bool IsBareInnerAccess(ParameterExpression rootParam, Expression node)
        => node is MemberExpression { Member.Name: "Inner" } member
           && ReferenceEquals(member.Expression, rootParam)
           && member.IsTransparentIdentifierOuterOrInnerAccess();

    /// <summary>
    /// <c>customers.Contains(od.Order)</c>, arriving as <c>customers.Contains(ti.Inner)</c> after nav-expansion.
    /// Needs no <c>$lookup</c>: the navigation's FK on the outer document already holds the principal key, so this
    /// rewrites to an FK <c>$in</c> over the list's principal keys (the navigation analog of
    /// <see cref="MongoExpressionTranslator.TryTranslateEntityListContains"/>). A dangling FK still compares correctly.
    /// </summary>
    /// <remarks>
    /// Single-property, non-shadow FK and principal key only (<see cref="MongoInExpression"/> can't express a
    /// composite <c>$in</c>). <paramref name="navigation"/> is always on the dependent side, since it comes from
    /// <see cref="JoinInfo.Navigation"/>.
    /// </remarks>
    public static bool TryMatchInnerListContains(
        ParameterExpression rootParam, Expression body, INavigation navigation,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (body is not MethodCallExpression call
            || !MongoExpressionTranslator.TryMatchContainsMethod(call, out var collection, out var item)
            || !IsBareInnerAccess(rootParam, MongoExpressionTranslator.Unwrap(item)))
            return false;

        if (navigation.IsCollection)
            return false; // reversed (principal-side) shape

        var foreignKey = navigation.ForeignKey;
        if (foreignKey.Properties.Count != 1)
            return false; // composite FK

        var fkProperty = foreignKey.Properties[0];
        if (fkProperty.IsShadowProperty())
            return false;

        var principalKeyProperties = foreignKey.PrincipalKey.Properties;
        if (principalKeyProperties.Count != 1)
            return false; // composite principal key

        var principalKeyProperty = principalKeyProperties[0];
        if (principalKeyProperty.IsShadowProperty())
            return false;

        var valuesNode = MongoExpressionTranslator.TranslateEntityKeyInValues(collection, principalKeyProperty);
        if (valuesNode is null)
            return false;

        result = new MongoInExpression(
            new MongoFieldExpression(fkProperty, MongoExpressionTranslator.GetKeyFieldPath(fkProperty)),
            valuesNode, negated: false);
        return true;
    }

    private static bool TryTranslateCore(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // rootParam.Type must be a flat TransparentIdentifier<TOuter,TInner> whose Outer/Inner types exactly match the
        // recorded scope. A chained second join reuses the first join's JoinScope, and its nested top-level Inner can
        // share the recorded InnerEntityType; rewriting it against the first join's scope would throw or resolve a
        // bogus field. See SameTargetTypeJoinTests.Filter_after_a_flattened_multi_join_chain_is_applied_not_dropped.
        //
        // Residual gap: after `.Join(a, b, ...).Select(x => x.Outer).Join(c, d, ...)` the second join's flat
        // TransparentIdentifier can match the first join's recorded types exactly, and this type check can't tell
        // them apart — scope.InnerPrefix would name the wrong $lookup alias, silently producing wrong data. Every
        // Inner-reaching caller closes it by requiring a single join: the Where/OrderBy Inner arms check
        // `mongoQ.Joins.Count == 1`, the Select arms `scope.Levels.Count == Joins.Count` with a depth-1 scope (see
        // MongoQueryableMethodTranslatingExpressionVisitor). Any new caller needing Inner access must do likewise.
        //
        // EF's TransparentIdentifier exposes Outer/Inner as fields, not properties; see
        // ExpressionExtensionMethods.IsTransparentIdentifierType.
        if (!rootParam.Type.IsTransparentIdentifierType()
            || !TryGetOuterOrInnerMemberType(rootParam.Type, "Outer", out var outerMemberType)
            || outerMemberType != scope.OuterEntityType.ClrType
            || !TryGetOuterOrInnerMemberType(rootParam.Type, "Inner", out var innerMemberType)
            || innerMemberType != scope.Levels[0].InnerEntityType.ClrType)
        {
            return false;
        }

        var outerParam = Expression.Parameter(scope.OuterEntityType.ClrType, "outerScope");
        var innerParam = Expression.Parameter(scope.Levels[0].InnerEntityType.ClrType, "innerScope");
        var splitter = new ScopeSplittingVisitor(rootParam, outerParam, innerParam);
        var rewritten = splitter.Visit(body);

        // Require at least one Outer/Inner access and no other access to rootParam. Otherwise a Where after an
        // Include-generated join (same JoinScope recording as a user Join) with an ordinary root-scoped body is
        // resolved against scope.InnerEntityType, producing a bogus path that silently returns zero rows instead of
        // falling back. Pinned by NativeJoinTests / RequiredNavigationUnwindTests / Ef369MultiJoinComposedTests.
        if (!splitter.SawScopedAccess || splitter.SawUnscopedRootAccess)
            return false;

        var translator = new MongoExpressionTranslator(
            scope.Levels[0].InnerEntityType, outerParam, scope.OuterEntityType, scope.Levels[0].InnerPrefix);

        return valueMode
            ? translator.TryTranslateValue(rewritten, out result)
            : translator.TryTranslate(rewritten, out result);
    }

    /// <summary>
    /// Flat, single-hop rewrite of <c>rootParam.Outer</c>/<c>rootParam.Inner</c> subtrees onto
    /// <paramref name="outerParam"/>/<paramref name="innerParam"/>; a bare <c>x.Outer</c> becomes the parameter itself.
    /// </summary>
    private sealed class ScopeSplittingVisitor(
        ParameterExpression rootParam, ParameterExpression outerParam, ParameterExpression innerParam)
        : ExpressionVisitor
    {
        /// <summary>Whether at least one <c>rootParam.Outer</c>/<c>rootParam.Inner</c> access was rewritten.</summary>
        public bool SawScopedAccess { get; private set; }

        /// <summary>
        /// Whether <c>rootParam</c> was referenced other than via <c>.Outer</c>/<c>.Inner</c>, meaning the body isn't
        /// shaped like the join's TransparentIdentifier.
        /// </summary>
        public bool SawUnscopedRootAccess { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (ReferenceEquals(node.Expression, rootParam))
            {
                // Checked by declaring type, not just name, so a joined entity with its own Outer/Inner member isn't
                // mistaken for join plumbing (must agree with MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs).
                if (node.IsTransparentIdentifierOuterOrInnerAccess())
                {
                    SawScopedAccess = true;
                    return node.Member.Name == "Outer" ? outerParam : innerParam;
                }

                SawUnscopedRootAccess = true;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, rootParam))
                SawUnscopedRootAccess = true;

            return base.VisitParameter(node);
        }
    }

    /// <summary>Backs <see cref="ReferencesInnerScope"/>.</summary>
    private sealed class InnerAccessDetector(ParameterExpression rootParam) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (ReferenceEquals(node.Expression, rootParam)
                && node.Member.Name == "Inner"
                && node.IsTransparentIdentifierOuterOrInnerAccess())
            {
                Found = true;
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>
    /// Returns the type of a <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c>'s "Outer"/"Inner" member, accepting a
    /// field (EF Core's actual shape) or a property (defensively).
    /// </summary>
    private static bool TryGetOuterOrInnerMemberType(Type transparentIdentifierType, string memberName, out Type? memberType)
    {
        memberType = transparentIdentifierType
            .GetMember(memberName, MemberTypes.Field | MemberTypes.Property, BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault() switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => null
        };
        return memberType is not null;
    }
}
