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

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

/// <summary>
/// <c>MongoQueryExpression.ApplyProjection</c>'s alias derivation: normally the projection member's own name, or
/// the override registered on <see cref="MongoSelectDefinition"/> while the query is still on the projection route.
/// </summary>
public class MongoQueryExpressionApplyProjectionTests
{
    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = null!;
        public decimal Price { get; set; }
    }

    class QueryDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    private static IEntityType ProductEntityType()
    {
        using var db = new QueryDbContext();
        return db.Model.FindEntityType(typeof(Product))!;
    }

    /// <summary>Populates <c>Projection</c> so <c>Route</c> resolves to <see cref="NativeRoute.Projection"/>.</summary>
    private static void MakeProjectionRoute(MongoQueryExpression queryExpression)
        => queryExpression.Select.AddProjection(
            new MongoProjection("Title", new MongoConstantExpression(1, forSerialization: null)));

    [Fact]
    public void An_empty_override_table_leaves_a_bare_member_with_a_null_alias()
    {
        // The root EntityProjectionExpression sits under the empty ProjectionMember (the bare-selector shape), so
        // with no override the alias is null.
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        MakeProjectionRoute(queryExpression);

        queryExpression.ApplyProjection();

        Assert.Null(Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void A_registered_bare_override_becomes_the_projection_alias()
    {
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        MakeProjectionRoute(queryExpression);
        queryExpression.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Name", ProjectionAliasTier.DocumentPath);

        queryExpression.ApplyProjection();

        Assert.Equal("Name", Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void A_registered_synthetic_bare_override_becomes_the_projection_alias()
    {
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        MakeProjectionRoute(queryExpression);
        queryExpression.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic);

        queryExpression.ApplyProjection();

        // ApplyProjection reads the alias only; the tier matters for the late-fallback strip, not here.
        Assert.Equal("_v", Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void An_override_is_ignored_once_Route_left_Projection_via_Fallback()
    {
        // The emit side registers an override, then a later operator marks the query non-native: the alias must
        // revert rather than name a $project that won't be emitted.
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        MakeProjectionRoute(queryExpression);
        queryExpression.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Name", ProjectionAliasTier.DocumentPath);
        queryExpression.Select.MarkNotNativelyRepresentable();

        Assert.Equal(NativeRoute.Fallback, queryExpression.Select.Route);

        queryExpression.ApplyProjection();

        Assert.Null(Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void An_override_is_ignored_once_Route_flipped_to_GroupBy()
    {
        // Real-world shape of the same hazard: a projected Distinct clears Projection, installs a Grouping and flips
        // Route to GroupBy after the emit side committed its override.
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        MakeProjectionRoute(queryExpression);
        queryExpression.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Name", ProjectionAliasTier.DocumentPath);
        queryExpression.Select.ClearProjections();
        queryExpression.Select.Grouping = new MongoGrouping(
            [new MongoGroupingKeyPart(null, new MongoConstantExpression(1, forSerialization: null))], []);

        Assert.Equal(NativeRoute.GroupBy, queryExpression.Select.Route);

        queryExpression.ApplyProjection();

        Assert.Null(Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void An_override_is_ignored_on_a_whole_entity_query()
    {
        var queryExpression = new MongoQueryExpression(ProductEntityType());
        queryExpression.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Name", ProjectionAliasTier.DocumentPath);

        Assert.Equal(NativeRoute.WholeEntity, queryExpression.Select.Route);

        queryExpression.ApplyProjection();

        Assert.Null(Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void A_named_member_keeps_its_own_name_when_no_override_is_registered()
    {
        var entityType = ProductEntityType();
        var queryExpression = new MongoQueryExpression(entityType);
        MakeProjectionRoute(queryExpression);
        RemapToNamedMember(queryExpression, entityType);

        queryExpression.ApplyProjection();

        Assert.Equal(nameof(Product.Name), Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void A_named_member_takes_its_registered_override()
    {
        // A named member whose emitted element name is its full document path.
        var entityType = ProductEntityType();
        var queryExpression = new MongoQueryExpression(entityType);
        MakeProjectionRoute(queryExpression);
        RemapToNamedMember(queryExpression, entityType);
        queryExpression.Select.AddProjectionAliasOverride(
            nameof(Product.Name), "Home.Name", ProjectionAliasTier.DocumentPath);

        queryExpression.ApplyProjection();

        Assert.Equal("Home.Name", Assert.Single(queryExpression.Projection).Alias);
    }

    private static void RemapToNamedMember(MongoQueryExpression queryExpression, IEntityType entityType)
        => queryExpression.ReplaceProjectionMapping(
            new Dictionary<ProjectionMember, Expression>
            {
                [new ProjectionMember().Append(typeof(Product).GetProperty(nameof(Product.Name))!)] =
                    new EntityProjectionExpression(entityType, new RootReferenceExpression(entityType))
            });

    // The two halves of ApplyProjection's "already has projection entries" guard; each test below pins one half.

    /// <summary>
    /// Simulates the reference-collection-list array leaf's own <c>AddToProjection</c> call, made before
    /// <c>ApplyProjection</c> runs. Unlike <see cref="MakeProjectionRoute"/>, this populates
    /// <c>MongoQueryExpression.Projection</c> itself.
    /// </summary>
    private static void SimulateArrayLeafProjectionRegistration(MongoQueryExpression queryExpression)
        => queryExpression.AddToProjection(Expression.Constant("array-leaf-placeholder"), "Orders");

    [Fact]
    public void A_scalar_sibling_mapping_is_flattened_even_when_Projection_is_already_non_empty()
    {
        // Projection is already non-empty (array leaf), but a scalar sibling's ProjectionMember mapping still needs
        // flattening to a Constant(int), or GetProjectionIndex throws at compile time. Fails if the guard reverts
        // to "if (Projection.Any()) return;".
        var entityType = ProductEntityType();
        var queryExpression = new MongoQueryExpression(entityType);
        MakeProjectionRoute(queryExpression);
        SimulateArrayLeafProjectionRegistration(queryExpression);
        RemapToNamedMember(queryExpression, entityType);

        queryExpression.ApplyProjection();

        Assert.Equal(2, queryExpression.Projection.Count);
        var mappedMember = new ProjectionMember().Append(typeof(Product).GetProperty(nameof(Product.Name))!);
        var mapped = queryExpression.GetMappedProjection(mappedMember);
        var constant = Assert.IsType<ConstantExpression>(mapped);
        var index = Assert.IsType<int>(constant.Value);
        Assert.Equal(nameof(Product.Name), queryExpression.Projection[index].Alias);
    }

    [Fact]
    public void A_sibling_mapping_is_NOT_flattened_once_Route_has_left_Projection()
    {
        // Once Route has left Projection (native declined, falls back), a non-constant mapping must not be
        // flattened even though Projection has entries; otherwise
        // NorthwindSelectQueryMongoTest.Custom_projection_reference_navigation_PK_to_FK_optimization silently
        // succeeds instead of throwing. Fails if the "|| Select.Route != NativeRoute.Projection" disjunct is dropped.
        var entityType = ProductEntityType();
        var queryExpression = new MongoQueryExpression(entityType);
        MakeProjectionRoute(queryExpression);
        SimulateArrayLeafProjectionRegistration(queryExpression);
        RemapToNamedMember(queryExpression, entityType);
        queryExpression.Select.MarkNotNativelyRepresentable();
        Assert.Equal(NativeRoute.Fallback, queryExpression.Select.Route);

        queryExpression.ApplyProjection();

        // Only the pre-existing array-leaf placeholder entry; nothing new was flattened in.
        Assert.Single(queryExpression.Projection);
        var mappedMember = new ProjectionMember().Append(typeof(Product).GetProperty(nameof(Product.Name))!);
        var mapped = queryExpression.GetMappedProjection(mappedMember);
        Assert.IsNotType<ConstantExpression>(mapped);
    }
}
