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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public static class MongoCollectionExpressionTests
{
    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    class Location
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; }
        public double Longitude { get; set; }
        public double Latitude { get; set; }
    }

    class CollectionDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }
        public DbSet<Location> Location { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    [Fact]
    public static void Can_set_properties_from_constructor()
    {
        using var db = new CollectionDbContext();

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var actual = new MongoCollectionExpression(entityType);
            Assert.Equal(entityType, actual.EntityType);
            Assert.Equal(entityType.GetCollectionName(), actual.CollectionName);
        }
    }
}
