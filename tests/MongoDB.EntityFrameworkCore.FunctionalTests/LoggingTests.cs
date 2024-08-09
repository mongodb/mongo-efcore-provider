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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class LoggingTests(SampleGuidesFixture fixture, ITestOutputHelper testOutputHelper)
{
    private readonly string _dbName = fixture.MongoDatabase.DatabaseNamespace.DatabaseName;

    [Fact]
    public void Query_writes_log_via_LogTo_with_mql_when_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        });

        var items = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotEmpty(items);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs,
            l => l.Contains(_dbName + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : { \"$gt\" : 1900 } } }])"));
    }

    [Fact]
    public void First_writes_log_via_LogTo_with_mql_when_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        });

        var item = db.Moons.FirstOrDefault(m => m.yearOfDiscovery > 1900);

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs,
            l => l.Contains(_dbName
                            + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : { \"$gt\" : 1900 } } }, { \"$limit\" : NumberLong(1) }])"));
    }

    [Fact]
    public void Single_writes_log_via_LogTo_with_mql_when_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        });

        var item = db.Moons.SingleOrDefault(m => m.yearOfDiscovery == 1949);

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs,
            l => l.Contains(_dbName
                            + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : 1949 } }, { \"$limit\" : NumberLong(2) }])"));
    }

    [Fact]
    public void Query_writes_log_via_LogTo_without_mql_when_no_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        }, sensitiveDataLogging: false);

        var items = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotEmpty(items);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs, l => l.Contains($"{_dbName}.moons.aggregate([?])"));
        Assert.DoesNotContain(logs, l => l.Contains("yearOfDiscovery"));
    }

    [Fact]
    public void First_writes_log_via_LogTo_without_mql_when_no_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        }, sensitiveDataLogging: false);

        var item = db.Moons.First(m => m.yearOfDiscovery > 1900);

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs, l => l.Contains($"{_dbName}.moons.aggregate([?])"));
        Assert.DoesNotContain(logs, l => l.Contains("yearOfDiscovery"));
    }

    [Fact]
    public void Single_writes_log_via_LogTo_without_mql_when_no_sensitive_logging()
    {
        List<string> logs = [];
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        }, sensitiveDataLogging: false);

        var item = db.Moons.Single(m => m.yearOfDiscovery == 1949);

        Assert.NotNull(item);
        Assert.Contains(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains(logs, l => l.Contains($"{_dbName}.moons.aggregate([?])"));
        Assert.DoesNotContain(logs, l => l.Contains("yearOfDiscovery"));
    }

    [Fact]
    public void Query_writes_event_via_LoggerFactory_with_mql_when_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory);

        var items = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotEmpty(items);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains(_dbName + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : { \"$gt\" : 1900 } } }])",
            message);
    }

    [Fact]
    public void First_writes_event_via_LoggerFactory_with_mql_when_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory);

        var item = db.Moons.First(m => m.yearOfDiscovery > 1900);

        Assert.NotNull(item);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains(
            _dbName
            + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : { \"$gt\" : 1900 } } }, { \"$limit\" : NumberLong(1) }])",
            message);
    }

    [Fact]
    public void Single_writes_event_via_LoggerFactory_with_mql_when_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory);

        var item = db.Moons.Single(m => m.yearOfDiscovery == 1949);

        Assert.NotNull(item);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains(
            _dbName + ".moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : 1949 } }, { \"$limit\" : NumberLong(2) }])",
            message);
    }

    [Fact]
    public void Query_writes_event_via_LoggerFactory_without_mql_when_no_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory, sensitiveDataLogging: false);

        var items = db.Moons.Where(m => m.yearOfDiscovery > 1900).ToArray();

        Assert.NotEmpty(items);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains($"{_dbName}.moons.aggregate([?])", message);
        Assert.DoesNotContain("yearOfDiscovery", message);
    }

    [Fact]
    public void First_writes_event_via_LoggerFactory_without_mql_when_no_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory, sensitiveDataLogging: false);

        var item = db.Moons.FirstOrDefault(m => m.yearOfDiscovery > 1900);

        Assert.NotNull(item);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains($"{_dbName}.moons.aggregate([?])", message);
        Assert.DoesNotContain("yearOfDiscovery", message);
    }

    [Fact]
    public void Single_writes_event_via_LoggerFactory_without_mql_when_no_sensitive_logging()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db = GuidesDbContext.Create(fixture.MongoDatabase, null, loggerFactory, sensitiveDataLogging: false);

        var item = db.Moons.SingleOrDefault(m => m.yearOfDiscovery > 1900);

        Assert.NotNull(item);

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains($"{_dbName}.moons.aggregate([?])", message);
        Assert.DoesNotContain("yearOfDiscovery", message);
    }

    [Fact]
    public void Single_writes_event_via_LoggerFactory_with_mql_even_when_linq_driver_throws()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var brokenDatabase = TestServer.BrokenClient.GetDatabase("na");
        using var db = GuidesDbContext.Create(brokenDatabase, null, loggerFactory, sensitiveDataLogging: true);

        Assert.Throws<TimeoutException>(() => db.Moons.SingleOrDefault(m => m.yearOfDiscovery == 1949));

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains("na.moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : 1949 } }, { \"$limit\" : NumberLong(2) }])",
            message);
    }

    [Fact]
    public void Where_writes_event_via_LoggerFactory_with_mql_even_when_linq_driver_throws()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var brokenDatabase = TestServer.BrokenClient.GetDatabase("na");
        using var db = GuidesDbContext.Create(brokenDatabase, null, loggerFactory, sensitiveDataLogging: true);

        Assert.Throws<TimeoutException>(() => db.Moons.Where(m => m.yearOfDiscovery == 1949).ToList());

        var message = GetLogMessageByEventId(spyLogger);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains("na.moons.aggregate([{ \"$match\" : { \"yearOfDiscovery\" : 1949 } }])",
            message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SaveChanges_writes_events_to_log_via_LogTo_with_counts(bool fail, bool async)
    {
        List<string> logs = [];

        await using var guidesFixture = new SampleGuidesFixture();
        await using var db = GuidesDbContext.Create(guidesFixture.MongoDatabase, s =>
        {
            logs.Add(s);
            testOutputHelper.WriteLine(s);
        });

        db.Planets.RemoveRange(db.Planets.Where(m => m.name.StartsWith("M")));
        foreach (var planet in db.Planets.Where(m => m.hasRings))
        {
            planet.hasRings = false;
        }

        var newPlanetId = fail
            ? ObjectId.Parse("621ff30d2a3e781873fcb661")
            : ObjectId.GenerateNewId();
        db.Planets.Add(new Planet {_id = newPlanetId, name = "Proxima Centauri d", hasRings = false, orderFromSun = -1});

        if (!fail)
        {
            var result = async ? await db.SaveChangesAsync() : db.SaveChanges();
            Assert.Equal(7, result);
        }
        else
        {
            if (async)
            {
                await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
            }
            else
            {
                Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
            }
        }

        var usingTransactions = db.Database.AutoTransactionBehavior != AutoTransactionBehavior.Never;
        if (usingTransactions)
        {
            Assert.Contains("Beginning transaction", AssertSingleLogEntry(logs, MongoEventId.TransactionStarting));
            Assert.Contains("Began transaction", AssertSingleLogEntry(logs, MongoEventId.TransactionStarted));
        }

        var executingBulkLog = Assert.Single(logs, l => l.Contains("MongoEventId.ExecutingBulkWrite"));
        Assert.Contains("Executing Bulk Write", executingBulkLog);
        Assert.Contains($"Collection='{guidesFixture.MongoDatabase.DatabaseNamespace.DatabaseName}.planets'", executingBulkLog);
        Assert.Contains("Insertions=1, Deletions=2, Modifications=4", executingBulkLog);

        if (!fail)
        {
            var executedBulkLog = Assert.Single(logs, l => l.Contains("MongoEventId.ExecutedBulkWrite"));
            Assert.Contains("Executed Bulk Write", executedBulkLog);
            Assert.Contains($"Collection='{guidesFixture.MongoDatabase.DatabaseNamespace.DatabaseName}.planets'", executedBulkLog);
            Assert.Contains("Inserted=1, Deleted=2, Modified=4", executedBulkLog);
        }
        else
        {
            Assert.DoesNotContain(logs, l => l.Contains("MongoEventId.ExecutedBulkWrite"));
        }

        if (usingTransactions)
        {
            if (fail)
            {
                Assert.Contains("Rolling back transaction.", AssertSingleLogEntry(logs, MongoEventId.TransactionRollingBack));
                Assert.Contains("Rolled back transaction.", AssertSingleLogEntry(logs, MongoEventId.TransactionRolledBack));
            }
            else
            {
                Assert.Contains("Committing transaction.", AssertSingleLogEntry(logs, MongoEventId.TransactionCommitting));
                Assert.Contains("Committed transaction.", AssertSingleLogEntry(logs, MongoEventId.TransactionCommitted));
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SaveChanges_writes_events_via_LoggerFactory_with_counts(bool fail, bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();

        await using var guidesFixture = new SampleGuidesFixture();
        await using var db = GuidesDbContext.Create(guidesFixture.MongoDatabase, null, loggerFactory, sensitiveDataLogging: false);

        db.Planets.RemoveRange(db.Planets.Where(m => m.name.StartsWith("M")));
        foreach (var planet in db.Planets.Where(m => m.hasRings))
        {
            planet.hasRings = false;
        }

        var newPlanetId = fail
            ? ObjectId.Parse("621ff30d2a3e781873fcb661")
            : ObjectId.GenerateNewId();
        db.Planets.Add(new Planet {_id = newPlanetId, name = "Proxima Centauri d", hasRings = false, orderFromSun = -1});

        if (!fail)
        {
            var result = async ? await db.SaveChangesAsync() : db.SaveChanges();
            Assert.Equal(7, result);
        }
        else
        {
            if (async)
            {
                await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
            }
            else
            {
                Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
            }
        }

        var usingTransactions = db.Database.AutoTransactionBehavior != AutoTransactionBehavior.Never;
        if (usingTransactions)
        {
            Assert.Contains("Beginning transaction", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionStarting));
            Assert.Contains("Began transaction", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionStarted));
        }

        var executingBulkEvent = GetLogMessageByEventId(spyLogger, MongoEventId.ExecutingBulkWrite);
        Assert.Contains("Executing Bulk Write", executingBulkEvent);
        Assert.Contains($"Collection='{guidesFixture.MongoDatabase.DatabaseNamespace.DatabaseName}.planets'", executingBulkEvent);
        Assert.Contains("Insertions=1, Deletions=2, Modifications=4", executingBulkEvent);

        if (!fail)
        {
            var executedBulkEvent = GetLogMessageByEventId(spyLogger, MongoEventId.ExecutedBulkWrite);
            Assert.Contains("Executed Bulk Write", executedBulkEvent);
            Assert.Contains($"Collection='{guidesFixture.MongoDatabase.DatabaseNamespace.DatabaseName}.planets'",
                executedBulkEvent);
            Assert.Contains("Inserted=1, Deleted=2, Modified=4", executedBulkEvent);
        }
        else
        {
            AssertNoLogMessageForEventId(spyLogger, MongoEventId.ExecutedBulkWrite);
        }

        if (usingTransactions)
        {
            if (fail)
            {
                Assert.Contains("Rolling back transaction.", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionRollingBack));
                Assert.Contains("Rolled back transaction.", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionRolledBack));
            }
            else
            {
                Assert.Contains("Committing transaction.", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionCommitting));
                Assert.Contains("Committed transaction.", GetLogMessageByEventId(spyLogger, MongoEventId.TransactionCommitted));
            }
        }
    }

    private static string GetLogMessageByEventId(SpyLoggerProvider spyLogger, EventId? eventId = null)
    {
        eventId ??= MongoEventId.ExecutedMqlQuery;
        var key = eventId.Value.Name.Substring(0, eventId.Value.Name.LastIndexOf('.'));
        var logger = Assert.Single(spyLogger.Loggers, s => s.Key == key).Value;

        return Assert.Single(logger.Records, log =>
            log.EventId == eventId &&
            log.Exception == null
        ).message;
    }

    private static void AssertNoLogMessageForEventId(SpyLoggerProvider spyLogger, EventId eventId)
    {
        var key = eventId.Name.Substring(0, eventId.Name.LastIndexOf('.'));
        var logger = spyLogger.Loggers.First(s => s.Key == key).Value;
        Assert.DoesNotContain(logger.Records, log => log.EventId == eventId);
    }

    private static string AssertSingleLogEntry(List<string> logs, EventId eventId)
        => Assert.Single(logs, l => l.Contains(nameof(MongoEventId) + "." + eventId.Name.Split('.').Last()));
}
