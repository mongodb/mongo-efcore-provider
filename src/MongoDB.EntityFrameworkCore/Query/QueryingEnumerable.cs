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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Query;

internal sealed class QueryingEnumerable<TSource, TTarget> : IAsyncEnumerable<TTarget>, IEnumerable<TTarget>
{
    private readonly MongoQueryContext _queryContext;
    private readonly MongoExecutableQuery _executableQuery;
    private readonly Func<MongoQueryContext, TSource, TTarget> _shaper;
    private readonly Type _contextType;
    private readonly bool _standAloneStateManager;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly Action<MongoQueryContext, MongoExecutableQuery>? _onZeroResults;

    public QueryingEnumerable(
        MongoQueryContext queryContext,
        MongoExecutableQuery executableQuery,
        Func<MongoQueryContext, TSource, TTarget> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        Action<MongoQueryContext, MongoExecutableQuery>? onZeroResults)
    {
        _queryContext = queryContext;
        _executableQuery = executableQuery;
        _contextType = contextType;
        _shaper = shaper;
        _standAloneStateManager = standAloneStateManager;
        _threadSafetyChecksEnabled = threadSafetyChecksEnabled;
        _onZeroResults = onZeroResults;
    }

    public IAsyncEnumerator<TTarget> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, cancellationToken);

    public IEnumerator<TTarget> GetEnumerator()
        => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private sealed class Enumerator : IEnumerator<TTarget>, IAsyncEnumerator<TTarget>
    {
        private readonly MongoQueryContext _queryContext;
        private readonly Func<MongoQueryContext, TSource, TTarget> _shaper;
        private readonly Type _contextType;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
        private readonly bool _standAloneStateManager;
        private readonly CancellationToken _cancellationToken;
        private readonly IConcurrencyDetector? _concurrencyDetector;
        private readonly IExceptionDetector _exceptionDetector;
        private readonly MongoExecutableQuery _executableQuery;
        private readonly Action<MongoQueryContext, MongoExecutableQuery>? _onZeroResults;
        private bool _gotResults;

        private IEnumerator<TSource>? _enumerator;
        private TSource? _currentRow;

        public Enumerator(QueryingEnumerable<TSource, TTarget> queryingEnumerable, CancellationToken cancellationToken = default)
        {
            _queryContext = queryingEnumerable._queryContext;
            _executableQuery = queryingEnumerable._executableQuery;
            _contextType = queryingEnumerable._contextType;
            _shaper = queryingEnumerable._shaper;
            _queryLogger = _queryContext.QueryLogger;
            _standAloneStateManager = queryingEnumerable._standAloneStateManager;
            _cancellationToken = cancellationToken;
            _exceptionDetector = _queryContext.ExceptionDetector;
            _onZeroResults = queryingEnumerable._onZeroResults;

            Current = default!;

            _concurrencyDetector = queryingEnumerable._threadSafetyChecksEnabled
                ? _queryContext.ConcurrencyDetector
                : null;
        }

        public TTarget Current { get; private set; }

        object IEnumerator.Current => Current!;

        public bool MoveNext()
        {
            try
            {
                using var _ = _concurrencyDetector?.EnterCriticalSection();

                return MoveNextHelper();
            }
            catch (Exception exception)
            {
                if (_exceptionDetector.IsCancellation(exception))
                {
                    _queryLogger.QueryCanceled(_contextType);
                }
                else
                {
                    _queryLogger.QueryIterationFailed(_contextType, exception);
                }

                throw;
            }
        }

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                using var _ = _concurrencyDetector?.EnterCriticalSection();

                _cancellationToken.ThrowIfCancellationRequested();

                return new ValueTask<bool>(MoveNextHelper());
            }
            catch (Exception exception)
            {
                if (_exceptionDetector.IsCancellation(exception, _cancellationToken))
                {
                    _queryLogger.QueryCanceled(_contextType);
                }
                else
                {
                    _queryLogger.QueryIterationFailed(_contextType, exception);
                }

                throw;
            }
        }

        private bool MoveNextHelper()
        {
            Action? logAction = null;

            if (_enumerator == null)
            {
#if !EF8
#pragma warning disable EF9101
                EntityFrameworkMetricsData.ReportQueryExecuting();
#pragma warning restore EF9101
#else
                EntityFrameworkEventSource.Log.QueryExecuting();
#endif

                // Initialize the state manager BEFORE creating the cursor. On the one-pass streaming path the
                // driver eagerly deserializes (and materializes) the first cursor batch DURING
                // MongoClient.Execute — the custom output serializer's Deserialize runs while the cursor is
                // being created — so a tracked query would otherwise see a null StateManager and NRE. Doing
                // this first is harmless for the DOM / driver-LINQ paths: they return lazy enumerables and
                // materialize later, per row, inside the shaper (which runs after this point regardless).
                _queryContext.InitializeStateManager(_standAloneStateManager);

                try
                {
                    _enumerator = _queryContext.MongoClient.Execute<TSource>(_executableQuery, out logAction).GetEnumerator();
                }
                catch
                {
                    // Ensure we log the query even when C# Driver throws
                    logAction?.Invoke();
                    throw;
                }
            }

            var hasNext = _enumerator.MoveNext();

            logAction?.Invoke();

            if (hasNext)
            {
                // A null source row (e.g. SingleOrDefault/FirstOrDefault with no match, returned as a
                // single null by the scalar path) must not be passed to the entity shaper, which would
                // dereference a null BsonDocument. Yield default(TTarget); a projected identity shaper
                // would produce the same null, so scalar/aggregate results are unaffected.
                var row = _enumerator.Current;
                _currentRow = row;
                Current = row is null ? default! : _shaper(_queryContext, row);

                // Under the default one-pass native streaming path (SP7), TSource == TResult: the cursor
                // yields the fully-materialized entity directly, the shaper is identity, and _currentRow /
                // Current / the entity handed to the caller are the SAME reference — it must NOT be disposed
                // here even if the entity type happens to implement IDisposable. Only the dormant
                // RawBsonDocument fallback row type (see ReleaseCurrentRow) is ever released.
                ReleaseCurrentRow();

                if (!_gotResults)
                {
                    _gotResults = true;
                }
            }
            else
            {
                Current = default!;

                if (!_gotResults && _onZeroResults != null)
                {
                    _onZeroResults(_queryContext, _executableQuery);
                }
            }

            return hasNext;
        }

        // Releases a fetched-but-not-yet-released RawBsonDocument byte buffer (the dormant, pre-SP7 streaming
        // row type — retained but currently unreachable, see the class-level notes). The default one-pass
        // native streaming row is the materialized entity itself (TSource == TResult), which must NEVER be
        // disposed here — disposing it would dispose the entity the caller just received (and, on the tracked
        // path, an entity now owned by the state manager). Narrowly typed to RawBsonDocument specifically
        // (rather than "any IDisposable _currentRow") so an entity type that happens to implement IDisposable
        // is never mistaken for a releasable row. Nulls the tracked field afterwards so a subsequent Dispose /
        // DisposeAsync does not double-dispose it.
        private void ReleaseCurrentRow()
        {
            if (_currentRow is RawBsonDocument raw)
            {
                raw.Dispose();
            }

            _currentRow = default;
        }

        public void Dispose()
        {
            // Release a fetched-but-not-yet-released streaming row (enumeration abandoned early or threw
            // mid-stream) before disposing the enumerator.
            ReleaseCurrentRow();

            _enumerator?.Dispose();
            _enumerator = null;
        }

        public ValueTask DisposeAsync()
        {
            ReleaseCurrentRow();

            var enumerator = _enumerator;
            _enumerator = null;

            if (enumerator is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            enumerator?.Dispose();
            return default;
        }

        public void Reset()
            => throw new NotSupportedException(CoreStrings.EnumerableResetNotSupported);
    }
}
