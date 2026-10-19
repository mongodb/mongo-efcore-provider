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
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A literal nested sub-document built at projection time from a CLR construction
/// (<c>new Book { Id = e.Id, Title = e.Title }</c>) over root-relative fields; distinct from an owned
/// navigation's stored sub-document (<see cref="MongoElementRefExpression"/>).
/// </summary>
/// <remarks>
/// Renders as an inline document value (<c>{Book: {Id: "$Id", ...}}</c>) keyed by <see cref="Members"/>' names.
/// <see cref="OriginalExpression"/> is used only to rebuild the CLR object on read; it is never re-translated.
/// </remarks>
internal sealed class MongoDocumentConstructionExpression : MongoExpression
{
    public MongoDocumentConstructionExpression(
        Expression originalExpression,
        IReadOnlyList<(string MemberName, MongoExpression Value)> members)
    {
        OriginalExpression = originalExpression;
        Members = members;
    }

    /// <summary>
    /// The original <see cref="NewExpression"/> or <see cref="MemberInitExpression"/>, used to rebuild the CLR
    /// object once member values are read back.
    /// </summary>
    public Expression OriginalExpression { get; }

    /// <summary>
    /// Member names paired with their translated values, in the original construction's declaration order.
    /// </summary>
    public IReadOnlyList<(string MemberName, MongoExpression Value)> Members { get; }

    /// <inheritdoc />
    public override Type Type
        => OriginalExpression.Type;
}
