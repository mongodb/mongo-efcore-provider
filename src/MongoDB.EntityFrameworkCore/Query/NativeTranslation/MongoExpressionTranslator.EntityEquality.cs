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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — key-based equality of the root entity against a different, non-null
/// entity value (<c>c == local</c>, <c>c == new Customer{...}</c>, and <c>.Equals(...)</c>/<c>!=</c> forms).
/// </summary>
/// <remarks>
/// <c>c == null</c> and <c>c == c</c> decline here (<see cref="TranslateComparisonCore"/>'s $$ROOT check handles them).
/// One operand must be <see cref="SelfParam"/>; the other a constant, object initializer or query parameter of the
/// same CLR type. Reference-navigation operands and two-sided join comparisons (EF-216) are out of scope.
/// Rewritten, as relational does, to per-key comparisons: <c>Equal</c> is an AND, <c>NotEqual</c> its exact De Morgan
/// complement (safe because <c>$eq</c>/<c>$ne</c> partition the value space). A constant comparand's key is read at
/// translation time; a query-parameter comparand becomes a <see cref="MongoParameterExpression"/> with
/// <see cref="MongoParameterExpression.ExtractFromEntityValue"/>, extracted per execution. Shadow-property keys
/// decline: a captured POCO has no value to read.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateEntityEquality(BinaryExpression be, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateEntityEquality(be.Left, be.Right, be.NodeType == ExpressionType.NotEqual, out result);

    /// <summary>
    /// The <c>.Equals(...)</c> spelling, the only one available for composite-key types (C# synthesizes no
    /// <c>==</c>). <c>!od.Equals(local)</c> arrives as a <c>Not</c> and is handled by the ordinary <c>Not</c> dispatch.
    /// </summary>
    private bool TryTranslateEntityEqualityCall(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (call.Method.Name != nameof(Equals) || call.Object is null || call.Arguments.Count != 1)
            return false;

        return TryTranslateEntityEquality(call.Object, call.Arguments[0], isNotEqual: false, out result);
    }

    private bool TryTranslateEntityEquality(Expression leftSide, Expression rightSide, bool isNotEqual, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null)
            return false;

        var left = Unwrap(leftSide);
        var right = Unwrap(rightSide);

        var leftIsSelf = IsSelfParamTheEntity(left);
        var rightIsSelf = IsSelfParamTheEntity(right);

        if (leftIsSelf == rightIsSelf)
            return false;

        var otherSide = leftIsSelf ? right : left;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null)
            return false;

        var keyProperties = primaryKey.Properties;
        if (keyProperties.Any(p => p.IsShadowProperty()))
            return false;

        // A null comparand belongs to TranslateComparisonCore's $$ROOT-vs-null check.
        if (otherSide is ConstantExpression { Value: null })
            return false;

        if (otherSide.Type != _entityType.ClrType)
            return false;

        result = CombineKeyComparisons(
            keyProperties,
            keyProperty =>
            {
                if (!TryExtractEntityMemberValue(otherSide, keyProperty, out var valueExpr))
                    return null;

                return new MongoBinaryExpression(
                    isNotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
                    new MongoFieldExpression(keyProperty, GetPropertyFieldPath(keyProperty)),
                    valueExpr);
            },
            combineWithAnd: !isNotEqual);

        return result is not null;
    }

    private static MongoExpression? CombineKeyComparisons(
        IReadOnlyList<IProperty> keyProperties, Func<IProperty, MongoExpression?> buildComparison, bool combineWithAnd)
    {
        MongoExpression? combined = null;
        foreach (var keyProperty in keyProperties)
        {
            var comparison = buildComparison(keyProperty);
            if (comparison is null)
                return null;

            combined = combined is null
                ? comparison
                : new MongoBinaryExpression(
                    combineWithAnd ? MongoBinaryOperator.AndAlso : MongoBinaryOperator.OrElse,
                    combined, comparison);
        }

        return combined;
    }

    private static bool TryExtractEntityMemberValue(Expression entityValue, IProperty property, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        switch (entityValue)
        {
            case ConstantExpression { Value: { } instance }:
                result = new MongoConstantExpression(property.GetGetter().GetClrValue(instance), property);
                return true;

            case MemberInitExpression memberInit:
                var assignment = memberInit.Bindings.OfType<MemberAssignment>()
                    .FirstOrDefault(b => MemberMatchesProperty(b.Member, property));
                return assignment is not null && TryTranslateBoundMemberValue(assignment.Expression, property, out result);

            case NewExpression { Members: { } members } newExpression:
                for (var i = 0; i < members.Count; i++)
                {
                    if (MemberMatchesProperty(members[i], property))
                        return TryTranslateBoundMemberValue(newExpression.Arguments[i], property, out result);
                }

                return false;

            case var parameterNode when NativeQueryParameter.TryGetQueryParameterName(parameterNode, out var parameterName):
                result = new MongoParameterExpression(parameterName, property, extractFromEntityValue: true);
                return true;

            default:
                return false;
        }
    }

    private static bool TryTranslateBoundMemberValue(Expression memberValue, IProperty property, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = memberValue switch
        {
            ConstantExpression constant => new MongoConstantExpression(constant.Value, property),
            var node when NativeQueryParameter.TryGetQueryParameterName(node, out var parameterName)
                => new MongoParameterExpression(parameterName, property),
            _ => null
        };

        return result is not null;
    }

    private static bool MemberMatchesProperty(MemberInfo member, IProperty property)
        => member == property.PropertyInfo || member == property.FieldInfo || member.Name == property.Name;

    /// <summary>
    /// <c>customers.Contains(c)</c> against a client-side entity list, rewritten to a primary-key <c>$in</c>. It
    /// compiles once, so it must not depend on the list's length. Null elements pass through into <c>$in</c> (they
    /// never match). A composite key takes a per-entity <c>$or</c> (<see cref="MongoCompositeKeyInExpression"/>), since
    /// <see cref="MongoInExpression"/> has no tuple form.
    /// </summary>
    internal bool TryTranslateEntityListContains(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null || !TryMatchContainsMethod(call, out var collection, out var item)
            || !IsSelfParamTheEntity(Unwrap(item)))
            return false;

        var elementType = Unwrap(collection).Type.TryGetEnumerableElementType();
        if (elementType != _entityType.ClrType)
            return false;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Any(p => p.IsShadowProperty()))
            return false;

        if (primaryKey.Properties.Count > 1)
        {
            var entityValues = TranslateEntityListValues(collection);
            if (entityValues is null)
                return false;

            result = new MongoCompositeKeyInExpression(primaryKey.Properties, entityValues, negated: false);
            return true;
        }

        var keyProperty = primaryKey.Properties[0];
        if (keyProperty.IsShadowProperty())
            return false;

        var valuesNode = TranslateEntityKeyInValues(collection, keyProperty);
        if (valuesNode is null)
            return false;

        result = new MongoInExpression(
            new MongoFieldExpression(keyProperty, GetPropertyFieldPath(keyProperty)), valuesNode, negated: false);
        return true;
    }

    internal static MongoExpression? TranslateEntityListValues(Expression collectionExpr)
    {
        var unwrapped = Unwrap(collectionExpr);
        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable } constant)
            return new MongoConstantExpression(constant.Value, forSerialization: null);

        // EF8 hands an inline `new[] { a, b }.Contains(e)` over as a NewArrayInit of constants (EF9+ folds it to a constant).
        if (unwrapped is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray
            && newArray.Expressions.All(e => Unwrap(e) is ConstantExpression))
        {
            return new MongoConstantExpression(
                newArray.Expressions.Select(e => ((ConstantExpression)Unwrap(e)).Value).ToList(), forSerialization: null);
        }

        return NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var name)
            ? new MongoParameterExpression(name, forSerialization: null)
            : null;
    }

    internal static MongoExpression? TranslateEntityKeyInValues(Expression collectionExpr, IProperty keyProperty)
    {
        var unwrapped = Unwrap(collectionExpr);

        if (unwrapped is ConstantExpression { Value: System.Collections.IEnumerable items })
        {
            var getter = keyProperty.GetGetter();
            var keys = new List<object?>();
            foreach (var item in items)
                keys.Add(item is null ? null : getter.GetClrValue(item));

            return new MongoConstantExpression(keys, keyProperty);
        }

        if (NativeQueryParameter.TryGetQueryParameterName(unwrapped, out var parameterName))
            return new MongoParameterExpression(parameterName, keyProperty, extractEntityKeyFromArrayElements: true);

        return null;
    }
}
