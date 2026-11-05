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

#if !EF8

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Visitors;

/// <summary>
/// Unit tests for <see cref="MongoBulkPlanBuilder"/>'s plan shape (what crosses to the executor) and key guard. The translation delegates
/// themselves need a live query context and are covered by the ExecuteUpdate/ExecuteDelete functional tests.
/// </summary>
public class MongoBulkPlanBuilderTests
{
    private class Product
    {
        public ObjectId Id { get; set; }
        public decimal Price { get; set; }
    }

    private class Keyless
    {
        public string? Name { get; set; }
    }

    private class BulkDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; } = null!;
        public DbSet<Keyless> Keyless { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Keyless>().HasNoKey();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    private static MongoBulkPlan Plan(
        BulkDbContext db, bool update, MongoNonQueryExpression.BulkStrategy strategy)
    {
        var entityType = db.Model.FindEntityType(typeof(Product))!;
        var source = new MongoQueryExpression(entityType);
        var nonQuery = update
            ? new MongoNonQueryExpression(
                source,
                [new MongoNonQueryExpression.Setter(
                    entityType.FindProperty(nameof(Product.Price))!, Expression.Constant(5m), IsSelfReferencing: false)],
                strategy)
            : new MongoNonQueryExpression(source, strategy);

        return MongoBulkPlanBuilder.Create(entityType, new BsonSerializerFactory(), nonQuery);
    }

    [Fact]
    public void Single_command_delete_plan_carries_only_a_filter_builder()
    {
        using var db = new BulkDbContext();

        var plan = Plan(db, update: false, MongoNonQueryExpression.BulkStrategy.SingleCommand);

        Assert.Equal(MongoBulkOperationKind.Delete, plan.Kind);
        Assert.Equal(MongoBulkStrategy.SingleCommand, plan.Strategy);
        Assert.Equal(db.Model.FindEntityType(typeof(Product))!.GetCollectionName(), plan.CollectionName);
        Assert.NotNull(plan.BuildFilter);
        Assert.Null(plan.BuildUpdate);
        Assert.Null(plan.BuildTargetIdQuery);
    }

    [Fact]
    public void Two_phase_delete_plan_carries_only_a_target_id_query_builder()
    {
        using var db = new BulkDbContext();

        var plan = Plan(db, update: false, MongoNonQueryExpression.BulkStrategy.TwoPhase);

        Assert.Equal(MongoBulkOperationKind.Delete, plan.Kind);
        Assert.Equal(MongoBulkStrategy.TwoPhase, plan.Strategy);
        Assert.Null(plan.BuildFilter);
        Assert.Null(plan.BuildUpdate);
        Assert.NotNull(plan.BuildTargetIdQuery);
    }

    [Fact]
    public void Single_command_update_plan_carries_filter_and_update_builders()
    {
        using var db = new BulkDbContext();

        var plan = Plan(db, update: true, MongoNonQueryExpression.BulkStrategy.SingleCommand);

        Assert.Equal(MongoBulkOperationKind.Update, plan.Kind);
        Assert.Equal(MongoBulkStrategy.SingleCommand, plan.Strategy);
        Assert.NotNull(plan.BuildFilter);
        Assert.NotNull(plan.BuildUpdate);
        Assert.Null(plan.BuildTargetIdQuery);
    }

    [Fact]
    public void Two_phase_update_plan_carries_update_and_target_id_query_builders()
    {
        using var db = new BulkDbContext();

        var plan = Plan(db, update: true, MongoNonQueryExpression.BulkStrategy.TwoPhase);

        Assert.Equal(MongoBulkOperationKind.Update, plan.Kind);
        Assert.Equal(MongoBulkStrategy.TwoPhase, plan.Strategy);
        Assert.Null(plan.BuildFilter);
        Assert.NotNull(plan.BuildUpdate);
        Assert.NotNull(plan.BuildTargetIdQuery);
    }

    [Fact]
    public void EnsureKeyOrThrow_accepts_an_entity_with_a_primary_key()
    {
        using var db = new BulkDbContext();
        var entityType = db.Model.FindEntityType(typeof(Product))!;

        MongoBulkPlanBuilder.EnsureKeyOrThrow(entityType, new MongoNonQueryExpression(new MongoQueryExpression(entityType)));
    }

    [Fact]
    public void EnsureKeyOrThrow_rejects_a_keyless_entity_naming_the_requirement()
    {
        using var db = new BulkDbContext();
        var entityType = db.Model.FindEntityType(typeof(Keyless))!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => MongoBulkPlanBuilder.EnsureKeyOrThrow(
                entityType, new MongoNonQueryExpression(new MongoQueryExpression(entityType))));

        Assert.Contains("primary key", ex.Message);
    }
}

#endif
