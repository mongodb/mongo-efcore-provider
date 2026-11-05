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

using System;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a reference to a named document field, identified by its
/// <see cref="IProperty"/> metadata and serialized element name.
/// </summary>
internal sealed class MongoFieldExpression : MongoExpression
{
    public MongoFieldExpression(IProperty property, string elementName, bool nullSafe = false)
    {
        Property = property;
        ElementName = elementName;
        NullSafe = nullSafe;
    }

    // 'new' hides the inherited static Expression.Property(...) factory.
    public new IProperty Property { get; }

    public string ElementName { get; }

    /// <summary>
    /// When <see langword="true"/>, the aggregation renderer wraps this field in <c>$ifNull</c> against <c>null</c>
    /// (as <see cref="MongoElementRefExpression.NullSafe"/> does). Needed for a field behind a left-outer
    /// <c>$lookup</c> + <c>$unwind</c>: an unmatched row's sub-document is missing, and <c>$expr</c>'s <c>$eq</c>/<c>$ne</c>
    /// don't treat missing as <c>null</c>. Set only for <c>==</c>/<c>!=</c> null comparisons.
    /// </summary>
    public bool NullSafe { get; }

    /// <inheritdoc />
    public override Type Type
        => Property.ClrType;
}
