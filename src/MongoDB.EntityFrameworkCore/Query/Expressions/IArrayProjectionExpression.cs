// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// What <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s
/// <c>CollectionShaperExpression</c> case needs from a node describing an array of entities, regardless of
/// how that array is addressed in the document it is read from. Two implementations:
/// <see cref="ObjectArrayProjectionExpression"/> reads the array at the navigation's own document path;
/// <see cref="ArrayAliasProjectionExpression"/> reads it at a <c>$project</c> output alias.
/// <para>
/// <b>Every implementer MUST also derive from <see cref="Expression"/>.</b> An interface cannot express that
/// (it cannot extend a class), but the requirement is real: the consuming visitor keys its
/// <c>_projectionBindings</c> dictionary on the node itself and therefore casts, <c>(Expression)arrayProjection</c>
/// — see <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s
/// <c>CollectionShaperExpression</c> case. A non-<see cref="Expression"/> implementer would compile fine and then
/// fail at run time with <see cref="System.InvalidCastException"/>, so the constraint is stated here rather than
/// enforced by the compiler. Both current implementations satisfy it.
/// </para>
/// </summary>
internal interface IArrayProjectionExpression
{
    /// <summary>The collection navigation this array materializes into.</summary>
    INavigation Navigation { get; }

    /// <summary>Access to the document that OWNS the array — used for the owned element's owner-key read.</summary>
    Expression AccessExpression { get; }

    /// <summary>The per-element entity projection.</summary>
    EntityProjectionExpression InnerProjection { get; }

    /// <summary>
    /// The BSON element name the array sits at, or <see langword="null"/> when the array is addressed by a
    /// projection alias instead. A <see langword="null"/> here means the caller must already have resolved an
    /// alias from the owning <see cref="ProjectionExpression"/>; see the <c>??=</c> in
    /// <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s <c>VisitBinary</c>.
    /// </summary>
    string? ArrayFieldName { get; }
}
