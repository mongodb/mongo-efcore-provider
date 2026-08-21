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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-372: a reference <c>Include</c>/<c>ThenInclude</c> chain of THREE OR MORE hops emitted an
/// UNPREFIXED <c>localField</c> at hop 3, so the inner <c>$unwind</c> matched nothing and dropped every
/// row — silent wrong data (0 rows where 3 are correct). The cause was in
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.RebindInnerShaperToOuterQuery</c>, where the
/// intermediate a transitive join must be scoped under was resolved ONLY as a navigation off the ROOT
/// entity type; at hop 3+ the intermediate is not reachable from the root, so the prefix was silently
/// omitted.
/// <para>
/// The tests deliberately discriminate on ROW COUNT plus an MQL pin on the emitted <c>localField</c>.
/// Navigation-equality assertions alone are NOT sufficient: with the bug live, a variant whose root-level
/// field name collided with the intermediate FK name returned 3 rows with every navigation correctly wired,
/// because EF's change-tracker identity fix-up repaired the object graph while the <c>$lookup</c> had
/// matched the WRONG field. Hence no root-level field here shares a name with an intermediate FK.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef372DeepReferenceIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- T1: the ThenInclude doorway, both query modes ----

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Three_hop_reference_ThenInclude_returns_every_row(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(Three_hop_reference_ThenInclude_returns_every_row));

        var results = db.Roots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .ThenInclude(l => l.Tip)
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid));
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf.Tip));
        Assert.Equal(["T1", "T2", "T3"], results.Select(r => r.Mid.Leaf.Tip.Label).OrderBy(x => x));
    }

    // ---- T2: the MQL pin. Hop 3's localField must be scoped under hop 2's lookup alias. ----

    [Fact]
    public void Three_hop_reference_ThenInclude_prefixes_the_third_localField()
    {
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Three_hop_reference_ThenInclude_prefixes_the_third_localField), out var spyLogger);

        var results = db.Roots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .ThenInclude(l => l.Tip)
            .ToList();

        Assert.Equal(3, results.Count);

        AssertMql(spyLogger, "\"localField\" : \"_lookup_Leaf.TipId\"");
    }

    // ---- T3: the second doorway — a user-authored chained Join of 3 levels ----

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Three_level_user_authored_chained_Join_returns_every_row(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(Three_level_user_authored_chained_Join_returns_every_row),
            out var spyLogger);

        var results = db.Roots
            .Join(db.Mids, r => r.MidId, m => m.Id, (r, m) => new { r, m })
            .Join(db.Leaves, x => x.m.LeafId, l => l.Id, (x, l) => new { x.r, x.m, l })
            .Join(db.Tips, x => x.l.TipId, t => t.Id, (x, t) => x.r.Name + "|" + t.Label)
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(["R1|T1", "R2|T2", "R3|T3"], results.OrderBy(x => x));

        AssertMql(spyLogger, "\"localField\" : \"_lookup_Leaf.TipId\"");
    }

    // ---- T4: no over-prefixing at depth 1 and 2 ----

    [Fact]
    public void One_hop_reference_Include_localField_is_unprefixed()
    {
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(One_hop_reference_Include_localField_is_unprefixed), out var spyLogger);

        var results = db.Roots.Include(r => r.Mid).ToList();

        Assert.Equal(3, results.Count);
        AssertMql(spyLogger, "\"localField\" : \"MidId\"");
        Assert.DoesNotContain("\"localField\" : \"_lookup_Mid.MidId\"",
            spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));
    }

    [Fact]
    public void Two_hop_reference_ThenInclude_prefixes_only_the_second_localField()
    {
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Two_hop_reference_ThenInclude_prefixes_only_the_second_localField), out var spyLogger);

        var results = db.Roots.Include(r => r.Mid).ThenInclude(m => m.Leaf).ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf));

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"MidId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_Mid.LeafId\"", mql);
    }

    // ---- T5: depth 4. A fix that walks one level up instead of following the chain fails here. ----

    [Fact]
    public void Four_hop_reference_ThenInclude_prefixes_the_fourth_localField()
    {
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Four_hop_reference_ThenInclude_prefixes_the_fourth_localField), out var spyLogger);

        var results = db.Roots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .ThenInclude(l => l.Tip)
            .ThenInclude(t => t.Nub)
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid.Leaf.Tip.Nub));
        Assert.Equal(["N1", "N2", "N3"], results.Select(r => r.Mid.Leaf.Tip.Nub.Label).OrderBy(x => x));

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_lookup_Leaf.TipId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_Tip.NubId\"", mql);
    }

    // ---- T7: the LEFT-OUTER twin of T1. Nav-expansion emits LeftJoin (not Join) for an OPTIONAL
    // navigation, so a 3-hop chain of optional references walks TranslateLeftJoin through the same
    // TranslateJoinCore prefix resolution. Note the row COUNT cannot discriminate here — a left-outer
    // $unwind preserves the row and leaves the navigation null — so this asserts the navigations
    // themselves plus the MQL, and keeps preserveNullAndEmptyArrays pinned to prove it really is the
    // left-outer path. ----

    [Fact]
    public void Three_hop_OPTIONAL_reference_ThenInclude_prefixes_the_third_localField()
    {
        using var db = CreateOptionalContext(
            nameof(Three_hop_OPTIONAL_reference_ThenInclude_prefixes_the_third_localField), out var spyLogger);

        var query = db.OptRoots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Leaf)
            .ThenInclude(l => l.Tip);

#if EF8 || EF9
        // PRE-EXISTING, unrelated to EF-372 and unchanged by it: EF's nav-expansion lowers an OPTIONAL
        // reference navigation to Queryable.LeftJoin, which has no dispatch case at all before EF10, so EF
        // Core rejects the whole query before any $lookup is built.
        // The gap is BLANKET and DEPTH-INDEPENDENT, not specific to a deep chain: MEASURED, a ONE-hop
        // `OptRoots.Include(r => r.Mid)` fails identically on both EF8 and EF9. The precedent for pinning
        // this disposition rather than compiling the test out is
        // RequiredNavigationUnwindTests.Optional_reference_Include_is_not_translated_on_EF8_EF9.
        var ex = Assert.Throws<InvalidOperationException>(() => query.ToList());

        // Pin the message, not the bare type: InvalidOperationException is also what a materialization
        // failure throws, so the type alone cannot tell "declined to translate" from "translated and then
        // broke".
        Assert.Contains("could not be translated", ex.Message);
        Assert.Contains("ThenInclude", ex.Message);
#else
        var results = query.ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Mid));
        Assert.All(results, r => Assert.NotNull(r.Mid!.Leaf));
        Assert.All(results, r => Assert.NotNull(r.Mid!.Leaf!.Tip));
        Assert.Equal(["OT1", "OT2", "OT3"], results.Select(r => r.Mid!.Leaf!.Tip!.Label).OrderBy(x => x));

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_lookup_Mid.LeafId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_Leaf.TipId\"", mql);
        Assert.Contains("\"preserveNullAndEmptyArrays\" : true", mql);
#endif
    }

    // ---- T6: two same-typed navigations (Order.Buyer / Order.Approver, both Person) is an ORDINARY
    // model. The prefix resolution must not treat the model's mere shape as ambiguous: it reads the
    // navigation a PRIOR JOIN actually recorded, so the branch this query really uses is unambiguous and
    // WORKS. All three variants — first branch alone, second branch alone, and both at once — now return
    // correct data; the latter two were pinned here as declines until EF-375/EF-376 landed on main, and each
    // test below records what specifically changed. The fixture's two mids point at DIFFERENT leaves ("A" and
    // "B") so a wrong prefix shows up as wrong data rather than as a coincidentally-equal value. ----

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Two_same_typed_navigations_single_branch_ThenInclude_returns_correct_rows(MongoQueryMode mode)
    {
        using var db = CreateAmbiguousContext(
            nameof(Two_same_typed_navigations_single_branch_ThenInclude_returns_correct_rows), mode,
            out var spyLogger);

        var results = db.AmbRoots
            .Include(r => r.PrimaryMid).ThenInclude(m => m.Leaf)
            .ToList();

        Assert.Single(results);
        Assert.Equal("AM1", results[0].PrimaryMid.Label);

        // The teeth: the OTHER same-typed navigation reaches leaf "B", so resolving the prefix off the wrong
        // navigation yields "B" or a null navigation (a $lookup whose localField names a path nothing wrote)
        // rather than "A". A first pass at EF-372 THREW here instead, in every MongoQueryMode.
        Assert.NotNull(results[0].PrimaryMid.Leaf);
        Assert.Equal("A", results[0].PrimaryMid.Leaf.Label);

        AssertMql(spyLogger, "\"localField\" : \"_lookup_PrimaryMid.LeafId\"");
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Two_same_typed_navigations_sibling_ThenIncludes_return_both_branches(MongoQueryMode mode)
    {
        using var db = CreateAmbiguousContext(
            nameof(Two_same_typed_navigations_sibling_ThenIncludes_return_both_branches), mode, out var spyLogger);

        // BOTH branches at once. This shape used to be pinned here as a clean DECLINE, for two reasons that
        // EF-375/EF-376 on main have since removed: the transitive hop could not be attributed to one
        // intermediate (it was resolved by TARGET ENTITY TYPE, which cannot tell PrimaryMid from
        // SecondaryMid), and both hops derived the SAME alias "_lookup_Leaf" from the same navigation, so
        // AddLookup's alias de-duplication silently dropped one of the two. EF-376 resolves the "through"
        // join POSITIONALLY by walking the key selector's Outer/Inner chain, and EF-375 gives each join its
        // own $lookup alias — the first to claim a name keeps it unsuffixed, later ones are suffixed. So both
        // branches are now emitted and BOTH return correct data.
        var results = db.AmbRoots
            .Include(r => r.PrimaryMid).ThenInclude(m => m.Leaf)
            .Include(r => r.SecondaryMid).ThenInclude(m => m.Leaf)
            .ToList();

        Assert.Single(results);
        Assert.Equal("AM1", results[0].PrimaryMid.Label);
        Assert.Equal("AM2", results[0].SecondaryMid.Label);

        // The teeth. The fixture's two mids reach DIFFERENT leaves, so a hop scoped under the wrong
        // intermediate shows up as the wrong label rather than as an indistinguishable one. Never assert
        // merely != null here: identity fix-up can repair the graph over a wrong-field $lookup.
        Assert.Equal("A", results[0].PrimaryMid.Leaf.Label);
        Assert.Equal("B", results[0].SecondaryMid.Leaf.Label);

        // ...and because fix-up CAN repair even a crossed pair of lookups once both leaves are tracked, pin
        // the MQL too: two distinct transitive $lookups, each scoped under its OWN intermediate's alias, with
        // the second's "as" suffixed rather than colliding on "_lookup_Leaf" and being de-duplicated away.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_lookup_PrimaryMid.LeafId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_SecondaryMid.LeafId\"", mql);
        Assert.Contains("\"as\" : \"_lookup_Leaf\"", mql);
        Assert.Contains("\"as\" : \"_lookup_Leaf_1\"", mql);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Two_same_typed_navigations_second_branch_only_returns_the_second_branch(MongoQueryMode mode)
    {
        using var db = CreateAmbiguousContext(
            nameof(Two_same_typed_navigations_second_branch_only_returns_the_second_branch), mode, out var spyLogger);

        // The SECOND same-typed navigation, on its own — the twin of the single-branch test above, which
        // covers the FIRST. This used to be pinned as a decline: the retroactive lookup registration that
        // flattens the prior single-reference join picked its navigation by TARGET ENTITY TYPE alone, so for
        // this model it named "_lookup_PrimaryMid" whatever branch the query asked for, and the prefix
        // resolution — which refuses to name a path it cannot prove is written — declined rather than read
        // the wrong branch. EF-375 on main fixed the root cause: each join now carries the navigation it
        // actually resolved plus its own $lookup, so the flattening pass no longer re-derives a prior join's
        // lookup by target type. The query works, and reads the branch it asked for.
        var results = db.AmbRoots
            .Include(r => r.SecondaryMid).ThenInclude(m => m.Leaf)
            .ToList();

        Assert.Single(results);

        // The teeth: PrimaryMid reaches leaf "A" and SecondaryMid reaches leaf "B", so reading the wrong
        // branch yields "AM1"/"A" (or a null navigation) instead of "AM2"/"B".
        Assert.Equal("AM2", results[0].SecondaryMid.Label);
        Assert.Equal("B", results[0].SecondaryMid.Leaf.Label);

        // The un-Included sibling branch stays unloaded — nothing pulled PrimaryMid into the graph.
        Assert.Null(results[0].PrimaryMid);

        // Pin the MQL as well: the $lookup chain must be rooted on SecondaryMidId, not on PrimaryMidId.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"SecondaryMidId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_SecondaryMid.LeafId\"", mql);
        Assert.DoesNotContain("_lookup_PrimaryMid", mql);
    }

    // ---- T8: the alias in the emitted localField must come from the NAVIGATION NAME, not the target type
    // name. Every navigation in the model above happens to be named after its target type, so "_lookup_Leaf"
    // cannot tell the two derivations apart; here Mid.Next is of type AltLeaf, so they differ. ----

    [Fact]
    public void Three_hop_chain_localField_alias_comes_from_the_navigation_name()
    {
        using var db = CreateAltContext(
            nameof(Three_hop_chain_localField_alias_comes_from_the_navigation_name), out var spyLogger);

        var results = db.AltRoots
            .Include(r => r.Mid)
            .ThenInclude(m => m.Next)
            .ThenInclude(n => n.Tip)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(["AT1", "AT2"], results.Select(r => r.Mid.Next.Tip.Label).OrderBy(x => x));

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_lookup_Next.TipId\"", mql);
        Assert.DoesNotContain("_lookup_AltLeaf", mql);
    }

    // ---- T9: a transitive hop reached through a NAVIGATION-LESS first hop. This chain's first hop is a bare
    // key-equality Join with no model navigation at all, so the intermediate cannot be identified from an
    // INavigation. Pre-existing and unrelated to the depth defect EF-372 fixed (it was broken at one hop as
    // well as three) and it originally took the same silent route: 0 rows where 1 is correct. This test was
    // then pinned as a clean DECLINE, and EF-377 on main has since made the shape WORK — LookupExpression
    // gained a navigation-independent TargetEntityType plus a constructor that builds a $lookup straight from
    // raw join-key field paths, and MongoQueryExpression records that raw key info per navigation-less hop so
    // later hops (and the retroactive flattening pass) can still resolve it. What this test now guards is
    // that the second hop is scoped under the FIRST hop's lookup alias, which is what the original 0-row
    // silent failure got wrong. ----

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Join_chain_with_no_model_navigation_returns_every_row(MongoQueryMode mode)
    {
        using var db = CreateNavlessContext(
            nameof(Join_chain_with_no_model_navigation_returns_every_row), mode, out var spyLogger);

        var results = db.NoNavRoots
            .Join(db.NoNavMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
            .Join(db.NoNavLeaves, x => x.m.LeafId, l => l.Id, (x, l) => x.r.Name + "|" + l.Label)
            .ToList();

        // A projection of scalars, so there is no change tracker in play and no identity fix-up to mask a
        // wrong-field $lookup: the value itself is the evidence.
        Assert.Equal(["ZR1|ZL1"], results);

        // The second hop's key "x.m.LeafId" declares LeafId on the MID, not on the root, so its localField
        // must be scoped under the first (navigation-less) hop's alias. Unprefixed "LeafId" is the original
        // defect and matches nothing.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"MidKey\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_NoNavMid.LeafId\"", mql);
        Assert.DoesNotContain("\"localField\" : \"LeafId\"", mql);
    }

#if !EF8 && !EF9
    // ---- T10: the LEFT-OUTER route through the same-typed sibling shape. The sibling and navigation-less
    // tests above all reach TranslateJoinCore through Queryable.Join (required navigations, or a user-authored
    // join); TranslateLeftJoin was unexercised. Same shape as T6's sibling ThenIncludes, but over OPTIONAL
    // navigations, which nav-expansion lowers to LeftJoin. This was originally pinned as a decline on the
    // left-outer route; EF-375/EF-376 on main made it work there too, so it now asserts the data, and the
    // point of keeping it is that the positional "through"-join resolution and the per-join alias suffixing
    // reach the LeftJoin path and not only the Join path.
    //
    // EF10-only, for the PRE-EXISTING gap the T7 comment above measures: Queryable.LeftJoin has no dispatch
    // case at all before EF10, so on EF8/EF9 an optional reference Include never reaches TranslateJoinCore to
    // begin with — that gap is blanket and depth-independent, so there is no EF8/EF9 disposition of THIS
    // shape to pin. ----

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Optional_two_same_typed_navigations_sibling_ThenIncludes_return_both_branches(MongoQueryMode mode)
    {
        using var db = CreateOptionalAmbiguousContext(
            nameof(Optional_two_same_typed_navigations_sibling_ThenIncludes_return_both_branches), mode,
            out var spyLogger);

        var results = db.OptAmbRoots
            .Include(r => r.PrimaryMid).ThenInclude(m => m!.Leaf)
            .Include(r => r.SecondaryMid).ThenInclude(m => m!.Leaf)
            .ToList();

        Assert.Single(results);
        Assert.Equal("OAM1", results[0].PrimaryMid!.Label);
        Assert.Equal("OAM2", results[0].SecondaryMid!.Label);

        // The teeth, as in the required-navigation twin: the two mids reach DIFFERENT leaves, so a hop scoped
        // under the wrong intermediate reads "OB" where "OA" is correct (or leaves the navigation null).
        // A row COUNT cannot discriminate on the left-outer route — a preserving $unwind keeps the row and
        // just leaves the navigation null — so the values are the only signal.
        Assert.Equal("OA", results[0].PrimaryMid!.Leaf!.Label);
        Assert.Equal("OB", results[0].SecondaryMid!.Leaf!.Label);

        // ...plus the MQL, both to guard against fix-up repairing a crossed pair and to keep
        // preserveNullAndEmptyArrays pinned, which is what proves this really is the left-outer path.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_lookup_PrimaryMid.LeafId\"", mql);
        Assert.Contains("\"localField\" : \"_lookup_SecondaryMid.LeafId\"", mql);
        Assert.Contains("\"as\" : \"_lookup_Leaf\"", mql);
        Assert.Contains("\"as\" : \"_lookup_Leaf_1\"", mql);
        Assert.Contains("\"preserveNullAndEmptyArrays\" : true", mql);
        Assert.DoesNotContain("\"preserveNullAndEmptyArrays\" : false", mql);
    }

    // The companion to the test above: the SAME optional model, ONE branch, must also return correct rows on
    // the left-outer route. It covers the FIRST of the two same-typed navigations in isolation.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Optional_two_same_typed_navigations_single_branch_ThenInclude_returns_correct_rows(MongoQueryMode mode)
    {
        using var db = CreateOptionalAmbiguousContext(
            nameof(Optional_two_same_typed_navigations_single_branch_ThenInclude_returns_correct_rows), mode);

        var results = db.OptAmbRoots
            .Include(r => r.PrimaryMid).ThenInclude(m => m!.Leaf)
            .ToList();

        Assert.Single(results);
        Assert.Equal("OAM1", results[0].PrimaryMid!.Label);

        // The other same-typed navigation reaches leaf "OB", so a prefix resolved off the wrong navigation
        // shows up as "OB" or as a null navigation rather than as "OA".
        Assert.NotNull(results[0].PrimaryMid!.Leaf);
        Assert.Equal("OA", results[0].PrimaryMid!.Leaf!.Label);
    }
#endif

    // ---- fixture ----

    private DeepChainDbContext CreateContext(MongoQueryMode mode, string name, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "M" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "L" + suffix;
        var tips = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "T" + suffix;
        var nubs = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "N" + suffix;

        var nubIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var tipIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var leafIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var midIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };

        database.MongoDatabase.GetCollection<Nub>(nubs).InsertMany(
        [
            new() { Id = nubIds[0], Label = "N1" },
            new() { Id = nubIds[1], Label = "N2" },
            new() { Id = nubIds[2], Label = "N3" },
        ]);
        database.MongoDatabase.GetCollection<Tip>(tips).InsertMany(
        [
            new() { Id = tipIds[0], Label = "T1", NubId = nubIds[0] },
            new() { Id = tipIds[1], Label = "T2", NubId = nubIds[1] },
            new() { Id = tipIds[2], Label = "T3", NubId = nubIds[2] },
        ]);
        database.MongoDatabase.GetCollection<Leaf>(leaves).InsertMany(
        [
            new() { Id = leafIds[0], Label = "L1", TipId = tipIds[0] },
            new() { Id = leafIds[1], Label = "L2", TipId = tipIds[1] },
            new() { Id = leafIds[2], Label = "L3", TipId = tipIds[2] },
        ]);
        database.MongoDatabase.GetCollection<Mid>(mids).InsertMany(
        [
            new() { Id = midIds[0], Label = "M1", LeafId = leafIds[0] },
            new() { Id = midIds[1], Label = "M2", LeafId = leafIds[1] },
            new() { Id = midIds[2], Label = "M3", LeafId = leafIds[2] },
        ]);
        database.MongoDatabase.GetCollection<Root>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "R1", MidId = midIds[0] },
            new() { Id = ObjectId.GenerateNewId(), Name = "R2", MidId = midIds[1] },
            new() { Id = ObjectId.GenerateNewId(), Name = "R3", MidId = midIds[2] },
        ]);

        return new DeepChainDbContext(database, roots, mids, leaves, tips, nubs, mode, loggerFactory);
    }

    private DeepChainDbContext CreateContext(MongoQueryMode mode, string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateContext(mode, name, loggerFactory);
    }

    private AmbiguousChainDbContext CreateAmbiguousContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "AR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "AM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "AL" + suffix;

        // The two mids reach DIFFERENT leaves, so a prefix resolved off the wrong same-typed navigation
        // returns the wrong label (or none) instead of an indistinguishable one.
        var leafA = ObjectId.GenerateNewId();
        var leafB = ObjectId.GenerateNewId();
        var mid1 = ObjectId.GenerateNewId();
        var mid2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<AmbLeaf>(leaves).InsertMany(
        [
            new() { Id = leafA, Label = "A" },
            new() { Id = leafB, Label = "B" },
        ]);
        database.MongoDatabase.GetCollection<AmbMid>(mids).InsertMany(
        [
            new() { Id = mid1, Label = "AM1", LeafId = leafA },
            new() { Id = mid2, Label = "AM2", LeafId = leafB },
        ]);
        database.MongoDatabase.GetCollection<AmbRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "AR1", PrimaryMidId = mid1, SecondaryMidId = mid2 },
        ]);

        return new AmbiguousChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }

    private AmbiguousChainDbContext CreateAmbiguousContext(
        string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateAmbiguousContext(name, mode, loggerFactory);
    }

    private AltChainDbContext CreateAltContext(string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XL" + suffix;
        var tips = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XT" + suffix;

        var tipIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var leafIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var midIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };

        database.MongoDatabase.GetCollection<AltTip>(tips).InsertMany(
        [
            new() { Id = tipIds[0], Label = "AT1" },
            new() { Id = tipIds[1], Label = "AT2" },
        ]);
        database.MongoDatabase.GetCollection<AltLeaf>(leaves).InsertMany(
        [
            new() { Id = leafIds[0], Label = "XL1", TipId = tipIds[0] },
            new() { Id = leafIds[1], Label = "XL2", TipId = tipIds[1] },
        ]);
        database.MongoDatabase.GetCollection<AltMid>(mids).InsertMany(
        [
            new() { Id = midIds[0], Label = "XM1", NextId = leafIds[0] },
            new() { Id = midIds[1], Label = "XM2", NextId = leafIds[1] },
        ]);
        database.MongoDatabase.GetCollection<AltRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "XR1", MidId = midIds[0] },
            new() { Id = ObjectId.GenerateNewId(), Name = "XR2", MidId = midIds[1] },
        ]);

        return new AltChainDbContext(database, roots, mids, leaves, tips, loggerFactory);
    }

    private NavlessChainDbContext CreateNavlessContext(string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateNavlessContext(name, mode, loggerFactory);
    }

    private NavlessChainDbContext CreateNavlessContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "ZR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "ZM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "ZL" + suffix;

        var leafId = ObjectId.GenerateNewId();
        var midId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<NoNavLeaf>(leaves).InsertMany(
            [new() { Id = leafId, Label = "ZL1" }]);
        database.MongoDatabase.GetCollection<NoNavMid>(mids).InsertMany(
            [new() { Id = midId, Label = "ZM1", LeafId = leafId }]);
        database.MongoDatabase.GetCollection<NoNavRoot>(roots).InsertMany(
            [new() { Id = ObjectId.GenerateNewId(), Name = "ZR1", MidKey = midId }]);

        return new NavlessChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }

    private OptionalChainDbContext CreateOptionalContext(string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OL" + suffix;
        var tips = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OT" + suffix;

        var tipIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var leafIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };
        var midIds = new[] { ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId() };

        database.MongoDatabase.GetCollection<OptTip>(tips).InsertMany(
        [
            new() { Id = tipIds[0], Label = "OT1" },
            new() { Id = tipIds[1], Label = "OT2" },
            new() { Id = tipIds[2], Label = "OT3" },
        ]);
        database.MongoDatabase.GetCollection<OptLeaf>(leaves).InsertMany(
        [
            new() { Id = leafIds[0], Label = "OL1", TipId = tipIds[0] },
            new() { Id = leafIds[1], Label = "OL2", TipId = tipIds[1] },
            new() { Id = leafIds[2], Label = "OL3", TipId = tipIds[2] },
        ]);
        database.MongoDatabase.GetCollection<OptMid>(mids).InsertMany(
        [
            new() { Id = midIds[0], Label = "OM1", LeafId = leafIds[0] },
            new() { Id = midIds[1], Label = "OM2", LeafId = leafIds[1] },
            new() { Id = midIds[2], Label = "OM3", LeafId = leafIds[2] },
        ]);
        database.MongoDatabase.GetCollection<OptRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "OR1", MidId = midIds[0] },
            new() { Id = ObjectId.GenerateNewId(), Name = "OR2", MidId = midIds[1] },
            new() { Id = ObjectId.GenerateNewId(), Name = "OR3", MidId = midIds[2] },
        ]);

        return new OptionalChainDbContext(database, roots, mids, leaves, tips, loggerFactory);
    }

#if !EF8 && !EF9
    // T10's model: T6's two-same-typed-navigations shape with OPTIONAL (nullable-FK) navigations, which
    // nav-expansion lowers to Queryable.LeftJoin instead of Queryable.Join.
    private OptionalAmbiguousChainDbContext CreateOptionalAmbiguousContext(
        string name, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateOptionalAmbiguousContext(name, mode, loggerFactory);
    }

    private OptionalAmbiguousChainDbContext CreateOptionalAmbiguousContext(
        string name, MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roots = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OAR" + suffix;
        var mids = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OAM" + suffix;
        var leaves = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OAL" + suffix;

        var leafA = ObjectId.GenerateNewId();
        var leafB = ObjectId.GenerateNewId();
        var mid1 = ObjectId.GenerateNewId();
        var mid2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<OptAmbLeaf>(leaves).InsertMany(
        [
            new() { Id = leafA, Label = "OA" },
            new() { Id = leafB, Label = "OB" },
        ]);
        database.MongoDatabase.GetCollection<OptAmbMid>(mids).InsertMany(
        [
            new() { Id = mid1, Label = "OAM1", LeafId = leafA },
            new() { Id = mid2, Label = "OAM2", LeafId = leafB },
        ]);
        database.MongoDatabase.GetCollection<OptAmbRoot>(roots).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Name = "OAR1", PrimaryMidId = mid1, SecondaryMidId = mid2 },
        ]);

        return new OptionalAmbiguousChainDbContext(database, roots, mids, leaves, mode, loggerFactory);
    }
#endif

    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    private class Nub
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class Tip
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId NubId { get; set; }
        public Nub Nub { get; set; } = null!;
    }

    private class Leaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId TipId { get; set; }
        public Tip Tip { get; set; } = null!;
    }

    private class Mid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId LeafId { get; set; }
        public Leaf Leaf { get; set; } = null!;
    }

    private class Root
    {
        public ObjectId Id { get; set; }

        // Deliberately NOT named after any intermediate FK: a root field colliding with an intermediate FK
        // name masks the defect (the wrong-field $lookup then matches, and identity fix-up repairs the graph).
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public Mid Mid { get; set; } = null!;
    }

    private class DeepChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;
        private readonly string _tips;
        private readonly string _nubs;

        public DeepChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves, string tips, string nubs,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
            _tips = tips;
            _nubs = nubs;
        }

        private static DbContextOptions<DeepChainDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<DeepChainDbContext>()
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

        public DbSet<Root> Roots { get; set; } = null!;
        public DbSet<Mid> Mids { get; set; } = null!;
        public DbSet<Leaf> Leaves { get; set; } = null!;
        public DbSet<Tip> Tips { get; set; } = null!;
        public DbSet<Nub> Nubs { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Root>().ToCollection(_roots);
            modelBuilder.Entity<Root>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<Mid>().ToCollection(_mids);
            modelBuilder.Entity<Mid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<Leaf>().ToCollection(_leaves);
            modelBuilder.Entity<Leaf>().HasOne(l => l.Tip).WithMany().HasForeignKey(l => l.TipId);
            modelBuilder.Entity<Tip>().ToCollection(_tips);
            modelBuilder.Entity<Tip>().HasOne(t => t.Nub).WithMany().HasForeignKey(t => t.NubId);
            modelBuilder.Entity<Nub>().ToCollection(_nubs);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private class OptTip
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class OptLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? TipId { get; set; }
        public OptTip? Tip { get; set; }
    }

    private class OptMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? LeafId { get; set; }
        public OptLeaf? Leaf { get; set; }
    }

    private class OptRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? MidId { get; set; }
        public OptMid? Mid { get; set; }
    }

    private class OptionalChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;
        private readonly string _tips;

        public OptionalChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves, string tips,
            ILoggerFactory loggerFactory)
            : base(new DbContextOptionsBuilder<OptionalChainDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseLoggerFactory(loggerFactory)
                .EnableSensitiveDataLogging()
                .Options)
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
            _tips = tips;
        }

        public DbSet<OptRoot> OptRoots { get; set; } = null!;
        public DbSet<OptMid> OptMids { get; set; } = null!;
        public DbSet<OptLeaf> OptLeaves { get; set; } = null!;
        public DbSet<OptTip> OptTips { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<OptRoot>().ToCollection(_roots);
            modelBuilder.Entity<OptRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<OptMid>().ToCollection(_mids);
            modelBuilder.Entity<OptMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<OptLeaf>().ToCollection(_leaves);
            modelBuilder.Entity<OptLeaf>().HasOne(l => l.Tip).WithMany().HasForeignKey(l => l.TipId);
            modelBuilder.Entity<OptTip>().ToCollection(_tips);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private class AmbLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class AmbMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId LeafId { get; set; }
        public AmbLeaf Leaf { get; set; } = null!;
    }

    private class AmbRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId PrimaryMidId { get; set; }
        public AmbMid PrimaryMid { get; set; } = null!;
        public ObjectId SecondaryMidId { get; set; }
        public AmbMid SecondaryMid { get; set; } = null!;
    }

    private class AmbiguousChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public AmbiguousChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(Configure(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        private static DbContextOptions<AmbiguousChainDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<AmbiguousChainDbContext>()
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

        public DbSet<AmbRoot> AmbRoots { get; set; } = null!;
        public DbSet<AmbMid> AmbMids { get; set; } = null!;
        public DbSet<AmbLeaf> AmbLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AmbRoot>().ToCollection(_roots);
            modelBuilder.Entity<AmbRoot>().HasOne(r => r.PrimaryMid).WithMany().HasForeignKey(r => r.PrimaryMidId);
            modelBuilder.Entity<AmbRoot>().HasOne(r => r.SecondaryMid).WithMany().HasForeignKey(r => r.SecondaryMidId);
            modelBuilder.Entity<AmbMid>().ToCollection(_mids);
            modelBuilder.Entity<AmbMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<AmbLeaf>().ToCollection(_leaves);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

#if !EF8 && !EF9
    private class OptAmbLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class OptAmbMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? LeafId { get; set; }
        public OptAmbLeaf? Leaf { get; set; }
    }

    private class OptAmbRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? PrimaryMidId { get; set; }
        public OptAmbMid? PrimaryMid { get; set; }
        public ObjectId? SecondaryMidId { get; set; }
        public OptAmbMid? SecondaryMid { get; set; }
    }

    private class OptionalAmbiguousChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public OptionalAmbiguousChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves, MongoQueryMode mode,
            ILoggerFactory? loggerFactory)
            : base(Configure(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        private static DbContextOptions<OptionalAmbiguousChainDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<OptionalAmbiguousChainDbContext>()
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

        public DbSet<OptAmbRoot> OptAmbRoots { get; set; } = null!;
        public DbSet<OptAmbMid> OptAmbMids { get; set; } = null!;
        public DbSet<OptAmbLeaf> OptAmbLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<OptAmbRoot>().ToCollection(_roots);
            modelBuilder.Entity<OptAmbRoot>().HasOne(r => r.PrimaryMid).WithMany().HasForeignKey(r => r.PrimaryMidId);
            modelBuilder.Entity<OptAmbRoot>().HasOne(r => r.SecondaryMid).WithMany()
                .HasForeignKey(r => r.SecondaryMidId);
            modelBuilder.Entity<OptAmbMid>().ToCollection(_mids);
            modelBuilder.Entity<OptAmbMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<OptAmbLeaf>().ToCollection(_leaves);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
#endif

    // ---- T8's model: the navigation NAME differs from the target TYPE name (Mid.Next is an AltLeaf), so
    // the emitted alias discriminates a nav-name-derived alias from a type-name-derived one. ----

    private class AltTip
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class AltLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId TipId { get; set; }
        public AltTip Tip { get; set; } = null!;
    }

    private class AltMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId NextId { get; set; }
        public AltLeaf Next { get; set; } = null!;
    }

    private class AltRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidId { get; set; }
        public AltMid Mid { get; set; } = null!;
    }

    private class AltChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;
        private readonly string _tips;

        public AltChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves, string tips,
            ILoggerFactory loggerFactory)
            : base(new DbContextOptionsBuilder<AltChainDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseLoggerFactory(loggerFactory)
                .EnableSensitiveDataLogging()
                .Options)
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
            _tips = tips;
        }

        public DbSet<AltRoot> AltRoots { get; set; } = null!;
        public DbSet<AltMid> AltMids { get; set; } = null!;
        public DbSet<AltLeaf> AltLeaves { get; set; } = null!;
        public DbSet<AltTip> AltTips { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AltRoot>().ToCollection(_roots);
            modelBuilder.Entity<AltRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
            modelBuilder.Entity<AltMid>().ToCollection(_mids);
            modelBuilder.Entity<AltMid>().HasOne(m => m.Next).WithMany().HasForeignKey(m => m.NextId);
            modelBuilder.Entity<AltLeaf>().ToCollection(_leaves);
            modelBuilder.Entity<AltLeaf>().HasOne(l => l.Tip).WithMany().HasForeignKey(l => l.TipId);
            modelBuilder.Entity<AltTip>().ToCollection(_tips);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    // ---- T9's model: the root has a foreign-key PROPERTY but NO navigation to the mid, so a transitive hop
    // through the mid has no intermediate to be scoped under. ----

    private class NoNavLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class NoNavMid
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId LeafId { get; set; }
        public NoNavLeaf Leaf { get; set; } = null!;
    }

    private class NoNavRoot
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId MidKey { get; set; }
    }

    private class NavlessChainDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _leaves;

        public NavlessChainDbContext(
            TemporaryDatabaseFixture database, string roots, string mids, string leaves, MongoQueryMode mode,
            ILoggerFactory? loggerFactory)
            : base(Configure(database, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _leaves = leaves;
        }

        private static DbContextOptions<NavlessChainDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<NavlessChainDbContext>()
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

        public DbSet<NoNavRoot> NoNavRoots { get; set; } = null!;
        public DbSet<NoNavMid> NoNavMids { get; set; } = null!;
        public DbSet<NoNavLeaf> NoNavLeaves { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NoNavRoot>().ToCollection(_roots);
            modelBuilder.Entity<NoNavMid>().ToCollection(_mids);
            modelBuilder.Entity<NoNavMid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            modelBuilder.Entity<NoNavLeaf>().ToCollection(_leaves);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
