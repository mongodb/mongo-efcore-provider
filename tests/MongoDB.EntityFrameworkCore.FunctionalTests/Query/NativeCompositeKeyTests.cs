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

using System.Linq;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeCompositeKeyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Order
    {
        public int CustomerId { get; set; }
        public int OrderNumber { get; set; }
        public string Status { get; set; } = "";
    }

    [Fact]
    public void Predicate_over_composite_key_component_goes_native()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: b => b.Entity<Order>().HasKey(o => new { o.CustomerId, o.OrderNumber }),
            optionsBuilderAction: b => new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly));

        db.Entities.Add(new Order { CustomerId = 1, OrderNumber = 100, Status = "Open" });
        db.Entities.Add(new Order { CustomerId = 2, OrderNumber = 200, Status = "Closed" });
        db.SaveChanges();

        // Succeeds under NativeOnly => went native (would throw NativeTranslationNotSupportedException before the fix).
        var found = db.Entities.AsNoTracking().Single(o => o.CustomerId == 1);
        Assert.Equal(100, found.OrderNumber);
    }

    [Fact]
    public void OrderBy_composite_key_component_goes_native()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: b => b.Entity<Order>().HasKey(o => new { o.CustomerId, o.OrderNumber }),
            optionsBuilderAction: b => new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly));

        db.Entities.Add(new Order { CustomerId = 2, OrderNumber = 200, Status = "Closed" });
        db.Entities.Add(new Order { CustomerId = 1, OrderNumber = 100, Status = "Open" });
        db.SaveChanges();

        var ordered = db.Entities.AsNoTracking().OrderBy(o => o.CustomerId).ToList();
        Assert.Equal([1, 2], ordered.Select(o => o.CustomerId));
    }
}
