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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public static class MongoNonQueryExpressionTests
{
    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    class NonQueryDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    [Fact]
    public static void Delete_node_has_correct_kind_and_empty_setters()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var sourceQuery = new MongoQueryExpression(entityType);

        var actual = new MongoNonQueryExpression(sourceQuery);

        Assert.Equal(MongoNonQueryExpression.OperationKind.Delete, actual.Kind);
        Assert.Same(sourceQuery, actual.SourceQuery);
        Assert.Equal(typeof(int), actual.Type);
        Assert.Empty(actual.Setters);
    }

    [Fact]
    public static void Update_node_has_correct_kind_and_setters()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var property = db.GetProperty<Product, decimal>(p => p.Price)!;
        var sourceQuery = new MongoQueryExpression(entityType);
        var setters = new List<MongoNonQueryExpression.Setter>
        {
            new(property, Expression.Constant(5), false)
        };

        var actual = new MongoNonQueryExpression(sourceQuery, setters);

        Assert.Equal(MongoNonQueryExpression.OperationKind.Update, actual.Kind);
        Assert.Same(sourceQuery, actual.SourceQuery);
        Assert.Single(actual.Setters);
        Assert.Same(property, actual.Setters[0].Property);
    }

    [Fact]
    public static void Delete_marker_defaults_to_SingleCommand_strategy()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var sourceQuery = new MongoQueryExpression(entityType);
        var node = new MongoNonQueryExpression(sourceQuery);
        Assert.Equal(MongoNonQueryExpression.BulkStrategy.SingleCommand, node.Strategy);
    }

    [Fact]
    public static void Delete_marker_carries_TwoPhase_strategy()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var sourceQuery = new MongoQueryExpression(entityType);
        var node = new MongoNonQueryExpression(sourceQuery, MongoNonQueryExpression.BulkStrategy.TwoPhase);
        Assert.Equal(MongoNonQueryExpression.BulkStrategy.TwoPhase, node.Strategy);
    }

    [Fact]
    public static void Update_marker_defaults_to_SingleCommand_strategy()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var sourceQuery = new MongoQueryExpression(entityType);
        var setters = new List<MongoNonQueryExpression.Setter>();
        var node = new MongoNonQueryExpression(sourceQuery, setters);
        Assert.Equal(MongoNonQueryExpression.BulkStrategy.SingleCommand, node.Strategy);
        Assert.Equal(MongoNonQueryExpression.OperationKind.Update, node.Kind);
    }

    [Fact]
    public static void Update_marker_carries_TwoPhase_strategy()
    {
        using var db = new NonQueryDbContext();
        var entityType = db.Model.GetEntityTypes().First();
        var sourceQuery = new MongoQueryExpression(entityType);
        var setters = new List<MongoNonQueryExpression.Setter>();
        var node = new MongoNonQueryExpression(sourceQuery, setters, MongoNonQueryExpression.BulkStrategy.TwoPhase);
        Assert.Equal(MongoNonQueryExpression.BulkStrategy.TwoPhase, node.Strategy);
        Assert.Equal(MongoNonQueryExpression.OperationKind.Update, node.Kind);
    }
}
