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

    public string? Name
    {
        get { return null; }
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;

    public override string ToString()
        => "{root reference}";
}
