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
    /// <summary>The key of a placeholder sentinel document, <c>{ __mongoef_param__: &lt;index&gt; }</c>.</summary>
    /// <remarks>
    /// Must never start with <c>'$'</c>: <see cref="MongoQueryLanguageRenderer.RenderUnary"/>'s <c>!(x == value)</c> arm treats a
    /// document whose first key starts with <c>'$'</c> as an operator document, and a sentinel must be a bare value.
    /// </remarks>
    internal const string SentinelKey = "__mongoef_param__";

    /// <summary>One recorded parameter site, substituted per execution by <c>MongoPipelineFactory.Build</c>.</summary>
    internal readonly record struct Entry(
        string Name,
        IBsonSerializer? Serializer,
        bool IsArray,
        MongoRegexKind? RegexKind,
        IProperty? EntityMemberProperty,
        int? ArrayElementIndex,
        bool RegexCaseInsensitive,
        IReadOnlyList<IProperty>? CompositeKeyProperties = null);

    private readonly List<Entry> _entries = [];

    /// <summary>All accumulated placeholder entries, in insertion order.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    private Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>>? _runtimeEvaluators;

    /// <summary>
    /// Evaluators for runtime-computed parameters (<see cref="Expressions.MongoParameterExpression.RuntimeEvaluator"/>) by parameter
    /// name, or <see langword="null"/>; <see cref="MongoPipelineFactory"/> runs each once per Build, before substitution.
    /// </summary>
    public IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>>? RuntimeEvaluators
        => _runtimeEvaluators;

    /// <summary>Records the evaluator for <paramref name="parameterName"/>. Idempotent: a parameter node may be rendered more than once (trial render).</summary>
    public void RegisterRuntimeEvaluator(string parameterName, Func<IReadOnlyDictionary<string, object?>, object?> evaluator)
        => (_runtimeEvaluators ??= new Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>>())[parameterName]
            = evaluator;

    /// <summary>
    /// Appends a value placeholder and returns its sentinel. <paramref name="serializer"/> is <see langword="null"/> for
    /// property-less primitives (Skip/Take counts), serialized via <c>BsonValue.Create</c>.
    /// </summary>
    public BsonValue CreatePlaceholder(string parameterName, IBsonSerializer? serializer)
    {
        return Add(new Entry(parameterName, serializer, false, null, null, null, false));
    }

    /// <summary>
    /// Appends a placeholder for a whole-entity parameter (the <c>local</c> in <c>c == local</c>); at Build time the key is read
    /// from it via <paramref name="entityMemberProperty"/>'s getter before serializing.
    /// </summary>
    public BsonValue CreateEntityMemberPlaceholder(string parameterName, IProperty entityMemberProperty, IBsonSerializer serializer)
    {
        return Add(new Entry(parameterName, serializer, false, null, entityMemberProperty, null, false));
    }

    /// <summary>Appends a placeholder for a parameterized collection (<c>$in</c>/<c>$nin</c> values); <paramref name="elementSerializer"/> serializes each element.</summary>
    public BsonValue CreateArrayPlaceholder(string parameterName, IBsonSerializer elementSerializer)
    {
        return Add(new Entry(parameterName, elementSerializer, true, null, null, null, false));
    }

    /// <summary>
    /// Per-element analog of <see cref="CreateEntityMemberPlaceholder"/> for an entity-list <c>Contains</c>: the key is taken
    /// from each non-null entity; null elements pass through as BSON null.
    /// </summary>
    public BsonValue CreateEntityKeyArrayPlaceholder(string parameterName, IProperty entityMemberProperty, IBsonSerializer elementSerializer)
    {
        return Add(new Entry(parameterName, elementSerializer, true, null, entityMemberProperty, null, false));
    }

    /// <summary>
    /// Appends a placeholder for a composite-key entity-list <c>Contains</c>; at Build time <see cref="CompositeKeyClauses.Build"/>
    /// turns the list into the <c>$or</c>/<c>$nor</c> clause array, so the template doesn't depend on the list's length.
    /// </summary>
    public BsonValue CreateCompositeKeyArrayPlaceholder(string parameterName, IReadOnlyList<IProperty> keyProperties)
    {
        return Add(new Entry(parameterName, null, true, null, null, null, false, keyProperties));
    }

    /// <summary>Appends a placeholder extracting one element, at a constant index, from an array parameter (<c>args[0]</c>) per execution.</summary>
    public BsonValue CreateArrayElementPlaceholder(string parameterName, int elementIndex, IBsonSerializer? serializer)
    {
        return Add(new Entry(parameterName, serializer, false, null, null, elementIndex, false));
    }

    /// <summary>
    /// Appends a placeholder for a parameterized <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> term; at Build time the string
    /// is escaped and anchored via <see cref="MongoRegexPatternBuilder.BuildPattern"/> into a <see cref="BsonRegularExpression"/>,
    /// as <see cref="MongoQueryLanguageRenderer.RenderRegex"/> does for a constant. <paramref name="caseInsensitive"/> emits options <c>"is"</c> instead of <c>"s"</c>.
    /// </summary>
    public BsonValue CreateRegexPlaceholder(string parameterName, MongoRegexKind kind, bool caseInsensitive = false)
    {
        return Add(new Entry(parameterName, null, false, kind, null, null, caseInsensitive));
    }

    private BsonValue Add(Entry entry)
    {
        var index = _entries.Count;
        _entries.Add(entry);
        return new BsonDocument(SentinelKey, new BsonInt32(index));
    }

    /// <summary>Whether <paramref name="value"/> is a sentinel produced by <see cref="CreatePlaceholder"/>; if so, its zero-based <paramref name="index"/>.</summary>
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
