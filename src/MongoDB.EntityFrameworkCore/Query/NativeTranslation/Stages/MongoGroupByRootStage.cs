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
/// EF-322: <c>{ "$group": { "_id": "$$ROOT" } }</c> — groups by the WHOLE document, the first half of the
/// whole-entity <c>Distinct()</c> dedup pattern (paired with a following <see cref="MongoReplaceRootStage"/>
/// reading <c>"$_id"</c> back out). Mirrors the literal BSON <c>MongoPipelineFactory.RenderUnionWith</c> already
/// emits for <c>Union</c>'s own dedup — a dedicated marker type here (rather than stretching
/// <see cref="MongoGroupStage"/>/<see cref="Expressions.MongoGrouping"/>, which model a NAMED key plus
/// accumulators) because this stage has neither: no key parts, no accumulators, nothing dialect-specific to
/// carry. A marker record — carries no data of its own.
/// </summary>
internal sealed class MongoGroupByRootStage : MongoPipelineStage
{
}
