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
/// A single cross-collection join registered on a <see cref="MongoQueryExpression"/>.
/// <para>
/// One entry per join, not per target entity type — otherwise two joins onto the same entity type
/// collapse into one and the flatten decision (<see cref="MongoQueryExpression.UsesDriverJoinFields"/>)
/// never fires for the second join. This also lets a self-referencing chain (e.g.
/// <c>Employee.Manager.Manager</c>) find the immediately-preceding hop by position rather than by
/// <see cref="IEntityType"/>, which can't distinguish repeat hops against the same entity type.
/// </para>
/// </summary>
internal sealed class JoinInfo(IEntityType innerEntityType, bool isLeftOuter)
{
    /// <summary>The entity type of the joined (inner) collection.</summary>
    public IEntityType InnerEntityType { get; } = innerEntityType;

    /// <summary>
    /// Whether the LINQ operator that introduced this join is left-outer (<c>LeftJoin</c>/<c>GroupJoin</c>).
    /// Recorded from the operator actually used, since that can disagree with <c>ForeignKey.IsRequired</c>.
    /// </summary>
    public bool IsLeftOuter { get; } = isLeftOuter;

    /// <summary>The navigation this join materializes, or <see langword="null"/> for a plain key-equality join.</summary>
    public INavigation? Navigation { get; set; }

    /// <summary>
    /// The field this join's document lands in when flattened: <c>_lookup_&lt;Navigation&gt;</c>, suffixed when an
    /// earlier join claimed the name. Two joins onto the same navigation are independent (cross product), so they
    /// cannot share one <c>$lookup</c> output field.
    /// </summary>
    public string Alias { get; set; } = "";

    /// <summary>
    /// The forced-unwind <c>$lookup</c> that flattens this join, or <see langword="null"/> when no navigation was
    /// resolved. Built per join and registered on the query expression only once flattening is triggered.
    /// </summary>
    public LookupExpression? Lookup { get; set; }

    /// <summary>
    /// Whether this join alone satisfies the native join-scope requirements (resolved navigation, not a left-outer
    /// collection nav, <c>$lookup</c> reproduces the key equality, neither side GroupBy/Distinct-sourced). A chain
    /// is eligible only when every join is. See <c>MongoSelectDefinition.JoinScope</c>.
    /// </summary>
    public bool IsNativelyEligible { get; set; }

    /// <summary>
    /// Whether this join is a left-outer join over a reference navigation, whose <c>$unwind</c>
    /// (<c>preserveNullAndEmptyArrays: true</c>) neither drops nor multiplies rows. <see langword="false"/> when
    /// no <see cref="Lookup"/> was resolved (fail closed). See <see cref="MongoQueryExpression.AreAllJoinsRowCountPreserving"/>.
    /// </summary>
    internal bool IsRowCountPreserving => IsLeftOuter && Lookup is { Navigation.IsCollection: false };
}
