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

using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>A keyed <c>$group</c> stage: non-null <c>_id</c> key expression plus one or more accumulators.</summary>
internal sealed class MongoGroupStage(MongoGrouping grouping) : MongoPipelineStage
{
    /// <summary>The grouping key parts and accumulators to render into <c>$group</c>.</summary>
    public MongoGrouping Grouping { get; } = grouping;
}
