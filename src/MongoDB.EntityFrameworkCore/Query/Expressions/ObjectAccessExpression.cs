// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos ObjectAccessExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents access to an object within the BsonDocument result tree. The concrete subtype depends on
/// whether the access is described by an <see cref="INavigation"/>
/// (<see cref="NavigationObjectAccessExpression"/>) or by an <see cref="IEntityType"/> with no navigation,
/// as for an explicit cross-collection Join (<see cref="EntityTypeObjectAccessExpression"/>).
/// </summary>
internal abstract class ObjectAccessExpression : Expression, IPrintableExpression, IAccessExpression
{
    protected ObjectAccessExpression(
        Expression accessExpression,
        bool required,
        string name)
    {
        AccessExpression = accessExpression;
        Required = required;
        Name = name;
    }

    /// <summary>The navigation this access relates to, or <see langword="null"/> when none describes it.</summary>
    public virtual INavigation? Navigation
        => null;

    /// <summary>The entity type this access relates to when there is no navigation, otherwise <see langword="null"/>.</summary>
    internal virtual IEntityType? EntityType
        => null;

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    public abstract override Type Type { get; }

    public string Name { get; }

    public Expression AccessExpression { get; }

    public bool Required { get; }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression));

    /// <summary>Copies this expression with a new parent access expression, or returns it unchanged if equal.</summary>
    public abstract ObjectAccessExpression Update(Expression outerExpression);

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    /// <inheritdoc />
    public override string ToString()
        => $"{AccessExpression}[\"{Name}\"]";

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || (obj.GetType() == GetType()
                   && obj is ObjectAccessExpression objectAccessExpression
                   && Equals(objectAccessExpression)));

    /// <summary>
    /// Compares the members common to all subtypes; the caller has established the same runtime type, so overrides
    /// can safely cast.
    /// </summary>
    protected virtual bool Equals(ObjectAccessExpression objectAccessExpression)
        => Name == objectAccessExpression.Name
           && AccessExpression.Equals(objectAccessExpression.AccessExpression)
           && Required == objectAccessExpression.Required;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Name, AccessExpression, Required);
}
