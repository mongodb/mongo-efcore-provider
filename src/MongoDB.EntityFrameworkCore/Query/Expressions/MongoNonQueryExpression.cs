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

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Marker node produced by ExecuteDelete / ExecuteUpdate translation. Carries the source
/// query (collection + captured predicate chain) and, for updates, the ordered setters.
/// Recognized by <see cref="Visitors.MongoShapedQueryCompilingExpressionVisitor"/>.
/// </summary>
internal sealed class MongoNonQueryExpression : Expression
{
    public enum OperationKind { Delete, Update }

    public enum BulkStrategy { SingleCommand, TwoPhase }

    public sealed record Setter(IProperty Property, Expression ValueExpression, bool IsSelfReferencing);

    public MongoNonQueryExpression(MongoQueryExpression sourceQuery, BulkStrategy strategy = BulkStrategy.SingleCommand)
    {
        SourceQuery = sourceQuery;
        Kind = OperationKind.Delete;
        Setters = [];
        Strategy = strategy;
    }

    public MongoNonQueryExpression(
        MongoQueryExpression sourceQuery,
        IReadOnlyList<Setter> setters,
        BulkStrategy strategy = BulkStrategy.SingleCommand)
    {
        SourceQuery = sourceQuery;
        Kind = OperationKind.Update;
        Setters = setters;
        Strategy = strategy;
    }

    public MongoQueryExpression SourceQuery { get; }
    public OperationKind Kind { get; }
    public IReadOnlyList<Setter> Setters { get; }
    public BulkStrategy Strategy { get; }

    /// <summary>
    /// Unwraps the leading ExecuteDelete / ExecuteUpdate marker call (declared on
    /// <see cref="EntityFrameworkQueryableExtensions"/>) from a captured method chain, returning the underlying
    /// source query (e.g. <c>root.Where(...)</c>). EF captures the full chain including the bulk operator as the
    /// <see cref="MongoQueryExpression.CapturedExpression"/>; walkers that scope the operation need only the source.
    /// </summary>
    public static Expression? UnwrapBulkOperator(Expression? capturedExpression)
        => capturedExpression is MethodCallExpression { Method.DeclaringType: var declaringType } call
           && declaringType == typeof(EntityFrameworkQueryableExtensions)
            ? call.Arguments[0]
            : capturedExpression;

    public override ExpressionType NodeType => ExpressionType.Extension;
    public override System.Type Type => typeof(int);
    public override bool CanReduce => false;

    protected override Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor) => this;
}
