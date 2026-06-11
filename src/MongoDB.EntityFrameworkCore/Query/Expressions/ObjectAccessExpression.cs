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
    /// Create a <see cref="ObjectAccessExpression"/> for an embedded entity navigation.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> this object access relates to.</param>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">
    /// <see langword="true"/> if this object is required,
    /// <see langword="false"/> if it is optional.
    /// </param>
    /// <exception cref="InvalidOperationException"></exception>
    public ObjectAccessExpression(
        INavigation navigation,
        Expression accessExpression,
        bool required)
        : this(navigation, accessExpression, required,
            navigation.TargetEntityType.GetContainingElementName() ??
            throw new InvalidOperationException(
                $"Navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}' doesn't point to an embedded entity."))
    {
    }

    /// <summary>
    /// Create a <see cref="ObjectAccessExpression"/> with an explicit field name.
    /// Used for cross-collection $lookup results.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> this object access relates to.</param>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">Whether this object is required.</param>
    /// <param name="name">The explicit field name to access in the document.</param>
    public ObjectAccessExpression(
        INavigation navigation,
        Expression accessExpression,
        bool required,
        string name)
    {
        Name = name;
        Navigation = navigation;
        AccessExpression = accessExpression;
        Required = required;
    }

    /// <summary>
    /// Create a <see cref="ObjectAccessExpression"/> for a cross-collection join result
    /// where no navigation is defined (explicit Join).
    /// </summary>
    public ObjectAccessExpression(
        IEntityType entityType,
        Expression accessExpression,
        bool required,
        string name)
    {
        Name = name;
        EntityType = entityType;
        AccessExpression = accessExpression;
        Required = required;
    }

    internal IEntityType? EntityType { get; }

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    public override Type Type
        => Navigation?.ClrType ?? EntityType!.ClrType;

    public string Name { get; }

    public INavigation? Navigation { get; }

    public Expression AccessExpression { get; }

    public bool Required { get; }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression));

    public ObjectAccessExpression Update(Expression outerExpression)
        => outerExpression != AccessExpression
            ? (Navigation != null
                ? new ObjectAccessExpression(Navigation, outerExpression, Required)
                : new ObjectAccessExpression(EntityType!, outerExpression, Required, Name))
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
