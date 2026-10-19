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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-255: a non-deterministic call (Random, Guid.NewGuid) inside a query must never be evaluated once and baked into
/// the pipeline as a constant, which silently gives every row the same value.
/// </summary>
[XUnitCollection("QueryTests")]
public class NonDeterministicFunctionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Foo { get; set; }
        public DateTime Created { get; set; }
    }

    private SingleEntityDbContext<Row> CreateContext(MongoQueryMode mode, string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Row { Id = ObjectId.GenerateNewId(), Foo = 2, Created = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Row { Id = ObjectId.GenerateNewId(), Foo = 3, Created = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        ]);
        return SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_predicate_throws_rather_than_being_evaluated_once(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rp_{mode}");
        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > Random.Shared.Next(0, 2) + o.Foo - o.Foo).ToList());
        Assert.Contains("Next", ex.Message);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_compared_to_column_in_predicate_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rc_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > Random.Shared.Next()).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_ordering_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"ro_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(o => o.Foo + Random.Shared.Next()).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Guid_NewGuid_in_predicate_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"gp_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > 0 && Guid.NewGuid() != Guid.Empty && o.Foo == o.Foo).ToList());
    }

    // EF leaves Guid.NewGuid() in the tree, so a closed DateTime receiver of a native $dateAdd that contains one reaches
    // MongoExpressionTranslator.TryEvaluateClosedSubtree, which would otherwise evaluate it once at translation time.
    // The native path must decline (NativeOnly throws) and the driver-LINQ bridge then throws.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Guid_NewGuid_in_closed_date_add_receiver_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"gd_{mode}");

        Func<object> query = () => db.Entities
            .Where(o => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(Guid.NewGuid().GetHashCode()).AddDays(o.Foo)
                        > o.Created)
            .ToList();

        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(query);
        }
        else
        {
            Assert.Contains("Guid.NewGuid", Assert.Throws<InvalidOperationException>(query).Message);
        }
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void UtcNow_in_predicate_still_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"un_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTime.UtcNow).Count());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Now_in_predicate_still_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"nw_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTime.Now).Count());
    }

    // Pinned: before EF-255 a Random call in a projection was also evaluated once and returned the same value for every
    // row (the driver folds it into a constant), so it was never a working per-row client evaluation. It now fails loudly.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_projection_throws_rather_than_repeating_one_value(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rj_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Select(o => new { o.Foo, R = Random.Shared.Next() }).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_evaluated_client_side_before_the_query_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rv_{mode}");
        var threshold = Random.Shared.Next(-10, 0);
        Assert.Equal(3, db.Entities.Where(o => o.Foo > threshold).Count());
    }
}
