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
/// Tests whether a single-level reference-<c>Include</c>'s <c>$lookup</c> alias field is present
/// (<c>e.Manager != null</c>) or absent (<c>e.Manager == null</c>), post-<c>$unwind</c>.
/// </summary>
/// <remarks>
/// Not a <see cref="MongoFieldExpression"/>: <see cref="LookupAlias"/> names a synthetic field filled by
/// <c>$lookup</c>+<c>$unwind</c> (<c>preserveNullAndEmptyArrays: true</c>), not a stored property. Renders in both
/// dialects. Produced only for exactly <c>ti.Inner == null</c>/<c>!= null</c> and never nested under <c>Not</c>, a
/// quantifier, or <c>$elemMatch</c>, so the negator and <c>IsQueryDialectRenderable</c> fail closed on it.
/// </remarks>
internal sealed class MongoLookupNullCheckExpression(string lookupAlias, bool isNotNull) : MongoExpression
{
    public string LookupAlias { get; } = lookupAlias;

    /// <summary><see langword="true"/> for <c>!= null</c>, <see langword="false"/> for <c>== null</c>.</summary>
    public bool IsNotNull { get; } = isNotNull;

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
