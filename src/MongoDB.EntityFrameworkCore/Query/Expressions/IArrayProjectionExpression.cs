// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An array of entities as read by <see cref="Visitors.MongoProjectionBindingRemovingExpressionVisitor"/>'s
/// <c>CollectionShaperExpression</c> case: <see cref="ObjectArrayProjectionExpression"/> reads it at the
/// navigation's document path, <see cref="ArrayAliasProjectionExpression"/> at a <c>$project</c> alias.
/// <para>
/// Implementers must also derive from <see cref="Expression"/>: the visitor casts the node to key
/// <c>_projectionBindings</c>, so any other implementer throws <see cref="System.InvalidCastException"/> at run time.
/// </para>
/// </summary>
internal interface IArrayProjectionExpression
{
    /// <summary>The collection navigation this array materializes into.</summary>
    INavigation Navigation { get; }

    /// <summary>Access to the document that owns the array; used for the owned element's owner-key read.</summary>
    Expression AccessExpression { get; }

    /// <summary>The per-element entity projection.</summary>
    EntityProjectionExpression InnerProjection { get; }

    /// <summary>
    /// The BSON element name of the array, or <see langword="null"/> when it is addressed by a projection alias
    /// (the caller must then resolve the alias from the owning <see cref="ProjectionExpression"/>).
    /// </summary>
    string? ArrayFieldName { get; }
}
