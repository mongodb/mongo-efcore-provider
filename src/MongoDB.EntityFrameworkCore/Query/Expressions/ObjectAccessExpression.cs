// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos ObjectAccessExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents access to an object within the BsonDocument result tree.
/// </summary>
internal sealed class ObjectAccessExpression : Expression, IPrintableExpression, IAccessExpression
{
    /// <summary>
    /// Create a <see cref="ObjectAccessExpression"/>.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> this object access relates to.</param>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">
    /// <see langref="true"/> if this object is required,
    /// <see langref="false"/> if it is optional.
    /// </param>
    /// <exception cref="InvalidOperationException"></exception>
    public ObjectAccessExpression(
        INavigation navigation,
        Expression accessExpression,
        bool required)
    {
        Name = navigation.TargetEntityType.GetContainingElementName() ??
               throw new InvalidOperationException(
                   $"Navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}' doesn't point to an embedded entity.");

        Navigation = navigation;
        AccessExpression = accessExpression;
        Required = required;
    }

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    public override Type Type
        => Navigation.ClrType;

    public string Name { get; }

    public INavigation Navigation { get; }

    public Expression AccessExpression { get; }

    public bool Required { get; }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression));

    public ObjectAccessExpression Update(Expression outerExpression)
        => outerExpression != AccessExpression
            ? new ObjectAccessExpression(Navigation, outerExpression, Required)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    /// <inheritdoc />
    public override string ToString()
        => $"{AccessExpression}[\"{Name}\"]";

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || (obj is ObjectAccessExpression objectAccessExpression
                   && Equals(objectAccessExpression)));

    private bool Equals(ObjectAccessExpression objectAccessExpression)
        => Navigation == objectAccessExpression.Navigation
           && AccessExpression.Equals(objectAccessExpression.AccessExpression)
           && Required == objectAccessExpression.Required;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Navigation, AccessExpression, Required);
}
