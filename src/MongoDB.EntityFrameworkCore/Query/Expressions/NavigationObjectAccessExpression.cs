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
    /// <summary>Creates an access for an embedded entity navigation.</summary>
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

    /// <summary>Creates an access with an explicit field name, for cross-collection $lookup results.</summary>
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
