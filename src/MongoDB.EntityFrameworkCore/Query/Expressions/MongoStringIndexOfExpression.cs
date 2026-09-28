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
/// Ordinal single-argument <c>string.IndexOf(string)</c>, rendered as <c>$indexOfCP</c> in the
/// aggregation-expression dialect.
/// </summary>
/// <remarks>
/// Integer-valued with no query-dialect form, so <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>
/// must never admit it.
/// </remarks>
internal sealed class MongoStringIndexOfExpression(MongoExpression haystack, MongoExpression needle) : MongoExpression
{
    /// <summary>The string-valued expression searched.</summary>
    public MongoExpression Haystack { get; } = haystack;

    /// <summary>The string-valued expression searched for.</summary>
    public MongoExpression Needle { get; } = needle;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(int);
}
