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
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonElementAttributeConventionTests
{
    [Fact]
    public static void BsonElement_specified_names_are_used_as_element_names()
    {
        using var context = new BaseDbContext();
        Assert.Equal("attributeSpecifiedName", context.GetProperty((Customer c) => c.Name)?.GetElementName());
    }

    [Fact]
    public static void ModelBuilder_specified_names_override_BsonElement_names()
    {
        using var context = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("fluentSpecifiedName", context.GetProperty((Customer c) => c.Name)?.GetElementName());
    }

    class Customer
    {
        public int Id { get; set; }

        [BsonElement("attributeSpecifiedName")]
        public string Name { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }

    class ModelBuilderSpecifiedDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().Property(c => c.Name).HasElementName("fluentSpecifiedName");
        }
    }
}
