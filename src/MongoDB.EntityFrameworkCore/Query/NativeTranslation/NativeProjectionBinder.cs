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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Attempts to populate the native <c>$project</c> slot (<see cref="MongoSelectDefinition"/>
/// Projection) from a terminal member-access anonymous/DTO selector. Extracted from the QMTEV (EF-332).
/// Returns <see langword="true"/> (and fills <c>Select.Projection</c>) only when every leaf is a plain
/// member access the translator resolves to a document field, or a projected collection-navigation
/// <c>Count</c>/<c>LongCount</c> (EF-339 Task 4); otherwise leaves the slot empty.
/// </summary>
internal static class NativeProjectionBinder
{
    internal static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var projections = new List<MongoProjection>();
        // Lookups discovered by count-leaves are staged here rather than applied to mongoQ immediately,
        // so a later leaf failing native recognition (whole projection falls back) never leaves a
        // half-registered lookup behind on mongoQ.
        var pendingLookups = new List<LookupExpression>();
        // EF's MongoQueryExpression.AddToProjection disambiguates aliases case-insensitively (appending a
        // counter on collision). If two members here differ only by case, the DOM shaper would read the
        // disambiguated alias while the native $project emits the un-disambiguated one, silently dropping
        // a value. Bail to driver-LINQ rather than risk that.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (selector.Body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    var alias = newExpression.Members[i].Name;
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], newExpression.Arguments[i], pendingLookups, out var leaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                }
                break;

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;

                    var alias = binding.Member.Name;
                    if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], assignment.Expression, pendingLookups, out var leaf))
                        return false;
                    if (!seenAliases.Add(alias))
                        return false;
                    projections.Add(new MongoProjection(alias, leaf));
                }
                break;

            default:
                return false;
        }

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);
        foreach (var projection in projections)
            mongoQ.Select.AddProjection(projection);
        return true;
    }

    /// <summary>
    /// Translates a single projection leaf: either a plain top-level member access, or a projected
    /// collection-navigation <c>Count</c>/<c>LongCount</c> (see <see cref="TryTranslateProjectedCollectionCount"/>,
    /// which EF Core's nav-expansion lowers to a <see cref="MethodCallExpression"/>, NOT a member access).
    /// Anything else is not natively representable.
    /// </summary>
    private static bool TryTranslateLeaf(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        ParameterExpression outerParameter,
        Expression leafExpression,
        List<LookupExpression> pendingLookups,
        out MongoExpression result)
    {
        if (leafExpression is MemberExpression && translator.TryTranslateField(leafExpression, out var field))
        {
            result = field;
            return true;
        }

        if (TryTranslateProjectedCollectionCount(mongoQ, outerParameter, leafExpression, pendingLookups, out var sizeExpression))
        {
            result = sizeExpression!;
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Recognizes a projected collection-navigation <c>Count</c>/<c>LongCount</c> leaf
    /// (<c>select new { ..., OrderCount = c.Orders.Count }</c>) inside a terminal projection.
    /// </summary>
    /// <remarks>
    /// EF Core's nav-expansion rewrites <c>c.Orders.Count</c> directly to
    /// <c>Queryable.Count(Queryable.Where(DbSet&lt;Target&gt;(), predicate))</c> — mirroring the shape
    /// <see cref="Visitors.MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigationCount"/>
    /// recognizes on the driver-LINQ path (a bare <c>Count()</c>/<c>LongCount()</c> call with NO
    /// user-supplied predicate argument), but resolved here directly against
    /// <paramref name="outerParameter"/> (the selector's own lambda parameter) rather than a materialized
    /// outer shaper, because this binder runs on the raw selector before shaper substitution.
    /// <para>
    /// The <c>Where</c> predicate itself is EF's standard null-guarded correlation shape —
    /// <c>(outerKey != null) AndAlso Equals(Convert(outerKey, object), Convert(dependentKey, object))</c>
    /// (or, when the key type can't be null, the bare <c>Equals</c>/<c>==</c> form) — comparing the
    /// dependent-side FK property against <paramref name="outerParameter"/>'s key. This is recognized
    /// structurally via <see cref="TryExtractEqualitySides"/> plus an exactly-two-conjunct guard: any
    /// ADDITIONAL predicate conjunct (e.g. <c>c.Orders.Where(o =&gt; o.Amount &gt; 5).Count()</c>) nests the
    /// null-guard/equality pair one level deeper as the left operand of an outer <c>AndAlso</c>, so the
    /// direct-conjunct match below fails and the whole projection bails to driver-LINQ — never emitting a
    /// wrong-shape native count.
    /// </para>
    /// </remarks>
    private static bool TryTranslateProjectedCollectionCount(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression leafExpression,
        List<LookupExpression> pendingLookups,
        out MongoExpression? result)
    {
        result = null;

        if (leafExpression is not MethodCallExpression
            {
                Method: { DeclaringType: var countDeclaring } countMethod,
                Arguments: [var whereArg]
            }
            || countDeclaring != typeof(Queryable)
            || countMethod.Name is not (nameof(Queryable.Count) or nameof(Queryable.LongCount)))
        {
            return false;
        }

        if (whereArg is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, var predicateArg]
            }
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var dependentParameter = predicate.Parameters[0];

        if (!TryGetCorrelationEqualitySides(predicate.Body, out var side1, out var side2))
            return false;

        var side1Root = GetRootParameter(side1);
        var side2Root = GetRootParameter(side2);

        Expression dependentSide;
        if (ReferenceEquals(side1Root, dependentParameter) && ReferenceEquals(side2Root, outerParameter))
            dependentSide = side1;
        else if (ReferenceEquals(side2Root, dependentParameter) && ReferenceEquals(side1Root, outerParameter))
            dependentSide = side2;
        else
            return false;

        var dependentPropertyName = dependentSide.TryGetSimplePropertyName();
        if (dependentPropertyName == null)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var targetEntityType = rootExpression.EntityType;

        // Resolve the single collection navigation off the outer entity whose target and single-property FK
        // match. If more than one navigation matches (ambiguous) or none does, decline rather than guess.
        var candidates = outerEntityType.GetNavigations()
            .Where(n => n.IsCollection
                        && !n.IsEmbedded()
                        && n.TargetEntityType == targetEntityType
                        && n.ForeignKey.Properties.Count == 1
                        && n.ForeignKey.Properties[0].Name == dependentPropertyName)
            .ToList();

        if (candidates.Count != 1)
            return false;

        var navigation = candidates[0];

        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        if (!lookup.IsNativeCollectionLookup)
            return false;

        pendingLookups.Add(lookup);

        result = new MongoSizeExpression(LookupExpression.GetLookupAlias(navigation), leafExpression.Type);
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

    private static bool TryExtractEqualitySides(Expression node, out Expression left, out Expression right)
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

    private static bool IsNullGuard(Expression node)
        => node.RemoveConvert() is BinaryExpression { NodeType: ExpressionType.NotEqual } bin
           && (IsNullConstant(bin.Left) || IsNullConstant(bin.Right));

    private static bool IsNullConstant(Expression node)
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
