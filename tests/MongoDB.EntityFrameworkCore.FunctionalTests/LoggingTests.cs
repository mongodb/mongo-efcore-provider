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

using Microsoft.Extensions.Logging;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class LoggingTests(SampleGuidesFixture fixture, ITestOutputHelper testOutputHelper)
{
    private readonly string DbName = fixture.MongoDatabase.DatabaseNamespace.DatabaseName;

    [Fact]
    public void Query_writes_log_via_LogTo_with_mql_when_sensitive_logging()
    {
        List<string> logs = [];
        var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        });

        var item = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs,
            l => l.Contains($"{DbName}.moons.aggregate([{{ \"$match\" : {{ \"yearOfDiscovery\" : {{ \"$gt\" : 1900 }} }} }}])"));
    }

    [Fact]
    public void Query_writes_log_via_LogTo_without_mql_when_no_sensitive_logging()
    {
        List<string> logs = [];
        var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        }, sensitiveDataLogging: false);

        var item = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs, l => l.Contains($"{DbName}.moons.aggregate([?])"));
        Assert.DoesNotContain(logs, l => l.Contains("yearOfDiscovery"));
    }

    [Fact]
    public void Query_writes_event_via_LoggerFactory_with_mql_when_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory);

        var item = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotNull(item);
        var logger = Assert.Single(spyLogger.Loggers, s => s.Key == "Microsoft.EntityFrameworkCore.Database.Command").Value;

        var message = Assert.Single(logger.Records, log =>
            log.LogLevel == LogLevel.Information &&
            log.EventId == MongoEventId.ExecutedMqlQuery &&
            log.Exception == null
        ).message;

        Assert.Contains("Executed MQL query", message);
        Assert.Contains($"{DbName}.moons.aggregate([{{ \"$match\" : {{ \"yearOfDiscovery\" : {{ \"$gt\" : 1900 }} }} }}])",
            message);
    }

    [Fact]
    public void Query_writes_event_via_LoggerFactory_without_mql_when_no_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory, sensitiveDataLogging: false);

        var item = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotNull(item);
        var logger = Assert.Single(spyLogger.Loggers, s => s.Key == "Microsoft.EntityFrameworkCore.Database.Command").Value;

        var message = Assert.Single(logger.Records, log =>
            log.LogLevel == LogLevel.Information &&
            log.EventId == MongoEventId.ExecutedMqlQuery &&
            log.Exception == null
        ).message;

        Assert.Contains("Executed MQL query", message);
        Assert.Contains($"{DbName}.moons.aggregate([?])", message);
        Assert.DoesNotContain("yearOfDiscovery", message);
    }
}
