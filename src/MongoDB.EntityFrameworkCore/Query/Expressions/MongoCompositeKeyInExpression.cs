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
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// <c>entities.Contains(e)</c> over a composite primary key: <c>{ $or: [ { "_id.K1": v1, "_id.K2": v2 }, ... ] }</c>, one
/// conjunction of key paths per listed entity (<c>$nor</c> when <see cref="Negated"/>).
/// </summary>
/// <remarks>
/// A per-field conjunction rather than an <c>_id: { $in: [documents] }</c>: a sub-document match is order-sensitive, and
/// would silently differ from driver-LINQ's per-field OR chain on documents written with another key order. The parameter's
/// length isn't known at compile time, so the clause array is built per execution (<c>CompositeKeyClauses</c>) and the
/// template stays compiled once.
/// </remarks>
internal sealed class MongoCompositeKeyInExpression : MongoExpression
{
    public MongoCompositeKeyInExpression(IReadOnlyList<IProperty> keyProperties, MongoExpression values, bool negated)
    {
        KeyProperties = keyProperties;
        Values = values;
        Negated = negated;
    }

    /// <summary>The composite key's properties, in key order.</summary>
    public IReadOnlyList<IProperty> KeyProperties { get; }

    /// <summary>
    /// A <see cref="MongoConstantExpression"/> holding the entity instances, or a <see cref="MongoParameterExpression"/>
    /// bound to the entity list per execution.
    /// </summary>
    public MongoExpression Values { get; }

    /// <summary>Whether the test is for the entity not being in the list.</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
