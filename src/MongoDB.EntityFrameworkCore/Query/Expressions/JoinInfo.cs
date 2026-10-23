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
    /// Whether the LINQ operator that introduced this join is left-outer (<c>LeftJoin</c>/<c>GroupJoin</c>)
    /// or inner (<c>Join</c>, including EF's lowering of a REQUIRED reference navigation). Recorded from
    /// the operator actually used, since that can disagree with <c>ForeignKey.IsRequired</c>.
    /// </summary>
    public bool IsLeftOuter { get; } = isLeftOuter;

    /// <summary>
    /// The navigation this join materializes, once resolved from the join's outer key selector, or
    /// <see langword="null"/> for a key-equality join with no corresponding navigation.
    /// </summary>
    public INavigation? Navigation { get; set; }

    /// <summary>
    /// The field this join's document lands in when the query is flattened — <c>_lookup_&lt;Navigation&gt;</c>,
    /// suffixed when an earlier join already claimed that name. Two joins onto the same navigation are two
    /// independent joins (a cross product, per LINQ semantics), so they cannot share one <c>$lookup</c>
    /// output field. Both the lookup's <c>as</c> and the projection that reads it back come from here.
    /// </summary>
    public string Alias { get; set; } = "";

    /// <summary>
    /// The forced-unwind <c>$lookup</c> that flattens this join to its own root-level
    /// <c>_lookup_&lt;Navigation&gt;</c> field, or <see langword="null"/> when no navigation was resolved
    /// and so no lookup can be emitted. Built per join and registered on the query expression only once
    /// flattening is triggered, so a flattening join never has to re-derive a prior join's lookup by
    /// target type alone.
    /// </summary>
    public LookupExpression? Lookup { get; set; }

    /// <summary>
    /// Whether this join alone satisfies the native join-scope requirements; a chain is eligible only when every join is.
    /// </summary>
    public bool IsNativelyEligible { get; set; }

    /// <summary>
    /// Whether this join is a left-outer join over a reference navigation, which neither drops nor multiplies rows.
    /// <see langword="false"/> when no <see cref="Lookup"/> was resolved (fail closed).
    /// </summary>
    internal bool IsRowCountPreserving => IsLeftOuter && Lookup is { Navigation.IsCollection: false };

    /// <summary>
    /// Whether paging written BEFORE this join must stay ahead of its <c>$lookup</c> rather than be deferred past it:
    /// the join can multiply rows (a collection or navigation-less join) or drop them (an inner join onto an optional
    /// or principal-side reference). Deferred, it would page the joined rows, not the outer rows.
    /// </summary>
    /// <remarks>
    /// The one inner join that may still defer is a required dependent-side reference: only dangling data can drop a
    /// row, an accepted divergence (pinned by
    /// <c>Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly</c>).
    /// </remarks>
    internal bool PagingBeforeItMustStayAhead
        // A filtered inner drops rows however the foreign key is declared.
        => Lookup is { HasInnerPredicate: true }
           || Lookup?.Navigation is not { IsCollection: false } navigation
           || (!IsLeftOuter && !(navigation.IsOnDependent && navigation.ForeignKey.IsRequired));
}
