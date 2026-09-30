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

/// <summary>
/// How <see cref="NativeClientWholeEntityShape"/> sees one node, as answered by the caller's classifier.
/// </summary>
internal enum ClientWholeEntityOperand
{
    /// <summary>Walk the node structurally.</summary>
    Walk,

    /// <summary>The node reads nothing of the row (no entity reference at all).</summary>
    EntityFree,

    /// <summary>The node is the whole entity, unchanged.</summary>
    WholeEntity,

    /// <summary>
    /// The node is an already-bound read of the entity (a shaper-side projection binding); an accepted leaf, but not
    /// entity-free, so it can't be an extra operand of an opaque call.
    /// </summary>
    EntityRead
}

/// <summary>
/// The single shape rule for a projection evaluated entirely client-side over a fetched whole document: a client
/// method on the whole entity (<c>x =&gt; context.ClientMethod(x)</c>), a client-only combinator around one
/// (<c>x.Flag ? ClientMethod(x) : ""</c>), or a client construction with a whole-entity operand
/// (<c>new object[] { x }</c>, <c>new List&lt;object&gt; { x }</c>, <c>new Wrapper(x) { City = x.City }</c>).
/// </summary>
/// <remarks>
/// <para>
/// Called by both the emit side (<c>NativeProjectionBinder</c>, over the selector, where the entity is the selector
/// parameter) and the read side (<c>MongoShapedQueryCompilingExpressionVisitor.IsCtorWrappedEntityShaper</c>, over
/// the bound shaper, where it is a structural-type shaper). Only the classifier differs; the walk is this one method,
/// so the two sides cannot disagree about which shapes are admitted.
/// </para>
/// <para>
/// At least one client-only operand is required: an opaque call on the whole entity, or a whole entity as a direct
/// construction operand. Without it a translatable tree (<c>Ternary_Null_Equals_Non_Numeric_First_Part</c>) or a
/// scalar-only container (<c>new[] { x.Id }</c>) would switch to whole-document fetching.
/// </para>
/// <para>
/// A whole-entity operand is never row-independent, so this is disjoint from
/// <c>NativeProjectionBinder.IsRowIndependentLeaf</c>: a construction whose operands are all row-independent is
/// that predicate's, one with a whole-entity operand is this one's.
/// </para>
/// </remarks>
internal static class NativeClientWholeEntityShape
{
    /// <summary>
    /// True when <paramref name="node"/> is admitted: only conditional, binary, unary, member-access, opaque-call,
    /// array-init, single-argument list-init, member-less <c>new</c> and member-init nodes are walked; anything else
    /// declines.
    /// </summary>
    internal static bool IsClientOnlyTree(Expression node, Func<Expression, ClientWholeEntityOperand> classify)
    {
        var sawClientOnlyOperand = false;
        return Walk(node, classify, ref sawClientOnlyOperand) && sawClientOnlyOperand;
    }

    /// <summary>
    /// True when exactly one operand of <paramref name="methodCall"/> (receiver plus arguments) is the whole entity
    /// and every other operand is entity-free. <c>EF.Property</c> is excluded: it is a field accessor, not a client
    /// call.
    /// </summary>
    /// <remarks>
    /// A second entity-referencing operand (<c>context.ClientMethod(x, x.CustomerID)</c>) declines: supporting it
    /// would need the shaper fold to recurse through the opaque call's other operands, which is untested.
    /// </remarks>
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
        if (classify(node) != ClientWholeEntityOperand.Walk)
        {
            return true;
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

    // Member-less only: an anonymous/member-bearing `new` is bound member by member by the projection binding
    // visitor, which would register its arguments as projected members.
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

    // A whole entity as a direct construction operand (through the boxing/upcast Convert `new object[] { x }` adds) is
    // itself client-only: the construction runs over the materialized entity.
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
