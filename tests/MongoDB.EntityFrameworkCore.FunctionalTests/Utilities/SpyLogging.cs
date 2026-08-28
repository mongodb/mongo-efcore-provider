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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

internal class SpyLoggerProvider : ILoggerProvider
{
    public static (LoggerFactory, SpyLoggerProvider) Create()
    {
        var loggerFactory = new LoggerFactory();
        var spyLogger = new SpyLoggerProvider();
        loggerFactory.AddProvider(spyLogger);
        return (loggerFactory, spyLogger);
    }

    public readonly ConcurrentDictionary<string, SpyLogger> Loggers = [];

    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
        => Loggers.GetOrAdd(categoryName, _ => new SpyLogger());

    public string GetLogMessageByEventId(EventId eventId)
    {
        var key = eventId.Name[..eventId.Name.LastIndexOf('.')];
        var logger = Assert.Single(Loggers, s => s.Key == key).Value;
        return Assert.Single(logger.Records, log => log.EventId == eventId && log.Exception == null).Message;
    }

    /// <summary>
    /// The plural of <see cref="GetLogMessageByEventId"/>, in source order, for a test that runs more than one
    /// query on the same context and needs to assert something of EVERY one of them.
    /// </summary>
    public IReadOnlyList<string> GetLogMessagesByEventId(EventId eventId)
    {
        var key = eventId.Name[..eventId.Name.LastIndexOf('.')];
        var logger = Assert.Single(Loggers, s => s.Key == key).Value;
        return logger.Records
            .Where(log => log.EventId == eventId && log.Exception == null)
            .Select(log => log.Message)
            .ToList();
    }
}

internal class SpyLogger : ILogger
{
    public readonly List<SpyLogRecord> Records = [];

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Records.Add(new SpyLogRecord(logLevel, eventId, formatter(state, exception), exception));

    public bool IsEnabled(LogLevel logLevel)
        => true;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => throw new NotImplementedException();
}

internal record SpyLogRecord(LogLevel LogLevel, EventId EventId, string Message, Exception? Exception);
