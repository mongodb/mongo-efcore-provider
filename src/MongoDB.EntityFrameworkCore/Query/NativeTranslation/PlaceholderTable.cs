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

using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Accumulates <c>MongoParameterExpression</c> sites encountered during rendering.
/// Each entry records the parameter name and its <see cref="IBsonSerializer"/>, keyed by
/// the zero-based index of the sentinel placeholder that was embedded in the rendered BSON
/// template. Task 10's pipeline builder substitutes actual parameter values at per-execution
/// time using these entries.
/// </summary>
internal sealed class PlaceholderTable
{
    /// <summary>
    /// The reserved key used to identify a placeholder sentinel document.
    /// A sentinel is a <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>.
    /// This key is reserved and will never appear in server-sent BSON.
    /// </summary>
    internal const string SentinelKey = "__mongoef_param__";

    private readonly List<(string Name, IBsonSerializer? Serializer)> _entries = [];

    /// <summary>
    /// A read-only view of all accumulated placeholder entries, in insertion order.
    /// </summary>
    public IReadOnlyList<(string Name, IBsonSerializer? Serializer)> Entries => _entries;

    /// <summary>
    /// Appends a placeholder entry and returns a sentinel <see cref="BsonValue"/> to embed
    /// in the rendered BSON template at the parameter-value position.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__p_0</c>).</param>
    /// <param name="serializer">
    /// The <see cref="IBsonSerializer"/> that will serialize the run-time value, or <see langword="null"/>
    /// for property-less primitives (e.g. Skip/Take counts) that are serialized via <c>BsonValue.Create</c>.
    /// </param>
    /// <returns>
    /// A sentinel <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>
    /// where <c>index</c> is the zero-based position in <see cref="Entries"/>.
    /// </returns>
    public BsonValue CreatePlaceholder(string parameterName, IBsonSerializer? serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Determines whether <paramref name="value"/> is a placeholder sentinel, and if so
    /// extracts the zero-based <paramref name="index"/> it encodes.
    /// </summary>
    /// <param name="value">The <see cref="BsonValue"/> to inspect.</param>
    /// <param name="index">
    /// When this method returns <see langword="true"/>, the zero-based placeholder index.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> is a sentinel document produced
    /// by <see cref="CreatePlaceholder"/>; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetPlaceholderIndex(BsonValue value, out int index)
    {
        index = 0;
        if (value is BsonDocument doc
            && doc.ElementCount == 1
            && doc.TryGetValue(SentinelKey, out var indexValue)
            && indexValue is BsonInt32 bsonInt)
        {
            index = bsonInt.Value;
            return true;
        }

        return false;
    }
}
