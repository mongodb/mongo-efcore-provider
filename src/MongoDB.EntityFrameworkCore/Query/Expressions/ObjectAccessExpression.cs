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
    /// <summary>
    /// Create an <see cref="ObjectAccessExpression"/>.
    /// </summary>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">
    /// <see langword="true"/> if this object is required,
    /// <see langword="false"/> if it is optional.
    /// </param>
    /// <param name="name">The field name to access in the document.</param>
    protected ObjectAccessExpression(
        Expression accessExpression,
        bool required,
        string name)
    {
        AccessExpression = accessExpression;
        Required = required;
        Name = name;
    }

    /// <summary>
    /// The <see cref="INavigation"/> this object access relates to, or <see langword="null"/> when the
    /// access is not described by a navigation.
    /// </summary>
    public virtual INavigation? Navigation
        => null;

    /// <summary>
    /// The <see cref="IEntityType"/> this object access relates to when there is no navigation, otherwise
    /// <see langword="null"/>.
    /// </summary>
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

    /// <summary>
    /// Create a copy of this expression with the given parent access expression, or return this
    /// instance unchanged when the parent access expression is unchanged.
    /// </summary>
    /// <param name="outerExpression">The <see cref="Expression"/> of the parent containing the object.</param>
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
    /// Compare the members common to all <see cref="ObjectAccessExpression"/> subtypes. The caller has
    /// already established that <paramref name="objectAccessExpression"/> is of the same runtime type, so
    /// overrides can safely cast to add their distinguishing member.
    /// </summary>
    protected virtual bool Equals(ObjectAccessExpression objectAccessExpression)
        => Name == objectAccessExpression.Name
           && AccessExpression.Equals(objectAccessExpression.AccessExpression)
           && Required == objectAccessExpression.Required;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Name, AccessExpression, Required);
}
