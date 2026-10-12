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

/// <summary>What <see cref="MongoFieldPresenceExpression"/> tests about a stored element.</summary>
internal enum MongoFieldPresence
{
    /// <summary><c>Mql.Exists</c>: the element is present (null counts as present).</summary>
    Exists,

    /// <summary><c>Mql.IsMissing</c>: the element is absent.</summary>
    Missing,

    /// <summary><c>Mql.IsNullOrMissing</c>: the element is absent or BSON null.</summary>
    NullOrMissing,

    /// <summary>The complement of <see cref="NullOrMissing"/>: present and not BSON null.</summary>
    NotNullOrMissing
}

/// <summary>
/// <c>Mql.Exists</c>, <c>Mql.IsMissing</c> and <c>Mql.IsNullOrMissing</c> over a stored element, addressed by its dotted
/// document path: whether the element is present, absent, or null-or-absent.
/// </summary>
/// <remarks>
/// A sealed sibling type rather than a flag on a comparison, since it needs different handling at several sites.
/// Negation is an exact complement (<see cref="Negate"/>). The query dialect renders <c>{ path: { $exists: b } }</c>
/// and <c>{ path: null }</c>; the latter also matches an array that CONTAINS null, so a null-kind over a path that
/// may be an array is not query-dialect renderable (<see cref="PathMayBeArray"/>) and renders through <c>$expr</c>.
/// </remarks>
internal sealed class MongoFieldPresenceExpression : MongoExpression
{
    public MongoFieldPresenceExpression(string path, MongoFieldPresence kind, bool pathMayBeArray)
    {
        Path = path;
        Kind = kind;
        PathMayBeArray = pathMayBeArray;
    }

    /// <summary>The element's dotted document path.</summary>
    public string Path { get; }

    /// <summary>What is tested.</summary>
    public MongoFieldPresence Kind { get; }

    /// <summary>Whether the element may hold an array (a primitive collection or an owned collection).</summary>
    public bool PathMayBeArray { get; }

    /// <summary>Whether the kind tests for null as well as absence.</summary>
    public bool TestsNull => Kind is MongoFieldPresence.NullOrMissing or MongoFieldPresence.NotNullOrMissing;

    /// <summary>The exact logical complement.</summary>
    public MongoFieldPresenceExpression Negate()
        => new(
            Path,
            Kind switch
            {
                MongoFieldPresence.Exists => MongoFieldPresence.Missing,
                MongoFieldPresence.Missing => MongoFieldPresence.Exists,
                MongoFieldPresence.NullOrMissing => MongoFieldPresence.NotNullOrMissing,
                _ => MongoFieldPresence.NullOrMissing
            },
            PathMayBeArray);

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
