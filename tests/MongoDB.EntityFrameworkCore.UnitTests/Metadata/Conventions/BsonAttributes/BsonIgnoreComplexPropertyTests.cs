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

public static class BsonIgnoreComplexPropertyTests
{
    [Fact]
    public static void BsonIgnore_on_complex_property_member_is_unmapped()
    {
        using var db = new DbContextWithComplexType();

        var entityType = db.Model.FindEntityType(typeof(Order));
        Assert.NotNull(entityType);

        var complexProperty = entityType.FindComplexProperty(nameof(Order.Address));
        Assert.NotNull(complexProperty);

        var ignoredProperty = complexProperty.ComplexType.FindProperty(nameof(Address.InternalCode));
        Assert.Null(ignoredProperty);

        var keptProperty = complexProperty.ComplexType.FindProperty(nameof(Address.Street));
        Assert.NotNull(keptProperty);
    }

    class Order
    {
        public int Id { get; set; }
        public Address Address { get; set; }
    }

    class Address
    {
        public string Street { get; set; }
        public string City { get; set; }

        [BsonIgnore]
        public string InternalCode { get; set; }
    }

    class DbContextWithComplexType : DbContext
    {
        public DbSet<Order> Orders { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().ComplexProperty(o => o.Address);
        }
    }
}
