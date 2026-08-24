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
/// EF-379. <c>MongoQueryableMethodTranslatingExpressionVisitor.RebindInnerShaperToOuterQuery</c> resolved a
/// join's navigation off the ROOT entity type — first by FK-property NAME, then by TARGET ENTITY TYPE alone —
/// before ever considering that the hop might be TRANSITIVE. Both root tiers DROP the RECEIVER of the FK
/// access, so a hop that actually reaches its target THROUGH a previously-joined intermediate could match a
/// ROOT navigation, be treated as root-level, emit an UNPREFIXED <c>localField</c> reading the ROOT's own
/// field, and never consult EF-372's prefix-or-decline resolver at all.
/// <para>
/// The fix classifies the hop from <c>outerKeySelector.Body</c>'s RECEIVER before attempting either root tier:
/// a receiver that peels back to the lambda parameter through only <c>"Outer"</c> members is a ROOT hop; one
/// whose chain contains any <c>"Inner"</c> member is TRANSITIVE and skips both root tiers.
/// </para>
/// <para>
/// BOTH root tiers misfired INDEPENDENTLY, so this file covers BOTH doorways with two separate fixtures:
/// the FK-NAME collision (<c>PRoot.LeafId</c> / <c>PMid.LeafId</c> — tier 1) and the RENAMED-FK /
/// type-only shape (<c>RRoot.SideLeafId</c>, no name collision anywhere — tier 2). A fix scoped to the
/// FK-name tier leaves the second one broken.
/// </para>
/// <para>
/// Every data assertion here pins the navigation's VALUE (<c>"RIGHT*"</c> vs <c>"WRONG"</c> vs null), never
/// merely <c>!= null</c>: EF's change-tracker identity fix-up can repair the object graph while the
/// <c>$lookup</c> matched the wrong field (the header of <c>Ef372DeepReferenceIncludeTests</c> records that
/// exact masking), so an existence-only assertion is not a discriminator. The seeds therefore make the two
/// paths DISAGREE — the ROOT's own leaf FK points at a leaf labelled <c>"WRONG"</c>, the INTERMEDIATE's at
/// the correct one. The measured symptom of the defect is a NULL navigation, not a wrong non-null value.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef379RootNavigationMisroutingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- Doorway 1: tier 1 (FK-property NAME). PRoot and PMid both declare a property named "LeafId". ----

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

        // The teeth. The ROOT's own LeafId reaches the leaf labelled "WRONG"; the MID's LeafId reaches
        // "RIGHT1"/"RIGHT2". At this branch's base the emitted localField was the root's unprefixed "LeafId",
        // and the MEASURED symptom was Mid.Leaf == null (the wrong leaf was unwound but the shaper, which
        // reads the mid's leaf, never picked it up). Assert the VALUE so either failure mode is caught.
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

        // The defect's signature: the leaf $lookup matching the ROOT's own colliding field.
        Assert.DoesNotContain("\"localField\" : \"LeafId\"", mql);
    }

    [Fact]
    public void Colliding_fk_name_transitive_hop_still_declines_under_NativeOnly()
    {
        // A ROUTING claim, so it needs NativeOnly — MQL shape cannot prove which path ran, and since EF-370
        // the driver-LINQ fallback emits the same flat _lookup_<Nav> shape. Multi-level (ThenInclude)
        // reference Include is a DEFERRED native shape, so it declines to the fallback; this fix changes the
        // localField it emits, not which path emits it.
        using var db = CreateCollidingContext(
            nameof(Colliding_fk_name_transitive_hop_still_declines_under_NativeOnly), MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.PRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList());
    }

    // ---- Doorway 2: tier 2 (TARGET ENTITY TYPE only). NO name collision exists anywhere in this model —
    // the root's FK is "SideLeafId" — yet the root still carries a navigation to the leaf TYPE, which is all
    // the type-only fallback needs to misfire. A fix that gates only tier 1 leaves this broken. ----

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

        // The tier-2 defect's signature: the leaf $lookup resolved off RRoot.SideLeaf and matching the
        // root's own renamed field, under the alias derived from that wrong navigation.
        Assert.DoesNotContain("\"localField\" : \"SideLeafId\"", mql);
        Assert.DoesNotContain("_lookup_SideLeaf", mql);
    }

    [Fact]
    public void Renamed_fk_transitive_hop_still_declines_under_NativeOnly()
    {
        // Same caveat as the tier-1 twin above, repeated rather than cross-referenced because the NAME reads
        // like a defect assertion and is not one: this is a ROUTING pin, NOT a guard for the EF-379 fix.
        // Multi-level (ThenInclude) reference Include is a DEFERRED native shape, so it declines to the
        // driver-LINQ fallback with or without this fix — the fix changes the localField that path emits,
        // not which path emits it. It is green against the unfixed tree too.
        using var db = CreateRenamedContext(
            nameof(Renamed_fk_transitive_hop_still_declines_under_NativeOnly), MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.RRoots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList());
    }

    // ---- The THIRD wrong-data family, and the one the two doorways above do NOT reach. Both of those give
    // the intermediate a NAVIGATION to the leaf, so they only exercise the path where the transitive scan
    // FINDS a candidate and swaps in an intermediate-scoped localField. Here the intermediate declares only a
    // BARE FK PROPERTY and no navigation at all, while the ROOT declares BOTH the FK and a navigation to the
    // joined type — so the scan finds nothing, `navigation` stays null, no $lookup is registered for this hop
    // and the join is left to the driver, which resolves it correctly.
    //
    // The wrong data at base came from the OTHER end: with no classification, tier 1 matched NRoot.Leaf on the
    // FK name "LeafId" (and tier 2 would have matched it on target type alone), registering a $lookup whose
    // unprefixed localField reads the ROOT's own LeafId. MEASURED: base returns "WRONG", HEAD returns
    // "RIGHT1"/"RIGHT2", in Native AND DriverLinq alike.
    //
    // WHY IT EARNS ITS PLACE, measured rather than asserted. The review that asked for this test predicted it
    // would stay GREEN under mutations A/B/C, making it orthogonal to the two doorways. That prediction is
    // FALSE for B and C and is recorded here corrected: this model's root carries BOTH baits (the colliding FK
    // NAME and a navigation onto the leaf TYPE), so gating either tier alone leaves the other one to match.
    // Measured red/green over the whole class, EF10:
    //
    //   base (2a544b7e)                                      RED  — ["WRONG","WRONG"], Native AND DriverLinq
    //   A  force TransitiveHop unconditionally               green
    //   B  gate tier 1 only                                  RED
    //   C  gate tier 2 only                                  RED
    //   D  re-add the withdrawn TransitiveHop decline        RED  (no candidate ⇒ the decline hard-fails it)
    //   E  force the declaring-type conjunct FALSE           RED
    //   F  force the declaring-type conjunct TRUE            green
    //   G  "only skip the root tiers when a transitive        RED  — and it is the ONLY test in this class
    //      candidate EXISTS"                                        that goes red under G (2 of 19)
    //
    // G is the discrimination that matters and the reason this is a separate test: it is exactly the plausible
    // future tightening of the gate, it looks harmless on both doorways above (they HAVE a candidate, so they
    // stay green), and it silently reintroduces the wrong answer here. Nothing else in the class catches it. ----

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

        // The teeth: the VALUE, not merely non-null. The root's own LeafId reaches the leaf labelled "WRONG";
        // the mid's reaches "RIGHT1"/"RIGHT2".
        Assert.Equal(["RIGHT1", "RIGHT2"], rows.Select(x => x.Leaf));
        Assert.DoesNotContain("WRONG", rows.Select(x => x.Leaf));
    }

    // ---- The control / tripwire against an OVER-BROAD transitive classification. Two SIBLING reference
    // Includes onto DIFFERENT target types: the second hop's receiver is "s.Outer", a genuine ROOT hop at the
    // same member-chain DEPTH as a transitive "j.Outer.Inner", which is exactly why depth cannot be the
    // discriminator. This must keep taking the root tiers and keep emitting an UNPREFIXED localField. ----

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

    // ---- Scenario F: a SELF-REFERENCING two-hop chain. The receiver of hop 2 is "f.Inner" typed FNode —
    // the SAME CLR type as the root — which is the counter-example to keying the classification on the
    // receiver's CLR TYPE rather than on its member-name chain. It is classified TRANSITIVE, but the
    // transitive resolver still cannot represent it (a self-referencing intermediate is skipped by the
    // prior-inner-collection scan, and LookupExpression.GetLookupAlias is navigation-name-only so both hops
    // would derive the same alias and AddLookup would de-duplicate one away).
    //
    // RE-BASELINED in EF-379 fix round 1, and the history matters because this test flip-flopped. The first
    // pass of EF-379 added a decline ("a TransitiveHop that resolves no navigation returns null") that turned
    // this shape's raw materialization crash into a clean translation failure, and this test asserted that.
    // The decline was a MEASURED REGRESSION for a shape that has nothing to do with self-references — an
    // owned SelectMany ALSO produces a transparent identifier, so a join off the unwound element classified
    // as TransitiveHop at the FIRST join and hard-failed a query that works at this branch's base (pinned by
    // Owned_SelectMany_then_join_off_the_unwound_element_still_works below) — so the decline was removed and
    // this shape reverted to its PRE-EXISTING disposition.
    //
    // MEASURED at the base commit (2a544b7e) and at the fixed tree, all three modes, byte-identical: it is a
    // LOUD failure, never silent wrong data. Native and DriverLinq crash at MATERIALIZATION with "Document
    // element is missing for required non-nullable property 'Id'"; NativeOnly declines earlier, at the gate.
    // The classification itself is innocent here: at base both root tiers missed anyway, so skipping them
    // changes nothing about what this shape does. ----

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

        // This shape used to fail LOUDLY at materialization ("Document element is missing for required
        // non-nullable property 'Id'") because both hops of a self-referencing chain collapsed onto one join:
        // every hop resolves the same navigation against the same target entity type, so the second hop's
        // $lookup could not be told apart from the first's. That was EF-371, fixed on the main-bound line by
        // recording one JoinInfo per join and giving each its own uniquified _lookup_ alias.
        //
        // Assert the two-hop VALUES, never `!= null`: EF's change-tracker identity fix-up can repair the
        // object graph from rows already in the change tracker even when the $lookup matched the wrong
        // field, so a null-check passes on wrong data. The seed is a cycle, F1 -> F2 -> F3 -> F1, chosen so
        // that a collapsed chain (Parent.Parent == Parent) is distinguishable from the correct answer at
        // every row.
        Assert.Equal(["F1", "F2", "F3"], nodes.Select(n => n.Label).ToArray());
        Assert.Equal(["F2", "F3", "F1"], nodes.Select(n => n.Parent.Label).ToArray());
        Assert.Equal(["F3", "F1", "F2"], nodes.Select(n => n.Parent.Parent.Label).ToArray());
    }

    [Fact]
    public void Self_referencing_two_hop_chain_declines_at_the_gate_under_NativeOnly()
    {
        // The third mode, pinned separately because its disposition differs: multi-level reference Include is
        // a deferred native shape, so NativeOnly refuses the driver-LINQ fallback at the gate and never
        // reaches the materialization crash above. Also measured byte-identical at the base commit.
        using var db = CreateSelfRefContext(
            nameof(Self_referencing_two_hop_chain_declines_at_the_gate_under_NativeOnly), MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.FNodes.Include(n => n.Parent).ThenInclude(p => p.Parent).ToList());
    }

    // ---- The regression control for EF-379 fix round 1: a transparent identifier is NOT only produced by a
    // prior JOIN. An owned-collection SelectMany produces one too, so the FIRST TranslateJoinCore call can
    // see a "ti.Inner" receiver and classify TransitiveHop. The transitive scan then finds nothing (there is
    // no prior inner collection at all), and the decline the first pass of EF-379 added hard-failed this
    // query in EVERY mode. It works at base and must keep working: PTag carries a plain ObjectId FK PROPERTY
    // with NO navigation, so the join is user-authored rather than nav-expanded. Its absence is what let the
    // regression through review. ----

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
        // The control for the decline above: the SHALLOW self-reference must keep working. Without this,
        // "declines" could be satisfied by declining every self-referencing Include.
        using var db = CreateSelfRefContext(nameof(Self_referencing_single_hop_still_works),
            MongoQueryMode.Native, out var spyLogger);

        var results = db.FNodes.Include(n => n.Parent).OrderBy(n => n.Label).ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(["F2", "F3", "F1"], results.Select(n => n.Parent.Label));

        AssertMql(spyLogger, "\"localField\" : \"ParentId\"");
    }

    // ---- fixture ----

    private CollidingChainDbContext CreateCollidingContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "PL" + suffix;

        // The seed makes the two paths DISAGREE: the root's own LeafId reaches "WRONG", the mid's reaches
        // "RIGHT1"/"RIGHT2". Every root points its own LeafId at the SAME wrong leaf, so a root-scoped
        // $lookup still matches a real document (the row is not dropped) and only the VALUE discriminates.
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

        // Same disagreement as the two doorways: every root's own LeafId points at the SAME "WRONG" leaf (so a
        // root-scoped $lookup still matches a real document and only the VALUE discriminates), while each
        // mid's LeafId points at its own "RIGHT*" leaf.
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

        // A REQUIRED self-reference, so nav-expansion lowers both hops to Queryable.Join on every EF major
        // (an OPTIONAL one would lower to LeftJoin, which has no dispatch case before EF10). A required FK
        // needs a cycle: F1 -> F2 -> F3 -> F1.
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

    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

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

    // ---- doorway 1's model: the ROOT declares a property named "LeafId", exactly like the intermediate. ----

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

        // The collision: the same property NAME the intermediate uses for its own foreign key. This is what
        // the FK-name tier matched, resolving PRoot.Leaf for a hop that belongs to PMid.
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

    // ---- doorway 2's model: NO name collision — the root's foreign key is "SideLeafId" — but the root
    // still carries a navigation to the leaf TYPE, which is all the target-type-only tier needs. ----

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

    // ---- the third family's model: the INTERMEDIATE carries only a bare FK PROPERTY (no navigation to the
    // leaf, so the transitive scan finds no candidate), while the ROOT carries BOTH the same-named FK and a
    // navigation to the leaf TYPE — so at base tier 1 matched on the name and tier 2 would have matched on the
    // type. Because NMid has no navigation, the chain has to be written as a user-authored Join. ----

    private class NLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class NMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";

        // Deliberately a bare foreign-key PROPERTY with NO NLeaf navigation beside it.
        public ObjectId LeafId { get; set; }
    }

    private class NRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public NMid Mid { get; set; } = null!;

        // Both root tiers' bait: the same property NAME the intermediate uses for its own foreign key, AND a
        // navigation onto the joined type.
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

    // ---- the control's model: two sibling reference navigations onto DIFFERENT target types. ----

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

    // ---- scenario F's model: a self-referencing REQUIRED navigation, so the receiver's CLR type at hop 2
    // is the ROOT's own CLR type. ----

    private class FNode
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId ParentId { get; set; }
        public FNode Parent { get; set; } = null!;
    }

    // ---- the regression control's model: an OWNED collection whose element carries a bare ObjectId foreign
    // key PROPERTY and NO navigation, so the join onto JProduct is user-authored. The SelectMany over the
    // owned collection is what produces the transparent identifier at the FIRST join. ----

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
