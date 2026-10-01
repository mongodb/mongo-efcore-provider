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
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Recognizes the client clock members (<c>DateTime.Now</c>/<c>UtcNow</c>/<c>Today</c>,
/// <c>DateTimeOffset.Now</c>/<c>UtcNow</c>) that EF leaves un-funcletized, and compiles a closed subtree holding one
/// into a delegate the native pipeline evaluates once per <em>execution</em>.
/// </summary>
/// <remarks>
/// The native template is built once and cached with the compiled query, so a clock evaluated at translation time
/// would be baked into every later execution. Driver-LINQ (the parity target) folds the clock per execution on the
/// client and serializes it with the DateTime serializer, so Local <c>Now</c>/<c>Today</c> are converted to UTC
/// instants; the per-execution value here takes the same serialization path.
/// </remarks>
internal static class RuntimeClock
{
    /// <summary>Whether <paramref name="member"/> is a static clock property.</summary>
    public static bool IsClockMember(MemberExpression member)
        => member is { Expression: null, Member: PropertyInfo property }
           && ((property.DeclaringType == typeof(DateTime)
                   && property.Name is nameof(DateTime.Now) or nameof(DateTime.UtcNow) or nameof(DateTime.Today))
               || (property.DeclaringType == typeof(DateTimeOffset)
                   && property.Name is nameof(DateTimeOffset.Now) or nameof(DateTimeOffset.UtcNow)));

    /// <summary>Whether <paramref name="node"/> contains a clock member anywhere.</summary>
    public static bool ContainsClock(Expression node)
    {
        var finder = new ClockFinder();
        finder.Visit(node);
        return finder.Found;
    }

    /// <summary>
    /// Whether <paramref name="node"/> is a closed subtree, holding a clock member, that the native path evaluates in
    /// .NET once per execution: typed <c>bool</c>/<c>bool?</c>/<c>DateTime</c>/<c>DateTime?</c>, referencing no lambda
    /// parameter or extension node other than EF query parameters, and no non-deterministic call
    /// (<see cref="NonDeterministicCalls"/>). <c>DateTimeOffset</c>-typed results are not admitted: they have no
    /// property-less BSON form, and only occur inside an admitted closed <c>bool</c>.
    /// </summary>
    /// <remarks>
    /// The single admission predicate: both the comparison gate (<c>IsSimpleValue</c>) and the parameter factory
    /// (<see cref="MongoExpressionTranslator.TryCreateRuntimeClockParameter"/>) call it.
    /// </remarks>
    public static bool IsRuntimeEvaluable(Expression node)
        => (node.Type == typeof(bool) || node.Type == typeof(bool?)
               || node.Type == typeof(DateTime) || node.Type == typeof(DateTime?))
           && ContainsClock(node)
           && IsClosedOverQueryParametersOnly(node);

    /// <summary>
    /// Compiles <paramref name="node"/> (which <see cref="IsRuntimeEvaluable"/> admitted) into a delegate over the
    /// execution's query-parameter values. Each EF query parameter is read from the dictionary by name.
    /// </summary>
    public static Func<IReadOnlyDictionary<string, object?>, object?> CompileEvaluator(Expression node)
    {
        var values = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "values");
        var body = new QueryParameterLookupRewriter(values).Visit(node);
        return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, object?>>(
            Expression.Convert(body, typeof(object)), values).Compile();
    }

    private static bool IsClosedOverQueryParametersOnly(Expression node)
    {
        var finder = new OpenNodeFinder();
        finder.Visit(node);
        return !finder.Found;
    }

    private sealed class ClockFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (node is null || Found)
                return node;

            // An EF10 QueryParameterExpression is an extension node with nothing to search inside.
            if (node.NodeType == ExpressionType.Extension)
                return node;

            return base.Visit(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (IsClockMember(node))
            {
                Found = true;
                return node;
            }

            return base.VisitMember(node);
        }
    }

    // Finds anything that makes the subtree not evaluable in isolation per execution: a lambda parameter (row data)
    // or an extension node other than an EF query parameter, or a non-deterministic call whose per-row semantics
    // one evaluation would silently replace (EF-255).
    private sealed class OpenNodeFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (node is null || Found)
                return node;

            if (NativeQueryParameter.TryGetQueryParameterName(node, out _))
                return node;

            if (node is ParameterExpression || node.NodeType == ExpressionType.Extension)
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (NonDeterministicCalls.IsNonDeterministic(node))
            {
                Found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class QueryParameterLookupRewriter(ParameterExpression values) : ExpressionVisitor
    {
        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (node is not null && NativeQueryParameter.TryGetQueryParameterName(node, out var name))
                return Expression.Convert(Expression.Property(values, "Item", Expression.Constant(name)), node.Type);

            return base.Visit(node);
        }
    }
}
