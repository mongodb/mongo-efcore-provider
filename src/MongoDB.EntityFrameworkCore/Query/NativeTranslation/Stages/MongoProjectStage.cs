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

using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A <c>$project</c> stage.
/// </summary>
internal sealed class MongoProjectStage : MongoPipelineStage
{
    /// <summary>
    /// Creates a <c>$project</c> stage with the given ordered output fields.
    /// </summary>
    public MongoProjectStage(IReadOnlyList<MongoProjection> projections)
    {
        Projections = projections;
    }

    /// <summary>
    /// The ordered output fields of the projection.
    /// </summary>
    public IReadOnlyList<MongoProjection> Projections { get; }
}
