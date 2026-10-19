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

/// <summary>Represents a <c>$count</c> aggregation stage producing a single count document.</summary>
internal sealed class MongoCountStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoCountStage"/> class.
    /// </summary>
    /// <param name="outputField">The output field name holding the count (conventionally "v").</param>
    public MongoCountStage(string outputField) => OutputField = outputField;

    /// <summary>The output field name holding the count (conventionally "v").</summary>
    public string OutputField { get; }
}
