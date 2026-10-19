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
/// Represents a single ordering key in a MongoDB query, carrying the key selector
/// expression and the sort direction.
/// </summary>
/// <param name="KeySelector">The expression whose value determines the sort key.</param>
/// <param name="Ascending"><see langword="true"/> for ascending order; <see langword="false"/> for descending.</param>
internal readonly record struct MongoOrdering(MongoExpression KeySelector, bool Ascending);
