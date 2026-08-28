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

using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Records that a native single-level <c>Join</c>/<c>LeftJoin</c> was seen on this select whose subsequent
/// <c>Where</c>/<c>Select</c> may resolve member access against either side (<c>x.Outer.Foo</c> /
/// <c>x.Inner.Foo</c>) via the two-scope <see cref="NativeTranslation.MongoExpressionTranslator"/>. Recording
/// this is pure metadata — it does NOT register the join's <c>$lookup</c> on
/// <see cref="MongoQueryExpression"/> (see <c>AddLookup</c>); that stays deferred until a
/// <c>Where</c>/<c>Select</c> actually succeeds translating against this scope, so a query that ends up
/// falling back to driver-LINQ for an unrelated reason never has its <c>UsesDriverJoinFields</c> document
/// shape perturbed. See <c>docs/superpowers/specs/2026-08-27-native-join-translation-v2-design.md</c>.
/// </summary>
internal sealed class MongoJoinScope(
    IEntityType outerEntityType, IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)
{
    public IEntityType OuterEntityType { get; } = outerEntityType;

    public IEntityType InnerEntityType { get; } = innerEntityType;

    /// <summary>The <c>$lookup</c> alias (<c>joinInfo.Alias</c>) inner-scope field refs are prefixed with.</summary>
    public string InnerPrefix { get; } = innerPrefix;

    /// <summary>Whether this join is left-outer (<c>LeftJoin</c>) or inner (<c>Join</c>).</summary>
    public bool IsLeftOuter { get; } = isLeftOuter;
}
