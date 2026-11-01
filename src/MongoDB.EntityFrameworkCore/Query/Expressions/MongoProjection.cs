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
/// A single output field of a native <c>$project</c> stage: an output element name paired with the dialect-neutral
/// <see cref="MongoExpression"/> that produces its value.
/// </summary>
/// <param name="Alias">The output element name; must match the alias the DOM shaper reads by.</param>
/// <param name="Expression">The source expression.</param>
/// <param name="Source">
/// The LINQ selector subtree <paramref name="Expression"/> was translated from, rebased onto the source shaper
/// (see <see cref="MongoSelectDefinition.RebaseProjectionSources"/>), or <see langword="null"/> when not recorded;
/// lets the projection binding visitor recognize the exact node the server computed.
/// </param>
/// <param name="ThrowsOnNull">
/// The value is a null-propagated scalar read back as a non-nullable value type (<c>x.S.Length</c> over a nullable
/// <c>S</c>; see <c>MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead</c>). The read side reads it as
/// <c>T?</c> and throws EF's "Nullable object must have a value." on null rather than reading <c>default(T)</c>. Set
/// by the emit side from the same predicate call that admitted the leaf; never re-derived.
/// </param>
/// <param name="PrunedElementFields">
/// For a projected owned array whose consumer reads only some of each element's sub-documents and arrays (an element
/// <c>Select</c>, <c>NativeProjectionBinder.TryMatchOwnedArraySelectLeaf</c>): the element names each array element keeps, rendered as a
/// per-element <c>$map</c> by <c>MongoPipelineFactory</c>. Every stored scalar is kept, so a required property is never missing from
/// a document that had it; only owned navigations the consumer never touches are left out. <see langword="null"/> projects the array whole. The
/// one exception: a Select the cursor evaluates in place, which constructs no element, narrows this to the scalars it reads once the streaming
/// shaper is chosen (<c>MongoSelectDefinition.TryNarrowArrayProjection</c>).
/// </param>
/// <param name="ThrowsOnMalformedNull">
/// Read-side only: a reference to an upstream computed leaf classified <c>NonNullableValueRead.ThrowOnMalformedNull</c>
/// (a projected Distinct's flattened key over <c>x.Rank + 1</c>), read strictly. Unlike
/// <paramref name="ThrowsOnNull"/>, no emit-side consumer looks at it, so no later operator declines over it.
/// </param>
/// <param name="DefaultsOnMalformedMissing">
/// Read-side only: a projected Distinct's flattened key over a leaf classified
/// <c>NonNullableValueRead.DefaultOnMalformedMissing</c> (<c>x.Rank > 1 ? -1 : x.Rank</c>); its key part
/// <c>MongoGroupingKeyPart.MarksMissing</c> and the flatten restores MISSING from the marker. A missing value reads
/// as <c>default(T)</c>, a null throws.
/// </param>
internal readonly record struct MongoProjection(
    string Alias,
    MongoExpression Expression,
    System.Linq.Expressions.Expression? Source = null,
    bool ThrowsOnNull = false,
    bool ThrowsOnMalformedNull = false,
    bool DefaultsOnMalformedMissing = false,
    System.Collections.Generic.IReadOnlyList<string>? PrunedElementFields = null);
