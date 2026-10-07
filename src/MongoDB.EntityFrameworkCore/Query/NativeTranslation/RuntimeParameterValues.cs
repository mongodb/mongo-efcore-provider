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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// One execution's parameter values: the EF query parameters plus the runtime-computed ones
/// (<see cref="PlaceholderTable.RuntimeEvaluators"/>). Each evaluator runs at most once per instance, against the EF
/// values (never this wrapper), and its result is cached; <see cref="MongoPipelineFactory"/> creates one instance
/// per Build, so every placeholder of one parameter sees one reading of the clock.
/// </summary>
internal sealed class RuntimeParameterValues : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, object?> _inner;
    private readonly IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>> _evaluators;
    private readonly Dictionary<string, object?> _computed = new();

    private RuntimeParameterValues(
        IReadOnlyDictionary<string, object?> inner,
        IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>> evaluators)
    {
        _inner = inner;
        _evaluators = evaluators;
    }

    /// <summary>
    /// Returns <paramref name="inner"/> unchanged when there are no evaluators, otherwise a fresh wrapper.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Wrap(
        IReadOnlyDictionary<string, object?> inner,
        IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>>? evaluators)
        => evaluators is null || evaluators.Count == 0 ? inner : new RuntimeParameterValues(inner, evaluators);

    /// <inheritdoc />
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        if (_evaluators.TryGetValue(key, out var evaluator))
        {
            if (!_computed.TryGetValue(key, out value))
            {
                value = evaluator(_inner);
                _computed[key] = value;
            }

            return true;
        }

        return _inner.TryGetValue(key, out value);
    }

    /// <inheritdoc />
    public object? this[string key]
        => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    /// <inheritdoc />
    public bool ContainsKey(string key)
        => _evaluators.ContainsKey(key) || _inner.ContainsKey(key);

    /// <inheritdoc />
    public IEnumerable<string> Keys
        => _inner.Keys.Concat(_evaluators.Keys);

    /// <inheritdoc />
    public IEnumerable<object?> Values
        => Keys.Select(k => this[k]);

    /// <inheritdoc />
    public int Count
        => _inner.Count + _evaluators.Count;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => Keys.Select(k => new KeyValuePair<string, object?>(k, this[k])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}
