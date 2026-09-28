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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>RebindInnerShaperToOuterQuery</c> must not resolve a transitive join hop (one reaching its target through
/// a previously-joined intermediate) against the root entity type, by FK-property name (tier 1) or by target
/// type alone (tier 2). Either would emit an unprefixed <c>localField</c> reading the root's own field.
/// <para>
/// The hop is classified from <c>outerKeySelector.Body</c>'s receiver: only <c>"Outer"</c> members back to the
/// parameter is a root hop; any <c>"Inner"</c> member makes it transitive and skips both root tiers. Separate
/// fixtures cover each tier, since they misfire independently.
/// </para>
/// <para>
/// Assertions pin the navigation's value (<c>"RIGHT*"</c> vs <c>"WRONG"</c> vs null), never just non-null:
/// change-tracker fix-up can repair the graph even when the <c>$lookup</c> matched the wrong field. Seeds point
/// the root's own leaf FK at <c>"WRONG"</c> and the intermediate's at the correct leaf.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef379RootNavigationMisroutingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // Tier 1 (FK-property name): PRoot and PMid both declare "LeafId".

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Colliding_fk_name_transitive_hop_reads_the_intermediate_not_the_root(MongoQueryMode mode)
    {
        using var db = CreateCollidingContext(
            nameof(Colliding_fk_name_transitive_hop_reads_the_intermediate_not_the_root), mode);

        var results = db.PRoots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .OrderBy(r => r.Name)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(["M1", "M2"], results.Select(r => r.Mid.Label));

        // The root's LeafId reaches "WRONG", the mid's "RIGHT1"/"RIGHT2". A root-scoped localField shows up as
        // a null Mid.Leaf; asserting the value catches that and a wrong non-null leaf alike.
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
        Assert.Equal(["RIGHT1", "RIGHT2"], results.Select(r => r.Mid.Leaf.Label));
        Assert.DoesNotContain("WRONG", results.Select(r => r.Mid.Leaf.Label));
    }

    [Fact]
    public void Colliding_fk_name_transitive_hop_prefixes_the_second_localField()
    {
        using var db = CreateCollidingContext(
            nameof(Colliding_fk_name_transitive_hop_prefixes_the_second_localField), MongoQueryMode.Native,
            out var spyLogger);

        var results = db.PRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

        Assert.Equal(2, results.Count);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"MidId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_Mid.LeafId\"", mql);

        // The defect's signature: the leaf $lookup matching the root's own colliding field.
        Assert.DoesNotContain("\"localField\" : \"LeafId\"", mql);
    }

    [Fact]
    public void Colliding_fk_name_transitive_hop_now_goes_native_under_NativeOnly()
    {
        // A routing pin, so NativeOnly: the fallback emits the same flat _lookup_<Nav> shape. A linear 2-hop
        // reference ThenInclude chain goes native.
        using var db = CreateCollidingContext(
            nameof(Colliding_fk_name_transitive_hop_now_goes_native_under_NativeOnly), MongoQueryMode.NativeOnly);

        var results = db.PRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
    }

    // Tier 2 (target type only): no name collision (the root's FK is "SideLeafId"), but the root has a
    // navigation to the leaf type.

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Renamed_fk_transitive_hop_reads_the_intermediate_not_the_root(MongoQueryMode mode)
    {
        using var db = CreateRenamedContext(
            nameof(Renamed_fk_transitive_hop_reads_the_intermediate_not_the_root), mode);

        var results = db.RRoots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .OrderBy(r => r.Name)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(["RM1", "RM2"], results.Select(r => r.Mid.Label));

        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
        Assert.Equal(["RIGHT1", "RIGHT2"], results.Select(r => r.Mid.Leaf.Label));
        Assert.DoesNotContain("WRONG", results.Select(r => r.Mid.Leaf.Label));
    }

    [Fact]
    public void Renamed_fk_transitive_hop_prefixes_the_second_localField()
    {
        using var db = CreateRenamedContext(
            nameof(Renamed_fk_transitive_hop_prefixes_the_second_localField), MongoQueryMode.Native,
            out var spyLogger);

        var results = db.RRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

        Assert.Equal(2, results.Count);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"MidId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_Mid.LeafId\"", mql);

        // The tier-2 defect's signature: a leaf $lookup resolved off RRoot.SideLeaf, matching the root's field.
        Assert.DoesNotContain("\"localField\" : \"SideLeafId\"", mql);
        Assert.DoesNotContain("_lookup_SideLeaf", mql);
    }

    [Fact]
    public void Renamed_fk_transitive_hop_now_goes_native_under_NativeOnly()
    {
        // A routing pin, not a guard for the misrouting fix (see the tier-1 twin).
        using var db = CreateRenamedContext(
            nameof(Renamed_fk_transitive_hop_now_goes_native_under_NativeOnly), MongoQueryMode.NativeOnly);

        var results = db.RRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
    }

    // The intermediate has only a bare FK property (no navigation), while the root has both the same-named FK
    // and a navigation to the joined type. The transitive scan finds no candidate, no $lookup is registered,
    // and the driver resolves the join correctly, as long as the root tiers are still skipped. The only test
    // here that catches a gate tightened to "skip the root tiers only when a transitive candidate exists".

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void No_intermediate_navigation_transitive_hop_still_skips_the_root_tiers(MongoQueryMode mode)
    {
        using var db = CreateNoNavContext(
            nameof(No_intermediate_navigation_transitive_hop_still_skips_the_root_tiers), mode);

        var rows = (from r in db.NRoots
                    join m in db.NMids on r.MidId equals m.Id
                    join l in db.NLeaves on m.LeafId equals l.Id
                    select new { r.Name, Leaf = l.Label })
            .OrderBy(x => x.Name)
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["N1", "N2"], rows.Select(x => x.Name));

        // The root's LeafId reaches "WRONG"; the mid's reaches "RIGHT1"/"RIGHT2".
        Assert.Equal(["RIGHT1", "RIGHT2"], rows.Select(x => x.Leaf));
        Assert.DoesNotContain("WRONG", rows.Select(x => x.Leaf));
    }

    // Control against over-broad transitive classification: sibling reference Includes onto different types.
    // The second hop's receiver "s.Outer" is a root hop at the same depth as a transitive "j.Outer.Inner", so
    // depth can't be the discriminator. Must keep unprefixed localFields.

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sibling_root_reference_Includes_stay_root_scoped(MongoQueryMode mode)
    {
        using var db = CreateSiblingContext(nameof(Sibling_root_reference_Includes_stay_root_scoped), mode);

        var results = db.SRoots
            .Include(r => r.Alpha)
            .Include(r => r.Beta)
            .OrderBy(r => r.Name)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(["A1", "A2"], results.Select(r => r.Alpha.Label));
        Assert.Equal(["B1", "B2"], results.Select(r => r.Beta.Label));
    }

    [Fact]
    public void Sibling_root_reference_Includes_emit_unprefixed_localFields()
    {
        using var db = CreateSiblingContext(
            nameof(Sibling_root_reference_Includes_emit_unprefixed_localFields), MongoQueryMode.Native,
            out var spyLogger);

        var results = db.SRoots.Include(r => r.Alpha).Include(r => r.Beta).ToList();

        Assert.Equal(2, results.Count);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"AlphaId\"", mql);
        Assert.Contains("\"localField\" : \"BetaId\"", mql);
        Assert.DoesNotContain("_lookup_Alpha.BetaId", mql);
        Assert.DoesNotContain("_lookup_Beta.AlphaId", mql);
    }

    // Self-referencing two-hop chain: hop 2's receiver "f.Inner" has the root's CLR type, which is why the
    // classification keys on the member-name chain, not the receiver type. Classified transitive.

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Self_referencing_two_hop_chain_now_returns_the_correct_chain(MongoQueryMode mode)
    {
        using var db = CreateSelfRefContext(nameof(Self_referencing_two_hop_chain_now_returns_the_correct_chain), mode);

        var nodes = db.FNodes
            .Include(n => n.Parent)
            .ThenInclude(p => p.Parent)
            .OrderBy(n => n.Label)
            .ToList();

        // Both hops resolve the same navigation, so each join needs its own JoinInfo and uniquified _lookup_
        // alias. Assert values, never non-null: the seed cycle F1 -> F2 -> F3 -> F1 makes a collapsed chain
        // (Parent.Parent == Parent) distinguishable at every row.
        Assert.Equal(["F1", "F2", "F3"], nodes.Select(n => n.Label).ToArray());
        Assert.Equal(["F2", "F3", "F1"], nodes.Select(n => n.Parent.Label).ToArray());
        Assert.Equal(["F3", "F1", "F2"], nodes.Select(n => n.Parent.Parent.Label).ToArray());
    }

    [Fact]
    public void Self_referencing_two_hop_chain_now_goes_native_under_NativeOnly()
    {
        // A linear ThenInclude chain goes native, including the self-referencing case (per-join JoinInfo and
        // uniquified aliases disambiguate the hops).
        using var db = CreateSelfRefContext(
            nameof(Self_referencing_two_hop_chain_now_goes_native_under_NativeOnly), MongoQueryMode.NativeOnly);

        var nodes = db.FNodes.Include(n => n.Parent).ThenInclude(p => p.Parent).OrderBy(n => n.Label).ToList();

        Assert.Equal(["F1", "F2", "F3"], nodes.Select(n => n.Label).ToArray());
        Assert.Equal(["F2", "F3", "F1"], nodes.Select(n => n.Parent.Label).ToArray());
        Assert.Equal(["F3", "F1", "F2"], nodes.Select(n => n.Parent.Parent.Label).ToArray());
    }

    // An owned-collection SelectMany also produces a transparent identifier, so the first TranslateJoinCore
    // call can see a "ti.Inner" receiver and classify TransitiveHop with no prior inner collection. A
    // "transitive hop with no navigation declines" rule would hard-fail this user-authored join in every mode.

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Owned_SelectMany_then_join_off_the_unwound_element_still_works(MongoQueryMode mode)
    {
        using var db = CreateOwnedJoinContext(
            nameof(Owned_SelectMany_then_join_off_the_unwound_element_still_works), mode);

        var rows = (from o in db.JOrders
                    from t in o.Tags
                    join p in db.JProducts on t.ProductId equals p.Id
                    select new { o.Total, p.Name })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Gadget", "Widget", "Widget"], rows.Select(r => r.Name));
        Assert.Equal([10m, 10m, 20m], rows.Select(r => r.Total));
    }

    [Fact]
    public void Self_referencing_single_hop_still_works()
    {
        // Control: a shallow self-reference must keep working.
        using var db = CreateSelfRefContext(nameof(Self_referencing_single_hop_still_works),
            MongoQueryMode.Native, out var spyLogger);

        var results = db.FNodes.Include(n => n.Parent).OrderBy(n => n.Label).ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(["F2", "F3", "F1"], results.Select(n => n.Parent.Label));

        spyLogger.AssertExecutedMqlContains("\"localField\" : \"ParentId\"");
    }

    // Fixtures.

    private CollidingChainDbContext CreateCollidingContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PL" + suffix;

        // The root's LeafId reaches "WRONG", the mid's "RIGHT1"/"RIGHT2". A root-scoped $lookup still matches a
        // real document, so only the value discriminates.
        var wrongLeaf = ObjectId.GenerateNewId();
        var rightLeaf1 = ObjectId.GenerateNewId();
        var rightLeaf2 = ObjectId.GenerateNewId();
        var mid1 = ObjectId.GenerateNewId();
        var mid2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<PLeaf>(leaves).InsertMany(
        [
            new() { Id = wrongLeaf, Label = "WRONG" },
            new() { Id = rightLeaf1, Label = "RIGHT1" },
            new() { Id = rightLeaf2, Label = "RIGHT2" },
        ]);
        database.MongoDatabase.GetCollection<PMid>(mids).InsertMany(
        [
            new() { Id = mid1, Label = "M1", LeafId = rightLeaf1 },
            new() { Id = mid2, Label = "M2", LeafId = rightLeaf2 },
        ]);
        database.MongoDatabase.GetCollection<PRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "P1", MidId = mid1, LeafId = wrongLeaf },
            new() { Id = ObjectId.GenerateNewId(), Name = "P2", MidId = mid2, LeafId = wrongLeaf },
        ]);

        return new CollidingChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }

    private CollidingChainDbContext CreateCollidingContext(
        string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateCollidingContext(name, mode, loggerFactory);
    }

    private RenamedChainDbContext CreateRenamedContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "RR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "RM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "RL" + suffix;

        var wrongLeaf = ObjectId.GenerateNewId();
        var rightLeaf1 = ObjectId.GenerateNewId();
        var rightLeaf2 = ObjectId.GenerateNewId();
        var mid1 = ObjectId.GenerateNewId();
        var mid2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<RLeaf>(leaves).InsertMany(
        [
            new() { Id = wrongLeaf, Label = "WRONG" },
            new() { Id = rightLeaf1, Label = "RIGHT1" },
            new() { Id = rightLeaf2, Label = "RIGHT2" },
        ]);
        database.MongoDatabase.GetCollection<RMid>(mids).InsertMany(
        [
            new() { Id = mid1, Label = "RM1", LeafId = rightLeaf1 },
            new() { Id = mid2, Label = "RM2", LeafId = rightLeaf2 },
        ]);
        database.MongoDatabase.GetCollection<RRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "S1", MidId = mid1, SideLeafId = wrongLeaf },
            new() { Id = ObjectId.GenerateNewId(), Name = "S2", MidId = mid2, SideLeafId = wrongLeaf },
        ]);

        return new RenamedChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }

    private RenamedChainDbContext CreateRenamedContext(
        string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateRenamedContext(name, mode, loggerFactory);
    }

    private NoNavChainDbContext CreateNoNavContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "NR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "NM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "NL" + suffix;

        // Same disagreement as the tier fixtures: roots point at "WRONG", mids at their own "RIGHT*" leaf.
        var wrongLeaf = ObjectId.GenerateNewId();
        var rightLeaf1 = ObjectId.GenerateNewId();
        var rightLeaf2 = ObjectId.GenerateNewId();
        var mid1 = ObjectId.GenerateNewId();
        var mid2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<NLeaf>(leaves).InsertMany(
        [
            new() { Id = wrongLeaf, Label = "WRONG" },
            new() { Id = rightLeaf1, Label = "RIGHT1" },
            new() { Id = rightLeaf2, Label = "RIGHT2" },
        ]);
        database.MongoDatabase.GetCollection<NMid>(mids).InsertMany(
        [
            new() { Id = mid1, Label = "NM1", LeafId = rightLeaf1 },
            new() { Id = mid2, Label = "NM2", LeafId = rightLeaf2 },
        ]);
        database.MongoDatabase.GetCollection<NRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "N1", MidId = mid1, LeafId = wrongLeaf },
            new() { Id = ObjectId.GenerateNewId(), Name = "N2", MidId = mid2, LeafId = wrongLeaf },
        ]);

        return new NoNavChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }

    private SiblingRootDbContext CreateSiblingContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "SR" + suffix;
        var alphas = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "SA" + suffix;
        var betas = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "SB" + suffix;

        var alpha1 = ObjectId.GenerateNewId();
        var alpha2 = ObjectId.GenerateNewId();
        var beta1 = ObjectId.GenerateNewId();
        var beta2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<SAlpha>(alphas).InsertMany(
        [
            new() { Id = alpha1, Label = "A1" },
            new() { Id = alpha2, Label = "A2" },
        ]);
        database.MongoDatabase.GetCollection<SBeta>(betas).InsertMany(
        [
            new() { Id = beta1, Label = "B1" },
            new() { Id = beta2, Label = "B2" },
        ]);
        database.MongoDatabase.GetCollection<SRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "T1", AlphaId = alpha1, BetaId = beta1 },
            new() { Id = ObjectId.GenerateNewId(), Name = "T2", AlphaId = alpha2, BetaId = beta2 },
        ]);

        return new SiblingRootDbContext(database, roots, alphas, betas, mode, loggerFactory);
    }

    private SiblingRootDbContext CreateSiblingContext(
        string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateSiblingContext(name, mode, loggerFactory);
    }

    private SelfRefDbContext CreateSelfRefContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodes = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "FN" + suffix;

        // Required, so both hops lower to Queryable.Join on every EF major (optional would be EF10-only
        // LeftJoin). A required FK needs a cycle: F1 -> F2 -> F3 -> F1.
        var f1 = ObjectId.GenerateNewId();
        var f2 = ObjectId.GenerateNewId();
        var f3 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<FNode>(nodes).InsertMany(
        [
            new() { Id = f1, Label = "F1", ParentId = f2 },
            new() { Id = f2, Label = "F2", ParentId = f3 },
            new() { Id = f3, Label = "F3", ParentId = f1 },
        ]);

        return new SelfRefDbContext(database, nodes, mode, loggerFactory);
    }

    private SelfRefDbContext CreateSelfRefContext(string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateSelfRefContext(name, mode, loggerFactory);
    }

    private OwnedJoinDbContext CreateOwnedJoinContext(string name, MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orders = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "JO" + suffix;
        var products = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "JP" + suffix;

        var widget = ObjectId.GenerateNewId();
        var gadget = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<JProduct>(products).InsertMany(
        [
            new() { Id = widget, Name = "Widget" },
            new() { Id = gadget, Name = "Gadget" },
        ]);
        database.MongoDatabase.GetCollection<JOrder>(orders).InsertMany(
        [
            new()
            {
                Id = ObjectId.GenerateNewId(), Total = 10m,
                Tags = [new() { ProductId = widget }, new() { ProductId = gadget }]
            },
            new() { Id = ObjectId.GenerateNewId(), Total = 20m, Tags = [new() { ProductId = widget }] },
        ]);

        return new OwnedJoinDbContext(database, orders, products, mode);
    }

    private static DbContextOptions<TContext> Configure<TContext>(
        TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        if (loggerFactory != null)
        {
            builder = builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
        }

        return builder.Options;
    }

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    // Tier-1 model: the root declares "LeafId", like the intermediate.

    private class PLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class PMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId LeafId { get; set; }
        public PLeaf Leaf { get; set; } = null!;
    }

    private class PRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public PMid Mid { get; set; } = null!;

        // Same name as PMid's FK; the FK-name tier would resolve PRoot.Leaf for PMid's hop.
        public ObjectId LeafId { get; set; }
        public PLeaf Leaf { get; set; } = null!;
    }

    private class CollidingChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public CollidingChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure<CollidingChainDbContext>(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        public DbSet<PRoot> PRoots { get; set; } = null!;
        public DbSet<PMid> PMids { get; set; } = null!;
        public DbSet<PLeaf> PLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<PRoot>().ToCollection(_roots);
            modelBuilder.Entity<PRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<PRoot>().HasOne(r => r.Leaf).WithMany().HasForeignKey(r => r.LeafId);
            modelBuilder.Entity<PMid>().ToCollection(_mids);
            modelBuilder.Entity<PMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<PLeaf>().ToCollection(_leaves);
        }
    }

    // Tier-2 model: no name collision, but the root navigates to the leaf type.

    private class RLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class RMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId LeafId { get; set; }
        public RLeaf Leaf { get; set; } = null!;
    }

    private class RRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public RMid Mid { get; set; } = null!;
        public ObjectId SideLeafId { get; set; }
        public RLeaf SideLeaf { get; set; } = null!;
    }

    private class RenamedChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public RenamedChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure<RenamedChainDbContext>(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        public DbSet<RRoot> RRoots { get; set; } = null!;
        public DbSet<RMid> RMids { get; set; } = null!;
        public DbSet<RLeaf> RLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<RRoot>().ToCollection(_roots);
            modelBuilder.Entity<RRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<RRoot>().HasOne(r => r.SideLeaf).WithMany().HasForeignKey(r => r.SideLeafId);
            modelBuilder.Entity<RMid>().ToCollection(_mids);
            modelBuilder.Entity<RMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<RLeaf>().ToCollection(_leaves);
        }
    }

    // No-intermediate-navigation model: NMid has a bare FK only (so the chain is a user-authored Join); NRoot
    // has both the same-named FK and a navigation to the leaf type.

    private class NLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class NMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";

        // Deliberately no NLeaf navigation.
        public ObjectId LeafId { get; set; }
    }

    private class NRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public NMid Mid { get; set; } = null!;

        // Bait for both root tiers: NMid's FK name and a navigation to the joined type.
        public ObjectId LeafId { get; set; }
        public NLeaf Leaf { get; set; } = null!;
    }

    private class NoNavChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public NoNavChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure<NoNavChainDbContext>(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        public DbSet<NRoot> NRoots { get; set; } = null!;
        public DbSet<NMid> NMids { get; set; } = null!;
        public DbSet<NLeaf> NLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NRoot>().ToCollection(_roots);
            modelBuilder.Entity<NRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<NRoot>().HasOne(r => r.Leaf).WithMany().HasForeignKey(r => r.LeafId);
            modelBuilder.Entity<NMid>().ToCollection(_mids);
            modelBuilder.Entity<NLeaf>().ToCollection(_leaves);
        }
    }

    // Sibling control model: two reference navigations onto different target types.

    private class SAlpha
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class SBeta
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class SRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId AlphaId { get; set; }
        public SAlpha Alpha { get; set; } = null!;
        public ObjectId BetaId { get; set; }
        public SBeta Beta { get; set; } = null!;
    }

    private class SiblingRootDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _alphas;
        private readonly string _betas;

        public SiblingRootDbContext(
            TemporaryDatabaseFixture database, string roots, string alphas, string betas,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure<SiblingRootDbContext>(database, mode, loggerFactory))
        {
            _roots = roots;
            _alphas = alphas;
            _betas = betas;
        }

        public DbSet<SRoot> SRoots { get; set; } = null!;
        public DbSet<SAlpha> SAlphas { get; set; } = null!;
        public DbSet<SBeta> SBetas { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<SRoot>().ToCollection(_roots);
            modelBuilder.Entity<SRoot>().HasOne(r => r.Alpha).WithMany().HasForeignKey(r => r.AlphaId);
            modelBuilder.Entity<SRoot>().HasOne(r => r.Beta).WithMany().HasForeignKey(r => r.BetaId);
            modelBuilder.Entity<SAlpha>().ToCollection(_alphas);
            modelBuilder.Entity<SBeta>().ToCollection(_betas);
        }
    }

    // Self-reference model: a required self-referencing navigation.

    private class FNode
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId ParentId { get; set; }
        public FNode Parent { get; set; } = null!;
    }

    // Owned-SelectMany model: owned elements carry a bare ObjectId FK (no navigation), so the join onto
    // JProduct is user-authored.

    private class JProduct
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class JTag
    {
        public ObjectId ProductId { get; set; }
    }

    private class JOrder
    {
        public ObjectId Id { get; set; }
        public decimal Total { get; set; }
        public List<JTag> Tags { get; set; } = [];
    }

    private class OwnedJoinDbContext(
        TemporaryDatabaseFixture database, string orders, string products, MongoQueryMode mode)
        : DbContext(Configure<OwnedJoinDbContext>(database, mode, null))
    {
        public DbSet<JOrder> JOrders { get; set; } = null!;
        public DbSet<JProduct> JProducts { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JOrder>().ToCollection(orders);
            modelBuilder.Entity<JOrder>().OwnsMany(o => o.Tags);
            modelBuilder.Entity<JProduct>().ToCollection(products);
        }
    }

    private class SelfRefDbContext : DbContext
    {
        private readonly string _nodes;

        public SelfRefDbContext(
            TemporaryDatabaseFixture database, string nodes, MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure<SelfRefDbContext>(database, mode, loggerFactory))
            => _nodes = nodes;

        public DbSet<FNode> FNodes { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<FNode>().ToCollection(_nodes);
            modelBuilder.Entity<FNode>().HasOne(n => n.Parent).WithMany().HasForeignKey(n => n.ParentId);
        }
    }
}
