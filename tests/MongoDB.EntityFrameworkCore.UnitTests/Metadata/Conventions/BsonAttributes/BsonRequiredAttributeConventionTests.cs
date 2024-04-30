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

public static class BsonRequiredAttributeConventionTests
{
    [Fact]
    public static void BsonRequired_specified_properties_are_required()
    {
        using var context = new BaseDbContext();

        var property = context.GetProperty((Customer c) => c.RequireMe);

        Assert.False(property?.IsNullable);
    }

    [Fact]
    public static void ModelBuilder_specified_not_required_override_BsonRequired_attribute()
    {
        using var context = new ModelBuilderSpecifiedDbContext();

        var property = context.GetProperty((Customer c) => c.RequireMe);

        Assert.True(property?.IsNullable);
    }

    class Customer
    {
        public int Id { get; set; }

        [BsonRequired]
        public string RequireMe { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }

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
            modelBuilder.Entity<Customer>().Property(p => p.RequireMe).IsRequired(false);
        }
    }
}
