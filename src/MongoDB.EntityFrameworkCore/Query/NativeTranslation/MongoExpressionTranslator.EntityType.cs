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
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.EntityFrameworkCore.Extensions; // IsInHierarchy()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>root.GetType() == typeof(T)</c> (and <c>!=</c>) for a root entity
/// type with no hierarchy.
/// </summary>
/// <remarks>
/// EF Core has no provider-agnostic rewrite for <see cref="object.GetType"/> comparisons (cf. relational's
/// <c>ProcessGetType</c>). With no base or derived types, <c>GetType()</c> always equals the entity's CLR type, so
/// the comparison folds to a constant. TPH hierarchies would need a discriminator predicate (as
/// <c>TryBuildDiscriminatorPredicate</c> does for <c>OfType&lt;T&gt;</c>) and decline.
/// <para>
/// This avoids EF-202 for the non-hierarchy shape: driver-LINQ narrows on discriminator presence rather than the
/// comparison type, so <c>GetType() == typeof(Unrelated)</c> wrongly matches every row.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private static readonly MethodInfo GetTypeMethodInfo = typeof(object).GetMethod(nameof(GetType))!;

    private bool TryTranslateGetTypeComparison(BinaryExpression be, out MongoExpression? result)
        => TryTranslateGetTypeComparison(be.Left, be.Right, be.NodeType == ExpressionType.Equal, out result);

    private bool TryTranslateGetTypeComparison(Expression leftSide, Expression rightSide, bool isEqual, out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null)
            return false;

        var left = Unwrap(leftSide);
        var right = Unwrap(rightSide);

        Type comparisonType;
        if (IsRootGetTypeCall(left) && right is ConstantExpression { Value: Type rightType })
        {
            comparisonType = rightType;
        }
        else if (IsRootGetTypeCall(right) && left is ConstantExpression { Value: Type leftType })
        {
            comparisonType = leftType;
        }
        else
        {
            return false;
        }

        // Hierarchy types need a discriminator predicate, not a constant; decline.
        if (_entityType.IsInHierarchy())
            return false;

        var matches = _entityType.ClrType == comparisonType;
        result = new MongoConstantExpression(matches == isEqual, forSerialization: null);
        return true;
    }

    private bool IsRootGetTypeCall(Expression expression)
        => expression is MethodCallExpression { Method: var method, Object: { } receiver }
            && method == GetTypeMethodInfo
            && IsSelfParamTheEntity(Unwrap(receiver));
}
