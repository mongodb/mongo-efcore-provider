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
/// Represents a native <c>{ $size: … }</c> aggregation expression over a named array field, identified by its
/// dotted document path.
/// </summary>
/// <remarks>
/// <para>
/// Two uses, distinguished by <see cref="NullSafe"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// A projected collection-navigation <c>Count</c> (<c>select new { ..., OrderCount = c.Orders.Count }</c>),
/// where <see cref="FieldName"/> is the synthetic <c>_lookup_&lt;Nav&gt;</c> array field written by the matching
/// <see cref="LookupExpression"/>. A <c>$lookup</c> always writes an array, so <see cref="NullSafe"/> is
/// <see langword="false"/> and the rendering is the plain <c>{ $size: "$path" }</c>.
/// </item>
/// <item>
/// An OWNED (embedded) collection's element count used in a predicate (<c>Where(b =&gt; b.Posts.Count &gt; 2)</c>),
/// where <see cref="FieldName"/> is the embedded array's dotted path. An embedded array can be MISSING or
/// explicitly BSON <c>null</c>, and <c>$size</c> against either is a hard server error that aborts the whole
/// aggregate — so <see cref="NullSafe"/> is <see langword="true"/> and the rendering wraps the path in
/// <c>$ifNull</c>, mapping both states to <c>[]</c> (count 0, which is what LINQ answers for a missing embedded
/// array).
/// </item>
/// </list>
/// <para>
/// This does not wrap a <see cref="MongoFieldExpression"/> because that node requires a backing
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>, and neither an array navigation nor a
/// <c>$lookup</c> alias has one.
/// </para>
/// </remarks>
internal sealed class MongoSizeExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoSizeExpression"/> over the named array field.
    /// </summary>
    /// <param name="fieldName">The array's dotted document path (e.g. <c>_lookup_Orders</c>, <c>Posts</c>).</param>
    /// <param name="type">The CLR type of the resulting count (typically <see cref="int"/> or <see cref="long"/>).</param>
    /// <param name="nullSafe">
    /// <see langword="true"/> to render <c>{ $size: { $ifNull: [ "$path", [] ] } }</c> — required for an
    /// embedded array, which may be missing or explicitly null. <see langword="false"/> (the default) renders
    /// the plain <c>{ $size: "$path" }</c>, preserving the emitted MQL of the projected-<c>Count</c> path.
    /// </param>
    public MongoSizeExpression(string fieldName, Type type, bool nullSafe = false)
    {
        FieldName = fieldName;
        Type = type;
        NullSafe = nullSafe;
    }

    /// <summary>The array's dotted document path this <c>$size</c> is computed over.</summary>
    public string FieldName { get; }

    /// <summary>
    /// Whether the array path is wrapped in <c>$ifNull</c> so a missing or explicitly-null array counts as
    /// empty instead of aborting the aggregate. See the class remarks.
    /// </summary>
    public bool NullSafe { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
