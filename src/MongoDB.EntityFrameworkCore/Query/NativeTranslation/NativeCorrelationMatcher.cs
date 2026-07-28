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

using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes a correlated <c>Where</c>-over-<see cref="Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression"/>
/// shape — EF Core's standard FK correlation predicate, comparing an outer key against a dependent-side FK
/// property — and resolves it to the single matching collection navigation off the outer entity. Extracted
/// (EF-347 slice 5, Task 1) from <see cref="NativeProjectionBinder"/>'s projected-<c>Count</c> recognition so
/// it can be shared with a reference-<c>SelectMany</c> binder.
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
