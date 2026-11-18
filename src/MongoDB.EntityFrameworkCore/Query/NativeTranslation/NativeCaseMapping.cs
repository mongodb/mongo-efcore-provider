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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes the string case-mapping and <c>DateTime.ToString</c> calls that no server form matches, so a projected leaf stages only the call's
/// receiver and the shaper re-applies the .NET call client-side. Shared by the projection binders, the translator, the projection-binding visitor
/// and the shaped-query compiler's grouped-query gate.
/// </summary>
internal static class NativeCaseMapping
{
    /// <summary>True for zero-argument <c>ToLower</c>/<c>ToUpper</c>/<c>ToLowerInvariant</c>/<c>ToUpperInvariant</c> on a <see langword="string"/>. Server forms are ASCII-only, so a leaf stages only the receiver (<see cref="PeelCaseMapping"/>) and the call is re-applied client-side.</summary>
    internal static bool IsCaseMappingCall(MethodCallExpression call)
        => call is { Object.Type: var objectType, Arguments.Count: 0 }
           && objectType == typeof(string)
           && call.Method.DeclaringType == typeof(string)
           && call.Method.Name is nameof(string.ToLower) or nameof(string.ToUpper)
               or nameof(string.ToLowerInvariant) or nameof(string.ToUpperInvariant);

    /// <summary>
    /// The receiver of a (possibly repeated) case-mapping call, which is what the leaf stages; the call stays in the shaper (<c>MongoProjectionBindingExpressionVisitor.Visit</c>;
    /// positional-ctor: <c>BindPositionalCtorProjectionMember</c>). <paramref name="includeDateTimeToString"/> also peels <see cref="IsDateTimeToString"/> (plain-<c>Select</c> callers only: their read side re-applies it).
    /// </summary>
    internal static Expression PeelCaseMapping(Expression leafExpression, bool includeDateTimeToString = false)
    {
        while (true)
        {
            switch (leafExpression)
            {
                case MethodCallExpression call
                    when IsCaseMappingCall(call) || includeDateTimeToString && IsDateTimeToString(call) || IsClientStringChainStep(call):
                    leafExpression = call.Object!;
                    continue;

                case BinaryExpression binary when TryMatchClientStringConcat(binary, out var chainSide, out _, out _):
                    leafExpression = chainSide;
                    continue;
            }

            return leafExpression;
        }
    }

    // The string instance methods that map a string to a string, with literal arguments, and that a server form would compute differently
    // from the .NET call only on malformed input (out of range). A step above a case mapping is applied by the client with it.
    private static readonly System.Collections.Generic.HashSet<string> ClientStringStepNames =
    [
        nameof(string.Substring), nameof(string.Trim), nameof(string.TrimStart), nameof(string.TrimEnd), nameof(string.Replace),
        nameof(string.PadLeft), nameof(string.PadRight), nameof(string.Insert), nameof(string.Remove)
    ];

    /// <summary>
    /// True for a string-to-string instance call with literal arguments (<c>Substring(0, 1)</c>, <c>Trim()</c>) whatever its receiver: the shape the
    /// client form of <see cref="IsClientStringChainStep"/> has once its receiver is bound.
    /// </summary>
    internal static bool IsClientStringStepMethod(MethodCallExpression call)
        => call is { Object.Type: var objectType, Method: { DeclaringType: var declaringType, ReturnType: var returnType } method }
           && objectType == typeof(string)
           && declaringType == typeof(string)
           && returnType == typeof(string)
           && ClientStringStepNames.Contains(method.Name)
           && call.Arguments.All(a => a is ConstantExpression);

    /// <summary>
    /// True for a step (<see cref="IsClientStringStepMethod"/>) applied to a value that holds a case mapping, which runs client-side: the whole chain is
    /// peeled to the raw receiver the leaf stages, and the client applies each step to the mapped value, null propagated. A step over a receiver with
    /// no case mapping is an ordinary computed leaf and stays on the server.
    /// </summary>
    internal static bool IsClientStringChainStep(MethodCallExpression call)
        => IsClientStringStepMethod(call) && ContainsCaseMappingCall(call.Object!);

    /// <summary>
    /// True for <c>mapped + "literal"</c> or <c>"literal" + mapped</c> where <c>mapped</c> holds a case mapping: concatenation with a literal is a step of the
    /// client chain, null propagated.
    /// </summary>
    internal static bool TryMatchClientStringConcat(
        BinaryExpression binary, out Expression chainSide, out ConstantExpression constantSide, out bool chainIsLeft)
    {
        chainSide = null!;
        constantSide = null!;
        chainIsLeft = false;
        if (binary is not { NodeType: ExpressionType.Add, Type: var type, Method: { Name: "Concat", DeclaringType: var declaringType } }
            || type != typeof(string)
            || declaringType != typeof(string))
        {
            return false;
        }

        if (binary.Right is ConstantExpression { Type: var rightType } rightConstant && rightType == typeof(string)
            && ContainsCaseMappingCall(binary.Left))
        {
            (chainSide, constantSide, chainIsLeft) = (binary.Left, rightConstant, true);
            return true;
        }

        if (binary.Left is ConstantExpression { Type: var leftType } leftConstant && leftType == typeof(string)
            && ContainsCaseMappingCall(binary.Right))
        {
            (chainSide, constantSide, chainIsLeft) = (binary.Right, leftConstant, false);
            return true;
        }

        return false;
    }

    /// <summary>True for <c>ToString()</c>/<c>ToString("format")</c> on a <see cref="DateTime"/> (or nullable). No server form matches .NET's culture-sensitive output (driver-LINQ pushes down an ISO <c>$toString</c>, wrong on main), so a leaf stages only the receiver and the call is re-applied client-side, as for <c>DateTimeOffset.ToString</c> (D-TOSTRING).</summary>
    internal static bool IsDateTimeToString(MethodCallExpression call)
        => call is { Method.Name: nameof(ToString), Object: { } receiver }
           && receiver.Type.UnwrapNullableType() == typeof(DateTime)
           && call.Arguments is [] or [ConstantExpression { Value: string }];

    /// <summary>
    /// The value a grouped result member (or bare body) stages when it projects a client case mapping (<c>g.Key.ToUpper()</c>, also under a <c>Convert</c>): the
    /// mapping's receiver, whose raw value the <c>$group</c> output holds while the shaper re-applies the .NET call; otherwise <paramref name="value"/>. Driver-LINQ
    /// would compute it with the ASCII-only, null-as-<c>""</c> <c>$toUpper</c>/<c>$toLower</c>, so <see cref="ContainsCaseMappingCall"/> declines it.
    /// </summary>
    internal static Expression PeelResultMemberCaseMapping(Expression value)
        => value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
           && PeelResultMemberCaseMapping(convert.Operand) is var receiver
           && !ReferenceEquals(receiver, convert.Operand)
            ? receiver
            : PeelCaseMapping(value);

    /// <summary>True when <paramref name="expression"/> holds a case-mapping call (<see cref="IsCaseMappingCall"/>) anywhere; the shaped-query compiler declines a grouped query holding one rather than run it on driver-LINQ (ASCII-only, null-as-<c>""</c> <c>$toUpper</c>/<c>$toLower</c>).</summary>
    internal static bool ContainsCaseMappingCall(Expression expression)
    {
        var finder = new CaseMappingCallFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class CaseMappingCallFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
            => Found ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsCaseMappingCall(node))
            {
                Found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }

        // Query roots and other provider nodes carry no case mapping, and not all can visit children.
        protected override Expression VisitExtension(Expression node)
            => node;
    }
}
