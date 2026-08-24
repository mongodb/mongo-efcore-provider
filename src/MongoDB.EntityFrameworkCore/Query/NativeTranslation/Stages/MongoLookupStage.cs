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
/// Represents a <c>$lookup</c> aggregation stage that performs a join with another collection.
/// </summary>
internal sealed class MongoLookupStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoLookupStage"/> class.
    /// </summary>
    /// <param name="lookup">The lookup expression that specifies the join parameters.</param>
    public MongoLookupStage(LookupExpression lookup)
    {
        Lookup = lookup;
    }

    /// <summary>
    /// Gets the lookup expression that specifies the join parameters.
    /// </summary>
    public LookupExpression Lookup { get; }
}
