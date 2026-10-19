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
    /// Extracts the compared sides of a bare equality, or of an equality guarded by exactly one null check
    /// (<c>k != null &amp;&amp; equality</c>, either order; EF Core emits this for a nullable outer key). Any
    /// other conjunct declines, so a user-filtered count (<c>c.Orders.Where(pred).Count()</c>) isn't mistaken
    /// for a plain FK correlation.
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
    /// Matches the <c>Queryable.Where(root, correlationPredicate)</c> EF Core's nav-expansion wraps around a
    /// reference-collection <c>Count</c>/<c>LongCount</c> (see
    /// <see cref="MongoExpressionTranslator.TryMatchCountExpression"/>) and resolves its navigation. Shared by
    /// <see cref="NativeProjectionBinder"/> and <see cref="NativeReferenceCollectionCountPredicateBinder"/>.
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
    /// Builds (or reuses) the <c>$lookup</c> a reference-collection <c>Count</c> needs and stages it into
    /// <paramref name="pendingLookups"/>. An existing lookup at the same alias is reused only if it is the same
    /// kind; a <see cref="LookupPipelineKind.CorrelatedReducer"/> lookup unwinds to one document, so colliding
    /// with one declines rather than emit a <c>$size</c> over the wrong shape.
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
