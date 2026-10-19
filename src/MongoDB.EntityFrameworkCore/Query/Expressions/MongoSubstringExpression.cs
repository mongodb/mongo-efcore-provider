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

using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// <c>string.Substring(start[, length])</c> as <c>$substrCP</c> (code points, like <c>$strLenCP</c> for
/// <c>Length</c>). Aggregation dialect only. Out-of-range arguments return a truncated/empty string rather than
/// throwing as .NET does.
/// </summary>
internal sealed class MongoSubstringExpression(MongoExpression source, MongoExpression start, MongoExpression? length)
    : MongoExpression
{
    public MongoExpression Source { get; } = source;

    public MongoExpression Start { get; } = start;

    /// <summary>The length, or <see langword="null"/> for "to the end" (rendered from <c>$strLenCP</c>).</summary>
    public MongoExpression? Length { get; } = length;

    /// <inheritdoc />
    public override Type Type => typeof(string);
}
