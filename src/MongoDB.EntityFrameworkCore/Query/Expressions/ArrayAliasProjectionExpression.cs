// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An array of owned entities read back from a native <c>$project</c> output alias rather than from the
/// navigation's own document path — the alias-addressed sibling of <see cref="ObjectArrayProjectionExpression"/>.
/// </summary>
/// <remarks>
/// <para>
/// Carries no alias of its own: <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/> resolves it
/// from the <see cref="ProjectionExpression"/> built from the <c>ProjectionMember</c>, like any scalar leaf. A
/// second copy here could diverge from the emitted name.
/// </para>
/// <para>
/// A separate type rather than a flag on <see cref="ObjectArrayProjectionExpression"/>, whose <c>Name</c>
/// (from <c>GetContainingElementName()</c>) participates in equality; <c>_projectionBindings</c> is keyed on the
/// node, so nodes addressing the same array differently must not compare equal.
/// </para>
/// </remarks>
internal sealed class ArrayAliasProjectionExpression : Expression, IPrintableExpression, IArrayProjectionExpression
{
    public ArrayAliasProjectionExpression(
        INavigation navigation,
        Expression accessExpression,
        EntityProjectionExpression? innerProjection = null)
        : this(navigation.TargetEntityType, navigation, accessExpression, innerProjection, identity: null)
    {
    }

    /// <summary>
    /// An array of <paramref name="targetType"/> entities that materializes into no navigation: a filtered, ordered or navigation-less
    /// list read from a projected alias (<see cref="Navigation"/> is <see langword="null"/>).
    /// </summary>
    /// <param name="targetType">The element entity type.</param>
    /// <param name="accessExpression">Access to the document that holds the array.</param>
    /// <param name="identity">
    /// What tells this array from another of the same element type in the same projection (its projection member). Nothing else
    /// does: without a navigation two such arrays are otherwise equal, so the shaper's bindings, which are keyed on this node,
    /// would conflate them.
    /// </param>
    /// <param name="innerProjection">The per-element projection, or <see langword="null"/> for a fresh one.</param>
    public ArrayAliasProjectionExpression(
        IEntityType targetType,
        Expression accessExpression,
        object identity,
        EntityProjectionExpression? innerProjection = null)
        : this(targetType, null, accessExpression, innerProjection, identity)
    {
    }

    private ArrayAliasProjectionExpression(
        IEntityType targetType,
        INavigation? navigation,
        Expression accessExpression,
        EntityProjectionExpression? innerProjection,
        object? identity)
    {
        TargetEntityType = targetType;
        Identity = identity;
        Type = typeof(IEnumerable<>).MakeGenericType(targetType.ClrType);
        Navigation = navigation;
        AccessExpression = accessExpression;
        InnerProjection = innerProjection
                          ?? new EntityProjectionExpression(
                              targetType, identity is null ? new RootReferenceExpression(targetType) : new RootReferenceExpression(targetType, identity));
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type { get; }

    public IEntityType TargetEntityType { get; }

    /// <summary>The projection member that tells a navigation-less array from another of the same type, or <see langword="null"/>.</summary>
    public object? Identity { get; }

    /// <summary>The collection navigation this array materializes into, or <see langword="null"/> for none.</summary>
    public INavigation? Navigation { get; }

    IEntityType? IArrayProjectionExpression.OwnerEntityType => Navigation?.DeclaringEntityType;

    public Expression AccessExpression { get; }

    public EntityProjectionExpression InnerProjection { get; }

    /// <inheritdoc />
    /// <remarks>Always <see langword="null"/>: this array is addressed by projection alias.</remarks>
    public string? ArrayFieldName => null;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Update(visitor.Visit(AccessExpression), (EntityProjectionExpression)visitor.Visit(InnerProjection));

    public ArrayAliasProjectionExpression Update(
        Expression accessExpression,
        EntityProjectionExpression innerProjection)
        => accessExpression != AccessExpression || innerProjection != InnerProjection
            ? new ArrayAliasProjectionExpression(TargetEntityType, Navigation, accessExpression, innerProjection, Identity)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    public override string ToString()
        => $"{AccessExpression}[<alias>] /* {Navigation?.Name ?? TargetEntityType.DisplayName()} */";

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ArrayAliasProjectionExpression other && Equals(other));

    private bool Equals(ArrayAliasProjectionExpression other)
        => TargetEntityType == other.TargetEntityType
           && Navigation == other.Navigation
           && Equals(Identity, other.Identity)
           && AccessExpression.Equals(other.AccessExpression)
           && InnerProjection.Equals(other.InnerProjection);

    public override int GetHashCode()
        => HashCode.Combine(TargetEntityType, Navigation, Identity, AccessExpression, InnerProjection);
}
