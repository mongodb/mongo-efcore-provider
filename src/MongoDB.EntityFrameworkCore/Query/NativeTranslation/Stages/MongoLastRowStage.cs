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
/// <c>{ "$group": { "_id": null, "_last": { "$last": "$$ROOT" } } }</c> — first half of the unordered
/// Last()/LastOrDefault() pattern, followed by a <see cref="MongoReplaceRootStage"/> reading <c>"$_last"</c>.
/// Matches the MQL driver-LINQ emits for this shape. A marker type (like <see cref="MongoGroupByRootStage"/>)
/// because <see cref="MongoGroupStage"/> models a named key plus accumulators, and this stage has neither.
/// </summary>
internal sealed class MongoLastRowStage : MongoPipelineStage
{
}
