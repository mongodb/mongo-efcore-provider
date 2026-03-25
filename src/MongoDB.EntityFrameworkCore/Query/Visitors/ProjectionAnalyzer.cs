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

using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Analyzes a shaper expression to determine whether a Select projection can be
/// fully pushed down to the MongoDB LINQ V3 provider, or whether it needs client-side
/// handling (the "mixed" path).
/// </summary>
internal static class ProjectionAnalyzer
{
    /// <summary>
    /// Determines whether the given shaper expression can be fully pushed down to the
    /// MongoDB LINQ V3 provider. Returns <see langword="false"/> when the projection
    /// contains entity references or other constructs that require client-side materialization.
    /// </summary>
    public static bool CanPushDown(Expression shaperExpression)
        => !ContainsEntityReference(shaperExpression);

    private static bool ContainsEntityReference(Expression expression)
    {
        switch (expression)
        {
            case StructuralTypeShaperExpression:
            case IncludeExpression:
                return true;

            case NewExpression newExpression:
                foreach (var arg in newExpression.Arguments)
                {
                    if (ContainsEntityReference(arg))
                        return true;
                }
                return false;

            case MemberInitExpression memberInitExpression:
                if (ContainsEntityReference(memberInitExpression.NewExpression))
                    return true;

                foreach (var binding in memberInitExpression.Bindings)
                {
                    if (binding is MemberAssignment assignment && ContainsEntityReference(assignment.Expression))
                        return true;
                }
                return false;

            case UnaryExpression unaryExpression:
                return ContainsEntityReference(unaryExpression.Operand);

            case ConditionalExpression conditionalExpression:
                // The Test always evaluates to bool — it never produces an entity in the result,
                // so entity references in the test (e.g., entity != null) don't prevent push-down.
                return ContainsEntityReference(conditionalExpression.IfTrue)
                    || ContainsEntityReference(conditionalExpression.IfFalse);

            case BinaryExpression binaryExpression:
                return ContainsEntityReference(binaryExpression.Left)
                    || ContainsEntityReference(binaryExpression.Right);

            case MethodCallExpression methodCallExpression:
                if (methodCallExpression.Object != null && ContainsEntityReference(methodCallExpression.Object))
                    return true;

                foreach (var arg in methodCallExpression.Arguments)
                {
                    if (ContainsEntityReference(arg))
                        return true;
                }
                return false;

            case NewArrayExpression newArrayExpression:
                foreach (var element in newArrayExpression.Expressions)
                {
                    if (ContainsEntityReference(element))
                        return true;
                }
                return false;

            case MemberExpression memberExpression:
                return memberExpression.Expression != null && ContainsEntityReference(memberExpression.Expression);

            case LambdaExpression lambdaExpression:
                return ContainsEntityReference(lambdaExpression.Body);

            case ProjectionBindingExpression:
            case ConstantExpression:
            case ParameterExpression:
            case DefaultExpression:
                return false;

            default:
                // Unknown expression types conservatively prevent push-down to avoid
                // silently mishandling future expression types that may wrap entities.
                Debug.Assert(true, $"Unknown expression type {expression.GetType().Name}");
                return true;
        }
    }
}
