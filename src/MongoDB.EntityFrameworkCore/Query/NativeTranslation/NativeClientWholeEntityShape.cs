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
using Microsoft.EntityFrameworkCore.Infrastructure;  // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

internal enum ClientWholeEntityOperand
{
    /// <summary>Walk the node structurally.</summary>
    Walk,

    /// <summary>The node reads nothing of the row (no entity reference at all).</summary>
    EntityFree,

    /// <summary>The node is the whole entity, unchanged.</summary>
    WholeEntity
}

/// <summary>
/// The single shape rule for a projection evaluated entirely client-side over a fetched whole document: a client
/// method on the whole entity (<c>x =&gt; context.ClientMethod(x)</c>), a client-only combinator around one
/// (<c>x.Flag ? ClientMethod(x) : ""</c>), or a client construction with a whole-entity operand
/// (<c>new object[] { x }</c>, <c>new List&lt;object&gt; { x }</c>, <c>new Wrapper(x) { City = x.City }</c>).
/// </summary>
/// <remarks>
/// Called by both the emit side (<c>NativeProjectionBinder</c>) and the read side
/// (<c>MongoShapedQueryCompilingExpressionVisitor.IsCtorWrappedEntityShaper</c>); only the classifier differs, so the two
/// cannot disagree about which shapes are admitted. At least one client-only operand is required, else a translatable tree
/// (<c>Ternary_Null_Equals_Non_Numeric_First_Part</c>) or scalar-only container (<c>new[] { x.Id }</c>) would switch to
/// whole-document fetching. A whole-entity operand is never row-independent, so this is disjoint from
/// <c>NativeProjectionBinder.IsRowIndependentLeaf</c>.
/// </remarks>
internal static class NativeClientWholeEntityShape
{
    internal static bool IsClientOnlyTree(Expression node, Func<Expression, ClientWholeEntityOperand> classify)
    {
        var sawClientOnlyOperand = false;
        return Walk(node, classify, ref sawClientOnlyOperand) && sawClientOnlyOperand;
    }

    /// <summary>
    /// True when exactly one operand of <paramref name="methodCall"/> is the whole entity and every other is entity-free
    /// (<c>EF.Property</c> excluded). A second entity-referencing operand declines: the shaper fold would have to recurse through
    /// the opaque call's other operands, which is untested.
    /// </summary>
    internal static bool HasSoleWholeEntityOperand(
        MethodCallExpression methodCall, Func<Expression, ClientWholeEntityOperand> classify)
    {
        if (methodCall.Method.IsEFPropertyMethod())
        {
            return false;
        }

        var sawWholeEntity = false;
        if (methodCall.Object != null && !AcceptCallOperand(methodCall.Object))
        {
            return false;
        }

        foreach (var argument in methodCall.Arguments)
        {
            if (!AcceptCallOperand(argument))
            {
                return false;
            }
        }

        return sawWholeEntity;

        bool AcceptCallOperand(Expression operand)
        {
            switch (classify(operand))
            {
                case ClientWholeEntityOperand.EntityFree:
                    return true;
                case ClientWholeEntityOperand.WholeEntity when !sawWholeEntity:
                    sawWholeEntity = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    private static bool Walk(
        Expression node, Func<Expression, ClientWholeEntityOperand> classify, ref bool sawClientOnlyOperand)
    {
        // Fails closed: any other row read the walk can't see into (a shaper-side ProjectionBindingExpression, an unknown
        // extension node) would be resolved off the raw whole document, not a narrowed $project, and read as null.
        switch (classify(node))
        {
            case ClientWholeEntityOperand.EntityFree or ClientWholeEntityOperand.WholeEntity:
                return true;
            case not ClientWholeEntityOperand.Walk:
                return false;
        }

        switch (node)
        {
            case ConditionalExpression conditional:
                return Walk(conditional.Test, classify, ref sawClientOnlyOperand)
                    && Walk(conditional.IfTrue, classify, ref sawClientOnlyOperand)
                    && Walk(conditional.IfFalse, classify, ref sawClientOnlyOperand);

            case BinaryExpression binary:
                return Walk(binary.Left, classify, ref sawClientOnlyOperand)
                    && Walk(binary.Right, classify, ref sawClientOnlyOperand);

            case UnaryExpression unary:
                return Walk(unary.Operand, classify, ref sawClientOnlyOperand);

            case MemberExpression { Expression: not null } member:
                return Walk(member.Expression, classify, ref sawClientOnlyOperand);

            case MethodCallExpression methodCall when HasSoleWholeEntityOperand(methodCall, classify):
                sawClientOnlyOperand = true;
                return true;

            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } newArray:
                foreach (var element in newArray.Expressions)
                {
                    if (!WalkConstructionOperand(element, classify, ref sawClientOnlyOperand))
                    {
                        return false;
                    }
                }

                return true;

            case ListInitExpression listInit:
                if (!WalkConstructorArguments(listInit.NewExpression, classify, ref sawClientOnlyOperand))
                {
                    return false;
                }

                foreach (var initializer in listInit.Initializers)
                {
                    if (initializer.Arguments is not [var added]
                        || !WalkConstructionOperand(added, classify, ref sawClientOnlyOperand))
                    {
                        return false;
                    }
                }

                return true;

            case MemberInitExpression memberInit:
                if (!WalkConstructorArguments(memberInit.NewExpression, classify, ref sawClientOnlyOperand))
                {
                    return false;
                }

                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment
                        || !WalkConstructionOperand(assignment.Expression, classify, ref sawClientOnlyOperand))
                    {
                        return false;
                    }
                }

                return true;

            case NewExpression newExpression:
                return WalkConstructorArguments(newExpression, classify, ref sawClientOnlyOperand);

            default:
                return false;
        }
    }

    // Member-less only: a member-bearing `new` is bound member by member by the projection binding visitor.
    private static bool WalkConstructorArguments(
        NewExpression newExpression, Func<Expression, ClientWholeEntityOperand> classify, ref bool sawClientOnlyOperand)
    {
        if (newExpression.Members != null)
        {
            return false;
        }

        foreach (var argument in newExpression.Arguments)
        {
            if (!WalkConstructionOperand(argument, classify, ref sawClientOnlyOperand))
            {
                return false;
            }
        }

        return true;
    }

    // A whole entity as a direct construction operand (through the boxing Convert `new object[] { x }` adds) is client-only.
    private static bool WalkConstructionOperand(
        Expression operand, Func<Expression, ClientWholeEntityOperand> classify, ref bool sawClientOnlyOperand)
    {
        if (classify(operand.RemoveConvert()) == ClientWholeEntityOperand.WholeEntity)
        {
            sawClientOnlyOperand = true;
            return true;
        }

        return Walk(operand, classify, ref sawClientOnlyOperand);
    }
}
