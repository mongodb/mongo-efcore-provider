// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos ProjectionExpression.

using System;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents an individual member projection within the expression tree.
/// </summary>
internal sealed class ProjectionExpression : Expression
{
    public ProjectionExpression(
        Expression expression,
        string alias,
        bool required)
    {
        Expression = expression;
        Alias = alias;
        Required = required;
    }

    public Expression Expression { get; }

    public string Alias { get; }

    public bool Required { get; }

    /// <inheritdoc />
    public override Type Type
        => Expression.Type;

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(Expression));

    public ProjectionExpression Update(Expression expression)
        => expression != Expression
            ? new ProjectionExpression(expression, Alias, Required)
            : this;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || (obj is ProjectionExpression projectionExpression
                   && Equals(projectionExpression)));

    private bool Equals(ProjectionExpression projectionExpression)
        => Alias == projectionExpression.Alias
           && Expression.Equals(projectionExpression.Expression)
           && Required == projectionExpression.Required;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Alias, Expression, Required);
}
