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
/// Represents a LITERAL nested sub-document built, at projection time, from a fresh CLR object construction
/// (<c>new Book { Id = e.Id, Title = e.Title }</c>) whose own member values are plain root-relative fields —
/// as opposed to an owned navigation's ALREADY-STORED sub-document (<see cref="MongoElementRefExpression"/>,
/// EF-441), which this node is deliberately NOT a replacement for.
/// </summary>
/// <remarks>
/// <para>
/// EF-447. Renders as an inline BSON document whose keys are <see cref="Members"/>' names and whose values are
/// each member's own rendered <see cref="MongoExpression"/> (see <c>MongoAggregationExpressionRenderer</c>) — a
/// <c>$project</c> stage can legally emit a nested document VALUE this way (<c>{Book: {Id: "$Id", ...}}</c>),
/// distinct from a dotted OUTPUT KEY.
/// </para>
/// <para>
/// <see cref="OriginalExpression"/> is carried purely for MATERIALIZATION — rebuilding the CLR object
/// (<see cref="Expression.New(System.Reflection.ConstructorInfo)"/>/<see cref="MemberInitExpression"/>) once
/// each member's value has been read back off the result document. It is never re-translated; all translation
/// happened once, into <see cref="Members"/>, at emit time.
/// </para>
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
    /// The original <see cref="NewExpression"/> or <see cref="MemberInitExpression"/> this node was translated
    /// from — kept only so the read side can rebuild the same CLR construction (constructor/member set) once it
    /// has read each member's value back off the result document.
    /// </summary>
    public Expression OriginalExpression { get; }

    /// <summary>
    /// The leaf's own members, in the ORIGINAL construction's declaration order — each a member name paired
    /// with the natively-translated <see cref="MongoExpression"/> that produces its value.
    /// </summary>
    public IReadOnlyList<(string MemberName, MongoExpression Value)> Members { get; }

    /// <inheritdoc />
    public override Type Type
        => OriginalExpression.Type;
}
