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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes a correlated <c>Where</c>-over-<see cref="Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression"/>
/// shape — EF Core's standard FK correlation predicate, comparing an outer key against a dependent-side FK
/// property — and resolves it to the single matching collection navigation off the outer entity. Shared by
/// <see cref="NativeProjectionBinder"/>'s projected-<c>Count</c> recognition and a reference-<c>SelectMany</c> binder.
/// </summary>
internal static class NativeCorrelationMatcher
{
    /// <summary>
    /// Recognizes the correlation predicate <paramref name="whereBody"/> (null-guard/equality — see
    /// <see cref="TryGetCorrelationEqualitySides"/>) as comparing <paramref name="outerParameter"/>'s key
    /// against a dependent-side FK property, then resolves the single collection navigation off
    /// <paramref name="outerEntityType"/> whose target and single-property FK match — filtered by
    /// <c>IsEmbedded() == requireEmbedded</c> so a caller can select either a reference (<c>false</c>) or an
    /// embedded/owned (<c>true</c>) collection navigation. Returns <see langword="false"/> on no match, an
    /// ambiguous (more than one candidate) match, or an unrecognized predicate shape (including an extra
    /// predicate conjunct beyond the null-guard/equality pair).
    /// </summary>
    internal static bool TryMatchCorrelatedCollection(
        Expression whereBody,
        IEntityType outerEntityType,
        ParameterExpression outerParameter,
        IEntityType targetEntityType,
        bool requireEmbedded,
        out INavigation navigation)
    {
        navigation = null!;

        if (!TryGetCorrelationEqualitySides(whereBody, out var side1, out var side2))
            return false;

        var side1Root = GetRootParameter(side1);
        var side2Root = GetRootParameter(side2);

        Expression dependentSide;
        if (ReferenceEquals(side2Root, outerParameter) && side1Root != null && !ReferenceEquals(side1Root, outerParameter))
            dependentSide = side1;
        else if (ReferenceEquals(side1Root, outerParameter) && side2Root != null && !ReferenceEquals(side2Root, outerParameter))
            dependentSide = side2;
        else
            return false;

        var dependentPropertyName = dependentSide.TryGetSimplePropertyName();
        if (dependentPropertyName == null)
            return false;

        // Resolve the single collection navigation off the outer entity whose target and single-property FK
        // match. If more than one navigation matches (ambiguous) or none does, decline rather than guess.
        var candidates = outerEntityType.GetNavigations()
            .Where(n => n.IsCollection
                        && n.IsEmbedded() == requireEmbedded
                        && n.TargetEntityType == targetEntityType
                        && n.ForeignKey.Properties.Count == 1
                        && n.ForeignKey.Properties[0].Name == dependentPropertyName)
            .ToList();

        if (candidates.Count != 1)
            return false;

        navigation = candidates[0];
        return true;
    }

    /// <summary>
    /// Extracts the two compared sides from a correlation predicate body that is EITHER a bare equality
    /// (<c>Equal</c> <see cref="BinaryExpression"/>, or an <c>object.Equals(x, y)</c>/<c>x.Equals(y)</c>
    /// call — the forms EF Core's nav-expansion emits for key comparisons) OR that same equality guarded by
    /// exactly one null-check conjunct (<c>(k != null) AndAlso equality</c>, in either operand order — the
    /// form EF Core emits when the outer key's CLR type is nullable). Any other shape — most importantly an
    /// <c>AndAlso</c> with additional conjuncts beyond the single null-guard — returns
    /// <see langword="false"/>, which correctly routes an actual filtered count
    /// (<c>c.Orders.Where(pred).Count()</c> with a real user predicate) to fallback rather than
    /// misidentifying it as a plain FK correlation.
    /// </summary>
    private static bool TryGetCorrelationEqualitySides(Expression body, out Expression left, out Expression right)
    {
        var stripped = body.RemoveConvert();

        if (TryExtractEqualitySides(stripped, out left!, out right!))
            return true;

        if (stripped is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            if (IsNullGuard(andAlso.Left) && TryExtractEqualitySides(andAlso.Right, out left!, out right!))
                return true;
            if (IsNullGuard(andAlso.Right) && TryExtractEqualitySides(andAlso.Left, out left!, out right!))
                return true;
        }

        left = right = null!;
        return false;
    }

    /// <summary>
    /// Extracts the two compared sides of an equality conjunct, in all three spellings EF Core's nav-expansion
    /// can produce: <c>==</c>, the static <c>object.Equals(x, y)</c> it uses for a null-safe key comparison, and
    /// the instance <c>x.Equals(y)</c>.
    /// </summary>
    /// <remarks>
    /// <b>internal, not private</b>: <c>NativeSelectManyBinder</c> needs the identical structural match and used
    /// to hold its own byte-identical copy, on the stated grounds that "the shared matcher's own contract is not
    /// widened for this caller" — which is true of <see cref="TryMatchCorrelatedCollection"/> but says nothing
    /// about a pure structural helper, so the copy bought a drift risk for nothing.
    /// </remarks>
    internal static bool TryExtractEqualitySides(Expression node, out Expression left, out Expression right)
    {
        switch (node.RemoveConvert())
        {
            case BinaryExpression { NodeType: ExpressionType.Equal } eq:
                left = eq.Left;
                right = eq.Right;
                return true;

            // object.Equals(x, y) — the static overload EF Core's nav-expansion uses for a null-safe
            // key comparison inside the correlation predicate.
            case MethodCallExpression
            {
                Method: { Name: nameof(Equals), IsStatic: true, DeclaringType: var declaringType },
                Arguments: [var arg0, var arg1]
            } when declaringType == typeof(object):
                left = arg0;
                right = arg1;
                return true;

            // x.Equals(y) — the instance overload, for completeness.
            case MethodCallExpression
            {
                Method.Name: nameof(Equals),
                Object: { } instance,
                Arguments: [var arg]
            }:
                left = instance;
                right = arg;
                return true;

            default:
                left = right = null!;
                return false;
        }
    }

    /// <summary>
    /// Recognizes the <c>Queryable.Where(root, correlationPredicate)</c> shape EF Core's nav-expansion always
    /// wraps a reference-collection-navigation <c>Count</c>/<c>LongCount</c> in (see
    /// <see cref="MongoExpressionTranslator.TryMatchCountExpression"/>'s own remarks — the <c>Where</c> here is
    /// EF's own FK-correlation plumbing, present for both a bare <c>c.Orders.Count</c> and a user-filtered
    /// <c>c.Orders.Where(pred).Count()</c> alike), then resolves the single matching collection navigation via
    /// <see cref="TryMatchCorrelatedCollection"/>. Shared by <see cref="NativeProjectionBinder"/>'s
    /// projected-<c>Count</c> leaf and <see cref="NativeReferenceCollectionCountPredicateBinder"/>'s predicate
    /// comparison — both need the identical "which navigation does this whereArg correlate to" answer.
    /// </summary>
    internal static bool TryMatchReferenceCollectionCountNavigation(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression whereArg,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;

        if (whereArg is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        return TryMatchCorrelatedCollection(
            predicate.Body, outerEntityType, outerParameter, targetEntityType, requireEmbedded: false, out navigation!);
    }

    /// <summary>
    /// Builds (or reuses, via the same cross-leaf alias-collision dedupe rule
    /// <see cref="NativeProjectionBinder.TryTranslateProjectedCollectionCount"/> has always applied) the
    /// <c>$lookup</c> a reference-collection <c>Count</c> needs, and stages it into <paramref name="pendingLookups"/>
    /// for the caller to commit. Two leaves in one query can both want the same <c>_lookup_&lt;Nav&gt;</c> alias;
    /// that is only safe when they are INTERCHANGEABLE — a same-kind lookup (another count over the same nav, or
    /// an already-pending collection-Include lookup for it) is reused, but colliding with a
    /// <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup (which unwinds to a single document, not an
    /// array) is not, and declines instead of risking a <c>$size</c> over the wrong shape.
    /// </summary>
    /// <remarks>
    /// EF-322 final review (round 3, NEW Critical): whichever bare <see cref="LookupExpression"/> ends up
    /// backing this alias — freshly registered here, or an already-pending one this call reuses — must
    /// eventually be stamped <see cref="LookupExpression.IsBareCountSizeSource"/>. That flag is what lets
    /// <c>MongoProjectionBindingExpressionVisitor</c>'s Include-registration collision check (which already
    /// reroutes an incoming lookup away from an incompatible, $unwind-ed join lookup at the same alias — see
    /// <see cref="LookupExpression.RenamedToAvoidJoinCollision"/>) ALSO reroute a LATER, paged Include for this
    /// same navigation away from this bare entry, instead of letting <see cref="MongoQueryExpression.AddLookup"/>
    /// silently merge the Include's pipeline into it. See <see cref="LookupExpression.IsBareCountSizeSource"/>'s
    /// own remarks for the full mechanism and why the fix lives at that OTHER call site rather than here or in
    /// <c>AddLookup</c> itself.
    /// </remarks>
    /// <remarks>
    /// EF-322 final cleanup (round 4, Minor): this method does NOT stamp <see cref="LookupExpression.IsBareCountSizeSource"/>
    /// itself — per Query/AGENTS.md's "a recognizer must not mutate then decline" invariant, doing so here would
    /// mutate an already-registered <paramref name="pendingLookups"/>/<c>mongoQ</c> entry (or, for the fresh case,
    /// set a field on an object about to be staged) BEFORE the caller's own remaining gates (e.g. the predicate
    /// binder's <c>TryTranslateValue</c> on the comparison's other operand) have run — any of which can still
    /// decline the whole leaf after this method returns <see langword="true"/>. Instead, the lookup that needs the
    /// stamp is handed back via <paramref name="lookupToStamp"/>; the CALLER applies
    /// <see cref="LookupExpression.IsBareCountSizeSource"/> = <see langword="true"/> only at its own commit point,
    /// once every other gate has passed.
    /// </remarks>
    internal static bool TryBuildReferenceCollectionCountLookup(
        MongoQueryExpression mongoQ,
        INavigation navigation,
        List<LookupExpression> pendingLookups,
        Type resultType,
        out LookupExpression? lookupToStamp,
        [NotNullWhen(true)] out MongoSizeExpression? result)
    {
        result = null;
        lookupToStamp = null;

        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        if (!lookup.IsNativeCollectionLookup)
            return false;

        var collidingLookup = pendingLookups.FirstOrDefault(l => l.As == lookup.As)
            ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
            lookupToStamp = lookup;
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }
        else
        {
            lookupToStamp = collidingLookup;
        }

        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), resultType);
        return true;
    }

    private static bool IsNullGuard(Expression node)
        => node.RemoveConvert() is BinaryExpression { NodeType: ExpressionType.NotEqual } bin
           && (IsNullConstant(bin.Left) || IsNullConstant(bin.Right));

    internal static bool IsNullConstant(Expression node)
        => node.RemoveConvert() is ConstantExpression { Value: null };

    private static ParameterExpression? GetRootParameter(Expression expression)
    {
        var stripped = expression.RemoveConvert();
        return stripped switch
        {
            MemberExpression { Expression: { } inner } => inner.RemoveConvert() as ParameterExpression,
            MethodCallExpression call when call.Method.IsEFPropertyMethod() && call.Arguments.Count == 2
                => call.Arguments[0].RemoveConvert() as ParameterExpression,
            ParameterExpression parameter => parameter,
            _ => null
        };
    }
}
