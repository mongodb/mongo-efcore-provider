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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Compiles cross-collection <c>Include</c> sub-queries (EF-117).
/// </summary>
/// <remarks>
/// <para>
/// For each <c>IncludeExpression</c> whose navigation crosses collection boundaries
/// (<see cref="MongoNavigationExtensions.IsEmbedded"/> returns <see langword="false"/>),
/// this type will produce:
/// </para>
/// <list type="bullet">
///   <item>a parameterized <see cref="MongoExecutableQuery"/> rooted at the related entity's
///   collection, with <c>$match</c> keyed off the principal's PK / FK values;</item>
///   <item>a compiled key-extractor reading those values from the materialized principal;</item>
///   <item>a compiled shaper for the related entity (re-entering
///   <see cref="MongoShapedQueryCompilingExpressionVisitor"/>, so nested
///   <c>ThenInclude</c> chains work recursively);</item>
///   <item>a "loader" closure that, given a <see cref="MongoQueryContext"/> and a principal,
///   issues the sub-query, materializes the result, and feeds it to the existing
///   <c>IncludeReference</c> / <c>IncludeCollection</c> helpers in
///   <see cref="MongoProjectionBindingRemovingExpressionVisitor"/>.</item>
/// </list>
/// <para>
/// This file is a Stage-0 scaffold for EF-117; the implementation lands in Stages 1–3
/// per <c>docs/EF-117-include-implementation-plan.md</c>.
/// </para>
/// </remarks>
internal static class MongoIncludeCompiler
{
    // Stage 1: reference navigation, dependent → principal (Order.Customer).
    // Stage 2: collection navigation, principal → dependents (Customer.Orders).
    // Stage 3: ThenInclude chains and cycles.

    /// <summary>
    /// Partitions an <see cref="IncludeExpression"/> into the three cases the provider
    /// handles distinctly: embedded (already supported), cross-collection (EF-117 stages
    /// 1+), and many-to-many via skip navigation (explicitly out of scope for EF-117).
    /// </summary>
    /// <param name="includeExpression">The include node coming from EF Core's
    /// navigation expansion.</param>
    /// <returns>The classified <see cref="INavigation"/> when the include is supported
    /// today (i.e. embedded). The current Stage-0 implementation only returns for
    /// embedded navigations and throws for the other two cases.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the navigation is a
    /// many-to-many (<see cref="ISkipNavigation"/>) — out of scope for EF-117 — or when
    /// the navigation crosses collection boundaries. The cross-collection case will
    /// stop throwing as EF-117 stages 1–3 land their implementation.</exception>
    public static INavigation ClassifyIncludeNavigation(IncludeExpression includeExpression)
    {
        // INavigationBase has exactly two implementations: INavigation (reference / collection)
        // and ISkipNavigation (many-to-many). EF-117 covers only the former.
        if (includeExpression.Navigation is not INavigation navigation)
        {
            var skipNavigation = includeExpression.Navigation;
            throw new InvalidOperationException(
                $"Including the many-to-many navigation '{skipNavigation.DeclaringEntityType.DisplayName()
                }.{skipNavigation.Name}' is not yet supported by the MongoDB EF Core provider. "
                + "Many-to-many Include is tracked as a follow-up to EF-117.");
        }

        if (!navigation.IsEmbedded())
        {
            // Cross-collection Include: full implementation lands in later EF-117 stages.
            // The message string is intentionally kept compatible with the legacy text
            // the provider has shipped — the ~500 specification-test overrides assert on
            // "Including navigation 'Navigation' is not supported" verbatim. Stages 1–5
            // remove individual overrides as each include shape starts translating.
            throw new InvalidOperationException(
                "Including navigation 'Navigation' is not supported as the navigation is not "
                + "embedded in same resource. Cross-collection Include support is being added "
                + $"incrementally under EF-117 (navigation: '{navigation.DeclaringEntityType.DisplayName()
                }.{navigation.Name}').");
        }

        return navigation;
    }
}
