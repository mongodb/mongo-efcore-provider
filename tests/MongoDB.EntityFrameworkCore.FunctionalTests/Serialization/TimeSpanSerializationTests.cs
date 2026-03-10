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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class TimeSpanSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-5000)]
    public void TimeSpan_round_trips(long ms)
    {
        var expected = TimeSpan.FromTicks(ms * TimeSpan.TicksPerMillisecond);
        var collection = Database.CreateCollection<TimeSpanEntity>(values: ms);
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new TimeSpanEntity { aTimeSpan = expected });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(expected, result.aTimeSpan);
    }

    [Fact]
    public void TimeSpan_max_value_round_trips()
    {
        var collection = Database.CreateCollection<TimeSpanEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new TimeSpanEntity { aTimeSpan = TimeSpan.MaxValue });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(TimeSpan.MaxValue, result.aTimeSpan);
    }

    [Fact]
    public void Nullable_TimeSpan_round_trips_null()
    {
        var collection = Database.CreateCollection<NullableTimeSpanEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new NullableTimeSpanEntity { aTimeSpan = null });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Null(result.aTimeSpan);
    }

    class TimeSpanEntity : BaseIdEntity
    {
        public TimeSpan aTimeSpan { get; set; }
    }

    class NullableTimeSpanEntity : BaseIdEntity
    {
        public TimeSpan? aTimeSpan { get; set; }
    }
}
