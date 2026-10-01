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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes EF Core's FK correlation predicate (a <c>Where</c> over an
/// <see cref="Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression"/> comparing an outer key to a
/// dependent FK) and resolves it to the single matching collection navigation off the outer entity.
/// </summary>
internal static class NativeCorrelationMatcher
{
    /// <summary>
    /// Matches <paramref name="whereBody"/> as a correlation of <paramref name="outerParameter"/>'s key with a
    /// dependent FK and resolves the single collection navigation (embedded iff <paramref name="requireEmbedded"/>)
    /// whose target and single-property FK match. Declines on no match, an ambiguous match, or any extra conjunct.
    /// </summary>
    /// <remarks>
    /// The outer operand must be a direct property of <paramref name="outerParameter"/> that resolves, by
    /// <see cref="IProperty"/> identity, to the navigation's principal key (either operand order). Binding the
    /// navigation emits <c>$lookup {localField: principalKey, foreignField: fk}</c>, so a correlation on any other
    /// outer member (<c>o.CustomerId == c.Code</c>) would silently read the key-correlated rows. A null guard is
    /// admitted only on that same outer key, as EF Core's nav-expansion emits it; a guard on any other member is a
    /// user filter the bound navigation would drop.
    /// </remarks>
    internal static bool TryMatchCorrelatedCollection(
        Expression whereBody,
        IEntityType outerEntityType,
        ParameterExpression outerParameter,
        IEntityType targetEntityType,
        bool requireEmbedded,
        out INavigation navigation)
    {
        navigation = null!;

        if (!TryGetCorrelationEqualitySides(whereBody, out var side1, out var side2, out var nullGuarded))
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

        var candidates = outerEntityType.GetNavigations()
            .Where(n => n.IsCollection
                        && n.IsEmbedded() == requireEmbedded
                        && n.TargetEntityType == targetEntityType
                        && n.ForeignKey.Properties.Count == 1
                        && n.ForeignKey.Properties[0].Name == dependentPropertyName)
            .ToList();

        if (candidates.Count != 1)
            return false;

        var principalKeyProperty = candidates[0].ForeignKey.PrincipalKey.Properties[0];
        var outerSide = ReferenceEquals(dependentSide, side1) ? side2 : side1;
        if (!IsOuterProperty(outerSide, outerParameter, outerEntityType, principalKeyProperty)
            || (nullGuarded != null && !IsOuterProperty(nullGuarded, outerParameter, outerEntityType, principalKeyProperty)))
        {
            return false;
        }

        navigation = candidates[0];
        return true;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> is a direct property access on <paramref name="outerParameter"/>
    /// (member or <c>EF.Property</c>, <c>Convert</c>-wrapped or not) resolving to exactly
    /// <paramref name="property"/>. Nested members (<c>c.Address.Code</c>) never match.
    /// </summary>
    private static bool IsOuterProperty(
        Expression expression, ParameterExpression outerParameter, IEntityType outerEntityType, IProperty property)
        => expression.RemoveConvert().TryGetMemberOrEFProperty(out var receiver, out var name)
           && ReferenceEquals(receiver.RemoveConvert(), outerParameter)
           && ReferenceEquals(outerEntityType.FindProperty(name), property);

    /// <summary>
    /// Extracts the compared sides of a bare equality, or of an equality guarded by exactly one null check
    /// (<c>k != null &amp;&amp; equality</c>, either order; EF Core emits this for a nullable outer key), returning
    /// the guarded operand in <paramref name="nullGuarded"/> for the caller to check it is the compared outer key.
    /// Any other conjunct declines, so a user-filtered count (<c>c.Orders.Where(pred).Count()</c>) isn't mistaken
    /// for a plain FK correlation.
    /// </summary>
    private static bool TryGetCorrelationEqualitySides(
        Expression body, out Expression left, out Expression right, out Expression? nullGuarded)
    {
        var stripped = body.RemoveConvert();
        nullGuarded = null;

        if (TryExtractEqualitySides(stripped, out left!, out right!))
            return true;

        if (stripped is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            if (TryGetNullGuardedOperand(andAlso.Left, out nullGuarded)
                && TryExtractEqualitySides(andAlso.Right, out left!, out right!))
                return true;
            if (TryGetNullGuardedOperand(andAlso.Right, out nullGuarded)
                && TryExtractEqualitySides(andAlso.Left, out left!, out right!))
                return true;
        }

        left = right = null!;
        nullGuarded = null;
        return false;
    }

    /// <summary>
    /// Extracts the two sides of an equality in the spellings EF Core's nav-expansion produces: <c>==</c>,
    /// static <c>object.Equals(x, y)</c>, and instance <c>x.Equals(y)</c>. Also used by <c>NativeSelectManyBinder</c>.
    /// </summary>
    internal static bool TryExtractEqualitySides(Expression node, out Expression left, out Expression right)
    {
        switch (node.RemoveConvert())
        {
            case BinaryExpression { NodeType: ExpressionType.Equal } eq:
                left = eq.Left;
                right = eq.Right;
                return true;

            case MethodCallExpression
            {
                Method: { Name: nameof(Equals), IsStatic: true, DeclaringType: var declaringType },
                Arguments: [var arg0, var arg1]
            } when declaringType == typeof(object):
                left = arg0;
                right = arg1;
                return true;

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
    /// Matches the correlated subquery EF Core's nav-expansion leaves of a reference-collection navigation,
    /// <c>Queryable.Where(EntityQueryRootExpression&lt;Target&gt;, predicate)</c> with a single-parameter predicate.
    /// Structural only: the predicate isn't checked to be an FK correlation.
    /// </summary>
    internal static bool TryMatchRootWhere(
        Expression expression,
        [NotNullWhen(true)] out EntityQueryRootExpression? root,
        [NotNullWhen(true)] out LambdaExpression? predicate)
    {
        if (expression is MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            && whereDeclaring == typeof(Queryable)
            && predicateArg.UnwrapLambdaFromQuote() is { Parameters.Count: 1 } predicateLambda)
        {
            root = rootExpression;
            predicate = predicateLambda;
            return true;
        }

        root = null;
        predicate = null;
        return false;
    }

    /// <summary>
    /// Matches <see cref="TryMatchRootWhere"/>'s <c>Queryable.Where(root, correlationPredicate)</c> as the FK
    /// correlation of <paramref name="outerParameter"/> (the query's root entity) with a non-embedded collection
    /// navigation, and resolves that navigation. This is the shape nav-expansion wraps around a reference-collection
    /// <c>Count</c>/<c>LongCount</c> (see <see cref="MongoExpressionTranslator.TryMatchCountExpression"/>), list
    /// (<c>c.Orders.ToList()</c>) or reducer (<c>c.Orders.FirstOrDefault()</c>). Shared by
    /// <see cref="NativeProjectionBinder"/> and <see cref="NativeReferenceCollectionCountPredicateBinder"/>.
    /// </summary>
    internal static bool TryMatchCorrelatedRootWhere(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression whereArg,
        [NotNullWhen(true)] out INavigation? navigation)
    {
        navigation = null;

        return TryMatchRootWhere(whereArg, out var rootExpression, out var predicate)
               && TryMatchCorrelatedCollection(
                   predicate.Body, mongoQ.CollectionExpression.EntityType, outerParameter, rootExpression.EntityType,
                   requireEmbedded: false, out navigation!);
    }

    /// <summary>
    /// Stages <paramref name="lookup"/> (a reference-collection <c>$lookup</c> a projection leaf needs) into
    /// <paramref name="pendingLookups"/>, or reuses an existing lookup at the same alias, staged or already pending on
    /// the query, if it is the same kind; <paramref name="stagedLookup"/> is whichever backs the leaf. A lookup that
    /// isn't a plain native collection lookup (TPH-derived target: discriminator <c>$match</c> staged,
    /// <see cref="LookupPipelineKind.FallbackOnly"/>) declines, as does a collision with a different kind (e.g. a
    /// filtered Include, or a <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup, which unwinds to one
    /// document): reading it would silently read the wrong shape.
    /// </summary>
    internal static bool TryStageNativeCollectionLookup(
        MongoQueryExpression mongoQ,
        LookupExpression lookup,
        List<LookupExpression> pendingLookups,
        [NotNullWhen(true)] out LookupExpression? stagedLookup)
    {
        stagedLookup = null;

        if (!lookup.IsNativeCollectionLookup)
            return false;

        var collidingLookup = FindStagedOrPendingLookup(mongoQ, pendingLookups, lookup.As);
        if (collidingLookup is null)
        {
            pendingLookups.Add(lookup);
            stagedLookup = lookup;
        }
        else if (collidingLookup.PipelineKind != lookup.PipelineKind)
        {
            return false;
        }
        else
        {
            stagedLookup = collidingLookup;
        }

        return true;
    }

    /// <summary>
    /// The lookup at <paramref name="alias"/> already staged in <paramref name="pendingLookups"/> or pending on
    /// <paramref name="mongoQ"/> (staged first), or <see langword="null"/>.
    /// </summary>
    internal static LookupExpression? FindStagedOrPendingLookup(
        MongoQueryExpression mongoQ, List<LookupExpression> pendingLookups, string alias)
        => pendingLookups.FirstOrDefault(l => l.As == alias)
           ?? mongoQ.GetPendingLookups().FirstOrDefault(l => l.As == alias);

    /// <summary>
    /// Builds (or reuses) the <c>$lookup</c> a reference-collection <c>Count</c> needs and stages it into
    /// <paramref name="pendingLookups"/> via <see cref="TryStageNativeCollectionLookup"/>; colliding with a
    /// <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup declines rather than emit a <c>$size</c> over the
    /// wrong shape.
    /// </summary>
    /// <remarks>
    /// The backing lookup must be stamped <see cref="LookupExpression.IsBareCountSizeSource"/> so a later paged
    /// Include for the same navigation is rerouted instead of merged into it (see that property). This method
    /// doesn't stamp it — a later caller gate may still decline, and recognizers must not mutate then decline —
    /// so it returns <paramref name="lookupToStamp"/> for the caller to stamp at its commit point.
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

        if (!TryStageNativeCollectionLookup(
                mongoQ, new LookupExpression(navigation) { InjectAfterRoot = true }, pendingLookups, out var stagedLookup))
        {
            return false;
        }

        lookupToStamp = stagedLookup;
        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), resultType);
        return true;
    }

    private static bool TryGetNullGuardedOperand(Expression node, [NotNullWhen(true)] out Expression? guarded)
    {
        guarded = null;
        if (node.RemoveConvert() is BinaryExpression { NodeType: ExpressionType.NotEqual } bin)
        {
            guarded = IsNullConstant(bin.Right) ? bin.Left : IsNullConstant(bin.Left) ? bin.Right : null;
        }

        return guarded != null;
    }

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
