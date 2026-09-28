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
/// <c>string.Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> (and <c>char</c>/<c>char[]</c> overloads) as MQL
/// <c>$trim</c>/<c>$ltrim</c>/<c>$rtrim</c>. Aggregation-expression dialect only.
/// </summary>
internal sealed class MongoTrimExpression : MongoExpression
{
    public MongoTrimExpression(MongoExpression source, MongoTrimSide side, MongoExpression? chars)
    {
        Source = source;
        Side = side;
        Chars = chars;
    }

    public MongoExpression Source { get; }

    public MongoTrimSide Side { get; }

    /// <summary>
    /// The characters to strip as one string constant (<c>"Se"</c> for <c>Trim('S','e')</c>), or
    /// <see langword="null"/> for MongoDB's default (whitespace).
    /// </summary>
    public MongoExpression? Chars { get; }

    /// <inheritdoc />
    public override Type Type => typeof(string);
}
