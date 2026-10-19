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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents an ordered list of independently-resolved candidate values — each element its own
/// <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> — used as
/// <see cref="MongoInExpression.Values"/> when a <c>Contains</c> collection is a <c>NewArrayInit</c> whose
/// elements are not all foldable into a single constant array or a single array-valued query parameter (e.g.
/// <c>new[] { prm1, prm2 }.Contains(...)</c> where <c>prm1</c>/<c>prm2</c> are separately closure-captured
/// locals that EF hoists as independently-named query parameters, rather than one array-typed parameter).
/// </summary>
internal sealed class MongoValueListExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoValueListExpression"/> wrapping <paramref name="elements"/>.
    /// </summary>
    /// <param name="elements">
    /// The per-element value nodes, each a <see cref="MongoConstantExpression"/> or
    /// <see cref="MongoParameterExpression"/>.
    /// </param>
    public MongoValueListExpression(IReadOnlyList<MongoExpression> elements)
    {
        Elements = elements;
    }

    /// <summary>The per-element value nodes.</summary>
    public IReadOnlyList<MongoExpression> Elements { get; }

    /// <inheritdoc />
    public override Type Type => typeof(object);
}
