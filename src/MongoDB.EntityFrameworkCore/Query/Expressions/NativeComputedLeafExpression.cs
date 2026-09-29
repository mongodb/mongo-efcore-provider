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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A shaper node whose whole value a native <c>$project</c> computes server-side (<c>x.S.Substring(1, 2)</c>,
/// <c>x.S.Contains("a")</c>, <c>x.S == null</c>), wrapping the ordinary client-side form of the same node.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientExpression"/> is exactly what the projection binding visitor would have produced without the
/// wrapper (the receiver bound to the alias, the call/operator re-applied client-side), so every path that isn't
/// the native alias reader stays correct by reading through it: the mixed shaper over whole documents, the
/// driver-LINQ push-down analysis (<c>ProjectionAnalyzer</c>, which sees the children), and anything that just
/// reduces the node.
/// </para>
/// <para>
/// Only <c>MongoProjectionBindingRemovingExpressionVisitor</c> (the alias reader) replaces the whole node with a raw
/// read of <see cref="Binding"/>'s alias, whose value the server (or a driver-LINQ push-down of the same Select)
/// already computed. Reading through <see cref="ClientExpression"/> there would apply the call twice.
/// </para>
/// </remarks>
internal sealed class NativeComputedLeafExpression : Expression
{
    public NativeComputedLeafExpression(Expression clientExpression, ProjectionBindingExpression binding)
    {
        ClientExpression = clientExpression;
        Binding = binding;
    }

    /// <summary>The client-side form: the receiver's projection binding with the call/operator re-applied.</summary>
    public Expression ClientExpression { get; }

    /// <summary>The binding whose alias holds the server-computed value.</summary>
    public ProjectionBindingExpression Binding { get; }

    /// <inheritdoc />
    public override Type Type => ClientExpression.Type;

    /// <inheritdoc />
    public override ExpressionType NodeType => ExpressionType.Extension;

    /// <inheritdoc />
    public override bool CanReduce => true;

    /// <inheritdoc />
    public override Expression Reduce() => ClientExpression;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visited = visitor.Visit(ClientExpression);
        return ReferenceEquals(visited, ClientExpression)
            ? this
            : new NativeComputedLeafExpression(visited, Binding);
    }
}
