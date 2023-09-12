// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos ObjectAccessExpression.

using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

internal sealed class ObjectAccessExpression : Expression, IPrintableExpression, IAccessExpression
{
    public ObjectAccessExpression(INavigation navigation, Expression accessExpression)
    {
        Name = navigation.TargetEntityType.GetContainingElementName() ??
               throw new InvalidOperationException(
                   $"Navigation '{navigation.DeclaringEntityType.DisplayName()}.{navigation.Name}' doesn't point to an embedded entity.");

        Navigation = navigation;
        AccessExpression = accessExpression;
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type
        => Navigation.ClrType;

    public string Name { get; }

    public INavigation Navigation { get; }

    public Expression AccessExpression { get; }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression));

    public ObjectAccessExpression Update(Expression outerExpression)
        => outerExpression != AccessExpression
            ? new ObjectAccessExpression(Navigation, outerExpression)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    public override string ToString()
        => $"{AccessExpression}[\"{Name}\"]";

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ObjectAccessExpression objectAccessExpression
               && Equals(objectAccessExpression));

    private bool Equals(ObjectAccessExpression objectAccessExpression)
        => Navigation == objectAccessExpression.Navigation
           && AccessExpression.Equals(objectAccessExpression.AccessExpression);

    public override int GetHashCode()
        => HashCode.Combine(Navigation, AccessExpression);
}
