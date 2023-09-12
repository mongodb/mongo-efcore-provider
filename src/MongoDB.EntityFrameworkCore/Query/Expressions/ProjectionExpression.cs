// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos ProjectionExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

internal sealed class ProjectionExpression : Expression
{
    public ProjectionExpression(Expression expression, string alias)
    {
        Expression = expression;
        Alias = alias;
    }

    public string Alias { get; }

    public Expression Expression { get; }

    public override Type Type
        => Expression.Type;

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(Expression));

    public ProjectionExpression Update(Expression expression)
        => expression != Expression
            ? new ProjectionExpression(expression, Alias)
            : this;

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ProjectionExpression projectionExpression
               && Equals(projectionExpression));

    private bool Equals(ProjectionExpression projectionExpression)
        => Alias == projectionExpression.Alias
           && Expression.Equals(projectionExpression.Expression);

    public override int GetHashCode()
        => HashCode.Combine(Alias, Expression);
}
