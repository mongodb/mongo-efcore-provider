// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Derived from EFCore.Cosmos EntityProjectionExpression.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents where an EF-Entity will be projected from and how it is mapped.
/// </summary>
internal sealed class EntityProjectionExpression : EntityTypedExpression, IPrintableExpression, IAccessExpression
{
    private readonly Dictionary<INavigation, IAccessExpression> _navigationExpressionsMap = new();

    /// <summary>
    /// Create a <see cref="EntityProjectionExpression"/>.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> being projected.</param>
    /// <param name="parentAccessExpression">The <see cref="Expression"/> used to access the parent tree.</param>
    public EntityProjectionExpression(
        IEntityType entityType,
        Expression parentAccessExpression)
        : base(entityType)
    {
        ParentAccessExpression = parentAccessExpression;
        Name = (parentAccessExpression as IAccessExpression)?.Name;
    }

    /// <summary>
    /// The <see cref="Expression"/> used to access the parent for this entity.
    /// </summary>
    public Expression ParentAccessExpression { get; }

    /// <summary>
    /// The name or alias assigned to this projection.
    /// </summary>
    public string? Name { get; }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(ParentAccessExpression));

    public Expression Update(Expression accessExpression)
        => accessExpression != ParentAccessExpression
            ? new EntityProjectionExpression(EntityType, accessExpression)
            : this;

    private Expression BindProperty(IProperty property)
    {
        if (!EntityType.IsAssignableFrom(property.DeclaringEntityType)
            && !property.DeclaringEntityType.IsAssignableFrom(EntityType))
        {
            throw new InvalidOperationException(
                $"Unable to bind 'property' '{property.Name}' to an entity projection of '{EntityType.DisplayName()}'.");
        }

        return null!;
    }

    public Expression BindNavigation(INavigation navigation)
    {
        if (!EntityType.IsAssignableFrom(navigation.DeclaringEntityType)
            && !navigation.DeclaringEntityType.IsAssignableFrom(EntityType))
        {
            throw new InvalidOperationException(
                $"Unable to bind 'navigation' '{navigation.Name}' to an entity projection of '{EntityType.DisplayName()}'.");
        }

        if (!_navigationExpressionsMap.TryGetValue(navigation, out IAccessExpression? expression))
        {
            expression = navigation.IsCollection
                ? new ObjectArrayProjectionExpression(navigation, ParentAccessExpression)
                : new EntityProjectionExpression(
                    navigation.TargetEntityType,
                    new ObjectAccessExpression(navigation, ParentAccessExpression, navigation.ForeignKey.IsRequiredDependent));

            _navigationExpressionsMap[navigation] = expression;
        }

        return (Expression)expression;
    }

    /// <summary>
    /// Bind a member for this entity projection by name.
    /// </summary>
    /// <param name="name">The name of the member to be bound.</param>
    /// <param name="entityType">The <see cref="IEntityType"/> being bound to.</param>
    /// <param name="propertyBase">An <see cref="IPropertyBase"/> that may be returned if the member is bound.</param>
    /// <returns>An <see cref="Expression"/> containing the member binding expression.</returns>
    public Expression? BindMember(
        string name,
        Type entityType,
        out IPropertyBase? propertyBase)
        => BindMember(MemberIdentity.Create(name), entityType, out propertyBase);

    /// <summary>
    /// Bind a member for this entity projection by <see cref="MemberInfo"/>.
    /// </summary>
    /// <param name="memberInfo"></param>
    /// <param name="entityType">The <see cref="IEntityType"/> being bound to.</param>
    /// <param name="propertyBase">An <see cref="IPropertyBase"/> that may be returned if the member is bound.</param>
    /// <returns>An <see cref="Expression"/> containing the member binding expression.</returns>
    public Expression? BindMember(
        MemberInfo memberInfo,
        Type entityType,
        out IPropertyBase? propertyBase)
        => BindMember(MemberIdentity.Create(memberInfo), entityType, out propertyBase);

    private Expression? BindMember(
        MemberIdentity member,
        Type? entityClrType,
        out IPropertyBase? propertyBase)
    {
        IEntityType entityType = EntityType;
        if (entityClrType != null
            && !entityClrType.IsAssignableFrom(entityType.ClrType))
        {
            entityType = entityType.GetDerivedTypes().First(e => entityClrType.IsAssignableFrom(e.ClrType));
        }

        IProperty? property = member.MemberInfo == null
            ? entityType.FindProperty(member.Name)
            : entityType.FindProperty(member.MemberInfo);

        if (property != null)
        {
            propertyBase = property;
            return BindProperty(property);
        }

        INavigation? navigation = member.MemberInfo == null
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

    /// <inheritdoc />
    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Visit(ParentAccessExpression);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || (obj is EntityProjectionExpression entityProjectionExpression
                   && Equals(entityProjectionExpression)));

    private bool Equals(EntityProjectionExpression entityProjectionExpression)
        => Equals(EntityType, entityProjectionExpression.EntityType)
           && ParentAccessExpression.Equals(entityProjectionExpression.ParentAccessExpression);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(EntityType, ParentAccessExpression);
}
