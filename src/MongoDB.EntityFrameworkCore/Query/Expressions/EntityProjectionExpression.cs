// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore.Cosmos EntityProjectionExpression.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

internal sealed class EntityProjectionExpression : EntityTypedExpression, IPrintableExpression, IAccessExpression
{
    private readonly Dictionary<IProperty, IAccessExpression> _propertyExpressionsMap = new();
    private readonly Dictionary<INavigation, IAccessExpression> _navigationExpressionsMap = new();

    public EntityProjectionExpression(IEntityType entityType, Expression accessExpression)
        : base(entityType)
    {
        AccessExpression = accessExpression;
        Name = (accessExpression as IAccessExpression)?.Name;
    }

    public Expression AccessExpression { get; }

    public string? Name { get; }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression));

    public Expression Update(Expression accessExpression)
        => accessExpression != AccessExpression
            ? new EntityProjectionExpression(EntityType, accessExpression)
            : this;

    public Expression BindProperty(IProperty property)
    {
        if (!EntityType.IsAssignableFrom(property.DeclaringEntityType)
            && !property.DeclaringEntityType.IsAssignableFrom(EntityType))
        {
            throw new InvalidOperationException($"Unable to bind 'property' '{property.Name}' to an entity projection of '{EntityType.DisplayName()}'.");
        }

        return null!;
    }

    public Expression BindNavigation(INavigation navigation)
    {
        if (!EntityType.IsAssignableFrom(navigation.DeclaringEntityType)
            && !navigation.DeclaringEntityType.IsAssignableFrom(EntityType))
        {
            throw new InvalidOperationException($"Unable to bind 'navigation' '{navigation.Name}' to an entity projection of '{EntityType.DisplayName()}'.");
        }

        if (!_navigationExpressionsMap.TryGetValue(navigation, out var expression))
        {
            expression = navigation.IsCollection
                ? new ObjectArrayProjectionExpression(navigation, AccessExpression)
                : new EntityProjectionExpression(
                    navigation.TargetEntityType,
                    new ObjectAccessExpression(navigation, AccessExpression));

            _navigationExpressionsMap[navigation] = expression;
        }

        return (Expression)expression;
    }

    public Expression? BindMember(
        string name,
        Type entityType,
        out IPropertyBase? propertyBase)
        => BindMember(MemberIdentity.Create(name), entityType, out propertyBase);

    public Expression? BindMember(
        MemberInfo memberInfo,
        Type entityType,
        out IPropertyBase? propertyBase)
        => BindMember(MemberIdentity.Create(memberInfo), entityType, out propertyBase);

    private Expression? BindMember(MemberIdentity member, Type? entityClrType, out IPropertyBase? propertyBase)
    {
        var entityType = EntityType;
        if (entityClrType != null
            && !entityClrType.IsAssignableFrom(entityType.ClrType))
        {
            entityType = entityType.GetDerivedTypes().First(e => entityClrType.IsAssignableFrom(e.ClrType));
        }

        var property = member.MemberInfo == null
            ? entityType.FindProperty(member.Name)
            : entityType.FindProperty(member.MemberInfo);
        if (property != null)
        {
            propertyBase = property;
            return BindProperty(property);
        }

        var navigation = member.MemberInfo == null
            ? entityType.FindNavigation(member.Name)
            : entityType.FindNavigation(member.MemberInfo);
        if (navigation != null)
        {
            propertyBase = navigation;
            return BindNavigation(navigation);
        }

        // Entity member not found
        propertyBase = null;
        return null;
    }

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Visit(AccessExpression);

    public override bool Equals(object? obj)
        => obj != null
            && (ReferenceEquals(this, obj)
                || obj is EntityProjectionExpression entityProjectionExpression
                && Equals(entityProjectionExpression));

    private bool Equals(EntityProjectionExpression entityProjectionExpression)
        => Equals(EntityType, entityProjectionExpression.EntityType)
            && AccessExpression.Equals(entityProjectionExpression.AccessExpression);

    public override int GetHashCode()
        => HashCode.Combine(EntityType, AccessExpression);
}
