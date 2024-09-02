// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos ObjectArrayProjectionExpression.cs

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

internal sealed class ObjectArrayProjectionExpression : Expression, IPrintableExpression, IAccessExpression
{
    public ObjectArrayProjectionExpression(
        INavigation navigation,
        Expression accessExpression,
        EntityProjectionExpression? innerProjection = null)
    {
        var targetType = navigation.TargetEntityType;
        Type = typeof(IEnumerable<>).MakeGenericType(targetType.ClrType);

        Name = targetType.GetContainingElementName()
               ?? throw new InvalidOperationException(
                   $"Navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}' doesn't point to an embedded entity.");

        Navigation = navigation;
        AccessExpression = accessExpression;
        InnerProjection = innerProjection
                          ?? new EntityProjectionExpression(
                              targetType,
                              new RootReferenceExpression(targetType));
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type { get; }

    public string? Name { get; }

    public INavigation Navigation { get; }

    public Expression AccessExpression { get; }

    public EntityProjectionExpression InnerProjection { get; }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var accessExpression = visitor.Visit(AccessExpression);
        var innerProjection = visitor.Visit(InnerProjection);

        return Update(accessExpression, (EntityProjectionExpression)innerProjection);
    }

    public ObjectArrayProjectionExpression Update(
        Expression accessExpression,
        EntityProjectionExpression innerProjection)
        => accessExpression != AccessExpression || innerProjection != InnerProjection
            ? new ObjectArrayProjectionExpression(Navigation, accessExpression, innerProjection)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    public override string ToString()
        => $"{AccessExpression}[\"{Name}\"]";

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ObjectArrayProjectionExpression arrayProjectionExpression
               && Equals(arrayProjectionExpression));

    private bool Equals(ObjectArrayProjectionExpression objectArrayProjectionExpression)
        => AccessExpression.Equals(objectArrayProjectionExpression.AccessExpression)
           && InnerProjection.Equals(objectArrayProjectionExpression.InnerProjection);

    public override int GetHashCode()
        => HashCode.Combine(AccessExpression, InnerProjection);
}
