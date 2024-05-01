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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class CollectionAttributeConventionTests
{
    [Fact]
    public static void Collection_attribute_specified_names_are_used_as_collection_names()
    {
        using var db = new BaseDbContext();
        Assert.Equal("attributedCollection", db.GetCollectionName<Customer>());
    }

    [Fact]
    public static void Model_builder_specified_names_override_collection_attribute_names()
    {
        using var db = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("namedCollection", db.GetCollectionName<Customer>());
    }

    [Collection("attributedCollection")]
    class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
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
            modelBuilder.Entity<Customer>().ToCollection("namedCollection");
        }
    }
}
