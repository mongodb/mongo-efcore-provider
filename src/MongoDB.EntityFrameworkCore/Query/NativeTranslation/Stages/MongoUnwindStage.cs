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
/// Represents an <c>$unwind</c> aggregation stage that deconstructs array fields into separate documents.
/// </summary>
internal sealed class MongoUnwindStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoUnwindStage"/> class.
    /// </summary>
    /// <param name="lookup">The lookup expression that specifies which array field to unwind.</param>
    /// <param name="preserveNullAndEmptyArrays">
    /// Whether a document with no match is kept (left-join) or dropped (inner-join, e.g. a reference SelectMany).
    /// Defaults to <see langword="true"/> (the conservative choice). The reference-Include <c>$unwind</c> passes
    /// <see cref="LookupExpression.PreserveNullAndEmptyArrays"/>, which follows the navigation's requiredness.
    /// </param>
    public MongoUnwindStage(LookupExpression lookup, bool preserveNullAndEmptyArrays = true)
    {
        Lookup = lookup;
        PreserveNullAndEmptyArrays = preserveNullAndEmptyArrays;
    }

    /// <summary>
    /// Gets the lookup expression that specifies which array field to unwind.
    /// </summary>
    public LookupExpression Lookup { get; }

    /// <summary>
    /// Whether a document with no match is kept (<see langword="true"/>, left-join) or dropped (inner-join).
    /// </summary>
    public bool PreserveNullAndEmptyArrays { get; }
}
