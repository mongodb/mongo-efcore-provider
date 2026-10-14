// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos ObjectAccessExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents access to an object within the BsonDocument result tree described by an <see cref="INavigation"/>.
/// </summary>
internal sealed class NavigationObjectAccessExpression : ObjectAccessExpression
{
    /// <summary>
    /// Create a <see cref="NavigationObjectAccessExpression"/> for an embedded entity navigation.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> this object access relates to.</param>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">
    /// <see langword="true"/> if this object is required,
    /// <see langword="false"/> if it is optional.
    /// </param>
    /// <exception cref="InvalidOperationException"></exception>
    public NavigationObjectAccessExpression(
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
    /// Create a <see cref="NavigationObjectAccessExpression"/> with an explicit field name.
    /// Used for cross-collection $lookup results.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> this object access relates to.</param>
    /// <param name="accessExpression">The <see cref="Expression"/> of the parent containing the object.</param>
    /// <param name="required">Whether this object is required.</param>
    /// <param name="name">The explicit field name to access in the document.</param>
    public NavigationObjectAccessExpression(
        INavigation navigation,
        Expression accessExpression,
        bool required,
        string name)
        : base(accessExpression, required, name)
    {
        Navigation = navigation;
    }

    /// <inheritdoc />
    public override INavigation Navigation { get; }

    /// <inheritdoc />
    public override Type Type
        => Navigation.ClrType;

    /// <inheritdoc />
    public override ObjectAccessExpression Update(Expression outerExpression)
        => outerExpression != AccessExpression
            ? new NavigationObjectAccessExpression(Navigation, outerExpression, Required, Name)
            : this;

    /// <inheritdoc />
    protected override bool Equals(ObjectAccessExpression objectAccessExpression)
        => base.Equals(objectAccessExpression)
           && Navigation == ((NavigationObjectAccessExpression)objectAccessExpression).Navigation;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), Navigation);
}
