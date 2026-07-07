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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a collection-membership test to determine whether a field's value is one of a set of
/// candidate values, optionally negated.
/// </summary>
internal sealed class MongoInExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoInExpression"/>.
    /// </summary>
    /// <param name="field">The document field being tested for membership.</param>
    /// <param name="values">The candidate values to test against.</param>
    /// <param name="negated"><see langword="true"/> for negated membership (the value is not in the set).</param>
    public MongoInExpression(MongoFieldExpression field, MongoExpression values, bool negated)
    {
        Field = field;
        Values = values;
        Negated = negated;
    }

    /// <summary>The document field being tested for membership.</summary>
    // 'new' hides the inherited Expression.Field(...) method; used for semantic clarity.
    public new MongoFieldExpression Field { get; }

    /// <summary>The candidate values to test against.</summary>
    public MongoExpression Values { get; }

    /// <summary><see langword="true"/> for negated membership (the value is not in the set).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
