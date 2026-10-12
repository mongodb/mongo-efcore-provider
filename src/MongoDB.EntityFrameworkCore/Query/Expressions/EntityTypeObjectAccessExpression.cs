// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos ObjectAccessExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents access to an object within the BsonDocument result tree described by an
/// <see cref="IEntityType"/> with no navigation, as for a cross-collection join result where no
/// navigation is defined (explicit Join).
/// </summary>
internal sealed class EntityTypeObjectAccessExpression : ObjectAccessExpression
{
    public EntityTypeObjectAccessExpression(
        IEntityType entityType,
        Expression accessExpression,
        bool required,
        string name)
        : base(accessExpression, required, name)
    {
        EntityType = entityType;
    }

    /// <inheritdoc />
    internal override IEntityType EntityType { get; }

    /// <inheritdoc />
    public override Type Type
        => EntityType.ClrType;

    /// <inheritdoc />
    public override ObjectAccessExpression Update(Expression outerExpression)
        => outerExpression != AccessExpression
            ? new EntityTypeObjectAccessExpression(EntityType, outerExpression, Required, Name)
            : this;

    /// <inheritdoc />
    protected override bool Equals(ObjectAccessExpression objectAccessExpression)
        => base.Equals(objectAccessExpression)
           && Equals(EntityType, ((EntityTypeObjectAccessExpression)objectAccessExpression).EntityType);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), EntityType);
}
