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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonDateTimeOptionsAttributeConventionTests
{
    [Fact]
    public static void BsonDateTimeOptions_specified_properties_are_of_a_specified_kind()
    {
        using var db = new BaseDbContext();

        var local = db.GetProperty((DatesEntity d) => d.Local);
        var utc = db.GetProperty((DatesEntity d) => d.Utc);
        var unspecified = db.GetProperty((DatesEntity d) => d.Unspecified);

        Assert.Equal(DateTimeKind.Local, local?.GetDateTimeKind());
        Assert.Equal(DateTimeKind.Utc, utc?.GetDateTimeKind());
        Assert.Equal(DateTimeKind.Unspecified, unspecified?.GetDateTimeKind());
    }

    [Fact]
    public static void ModelBuilder_specified_kind_override_BsonDateTimeOptions_attribute()
    {
        using var db = new ModelBuilderSpecifiedDbContext();

        var localToUtc = db.GetProperty((DatesEntity d) => d.Local);
        var utcToUnspecified = db.GetProperty((DatesEntity d) => d.Utc);
        var unspecifiedToLocal = db.GetProperty((DatesEntity d) => d.Unspecified);

        Assert.Equal(DateTimeKind.Utc, localToUtc?.GetDateTimeKind());
        Assert.Equal(DateTimeKind.Unspecified, utcToUnspecified?.GetDateTimeKind());
        Assert.Equal(DateTimeKind.Local, unspecifiedToLocal?.GetDateTimeKind());
    }

    class DatesEntity
    {
        public int Id { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Local)]
        public DateTime Local { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime Utc { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Unspecified)]
        public DateTime Unspecified { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<DatesEntity> Dates { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    class ModelBuilderSpecifiedDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<DatesEntity>(e =>
            {
                e.Property(p => p.Local).HasDateTimeKind(DateTimeKind.Utc);
                e.Property(p => p.Utc).HasDateTimeKind(DateTimeKind.Unspecified);
                e.Property(p => p.Unspecified).HasDateTimeKind(DateTimeKind.Local);
            });
        }
    }
}
