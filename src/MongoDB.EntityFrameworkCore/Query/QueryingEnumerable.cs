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

                _queryContext.InitializeStateManager(_standAloneStateManager);
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

                // Native streaming rows are IDisposable RawBsonDocuments backed by a byte buffer; the shaper
                // has consumed them, so release immediately. No-op for the plain-BsonDocument paths. Releasing
                // nulls out the tracked field so an abandoned-enumeration Dispose doesn't dispose it again.
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

        // Releases a fetched-but-not-yet-released streaming row (a RawBsonDocument byte buffer); nulls the
        // tracked field afterwards so a subsequent Dispose / DisposeAsync does not double-dispose it. No-op
        // for the plain-BsonDocument paths (the row is not IDisposable).
        private void ReleaseCurrentRow()
        {
            if (_currentRow is IDisposable disposableRow)
            {
                disposableRow.Dispose();
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
