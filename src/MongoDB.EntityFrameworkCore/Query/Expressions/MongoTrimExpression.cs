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

/// <summary>Which side(s) of <see cref="MongoTrimExpression.Source"/> to strip.</summary>
internal enum MongoTrimSide
{
    Both,
    Start,
    End
}

/// <summary>
/// Represents <c>string.Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> and their <c>char</c>/<c>char[]</c>-arg
/// overloads — MQL <c>$trim</c>/<c>$ltrim</c>/<c>$rtrim</c>, aggregation-expression dialect only (no query-
/// dialect form exists for these).
/// </summary>
internal sealed class MongoTrimExpression : MongoExpression
{
    public MongoTrimExpression(MongoExpression source, MongoTrimSide side, MongoExpression? chars)
    {
        Source = source;
        Side = side;
        Chars = chars;
    }

    /// <summary>The string-typed operand being trimmed.</summary>
    public MongoExpression Source { get; }

    /// <summary>Which side(s) to strip.</summary>
    public MongoTrimSide Side { get; }

    /// <summary>
    /// The characters to strip, as a single string constant (e.g. <c>"Se"</c> for <c>Trim(new[]{'S','e'})</c>),
    /// or <see langword="null"/> for the zero-arg overload (MongoDB's own default: whitespace).
    /// </summary>
    public MongoExpression? Chars { get; }

    /// <inheritdoc />
    public override Type Type => typeof(string);
}
