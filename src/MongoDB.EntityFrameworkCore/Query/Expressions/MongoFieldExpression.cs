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
    /// <summary>
    /// Creates a <see cref="MongoFieldExpression"/> for the given property.
    /// </summary>
    /// <param name="property">The EF Core <see cref="IProperty"/> this field corresponds to.</param>
    /// <param name="elementName">The document element name used in the document.</param>
    /// <param name="nullSafe">See <see cref="NullSafe"/>.</param>
    public MongoFieldExpression(IProperty property, string elementName, bool nullSafe = false)
    {
        Property = property;
        ElementName = elementName;
        NullSafe = nullSafe;
    }

    /// <summary>The EF Core property metadata for this field.</summary>
    // 'new' hides the inherited static Expression.Property(...) factory; the member name is spec-mandated.
    public new IProperty Property { get; }

    /// <summary>The document element name for this field in the document.</summary>
    public string ElementName { get; }

    /// <summary>
    /// When <see langword="true"/>, the aggregation-expression renderer wraps this field reference in
    /// <c>$ifNull</c> against a literal <c>null</c> before use — mirroring
    /// <see cref="MongoElementRefExpression.NullSafe"/>'s own precedent and reasoning. Needed for a field
    /// reached through a left-outer join/lookup's Inner side (e.g. <c>o.Customer.CustomerID</c> after
    /// <c>Orders.Include(o =&gt; o.Customer)</c>'s <c>$lookup</c> + <c>$unwind(preserveNullAndEmptyArrays:
    /// true)</c>): an unmatched row's <c>_lookup_Customer</c> sub-document is entirely MISSING (not an
    /// explicit BSON null), so <c>$_lookup_Customer.CustomerID</c> is itself missing — and <c>$expr</c>'s
    /// <c>$ne</c>/<c>$eq</c> do NOT treat a missing operand the same as an explicit <c>null</c> the way the
    /// ordinary query dialect's <c>{field: null}</c> does. Set only by
    /// <c>MongoExpressionTranslator.TranslateComparisonCore</c> (private) when comparing such a field to a
    /// literal null via <c>==</c>/<c>!=</c>; every other caller leaves this <see langword="false"/>, so their
    /// emitted MQL is unaffected.
    /// </summary>
    public bool NullSafe { get; }

    /// <inheritdoc />
    public override Type Type
        => Property.ClrType;
}
