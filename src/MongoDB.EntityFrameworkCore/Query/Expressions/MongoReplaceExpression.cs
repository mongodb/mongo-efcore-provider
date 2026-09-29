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
/// <c>string.Replace(oldValue, newValue)</c> / <c>Replace(oldChar, newChar)</c> as <c>$replaceAll</c> (ordinal,
/// all occurrences). A null <see cref="Replacement"/> means "" (.NET), rendered via <c>$ifNull</c>. An empty
/// <see cref="Find"/> throws in .NET; the server instead replaces at every position, inserting the replacement
/// between (and around) every character rather than erroring (observed: <c>"abc".Replace("", "X")</c> →
/// <c>"XaXbXcX"</c>).
/// </summary>
internal sealed class MongoReplaceExpression(MongoExpression input, MongoExpression find, MongoExpression replacement)
    : MongoExpression
{
    public MongoExpression Input { get; } = input;
    public MongoExpression Find { get; } = find;
    public MongoExpression Replacement { get; } = replacement;

    /// <inheritdoc />
    public override Type Type => typeof(string);
}
