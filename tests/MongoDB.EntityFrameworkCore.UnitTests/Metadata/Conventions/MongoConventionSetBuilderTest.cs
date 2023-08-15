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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class MongoConventionSetBuilderTest
{
    [Fact]
    public static void Can_build_a_model_with_default_conventions_without_DI()
    {
        var modelBuilder = new ModelBuilder(MongoConventionSetBuilder.Build());
        modelBuilder.Entity<Product>();
        modelBuilder.Entity<Location>();

        var model = modelBuilder.Model;

        Assert.Equal("Product", model.GetEntityTypes().Single(e => e.ClrType == typeof(Product)).GetCollectionName());
        Assert.Equal("Warehouses", model.GetEntityTypes().Single(e => e.ClrType == typeof(Location)).GetCollectionName());
    }

    [Fact]
    public static void Can_build_a_model_with_default_conventions_without_DI_new()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();
        modelBuilder.Entity<Product>();
        modelBuilder.Entity<Location>();

        var model = modelBuilder.Model;

        Assert.Equal("Product", model.GetEntityTypes().Single(e => e.ClrType == typeof(Product)).GetCollectionName());
        Assert.Equal("Warehouses",
            model.GetEntityTypes().Single(e => e.ClrType == typeof(Location)).GetCollectionName());
    }

    [Fact]
    public static void Can_identify_collection_name_from_dbset_property()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder<ProductsContext>();

        var model = modelBuilder.Model;

        Assert.Equal("Products", model.GetEntityTypes().Single(e => e.ClrType == typeof(Product)).GetCollectionName());
        Assert.Equal("Warehouses",
            model.GetEntityTypes().Single(e => e.ClrType == typeof(Location)).GetCollectionName());
    }

    class Product
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
    }

    [Collection("Warehouses")]
    class Location
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
    }

    class ProductsContext : DbContext
    {
        public DbSet<Product> Products { get; set; }

        public DbSet<Location> Locations { get; set; }

        public ProductsContext(DbContextOptions<ProductsContext> options)
            : base(options)
        {
        }
    }
}
