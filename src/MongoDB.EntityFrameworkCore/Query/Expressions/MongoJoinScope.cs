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

using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Metadata for a native <c>Join</c>/<c>LeftJoin</c> chain on this select: a later <c>Where</c>/<c>OrderBy</c>
/// may resolve members against the root scope only, and a later <c>Select</c> may resolve a whole-entity leaf
/// against any level (see <c>NativeTranslation.NativeJoinScopeTranslator.TryTranslateRootScopeOnly</c> and
/// <c>NativeTranslation.NativeJoinScopeProjectionBinder</c>). Built eagerly at join registration; confirming the
/// join (registering its <c>$lookup</c>, leaving <c>Fallback</c>) is a separate, deferred step at the consuming
/// <c>Select</c>.
/// </summary>
internal sealed class MongoJoinScope(IEntityType outerEntityType, IReadOnlyList<MongoJoinScopeLevel> levels)
{
    public IEntityType OuterEntityType { get; } = outerEntityType;

    /// <summary>One entry per join, root-most first.</summary>
    public IReadOnlyList<MongoJoinScopeLevel> Levels { get; } = levels;
}

/// <summary>One join level in a (possibly chained) native join scope.</summary>
internal sealed class MongoJoinScopeLevel(IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)
{
    public IEntityType InnerEntityType { get; } = innerEntityType;

    /// <summary>The <c>$lookup</c> alias (<c>joinInfo.Alias</c>) this level's inner-scope field refs are prefixed with.</summary>
    public string InnerPrefix { get; } = innerPrefix;

    /// <summary>Whether this level's join is left-outer (<c>LeftJoin</c>) or inner (<c>Join</c>).</summary>
    public bool IsLeftOuter { get; } = isLeftOuter;
}
