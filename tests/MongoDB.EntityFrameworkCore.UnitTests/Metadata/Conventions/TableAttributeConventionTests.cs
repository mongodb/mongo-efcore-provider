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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class TableAttributeConventionTests
{
    [Fact]
    public static void TableAttribute_specified_names_are_used_as_Table_names()
    {
        using var context = new BaseDbContext();
        Assert.Equal("attributeSpecifiedName", GetCollectionName<Customer>(context));
    }

    [Fact]
    public static void ModelBuilder_specified_collection_names_override_TableAttribute_names()
    {
        using var context = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("fluentSpecifiedName", GetCollectionName<Customer>(context));
    }

    static string GetCollectionName<TEntity>(DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity))!.GetCollectionName();

    [Table("attributeSpecifiedName")]
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
            modelBuilder.Entity<Customer>().ToCollection("fluentSpecifiedName");
        }
    }
}
