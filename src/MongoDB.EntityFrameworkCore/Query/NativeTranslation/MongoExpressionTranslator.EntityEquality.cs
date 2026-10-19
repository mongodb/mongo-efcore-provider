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
/// EF Core has no provider-agnostic entity-equality rewrite (cf. relational's
/// <c>RelationalSqlTranslatingExpressionVisitor.StructuralEquality.cs</c>); without this, such predicates fall back
/// to driver-LINQ.
/// <para>
/// <c>c == null</c> and <c>c == c</c> decline here and are handled by <see cref="TranslateComparisonCore"/>'s
/// $$ROOT document-identity check. One operand must be reference-identical to <see cref="SelfParam"/>; the other a
/// constant, object initializer, or EF query parameter of the same CLR type. Reference-navigation operands and
/// two-sided join comparisons (EF-216) are out of scope.
/// </para>
/// <para>
/// Rewritten, as relational does, to per-key comparisons: <c>Equal</c> is an AND of key equalities;
/// <c>NotEqual</c> its exact De Morgan complement (safe because <c>$eq</c>/<c>$ne</c> partition the value space).
/// Composite-key components use the <c>_id.&lt;element&gt;</c> paths of
/// <see cref="MongoExpressionTranslator.TryResolveMember"/>.
/// </para>
/// <para>
/// A constant comparand's key is read immediately via <see cref="IPropertyBase.GetGetter"/>. A query-parameter
/// comparand isn't known until execution, so it becomes a <see cref="MongoParameterExpression"/> with
/// <see cref="MongoParameterExpression.ExtractFromEntityValue"/>, extracted per execution by
/// <see cref="PlaceholderTable"/>/<see cref="MongoPipelineFactory"/>. Shadow-property keys decline: a captured POCO
/// has no value to read.
/// </para>
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

        // Only the root entity has a primary key to compare; a projected/grouped row declines (IsSelfParamTheEntity).
        var leftIsSelf = IsSelfParamTheEntity(left);
        var rightIsSelf = IsSelfParamTheEntity(right);

        // Exactly one side must be the root entity; self-compare and neither-side decline.
        if (leftIsSelf == rightIsSelf)
            return false;

        var otherSide = leftIsSelf ? right : left;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null)
            return false;

        var keyProperties = primaryKey.Properties;
        if (keyProperties.Any(p => p.IsShadowProperty()))
            return false;

        // A null comparand is handled by TranslateComparisonCore's $$ROOT-vs-null check; decline explicitly (the type
        // check below would reject the object-typed null constant anyway).
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
                    new MongoFieldExpression(keyProperty, GetKeyFieldPath(keyProperty)),
                    valueExpr);
            },
            // Equal: all key components match (AND). NotEqual: any differs (OR).
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

    internal static string GetKeyFieldPath(IProperty property)
        => IsCompositeKeyComponent(property) ? "_id." + property.GetElementName() : property.GetElementName();

    /// <summary>
    /// Extracts <paramref name="property"/>'s value from the entity-typed comparand: a whole-entity constant or query
    /// parameter is read via its getter; a surviving <see cref="MemberInitExpression"/>/<see cref="NewExpression"/>
    /// is matched to <paramref name="property"/> by member binding name.
    /// </summary>
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

    /// <summary>
    /// Translates one member binding of an inline <c>new Customer{...}</c>: a constant, or an ordinary query
    /// parameter when that member was captured (<c>CustomerID = someLocal</c>). No per-execution extraction needed.
    /// </summary>
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
    /// <c>customers.Contains(c)</c> against a client-side list of entities, rewritten to a primary-key <c>$in</c>.
    /// Unlike driver-LINQ's per-execution OR-chain, this compiles once, so it must not depend on the list's length.
    /// Null elements pass through into <c>$in</c> (as Cosmos does); they can never match a real row.
    /// </summary>
    /// <remarks>
    /// Single-property, non-shadow keys only; a composite key would need a tuple <c>$in</c> that
    /// <see cref="MongoInExpression"/> can't express, so it declines.
    /// </remarks>
    internal bool TryTranslateEntityListContains(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null || !TryMatchContainsMethod(call, out var collection, out var item)
            || !IsSelfParamTheEntity(Unwrap(item)))
            return false;

        var elementType = GetEnumerableElementType(Unwrap(collection).Type);
        if (elementType != _entityType.ClrType)
            return false;

        var primaryKey = _entityType.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1)
            return false; // composite key: no multi-field $in

        var keyProperty = primaryKey.Properties[0];
        if (keyProperty.IsShadowProperty())
            return false;

        var valuesNode = TranslateEntityKeyInValues(collection, keyProperty);
        if (valuesNode is null)
            return false;

        result = new MongoInExpression(
            new MongoFieldExpression(keyProperty, GetKeyFieldPath(keyProperty)), valuesNode, negated: false);
        return true;
    }

    /// <summary>
    /// Translates the collection side of <see cref="TryTranslateEntityListContains"/> into <c>$in</c> values: a
    /// <see cref="MongoConstantExpression"/> of extracted keys for a known list, or a
    /// <see cref="MongoParameterExpression"/> with <see cref="MongoParameterExpression.ExtractEntityKeyFromArrayElements"/>
    /// for a parameter list, extracted per execution.
    /// </summary>
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
