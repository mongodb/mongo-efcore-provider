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
/// <c>MongoQueryExpression.ApplyProjection</c>'s alias derivation — site A of EF-322 step 3a's four
/// alias-derivation sites. It normally uses the projection member's own name; when the emit side registered
/// an override on <see cref="MongoSelectDefinition"/> (and only while the query is still on the projection
/// route) it uses that instead.
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
        // The constructor installs the root EntityProjectionExpression under the EMPTY ProjectionMember —
        // the same "no last member" shape a bare selector body produces — so with no override registered the
        // alias must still come out null, exactly as before step 3a.
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

        // Site A reads the alias only; the tier exists for the late-fallback strip, not for this site.
        Assert.Equal("_v", Assert.Single(queryExpression.Projection).Alias);
    }

    [Fact]
    public void An_override_is_ignored_once_Route_left_Projection_via_Fallback()
    {
        // The hazard this guard closes: the emit side registers an override, then a later operator marks the
        // query non-native. Site A must revert to the pre-3a derivation rather than alias a $project that is
        // no longer going to be emitted.
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
        // The measured shape of the same hazard: a projected Distinct clears Projection, installs a Grouping
        // and flips Route to GroupBy AFTER the emit side already committed its override.
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
        // Not reached by step 3a itself (which only ever registers the bare sentinel), but this is the shape
        // EF-362 needs: a named member whose emitted element name is its full document path.
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
}
