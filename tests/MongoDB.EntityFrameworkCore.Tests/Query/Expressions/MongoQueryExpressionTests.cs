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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Tests.Query.Expressions;

public static class MongoQueryExpressionTests
{
    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    class QueryDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }

    [Fact]
    public static void Set_collection_to_entity_passed_in_constructor()
    {
        var context = new QueryDbContext();
        var expectedEntityType = context.Model.GetEntityTypes().First();

        var actual = new MongoQueryExpression(expectedEntityType);

        Assert.Equal(expectedEntityType, actual.CollectionExpression.EntityType);
    }

    [Fact]
    public static void Can_roundtrip_projection_mappings()
    {
        var nestedProjectionMember = new ProjectionMember();
        foreach (var memberInfo in typeof(Product).GetMembers())
            nestedProjectionMember.Append(memberInfo);

        var newMappings = new Dictionary<ProjectionMember, Expression>
        {
            {new ProjectionMember().Append(typeof(Product).GetMember("Name")[0]), Expression.Constant("productName")},
            {nestedProjectionMember, Expression.Constant("productProperties")}
        };

        var context = new QueryDbContext();
        var mongoQuery = new MongoQueryExpression(context.Model.GetEntityTypes().First());
        mongoQuery.ReplaceProjectionMapping(newMappings);

        foreach (var mapping in newMappings)
        {
            var foundProjection = mongoQuery.GetMappedProjection(mapping.Key);
            Assert.Equal(mapping.Value, foundProjection);
        }
    }
}
