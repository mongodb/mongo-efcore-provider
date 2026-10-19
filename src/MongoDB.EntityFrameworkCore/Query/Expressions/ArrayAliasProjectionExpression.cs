// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An array of owned entities read back from a native <c>$project</c> OUTPUT ALIAS rather than from the
/// navigation's own document path — the alias-addressed sibling of <see cref="ObjectArrayProjectionExpression"/>.
/// </summary>
/// <remarks>
/// <para>
/// This node deliberately carries NO alias. The alias is resolved by
/// <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s <c>VisitBinary</c> from the
/// <see cref="ProjectionExpression"/> that the post-processor built from this projection's
/// <c>ProjectionMember</c> — the identical mechanism every scalar projection leaf uses. Carrying an alias
/// here as well would create a second, independently-derived copy of the same name and reintroduce exactly
/// the emit-side/shaper-side alias divergence documented in <c>Query/AGENTS.md</c>.
/// </para>
/// <para>
/// It is a separate type from <see cref="ObjectArrayProjectionExpression"/>, not a flag on it, because that
/// node's contract is "read this navigation at its containing element name": its <c>Name</c> is derived from
/// <c>GetContainingElementName()</c> and participates in its equality. Two nodes that address the same array
/// by different mechanisms must not compare equal — <c>_projectionBindings</c> is keyed on the node itself.
/// </para>
/// </remarks>
internal sealed class ArrayAliasProjectionExpression : Expression, IPrintableExpression, IArrayProjectionExpression
{
    public ArrayAliasProjectionExpression(
        INavigation navigation,
        Expression accessExpression,
        EntityProjectionExpression? innerProjection = null)
    {
        var targetType = navigation.TargetEntityType;
        Type = typeof(IEnumerable<>).MakeGenericType(targetType.ClrType);
        Navigation = navigation;
        AccessExpression = accessExpression;
        InnerProjection = innerProjection
                          ?? new EntityProjectionExpression(targetType, new RootReferenceExpression(targetType));
    }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type { get; }

    public INavigation Navigation { get; }

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
            ? new ArrayAliasProjectionExpression(Navigation, accessExpression, innerProjection)
            : this;

    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(ToString());

    public override string ToString()
        => $"{AccessExpression}[<alias>] /* {Navigation.Name} */";

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ArrayAliasProjectionExpression other && Equals(other));

    private bool Equals(ArrayAliasProjectionExpression other)
        => Navigation == other.Navigation
           && AccessExpression.Equals(other.AccessExpression)
           && InnerProjection.Equals(other.InnerProjection);

    public override int GetHashCode()
        => HashCode.Combine(Navigation, AccessExpression, InnerProjection);
}
