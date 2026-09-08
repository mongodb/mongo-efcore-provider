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

/// <summary>
/// Represents a <c>$match</c> aggregation stage that filters documents based on a predicate.
/// </summary>
internal sealed class MongoMatchStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoMatchStage"/> class.
    /// </summary>
    /// <param name="predicate">The predicate expression that filters documents.</param>
    public MongoMatchStage(MongoExpression predicate)
    {
        Predicate = predicate;
    }

    /// <summary>
    /// Gets the predicate expression that filters documents.
    /// </summary>
    public MongoExpression Predicate { get; }
}
