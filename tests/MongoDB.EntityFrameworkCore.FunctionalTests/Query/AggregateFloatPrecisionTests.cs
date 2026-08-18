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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Regression tests for EF-228: <c>Average</c>/<c>Sum</c> over a <c>float</c>-typed selector threw
/// <see cref="MongoDB.Bson.TruncationException"/> because MongoDB always computes <c>$avg</c>/<c>$sum</c>
/// in double precision, and the driver's default <c>SingleSerializer</c> refuses to narrow a double
/// result back to <c>float</c> if any precision would be lost.
/// </summary>
[XUnitCollection("QueryTests")]
public class AggregateFloatPrecisionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private record Order
    {
        public ObjectId _id { get; set; }
        public float Discount { get; set; }
    }

    [Fact]
    public void Average_over_float_selector_does_not_throw_on_precision_loss()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.AddRange(new Order { Discount = 1.1f }, new Order { Discount = 2.2f });
        db.SaveChanges();

        var average = db.Entities.Average(e => e.Discount);

        Assert.Equal(1.65f, average, 3);
    }

    [Fact]
    public void Sum_over_float_selector_does_not_throw_on_precision_loss()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.AddRange(new Order { Discount = 1.1f }, new Order { Discount = 2.2f });
        db.SaveChanges();

        var sum = db.Entities.Sum(e => e.Discount);

        Assert.Equal(3.3f, sum, 3);
    }

    [Fact]
    public void Average_over_float_without_selector_does_not_throw_on_precision_loss()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.AddRange(new Order { Discount = 1.1f }, new Order { Discount = 2.2f });
        db.SaveChanges();

        var average = db.Entities.Select(e => e.Discount).Average();

        Assert.Equal(1.65f, average, 3);
    }

    [Fact]
    public void Sum_over_float_without_selector_does_not_throw_on_precision_loss()
    {
        var collection = database.CreateCollection<Order>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.AddRange(new Order { Discount = 1.1f }, new Order { Discount = 2.2f });
        db.SaveChanges();

        var sum = db.Entities.Select(e => e.Discount).Sum();

        Assert.Equal(3.3f, sum, 3);
    }

    private record NullableOrder
    {
        public ObjectId _id { get; set; }
        public float? Discount { get; set; }
    }

    [Fact]
    public void Average_over_nullable_float_selector_does_not_throw_on_precision_loss()
    {
        var collection = database.CreateCollection<NullableOrder>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.AddRange(
            new NullableOrder { Discount = 1.1f }, new NullableOrder { Discount = 2.2f }, new NullableOrder { Discount = null });
        db.SaveChanges();

        var average = db.Entities.Average(e => e.Discount);

        Assert.NotNull(average);
        Assert.Equal(1.65f, average.Value, 3);
    }
}
