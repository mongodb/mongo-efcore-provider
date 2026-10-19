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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Accumulates parameter sites encountered during rendering, keyed by the index of the sentinel embedded in the
/// BSON template. <c>MongoPipelineFactory.Build</c> substitutes the actual values per execution.
/// </summary>
internal sealed class PlaceholderTable
{
    /// <summary>
    /// The key of a placeholder sentinel document, <c>{ __mongoef_param__: &lt;index&gt; }</c>.
    /// </summary>
    /// <remarks>
    /// Must never start with <c>'$'</c>: <see cref="MongoQueryLanguageRenderer.RenderUnary"/>'s <c>!(x == value)</c>
    /// arm treats a <see cref="BsonDocument"/> whose first key starts with <c>'$'</c> as an operator document, and a
    /// sentinel must be treated as a bare value.
    /// </remarks>
    internal const string SentinelKey = "__mongoef_param__";

    private readonly List<(string Name, IBsonSerializer? Serializer, bool IsArray, MongoRegexKind? RegexKind, IProperty? EntityMemberProperty, int? ArrayElementIndex, bool RegexCaseInsensitive)> _entries = [];

    /// <summary>
    /// A read-only view of all accumulated placeholder entries, in insertion order.
    /// </summary>
    public IReadOnlyList<(string Name, IBsonSerializer? Serializer, bool IsArray, MongoRegexKind? RegexKind, IProperty? EntityMemberProperty, int? ArrayElementIndex, bool RegexCaseInsensitive)> Entries => _entries;

    /// <summary>
    /// Appends a value placeholder and returns its sentinel.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__p_0</c>).</param>
    /// <param name="serializer">
    /// The value's serializer, or <see langword="null"/> for property-less primitives (e.g. Skip/Take counts)
    /// serialized via <c>BsonValue.Create</c>.
    /// </param>
    /// <returns>A sentinel <c>{ __mongoef_param__: &lt;index&gt; }</c>, the index into <see cref="Entries"/>.</returns>
    public BsonValue CreatePlaceholder(string parameterName, IBsonSerializer? serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer, false, null, null, null, false));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends a placeholder whose parameter is a whole entity (the <c>local</c> in <c>c == local</c>); at
    /// <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/> time the key is read from it via
    /// <paramref name="entityMemberProperty"/>'s getter before serializing.
    /// </summary>
    public BsonValue CreateEntityMemberPlaceholder(string parameterName, IProperty entityMemberProperty, IBsonSerializer serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer, false, null, entityMemberProperty, null, false));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends a placeholder for a parameterized collection (e.g. the values of <c>$in</c>/<c>$nin</c>);
    /// <paramref name="elementSerializer"/> serializes each element.
    /// </summary>
    public BsonValue CreateArrayPlaceholder(string parameterName, IBsonSerializer elementSerializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, elementSerializer, true, null, null, null, false));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Per-element analog of <see cref="CreateEntityMemberPlaceholder"/> for an entity-list <c>Contains</c>
    /// (<c>customers.Contains(c)</c>): the key is extracted from each non-null entity; null elements pass through as
    /// BSON null.
    /// </summary>
    public BsonValue CreateEntityKeyArrayPlaceholder(string parameterName, IProperty entityMemberProperty, IBsonSerializer elementSerializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, elementSerializer, true, null, entityMemberProperty, null, false));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends a placeholder that extracts one element, at a constant index, from an array parameter (e.g.
    /// <c>args[0]</c> in a compiled query taking <c>args</c>), per execution, before serializing.
    /// </summary>
    public BsonValue CreateArrayElementPlaceholder(string parameterName, int elementIndex, IBsonSerializer? serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer, false, null, null, elementIndex, false));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends a placeholder for a parameterized <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> term. At
    /// <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/> time the string is escaped and
    /// anchored via <see cref="MongoRegexPatternBuilder.BuildPattern"/> into a <see cref="BsonRegularExpression"/>,
    /// as <see cref="MongoQueryLanguageRenderer.RenderRegex"/> does for a constant term.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name.</param>
    /// <param name="kind">Whether the term is a <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> test.</param>
    /// <param name="caseInsensitive">Emit options <c>"is"</c> instead of <c>"s"</c>.</param>
    public BsonValue CreateRegexPlaceholder(string parameterName, MongoRegexKind kind, bool caseInsensitive = false)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, null, false, kind, null, null, caseInsensitive));
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
