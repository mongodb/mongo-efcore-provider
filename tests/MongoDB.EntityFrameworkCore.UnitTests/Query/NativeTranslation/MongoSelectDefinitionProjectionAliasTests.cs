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

using System;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// The projection-alias override carrier on <see cref="MongoSelectDefinition"/> (EF-322 step 3a): the single
/// fact the emit side writes and every alias-deriving site reads, so the emitted <c>$project</c> key and the
/// element name the DOM shaper reads by can never be two independently derived strings.
/// </summary>
public class MongoSelectDefinitionProjectionAliasTests
{
    [Fact]
    public void No_override_registered_reads_as_absent_for_a_named_member()
    {
        var select = new MongoSelectDefinition();

        Assert.False(select.TryGetProjectionAlias("Title", out var alias));
        Assert.Null(alias);
    }

    [Fact]
    public void No_override_registered_reads_as_absent_for_a_bare_body()
    {
        var select = new MongoSelectDefinition();

        // A bare selector body's ProjectionMember has no last member, so the reading sites pass null. That
        // must be a clean miss, not an ArgumentNullException from the backing dictionary.
        Assert.False(select.TryGetProjectionAlias(null, out var alias));
        Assert.Null(alias);
        Assert.False(select.IsBareProjection);
        Assert.Null(select.BareProjectionTier);
    }

    [Fact]
    public void A_null_member_name_maps_onto_the_bare_sentinel_key()
    {
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.DocumentPath);

        // Registered under the sentinel; read back by passing null, which is what the alias-deriving sites do.
        Assert.True(select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Title", alias);

        // And the sentinel is readable by its own literal too — the same single entry, not two.
        Assert.True(select.TryGetProjectionAlias(MongoSelectDefinition.BareProjectionMemberKey, out var same));
        Assert.Equal("Title", same);
    }

    [Fact]
    public void The_bare_sentinel_cannot_collide_with_a_real_member_name()
    {
        // The sentinel carries a leading space, so no CLR member name can spell it.
        Assert.StartsWith(" ", MongoSelectDefinition.BareProjectionMemberKey);

        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("Notes", "Home.Notes", ProjectionAliasTier.DocumentPath);

        // A named-member override does not answer for a bare body...
        Assert.False(select.TryGetProjectionAlias(null, out _));
        Assert.False(select.IsBareProjection);
        Assert.Null(select.BareProjectionTier);

        // ...and it is still readable under its own key.
        Assert.True(select.TryGetProjectionAlias("Notes", out var alias));
        Assert.Equal("Home.Notes", alias);
    }

    [Fact]
    public void A_bare_override_is_read_many_times_with_the_same_answer()
    {
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Posts", ProjectionAliasTier.DocumentPath);

        // Reading is what the four alias-derivation sites do; none of them may consume or mutate the entry.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(select.TryGetProjectionAlias(null, out var alias));
            Assert.Equal("Posts", alias);
            Assert.True(select.IsBareProjection);
            Assert.Equal(ProjectionAliasTier.DocumentPath, select.BareProjectionTier);
        }
    }

    [Fact]
    public void A_document_path_tier_round_trips_through_BareProjectionTier()
        => AssertTierRoundTrips(ProjectionAliasTier.DocumentPath, "Title");

    [Fact]
    public void A_synthetic_tier_round_trips_through_BareProjectionTier()
        => AssertTierRoundTrips(ProjectionAliasTier.Synthetic, "_v");

    // Not a [Theory]: ProjectionAliasTier is internal, so it cannot appear in a public test signature.
    private static void AssertTierRoundTrips(ProjectionAliasTier tier, string alias)
    {
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(MongoSelectDefinition.BareProjectionMemberKey, alias, tier);

        // The tier is carried as DATA so the late-fallback strip never has to sniff the alias string for "_v".
        Assert.Equal(tier, select.BareProjectionTier);
        Assert.True(select.TryGetProjectionAlias(null, out var readBack));
        Assert.Equal(alias, readBack);
    }

    [Fact]
    public void Re_registering_the_same_key_throws_because_the_carrier_is_write_once()
    {
        // SETTLED in Task 2, which is the first task with a writer that could tell whether re-entry was
        // reachable: the carrier is WRITE-ONCE and enforces it, because the failure a second write causes is
        // silent. Two committed aliases for one projection member means the emitted $project key and the name
        // the shaper reads by can disagree, and a missed read returns null for a nullable/reference leaf and an
        // EMPTY collection for an array leaf, with no exception anywhere. This test pins the throw, and the
        // first alias staying readable — a half-applied second write would be worse than either outcome.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.DocumentPath);

        Assert.Throws<ArgumentException>(() => select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic));

        Assert.True(select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Title", alias);
        Assert.Equal(ProjectionAliasTier.DocumentPath, select.BareProjectionTier);
    }

    [Fact]
    public void A_named_override_does_not_report_a_bare_tier()
    {
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride("N", "_v", ProjectionAliasTier.Synthetic);

        Assert.Null(select.BareProjectionTier);
    }

    [Fact]
    public void The_carrier_does_not_by_itself_change_Route()
    {
        // The override is a naming fact, not a representability one: Route still comes from the populated
        // slots alone, so registering an override cannot open the emit gate on its own.
        var select = new MongoSelectDefinition();
        select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Title", ProjectionAliasTier.DocumentPath);

        Assert.Equal(NativeRoute.WholeEntity, select.Route);

        select.AddProjection(new MongoProjection("Title", new MongoConstantExpression(1, forSerialization: null)));
        Assert.Equal(NativeRoute.Projection, select.Route);
    }
}
