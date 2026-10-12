/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a reference to the root of the query from within a query expression tree.
/// </summary>
internal sealed class RootReferenceExpression : EntityTypedExpression, IAccessExpression
{
    public RootReferenceExpression(IEntityType entityType)
        : base(entityType)
    {
    }

    /// <summary>
    /// A reference to the root document of <paramref name="entityType"/> that is distinct from every other reference to it: for the
    /// elements of one particular array of another collection's entities. Two such arrays of the same type in one projection are
    /// otherwise structurally equal, and the shaper pipeline merges equal shapers into one variable that only the first array's
    /// scope declares.
    /// </summary>
    public RootReferenceExpression(IEntityType entityType, object identity)
        : base(entityType)
    {
        Identity = identity;
    }

    /// <summary>What tells this reference from another of the same entity type, or <see langword="null"/> for the plain reference.</summary>
    public object? Identity { get; }

    public override bool Equals(object? obj)
        => ReferenceEquals(this, obj)
           || (obj is RootReferenceExpression other && Equals(EntityType, other.EntityType) && Equals(Identity, other.Identity))
           || (Identity is null && obj is not RootReferenceExpression && base.Equals(obj));

    public override int GetHashCode()
        => Identity is null ? base.GetHashCode() : System.HashCode.Combine(base.GetHashCode(), Identity);

    public string? Name
    {
        get { return null; }
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;

    public override string ToString()
        => "bsonDoc";
}
