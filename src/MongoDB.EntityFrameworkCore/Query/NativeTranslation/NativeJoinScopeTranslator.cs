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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Resolves member access over a native join's flat <c>TransparentIdentifier(Outer, Inner)</c> shape
/// (<c>x.Outer.Foo</c> / <c>x.Inner.Foo</c>, one hop each — never nested, unlike SelectMany's chained scopes)
/// against a <see cref="MongoJoinScope"/>, reusing the existing two-scope
/// <see cref="MongoExpressionTranslator"/> constructor built for <c>NativeSelectManyBinder</c>'s correlated
/// inner-filter case.
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
    /// Whether <paramref name="body"/> references the join scope's Inner side anywhere (a bare
    /// <c>rootParam.Inner</c> leaf, or any deeper member access rooted on it). <c>NativeSlotPopulator</c>'s
    /// <c>Where</c> arm uses this to stay Outer-side-only for now: <c>PipelineOps</c> (<c>$match</c>) are
    /// always lowered BEFORE the <c>$lookup</c> stage that materializes the join's Inner side (both for a
    /// reference-Include's flat <c>$lookup</c> and for any future genuine-join lookup — see
    /// <c>MongoSelectLowerer</c>/<c>Query/AGENTS.md</c>), so a <c>Where</c> predicate reaching Inner would
    /// filter on a field that doesn't exist yet at that point in the pipeline — "succeeding" with a native
    /// <c>$match</c> that can never match anything, not a graceful decline. Resolving Inner access safely
    /// (via the <c>$lookup</c>'s own correlated sub-pipeline, the way <c>NativeSelectManyBinder</c>'s
    /// inner-filter case does) is deferred to the Select-side binder (Task 5); <see cref="TryTranslatePredicate"/>
    /// itself stays general-purpose (mixed Outer/Inner predicates translate fine structurally — see
    /// <c>NativeJoinScopeTranslatorTests.Translates_mixed_scope_equality_predicate</c>) since a future Select-
    /// or lookup-sub-pipeline caller may legitimately want Inner there; only the Where call site restricts.
    /// </summary>
    public static bool ReferencesInnerScope(ParameterExpression rootParam, Expression body)
    {
        var detector = new InnerAccessDetector(rootParam);
        detector.Visit(body);
        return detector.Found;
    }

    private static bool TryTranslateCore(
        MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // rootParam.Type must be a real EF-generated TransparentIdentifier<TOuter,TInner> whose "Outer" and
        // "Inner" members are EXACTLY the recorded scope's types — not a further-nested TransparentIdentifier.
        // A chained/nested join (e.g. two Joins onto the same select) recycles the SAME JoinScope for every
        // subsequent join, since eligibility ("outerQueryExpression.Select.JoinScope == null") only ever lets
        // the first join record one — so at the point of a chained SECOND join, rootParam.Type is
        // TransparentIdentifier(TransparentIdentifier(Outer1, Inner1), Inner2), whose OWN top-level "Inner"
        // can coincidentally share a CLR type with the recorded (first join's) InnerEntityType (e.g. two joins
        // onto the same target entity type) — rewriting that top-level Inner using the FIRST join's recorded
        // scope silently associates the wrong member with the synthetic parameter and either throws (a member
        // declared on the wrong nesting level's compiler-generated type) or resolves a bogus field. Requiring
        // an EXACT, non-nested type match on both members catches THIS specific shape (a chained join whose
        // outer side is STILL the flat TransparentIdentifier from the first join), but is NOT a complete
        // disambiguation — see the residual gap noted just below — without relying on depth-counting or a
        // chained-join flag. See SameTargetTypeJoinTests.Filter_after_a_flattened_multi_join_chain_is_applied_not_dropped.
        //
        // RESIDUAL GAP (read before extending this to the Select-side binder / Task 5): a chained join whose
        // FIRST join's result gets flattened back down to a plain entity before the second join runs (e.g.
        // `.Join(a, b, ...).Select(x => x.Outer).Join(c, d, ...)`) produces a SECOND join whose OWN, perfectly
        // flat `TransparentIdentifier<TOuter2,TInner2>` can have Outer/Inner CLR types that exactly equal the
        // FIRST join's recorded scope (e.g. re-joining onto the same two entity types in the same positions).
        // This guard cannot tell those two flat shapes apart — it would pass, but `scope.InnerPrefix` still
        // names the FIRST join's `$lookup` alias, not the second's. Today this is harmless ONLY because
        // `ReferencesInnerScope` blocks ALL Inner access at the Where call site (this file's only current
        // caller), so the wrong alias is never actually rendered. The moment a caller invokes
        // `TryTranslateValue`/`TryTranslateCore` for the Inner side WITHOUT that same Outer-only restriction
        // (e.g. Task 5's Select binder), this gap can resolve the wrong join's Inner side against the wrong
        // `InnerPrefix` and silently produce wrong data. Task 5 must either add a real per-join identity check
        // (not just a type-shape check) or keep re-deriving/re-validating the scope from the ACTUAL join being
        // bound, not from whatever `MongoSelectDefinition.JoinScope` happens to hold.
        //
        // Field-or-property-agnostic on purpose: a real EF-generated TransparentIdentifier<TOuter,TInner>
        // exposes Outer/Inner as public FIELDS (verified against EF8/EF10), not properties — GetProperty alone
        // returns null unconditionally for every real join parameter, which would make this guard (and the
        // whole Where-join-scope path) always decline. See ExpressionExtensionMethods.IsTransparentIdentifierType.
        if (!rootParam.Type.IsTransparentIdentifierType()
            || !TryGetOuterOrInnerMemberType(rootParam.Type, "Outer", out var outerMemberType)
            || outerMemberType != scope.OuterEntityType.ClrType
            || !TryGetOuterOrInnerMemberType(rootParam.Type, "Inner", out var innerMemberType)
            || innerMemberType != scope.InnerEntityType.ClrType)
        {
            return false;
        }

        var outerParam = Expression.Parameter(scope.OuterEntityType.ClrType, "outerScope");
        var innerParam = Expression.Parameter(scope.InnerEntityType.ClrType, "innerScope");
        var splitter = new ScopeSplittingVisitor(rootParam, outerParam, innerParam);
        var rewritten = splitter.Visit(body);

        // The body must actually be shaped like a join's TransparentIdentifier(Outer, Inner) — at least one
        // rootParam.Outer/rootParam.Inner access rewritten, and NO other access to rootParam left unrewritten.
        // Without this guard, a Where composed after an Include-generated join (whose predicate is an ordinary
        // root-entity-scoped body, e.g. x.SomeNav.Foo, with no Outer/Inner wrapper at all — Include's join
        // shares the exact same TranslateJoinCore/JoinScope recording as a genuine user Join, per the
        // indistinguishable-at-bind-time limitation) falls through to the "no rewrite happened" case: nothing
        // matches Outer/Inner, so ReferenceEquals(rootParam, outerParam) is false for every member the
        // two-scope MongoExpressionTranslator visits, and it silently resolves the untouched rootParam-rooted
        // members against the WRONG entity (scope.InnerEntityType, the two-scope translator's default), often
        // "succeeding" with a bogus dotted path that matches no document and silently returns zero rows
        // instead of falling back correctly. See NativeJoinTests / RequiredNavigationUnwindTests /
        // Ef369MultiJoinComposedTests regressions this guard fixes.
        if (!splitter.SawScopedAccess || splitter.SawUnscopedRootAccess)
            return false;

        var translator = new MongoExpressionTranslator(
            scope.InnerEntityType, outerParam, scope.OuterEntityType, scope.InnerPrefix);

        return valueMode
            ? translator.TryTranslateValue(rewritten, out result)
            : translator.TryTranslate(rewritten, out result);
    }

    /// <summary>
    /// Rewrites every <c>rootParam.Outer</c>-rooted subtree onto <paramref name="outerParam"/> and every
    /// <c>rootParam.Inner</c>-rooted subtree onto <paramref name="innerParam"/> — a flat, single-hop rewrite
    /// (a join's own result selector is always exactly this shape; see the design doc). A bare
    /// <c>rootParam.Outer</c>/<c>rootParam.Inner</c> leaf (no further member access, e.g. <c>x.Outer</c>
    /// alone) rewrites to the bare synthetic parameter itself.
    /// </summary>
    private sealed class ScopeSplittingVisitor(
        ParameterExpression rootParam, ParameterExpression outerParam, ParameterExpression innerParam)
        : ExpressionVisitor
    {
        /// <summary>Whether at least one <c>rootParam.Outer</c>/<c>rootParam.Inner</c> access was rewritten.</summary>
        public bool SawScopedAccess { get; private set; }

        /// <summary>
        /// Whether <c>rootParam</c> was referenced OUTSIDE an <c>.Outer</c>/<c>.Inner</c> member
        /// access (a bare use of the parameter, or a member access rooted on it with some other name) — the
        /// signal that this body isn't actually shaped like the join's TransparentIdentifier at all.
        /// </summary>
        public bool SawUnscopedRootAccess { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (ReferenceEquals(node.Expression, rootParam))
            {
                // Checked by declaring type (IsTransparentIdentifierOuterOrInnerAccess), not just member
                // name, so a joined entity that happens to declare its own real Outer/Inner member isn't
                // mistaken for join-chain plumbing — same rule MongoEFToLinqTranslatingExpressionVisitor.
                // LeftJoin.cs and ExpressionExtensionMethods.cs document callers "must stay in agreement" on.
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

    /// <summary>Backs <see cref="ReferencesInnerScope"/> — a plain existence check, no rewriting.</summary>
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
    /// Looks up <paramref name="memberName"/> ("Outer" or "Inner") on a real EF-generated
    /// <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> and returns its type, whether it's declared as a
    /// FIELD (the actual EF Core shape) or a PROPERTY (accepted too, defensively, in case a future EF Core
    /// version changes this — nothing here depends on which).
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
