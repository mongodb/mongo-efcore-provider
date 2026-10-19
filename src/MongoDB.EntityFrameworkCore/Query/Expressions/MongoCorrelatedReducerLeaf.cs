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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Records the empty-input semantics for a projection leaf built by
/// <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c> (EF-449) — a reference-collection-nav
/// <c>First</c>/<c>FirstOrDefault</c> reduced to a scalar member inside a projection, e.g.
/// <c>animal.IdentificationMethods.FirstOrDefault().Method</c>.
/// </summary>
/// <remarks>
/// The <c>$lookup</c>'s <c>$unwind</c> is ALWAYS left-outer (see
/// <c>MongoSelectLowerer.AppendLookupStages</c>'s <see cref="LookupPipelineKind.CorrelatedReducer"/> branch), so
/// the emitted pipeline is identical for both reducers; <see cref="ThrowOnEmpty"/> is what distinguishes
/// <c>First</c> (must throw when no element matched) from <c>FirstOrDefault</c> (a missing unwound field already
/// reads back as the member's own default, so no extra work is needed). Keeping the distinction on the READ side
/// rather than in the join shape is deliberate: an inner <c>$unwind</c> would drop the whole principal row, which
/// is not what <c>First</c> means inside a projection — the principal row must survive and the reduction itself
/// must throw.
/// </remarks>
/// <param name="Alias">The <c>$project</c> alias this leaf's value is emitted under.</param>
/// <param name="Lookup">The <c>$lookup</c> (with its <c>$match</c>/<c>$sort</c>/<c>$limit:1</c> sub-pipeline)
/// this leaf reads its value out of.</param>
/// <param name="Member">The reduced element's own document element name, relative to the unwound
/// <see cref="LookupExpression.As"/> field.</param>
/// <param name="ThrowOnEmpty">Whether the source reducer was <c>First</c> (rather than
/// <c>FirstOrDefault</c>).</param>
internal sealed record MongoCorrelatedReducerLeaf(
    string Alias,
    LookupExpression Lookup,
    string Member,
    bool ThrowOnEmpty);
