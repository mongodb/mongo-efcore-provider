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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>root.GetType() == typeof(T)</c> (and its <c>!=</c> form),
/// scoped to a NON-hierarchy root entity type.
/// </summary>
/// <remarks>
/// EF Core has no provider-agnostic rewrite for <see cref="object.GetType"/> comparisons either — like whole-
/// entity equality (see <c>MongoExpressionTranslator.EntityEquality.cs</c>), each provider implements its own
/// (relational's <c>RelationalSqlTranslatingExpressionVisitor.ProcessGetType</c>, InMemory's equivalent), so
/// without this, EVERY <c>c.GetType() == typeof(T)</c> predicate fell back to driver-LINQ.
/// <para>
/// <b>Scope, deliberately narrow.</b> Only a root entity type with NO hierarchy (no base type and no directly
/// derived types) is handled here — for such a type, <c>GetType()</c> is compile-time-known to always equal
/// the entity type's own CLR type, so the whole comparison collapses to a constant <see langword="true"/>/
/// <see langword="false"/>, needing no discriminator field at all. A TPH hierarchy needs a genuine
/// discriminator-value predicate (mirroring <c>TryBuildDiscriminatorPredicate</c>, used for <c>OfType&lt;T&gt;</c>)
/// and is out of scope here — it declines and keeps falling back.
/// </para>
/// <para>
/// <b>This incidentally fixes EF-202 for the non-hierarchy shape.</b> The driver-LINQ fallback's own
/// <c>GetType()</c> handling narrows on discriminator-field presence/absence (<c>_t: null</c> / <c>_t: {$ne:
/// null}</c>) rather than on the COMPARISON type, so <c>c.GetType() == typeof(SomeUnrelatedType)</c> against a
/// non-hierarchy <c>Customer</c> wrongly matches every row instead of none. Going native here sidesteps that
/// bug entirely: the comparison type is checked directly against the entity's own CLR type at translate time.
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

        // Hierarchy types need a real discriminator predicate, not a compile-time constant — decline and let
        // this keep falling back to driver-LINQ (see the type's own remarks).
        if (_entityType.BaseType is not null || _entityType.GetDirectlyDerivedTypes().Any())
            return false;

        var matches = _entityType.ClrType == comparisonType;
        result = new MongoConstantExpression(matches == isEqual, forSerialization: null);
        return true;
    }

    private bool IsRootGetTypeCall(Expression expression)
        => expression is MethodCallExpression { Method: var method, Object: { } receiver }
            && method == GetTypeMethodInfo
            && ReferenceEquals(Unwrap(receiver), SelfParam);
}
