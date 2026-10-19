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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// EF-322: <c>{ "$group": { "_id": null, "_last": { "$last": "$$ROOT" } } }</c> — the first half of the
/// Last()/LastOrDefault()-with-no-explicit-order reducer pattern (paired with a following
/// <see cref="MongoReplaceRootStage"/> reading <c>"$_last"</c> back out). LINQ leaves row order undefined for
/// an unordered source, so this doesn't invent a new notion of "the last row" — it reproduces, bit-for-bit,
/// the MQL the driver-LINQ fallback already emits for this exact shape. Mirrors <see cref="MongoGroupByRootStage"/>:
/// a dedicated marker type (rather than stretching <see cref="MongoGroupStage"/>/<see cref="Expressions.MongoGrouping"/>,
/// which model a NAMED key plus per-selector accumulators) because this stage has neither — no key parts, no
/// selector, nothing dialect-specific to carry. A marker record — carries no data of its own.
/// </summary>
internal sealed class MongoLastRowStage : MongoPipelineStage
{
}
