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

#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

// TODO: Add async support

internal sealed class QueryingEnumerable<T> : IEnumerable<T>
{
    private readonly MongoQueryContext _mongoQueryContext;
    private readonly IEnumerable<T> _serverEnumerable;
    private readonly Type _contextType;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
    private readonly bool _standAloneStateManager;
    private readonly bool _threadSafetyChecksEnabled;

    public QueryingEnumerable(
        MongoQueryContext mongoQueryContext,
        IEnumerable<T> serverEnumerable,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled)
    {
        _mongoQueryContext = mongoQueryContext;
        _serverEnumerable = serverEnumerable;
        _contextType = contextType;
        _queryLogger = mongoQueryContext.QueryLogger;
        _standAloneStateManager = standAloneStateManager;
        _threadSafetyChecksEnabled = threadSafetyChecksEnabled;
    }

    public IEnumerator<T> GetEnumerator()
        => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private sealed class Enumerator : IEnumerator<T>
    {
        private readonly MongoQueryContext _mongoQueryContext;
        private readonly Func<MongoQueryContext, T, T> _shaper;
        private readonly Type _contextType;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
        private readonly bool _standAloneStateManager;
        private readonly IConcurrencyDetector? _concurrencyDetector;
        private readonly IExceptionDetector _exceptionDetector;
        private readonly IEnumerable<T> _serverEnumerator;

        private IEnumerator<T> _enumerator;

        public Enumerator(QueryingEnumerable<T> queryingEnumerable)
        {
            _mongoQueryContext = queryingEnumerable._mongoQueryContext;
            _serverEnumerator = queryingEnumerable._serverEnumerable;
            _contextType = queryingEnumerable._contextType;
            _queryLogger = queryingEnumerable._queryLogger;
            _standAloneStateManager = queryingEnumerable._standAloneStateManager;
            _exceptionDetector = _mongoQueryContext.ExceptionDetector;

            _concurrencyDetector = queryingEnumerable._threadSafetyChecksEnabled
                ? _mongoQueryContext.ConcurrencyDetector
                : null;
        }

        public T Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            try
            {
                _concurrencyDetector?.EnterCriticalSection();

                try
                {
                    if (_enumerator == null)
                    {
                        EntityFrameworkEventSource.Log.QueryExecuting();

                        _enumerator = _serverEnumerator.GetEnumerator();
                        _mongoQueryContext.InitializeStateManager(_standAloneStateManager);
                    }

                    bool hasNext = _enumerator.MoveNext();

                    Current
                        = hasNext
                            ? _enumerator.Current
                            : default;

                    return hasNext;
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

        public void Dispose()
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }

        public void Reset()
            => throw new NotSupportedException(CoreStrings.EnumerableResetNotSupported);
    }
}
