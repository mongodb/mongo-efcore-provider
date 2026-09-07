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
/// Accumulates <c>MongoParameterExpression</c> sites encountered during rendering.
/// Each entry records the parameter name and its <see cref="IBsonSerializer"/>, keyed by
/// the zero-based index of the sentinel placeholder that was embedded in the rendered BSON
/// template. The pipeline factory (<c>MongoPipelineFactory.Build</c>) substitutes actual
/// parameter values at per-execution time using these entries.
/// </summary>
internal sealed class PlaceholderTable
{
    /// <summary>
    /// The reserved key used to identify a placeholder sentinel document.
    /// A sentinel is a <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>.
    /// This key is reserved and will never appear in server-sent BSON.
    /// </summary>
    /// <remarks>
    /// Load-bearing invariant: this key must never start with <c>'$'</c>.
    /// <see cref="MongoQueryLanguageRenderer.RenderUnary"/>'s <c>!(x == value)</c> arm distinguishes an
    /// already-built MongoDB operator document (e.g. <c>{ $gt: 5 }</c>) from a bare value it must still wrap
    /// in <c>{ $eq: … }</c> by checking whether the value is a <see cref="BsonDocument"/> whose first element
    /// name starts with <c>'$'</c>. A parameterized equality's rendered value IS a <see cref="BsonDocument"/>
    /// (this sentinel) but must be treated as a bare value, which only holds because this key is not
    /// <c>'$'</c>-prefixed.
    /// </remarks>
    internal const string SentinelKey = "__mongoef_param__";

    private readonly List<(string Name, IBsonSerializer? Serializer, bool IsArray, MongoRegexKind? RegexKind, IProperty? EntityMemberProperty, int? ArrayElementIndex)> _entries = [];

    /// <summary>
    /// A read-only view of all accumulated placeholder entries, in insertion order.
    /// </summary>
    public IReadOnlyList<(string Name, IBsonSerializer? Serializer, bool IsArray, MongoRegexKind? RegexKind, IProperty? EntityMemberProperty, int? ArrayElementIndex)> Entries => _entries;

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
        _entries.Add((parameterName, serializer, false, null, null, null));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends an <em>entity-member-extraction</em> placeholder entry — used when a query parameter's
    /// runtime value is a WHOLE ENTITY instance (e.g. the <c>local</c> side of <c>c == local</c>) rather
    /// than the value to compare directly. Unlike an ordinary value placeholder, at
    /// <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/> time the raw parameter
    /// value has <paramref name="entityMemberProperty"/>'s <see cref="IPropertyBase.GetGetter"/> applied to it
    /// first, per execution, to obtain the actual member value before it is serialized with
    /// <paramref name="serializer"/> — mirroring the deferred-computation pattern
    /// <see cref="CreateRegexPlaceholder"/> uses for a parameterized regex term.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__p_0</c>), bound to a whole entity.</param>
    /// <param name="entityMemberProperty">The primary-key property whose value is extracted from that entity.</param>
    /// <param name="serializer">The <see cref="IBsonSerializer"/> that will serialize the extracted value.</param>
    /// <returns>
    /// A sentinel <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>
    /// where <c>index</c> is the zero-based position in <see cref="Entries"/>.
    /// </returns>
    public BsonValue CreateEntityMemberPlaceholder(string parameterName, IProperty entityMemberProperty, IBsonSerializer serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer, false, null, entityMemberProperty, null));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends an <em>array</em> placeholder entry — used for a parameterized collection
    /// (e.g. the values side of a <c>$in</c>/<c>$nin</c> test) — and returns a sentinel
    /// <see cref="BsonValue"/> to embed in the rendered BSON template.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__p_0</c>).</param>
    /// <param name="elementSerializer">
    /// The <see cref="IBsonSerializer"/> that will serialize each element of the run-time collection
    /// value (the field's element serializer, not the collection's own serializer).
    /// </param>
    /// <returns>
    /// A sentinel <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>
    /// where <c>index</c> is the zero-based position in <see cref="Entries"/>.
    /// </returns>
    public BsonValue CreateArrayPlaceholder(string parameterName, IBsonSerializer elementSerializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, elementSerializer, true, null, null, null));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends an <em>array-element-extraction</em> placeholder entry — used when a query parameter's runtime
    /// value is an ARRAY and only ONE element of it (at a constant, compile-time-known index) is actually
    /// compared, e.g. <c>args[0]</c> in a compiled query whose lambda takes <c>args</c> as a top-level array
    /// parameter. Unlike an ordinary value placeholder, at
    /// <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/> time the raw parameter
    /// value has element <paramref name="elementIndex"/> extracted from it first, per execution, before it is
    /// serialized with <paramref name="serializer"/> — mirroring the deferred-computation pattern
    /// <see cref="CreateEntityMemberPlaceholder"/> uses for the entity-equality rewrite.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__args_0</c>), bound to an array.</param>
    /// <param name="elementIndex">The constant index of the element to extract from that array.</param>
    /// <param name="serializer">
    /// The <see cref="IBsonSerializer"/> that will serialize the extracted element, or <see langword="null"/>
    /// for a property-less primitive that is serialized via <c>BsonValue.Create</c>.
    /// </param>
    /// <returns>
    /// A sentinel <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>
    /// where <c>index</c> is the zero-based position in <see cref="Entries"/>.
    /// </returns>
    public BsonValue CreateArrayElementPlaceholder(string parameterName, int elementIndex, IBsonSerializer? serializer)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, serializer, false, null, null, elementIndex));
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>
    /// Appends a <em>regex</em> placeholder entry — used for a parameterized
    /// <c>string.StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> search term — and returns a sentinel
    /// <see cref="BsonValue"/> to embed in the rendered BSON template at the parameter-value position.
    /// Unlike an ordinary value placeholder, this one is never serialized verbatim: at
    /// <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/> time the raw runtime
    /// string is escaped and anchored per <paramref name="kind"/> (via
    /// <see cref="MongoRegexPatternBuilder.BuildPattern"/>) into a <see cref="BsonRegularExpression"/>,
    /// mirroring what <see cref="MongoQueryLanguageRenderer.RenderRegex"/> does for a constant term at
    /// render time.
    /// </summary>
    /// <param name="parameterName">The EF query-parameter name (e.g. <c>__p_0</c>).</param>
    /// <param name="kind">Whether the term is a <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> test.</param>
    /// <returns>
    /// A sentinel <see cref="BsonDocument"/> of the form <c>{ __mongoef_param__: &lt;index&gt; }</c>
    /// where <c>index</c> is the zero-based position in <see cref="Entries"/>.
    /// </returns>
    public BsonValue CreateRegexPlaceholder(string parameterName, MongoRegexKind kind)
    {
        var index = _entries.Count;
        _entries.Add((parameterName, null, false, kind, null, null));
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
