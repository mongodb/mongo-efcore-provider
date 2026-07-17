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
    /// Whether a principal document with no matching array element/document is preserved (LEFT-join
    /// semantics, e.g. a reference Include's streaming <c>$unwind</c>) rather than dropped (INNER-join
    /// semantics, e.g. the EF-347 slice-5 cross-collection reference SelectMany flatten). Defaults to
    /// <see langword="true"/> so every pre-existing caller (the reference-Include <c>$unwind</c>) is
    /// unchanged; only the SelectMany flatten passes <see langword="false"/>.
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
    /// Whether a principal document with no matching array element/document is preserved
    /// (<see langword="true"/>, LEFT-join) or dropped (<see langword="false"/>, INNER-join). See the
    /// constructor parameter docs for the two cases that set each value.
    /// </summary>
    public bool PreserveNullAndEmptyArrays { get; }
}
