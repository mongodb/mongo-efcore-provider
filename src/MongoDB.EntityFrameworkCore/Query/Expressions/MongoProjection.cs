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
/// A single output field of a native <c>$project</c> stage: an output element name (<paramref name="Alias"/>)
/// paired with the dialect-neutral <see cref="MongoExpression"/> that produces its value.
/// </summary>
/// <param name="Alias">The output element name; must match the alias the DOM shaper reads by.</param>
/// <param name="Expression">The source expression.</param>
/// <param name="Source">
/// The LINQ selector subtree <paramref name="Expression"/> was translated from, rebased onto the source shaper
/// (see <see cref="MongoSelectDefinition.RebaseProjectionSources"/>), or <see langword="null"/> when not recorded.
/// Lets the projection binding visitor recognize, structurally, the exact node the server computed.
/// </param>
/// <param name="ThrowsOnNull">
/// The value is a null-propagated scalar read back as a non-nullable value type (<c>x.S.Length</c> over a nullable
/// <c>S</c>, read as <see langword="int"/>; see <c>MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead</c>). The
/// server may answer null, so the read side reads it as <c>T?</c> and throws EF's "Nullable object must have a value."
/// on null rather than reading <c>default(T)</c>. Set by the emit side from the same predicate call that admitted the
/// leaf, and read by <c>MongoSelectDefinition.FindThrowOnNullProjection</c>; never re-derived.
/// </param>
/// <param name="ThrowsOnMalformedNull">
/// Read-side only: the value is a reference to an upstream computed leaf that classified
/// <c>NonNullableValueRead.ThrowOnMalformedNull</c> (a projected Distinct's flattened key over <c>x.Rank + 1</c>), so
/// the read side reads it strictly, as it would the leaf itself. Unlike <paramref name="ThrowsOnNull"/>, no emit-side
/// consumer looks at it, so no later operator declines over it.
/// </param>
internal readonly record struct MongoProjection(
    string Alias,
    MongoExpression Expression,
    System.Linq.Expressions.Expression? Source = null,
    bool ThrowsOnNull = false,
    bool ThrowsOnMalformedNull = false);
