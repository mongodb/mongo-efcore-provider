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
/// A projection leaf built by <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c>: a reference-collection
/// nav <c>First</c>/<c>FirstOrDefault</c> reduced to a scalar member, e.g.
/// <c>animal.IdentificationMethods.FirstOrDefault().Method</c>.
/// </summary>
/// <remarks>
/// The <c>$unwind</c> is always left-outer, so both reducers emit the same pipeline; <see cref="ThrowOnEmpty"/>
/// makes <c>First</c> throw on the read side. An inner <c>$unwind</c> would wrongly drop the principal row.
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
