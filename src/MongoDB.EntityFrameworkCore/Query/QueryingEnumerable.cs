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

    public QueryingEnumerable(
        MongoQueryContext queryContext,
        MongoExecutableQuery executableQuery,
        Func<MongoQueryContext, TSource, TTarget> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled)
    {
        _queryContext = queryContext;
        _executableQuery = executableQuery;
        _contextType = contextType;
        _shaper = shaper;
        _standAloneStateManager = standAloneStateManager;
        _threadSafetyChecksEnabled = threadSafetyChecksEnabled;
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

        private IEnumerator<TSource>? _enumerator;

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
                _concurrencyDetector?.EnterCriticalSection();

                try
                {
                    return MoveNextHelper();
                }
                finally
                {
                    _concurrencyDetector?.ExitCriticalSection();
                }
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
                _concurrencyDetector?.EnterCriticalSection();

                try
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    return new ValueTask<bool>(MoveNextHelper());
                }
                finally
                {
                    _concurrencyDetector?.ExitCriticalSection();
                }
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
                EntityFrameworkEventSource.Log.QueryExecuting();

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

            Current = hasNext
                ? _shaper(_queryContext, _enumerator.Current)
                : default!;

            return hasNext;
        }

        public void Dispose()
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }

        public ValueTask DisposeAsync()
        {
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
