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

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — an owned entity compared with a captured or constructed value of its type
/// (<c>p.Home == x</c>, <c>locations.Contains(x)</c>, <c>locations.Any(l =&gt; l == x)</c>).
/// </summary>
/// <remarks>
/// Parity with driver-LINQ, which rewrites owned equality to a per-property conjunction over every CLR-mapped, non-shadow
/// property of the owned type (<c>!=</c> is the disjunction): NOT a whole-sub-document match, so stored field order and
/// extra elements are irrelevant. Declines: owned == owned, an outer-scoped operand, a TPH owned hierarchy, a
/// comparand that is not a constant/inline construction/query parameter, and a parameter comparand when every compared
/// property is nullable (a runtime null would then become per-property null checks that match a present-but-empty
/// sub-document, where the bridge compares the reference to null).
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// The element parameter of an owned-collection element lambda (<c>Any</c>/<c>Contains</c> element scope). Distinct
    /// from <see cref="SelfParam"/> on purpose: <see cref="SelfParam"/> enables the root-entity arms (<c>$$ROOT</c>,
    /// primary-key equality), which are wrong inside <c>$elemMatch</c>.
    /// </summary>
    internal ParameterExpression? ElementParam { get; set; }

    private bool TryTranslateOwnedEntityEquality(
        Expression leftSide, Expression rightSide, bool isNotEqual, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (_innerPrefix is not null || _outerParam is not null || ProjectedAliasScope is not null)
            return false;

        var left = Unwrap(leftSide);
        var right = Unwrap(rightSide);

        // Which side is the owned operand? Exactly one may be.
        if (!TryResolveOwnedOperand(left, out var ownedType, out var prefix))
        {
            (left, right) = (right, left);
            if (!TryResolveOwnedOperand(left, out ownedType, out prefix))
                return false;
        }
        else if (TryResolveOwnedOperand(right, out _, out _))
        {
            return false; // owned == owned
        }

        // A null comparand is the owned-reference null check (TranslateComparisonCore); a different CLR type can't compare.
        if (right is ConstantExpression { Value: null } || right.Type != ownedType.ClrType)
            return false;

        if (ownedType.GetDerivedTypes().Any())
            return false; // a TPH owned hierarchy

        var properties = ownedType.GetProperties().Where(p => p.PropertyInfo != null || p.FieldInfo != null).ToList();
        if (properties.Count == 0)
            return false;

        if (NativeQueryParameter.TryGetQueryParameterName(right, out _) && properties.All(p => p.IsNullable))
            return false;

        var comparand = right;
        result = CombineKeyComparisons(
            properties,
            property =>
            {
                if (!TryExtractEntityMemberValue(comparand, property, out var value))
                    return null;

                var name = GetPropertyFieldPath(property);
                return new MongoBinaryExpression(
                    isNotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
                    new MongoFieldExpression(property, prefix.Length == 0 ? name : prefix + "." + name),
                    value);
            },
            combineWithAnd: !isNotEqual);

        return result is not null;

        bool TryResolveOwnedOperand(Expression node, [NotNullWhen(true)] out IEntityType? type, out string path)
        {
            type = null;
            path = "";

            if (ElementParam is not null && ReferenceEquals(node, ElementParam) && _entityType.IsOwned())
            {
                type = _entityType;
                return true;
            }

            if (TryResolveOwnedReferenceNavigationPath(node, out var navigationPath, out var targetType, out var isOuter)
                && !isOuter && targetType.IsOwned())
            {
                type = targetType;
                path = navigationPath;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// <c>ownedCollection.Contains(value)</c> as <c>{ path: { $elemMatch: &lt;per-property equality&gt; } }</c>
    /// (<c>$not</c> of it via the negator). The source arrives as <c>Enumerable.Contains</c>, <c>Queryable.Contains</c>
    /// over <c>AsQueryable(nav)</c>, or the instance <c>List.Contains</c>.
    /// </summary>
    private bool TryTranslateOwnedCollectionContains(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (call.Method.Name != nameof(System.Linq.Enumerable.Contains))
            return false;

        Expression? source;
        Expression? item;
        if (call.Method.IsStatic
            && call.Method.DeclaringType is var declaring
            && (declaring == typeof(System.Linq.Enumerable) || declaring == typeof(System.Linq.Queryable))
            && call.Arguments.Count == 2)
        {
            (source, item) = (call.Arguments[0], call.Arguments[1]);
        }
        else if (!TryMatchContainsMethod(call, out source, out item))
        {
            return false;
        }

        if (!TryResolveOwnedCollectionPath(Unwrap(UnwrapAsQueryable(source)), out var arrayPath, out var elementType, out var isOuter)
            || isOuter)
        {
            return false;
        }

        var element = Expression.Parameter(elementType.ClrType, "__e");
        var elementTranslator = new MongoExpressionTranslator(elementType) { ElementParam = element };
        if (!elementTranslator.TryTranslateOwnedEntityEquality(element, item, isNotEqual: false, out var predicate)
            || !MongoQueryLanguageRenderer.IsQueryDialectRenderable(predicate))
        {
            return false;
        }

        result = new MongoElemMatchExpression(arrayPath, predicate, negated: false);
        return true;
    }
}
