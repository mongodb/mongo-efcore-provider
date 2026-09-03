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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-347 end-to-end coverage of numeric arithmetic COMPUTED PROJECTION leaves (as opposed to EF-329's
/// comparison-operand coverage in <see cref="NativeExprComparisonTests"/>), which the native translator now
/// binds directly into <c>$project</c> via <see cref="MongoExpressionTranslator.TryTranslateValue"/> /
/// <c>NativeProjectionBinder</c>. Each in-scope shape is proven native via <see cref="MongoQueryMode.NativeOnly"/>
/// (succeeds ⇒ went native; a fallback shape would throw <c>NativeTranslationNotSupportedException</c>), asserted
/// for result-set parity between native and driver-LINQ execution, and asserted for the expected aggregation
/// operator in the captured MQL. Two shapes (integer division, string concatenation) are guarded OFF the native
/// path on purpose and are covered here as "graceful fallback" — they must still produce correct driver-LINQ
/// results and must still throw under <c>NativeOnly</c>.
///
/// EF-412 extends this file with a distinct shape: a whole-ROOT-ENTITY leaf mixed with a computed/scalar/count
/// sibling in the same projection (e.g. <c>Select(c => new { c, Total = c.Age * c.Score })</c>). This USED TO be
/// a fallback-only shape (and, before EF-356, a silent wrong-data bug on that fallback path — the computed
/// sibling's operand binding could be clobbered by the entity leaf). Both are now fixed: the shape has its own
/// native route (<c>NativeRoute.Projection</c>, distinct from the pre-existing bare <c>Select(c => c)</c>
/// <c>NativeRoute.WholeEntity</c> route) and is proven native, with correct values, under <c>NativeOnly</c>
/// below. The remaining fallback-mode tests in this file exist to pin the late-fallback leg (a query that starts
/// out routed native but has to fall back mid-compile) and the driver-LINQ leg, not to document a known gap.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeComputedProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public int Score { get; set; }
        public double Weight { get; set; }
        public int? MaybeAge { get; set; }
    }

    // Alice: Age=7,  Score=2,  Weight=70.0, MaybeAge=7   → 7*2=14, 7-2=5, 7+2=9, 7%2=1, 70.0/2=35.0
    // Bob:   Age=20, Score=20, Weight=200.0, MaybeAge=null → 20*20=400, 20-20=0, 20+20=40, 20%20=0, 200.0/20=10.0
    // Carol: Age=-7, Score=2,  Weight=35.0, MaybeAge=-7  → -7*2=-14, -7-2=-9, -7+2=-5, -7%2=-1 (negative), 35.0/2=17.5
    private (IMongoCollection<Customer> collection, List<string> logs) SeedCustomers(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 7 }, { "Score", 2 },
                { "Weight", 70.0 }, { "MaybeAge", 7 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 20 }, { "Score", 20 },
                { "Weight", 200.0 }, { "MaybeAge", BsonNull.Value }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -7 }, { "Score", 2 },
                { "Weight", 35.0 }, { "MaybeAge", -7 }
            },
        ]);
        return (database.MongoDatabase.GetCollection<Customer>(collectionName), []);
    }

    private SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, List<string> logs, MongoQueryMode mode,
        Action<ModelBuilder>? modelBuilderAction = null) where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.LogTo(logs.Add)
                    .EnableSensitiveDataLogging()
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── EF-412 Task 4: entity type + fixture for the owned-collection-count sibling variation ─────────

    private class CustomerWithPosts
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public int Score { get; set; }
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> CustomerWithPostsModel =
        mb => mb.Entity<CustomerWithPosts>().OwnsMany(c => c.Posts);

    // Alice: 2 posts, Bob: 0 posts, Carol: 1 post.
    private (IMongoCollection<CustomerWithPosts> collection, List<string> logs) SeedCustomersWithPosts(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 7 }, { "Score", 2 },
                {
                    "Posts", new BsonArray
                    {
                        new BsonDocument { { "Heading", "a1" } }, new BsonDocument { { "Heading", "a2" } }
                    }
                }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 20 }, { "Score", 20 },
                { "Posts", new BsonArray() }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -7 }, { "Score", 2 },
                { "Posts", new BsonArray { new BsonDocument { { "Heading", "c1" } } } }
            },
        ]);
        return (database.MongoDatabase.GetCollection<CustomerWithPosts>(collectionName), []);
    }

    private static string Mql(List<string> logs)
        => Assert.Single(logs, l => l.Contains("Executed MQL query"));

    // ── In-scope: each proven NativeOnly + Native==DriverLinq parity + expected MQL operator ─────────

    [Fact]
    public void Multiply_projection_goes_native_and_matches_driver()
    {
        var (collection, logs) = SeedCustomers(nameof(Multiply_projection_goes_native_and_matches_driver));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([14, 400, -14], results.Select(r => r.P).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Subtract_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Subtract_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([5, 0, -9], results.Select(r => r.D).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$subtract", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Add_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Add_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([9, 40, -5], results.Select(r => r.S).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$add", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Modulo_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Modulo_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
                .OrderBy(r => r.Name).ToList();
            // Alice: 7%2=1, Bob: 20%20=0, Carol: -7%2=-1 (negative dividend exercised)
            Assert.Equal([1, 0, -1], results.Select(r => r.M).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$mod", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Floating_division_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Floating_division_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            // c.Weight / c.Score: the compiler inserts an implicit int->double widening Convert on Score,
            // which TryTranslateValue must unwrap (allowNumericWidening: true) to go native.
            var results = nativeOnly.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([35.0, 10.0, 17.5], results.Select(r => r.R).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$divide", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Mixed_member_and_arithmetic_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Mixed_member_and_arithmetic_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name).ToArray());
            Assert.Equal([14, 400, -14], results.Select(r => r.T).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Nullable_operand_arithmetic_matches_driver()
    {
        var (collection, logs) = SeedCustomers(nameof(Nullable_operand_arithmetic_matches_driver));

        // Prove native: under NativeOnly a driver-LINQ fallback throws, so success ⇒ the $project went native.
        // Parity alone (Native == DriverLinq below) is NOT proof — a silent fallback would produce identical
        // results and still pass — so this block plus the $multiply MQL assertion is the actual native proof.
        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
            .OrderBy(r => r.Name).ToList();

        Assert.Equal(driverResults, nativeResults);
        // Bob's MaybeAge is null → the product must be null, not 0 or thrown.
        Assert.Null(nativeResults.Single(r => r.Name == "Bob").X);
        Assert.Equal(14, nativeResults.Single(r => r.Name == "Alice").X);
        Assert.Equal(-14, nativeResults.Single(r => r.Name == "Carol").X);
    }

    // ── EF-412 Task 4: breadth coverage for the whole-root-entity leaf mixed with a sibling ────────────

    // Step 1: entity leaf + a plain scalar MEMBER sibling (as opposed to the arithmetic sibling the earlier
    // Mixed_whole_entity_and_computed_leaf_* tests use) — proves the new Route == Projection branch is not
    // somehow keyed off the sibling being a BinaryExpression specifically.
    //
    // FINAL-REVIEW WIDENING (finding F2): a [Theory] over ALL THREE modes. Before this slice every shape that is
    // now native had Route == Fallback, so Select.Projection stayed EMPTY and the MIXED removing visitor never
    // saw a "c"-style $$ROOT alias at all; now any admitted shape populates Select.Projection under an explicit
    // DriverLinq too, so the IsWholeRootEntityAlias null-out in MongoProjectionBindingRemovingExpressionVisitor
    // is load-bearing for this shape as well. NativeOnly remains a row, so the original "goes native" proof
    // (a fallback shape would throw there) is not traded away for the fallback-leg coverage.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Mixed_whole_entity_and_scalar_member_leaf_works_in_every_mode(MongoQueryMode mode)
    {
        var (collection, logs) = SeedCustomers(
            nameof(Mixed_whole_entity_and_scalar_member_leaf_works_in_every_mode) + mode);
        using var db = CreateContext(collection, logs, mode);

        // Must not throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Entities.Select(c => new { Entity = c, Name = c.Name })
            .OrderBy(r => r.Entity.Name).ToList();

        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Entity.Name).ToArray());
        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name).ToArray());
        Assert.Equal([7, 20, -7], results.Select(r => r.Entity.Age).ToArray());
    }

    // Step 2: entity leaf + a projected owned-collection .Count sibling — exercises the interaction between
    // this slice's whole-entity-leaf branch and the pre-existing count-leaf branch in the same NewExpression.
    //
    // FINAL-REVIEW WIDENING (finding F2): a [Theory] over the two NATIVE-side modes, not NativeOnly alone —
    // NativeOnly proves the shape goes native, the default Native row proves the default mode agrees. The
    // DriverLinq row was ATTEMPTED and then split out: this exact shape fails on the explicit-DriverLinq leg,
    // and that failure is PRE-EXISTING, not caused by this slice (MEASURED at 8b996aa7 — the commit before the
    // slice — where all three modes failed: NativeOnly with the "projects a non-entity result" decline, and
    // BOTH Native and DriverLinq with a NullReferenceException from BsonBinding.TryGetValueAtPath). The slice
    // FIXED the two native legs and left the DriverLinq leg failing, now with a clearer compile-time throw.
    // DriverLinq is DELIBERATELY NOT a row here — see
    // Mixed_whole_entity_and_owned_collection_count_leaf_still_fails_under_explicit_DriverLinq below, which pins
    // the PRE-EXISTING (measured at 8b996aa7, the commit before this slice) failure of this ONE shape on the
    // explicit-DriverLinq leg. The other two newly-native shapes carry the DriverLinq coverage F2 asked for.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Mixed_whole_entity_and_owned_collection_count_leaf_works_in_both_native_modes(MongoQueryMode mode)
    {
        var (collection, logs) = SeedCustomersWithPosts(
            nameof(Mixed_whole_entity_and_owned_collection_count_leaf_works_in_both_native_modes) + mode);
        using var db = CreateContext(collection, logs, mode, CustomerWithPostsModel);

        // Must not throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Entities.Select(c => new { c, PostCount = c.Posts.Count })
            .OrderBy(r => r.c.Name).ToList();

        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        Assert.Equal([2, 0, 1], results.Select(r => r.PostCount).ToArray());
    }

    // The DriverLinq half of the shape above, pinned as a KNOWN, PRE-EXISTING gap rather than left as an
    // unmeasured hole — it was found by the final review's F2 widening (adding a DriverLinq row to the test
    // above), and MEASURED against the pre-slice baseline before being classified.
    //
    // MEASUREMENT (worktree at 8b996aa7, the commit immediately BEFORE this slice, running this same file):
    // all three modes failed for this shape — NativeOnly with NativeTranslationNotSupportedException ("projects
    // a non-entity result"), and BOTH Native and DriverLinq with a NullReferenceException out of
    // BsonBinding.TryGetValueAtPath. So `new { c, PostCount = c.Posts.Count }` NEVER worked on the fallback
    // legs; EF-412 fixed the two native legs and did not touch this one. What DID change is the exception:
    // the mixed removing visitor now resolves the populated Select.Projection's "PostCount" alias and asks for
    // a model property of that name, so the failure is a clearer compile-time InvalidOperationException instead
    // of a per-row NRE. Per the versioning rubric that is not a break (this shape has no working baseline to
    // regress, and the exception type of an unsupported shape is not contract), and per the read-side design it
    // is not silent wrong data either — it throws.
    //
    // Deliberately asserts only that it FAILS, not the exception type or message: the point of the test is that
    // the gap is known and covered, and a future fix should make this test fail loudly (delete it and add the
    // DriverLinq row back to the theory above) rather than leaving the gap re-discoverable. Follow-up ticket
    // filed: EF-443 — the owned-collection-count leaf's read side on the mixed/DriverLinq leg.
    [Fact]
    public void Mixed_whole_entity_and_owned_collection_count_leaf_still_fails_under_explicit_DriverLinq()
    {
        var (collection, logs) = SeedCustomersWithPosts(
            nameof(Mixed_whole_entity_and_owned_collection_count_leaf_still_fails_under_explicit_DriverLinq));
        using var db = CreateContext(collection, logs, MongoQueryMode.DriverLinq, CustomerWithPostsModel);

        Assert.ThrowsAny<Exception>(
            () => db.Entities.Select(c => new { c, PostCount = c.Posts.Count }).OrderBy(r => r.c.Name).ToList());
    }

    // Step 3: the DEGENERATE single-leaf case — Select(c => new { c }), i.e. the entity leaf with NO sibling
    // at all. This must go native via the NEW Route == Projection mechanism this slice adds, which is a
    // DIFFERENT NativeRoute value from the pre-existing bare Select(c => c) NativeRoute.WholeEntity route —
    // both are proven side by side here under NativeOnly so neither can be quietly broken by a change that
    // conflates the two.
    //
    // FINAL-REVIEW WIDENING (finding F2): a [Theory] over ALL THREE modes, for the same reason as
    // Mixed_whole_entity_and_owned_collection_count_leaf_works_in_both_native_modes above — the DEGENERATE
    // `new { c }` body is a second newly-native shape whose $$ROOT alias now reaches the mixed removing
    // visitor under an explicit DriverLinq, and it had no DriverLinq row. Both halves are driven by the same
    // mode: the bare `Select(c => c)` half takes the pre-existing WholeEntity route in every mode and is
    // unaffected, so keeping it in the loop costs nothing and keeps the two routes proven side by side.
    // The NativeOnly row still carries the original "both go native" proof.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Wrapped_and_bare_whole_entity_leaves_both_go_native_and_work_in_every_mode(MongoQueryMode mode)
    {
        var (collection, logs) = SeedCustomers(
            nameof(Wrapped_and_bare_whole_entity_leaves_both_go_native_and_work_in_every_mode) + mode);

        // The wrapped/degenerate shape: Route == Projection (this slice's mechanism), NOT WholeEntity.
        using (var wrapped = CreateContext(collection, logs, mode))
        {
            var results = wrapped.Entities.Select(c => new { c }).OrderBy(r => r.c.Name).ToList();

            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
            Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        }

        // The pre-existing bare shape: Route == WholeEntity. Side-by-side proof the two routes coexist.
        using (var bare = CreateContext(collection, [], mode))
        {
            var results = bare.Entities.OrderBy(c => c.Name).ToList();

            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(c => c.Name).ToArray());
            Assert.Equal([7, 20, -7], results.Select(c => c.Age).ToArray());
        }
    }

    // Step 4: entity leaf + computed sibling, ordered by the COMPUTED sibling rather than an entity member —
    // catches any accidental coupling of the native routing to OrderBy-by-entity-member, which every other
    // Mixed_whole_entity_and_computed_leaf_* test in this file happens to use (OrderBy(r => r.c.Name)).
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_ordered_by_the_computed_sibling_goes_native()
    {
        var (collection, logs) = SeedCustomers(
            nameof(Mixed_whole_entity_and_computed_leaf_ordered_by_the_computed_sibling_goes_native));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Must not throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score })
            .OrderBy(r => r.Total).ToList();

        Assert.Equal([-14, 14, 400], results.Select(r => r.Total).ToArray());
        Assert.Equal(["Carol", "Alice", "Bob"], results.Select(r => r.c.Name).ToArray());
    }

    // ── Guard fallbacks: graceful — there IS a driver-LINQ oracle, and results must agree ────────────

    // EF-434 RE-BASELINE. Was Integer_division_projection_falls_back_gracefully_except_under_NativeOnly, which
    // pinned TryTranslateValue's blanket integer-division decline. That guard is gone: an integral-result
    // division now translates to MongoBinaryOperator.IntegerDivide and renders as $trunc-of-$divide, so this
    // projection goes NATIVE and agrees with C#.
    //
    // The seed is deliberately NO LONGER evenly divisible. The old one (8/2, 21/7, -9/3) was chosen to
    // sidestep the very failure this ticket fixes — a non-integral $divide result cannot be deserialized into
    // an int member — so keeping it would have left the fix unmeasured. Each expected value below is one only
    // truncate-toward-zero produces: 7/2 -> 3 (raw 3.5), 20/3 -> 6 (raw 6.67), -7/2 -> -3 (raw -3.5, floor -4).
    [Fact]
    public void Integer_division_projection_goes_native_and_truncates_EF434()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Integer_division_projection_goes_native_and_truncates_EF434)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 7 }, { "Score", 2 }, { "Weight", 1.0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 20 }, { "Score", 3 }, { "Weight", 1.0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -7 }, { "Score", 2 }, { "Weight", 1.0 } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();

        // NativeOnly forbids the driver-LINQ fallback, so succeeding here IS the proof the shape went native.
        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
                .ToList().OrderBy(r => r.Name).ToList();

            Assert.Equal([3, 6, -3], results.Select(r => r.X).ToArray());
            Assert.Contains("$trunc", Mql(logs));
        }

        // The default mode must agree with NativeOnly. Driver-LINQ is deliberately NOT compared against here:
        // it still emits the raw $divide and therefore still throws on the non-integral quotients above —
        // that is the released-behaviour bug EF-434 fixes on the native path, not a parity target.
        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        Assert.Equal([3, 6, -3],
            native.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
                .ToList().OrderBy(r => r.Name).Select(r => r.X).ToArray());
    }

    // ── SUPERSEDED (EF-448): string CONCATENATION (`c.Name + "!"`) now goes native via $concat — see
    // NativeStringConcatTests. This test originally used concatenation as its "falls back" example; it now
    // uses a string-method-call leaf (ToUpper), which still has no native translation, to keep exercising the
    // graceful-fallback-except-under-NativeOnly contract for a genuinely unrepresentable computed leaf.

    [Fact]
    public void String_method_call_projection_falls_back_gracefully_except_under_NativeOnly()
    {
        var (collection, logs) = SeedCustomers(nameof(String_method_call_projection_falls_back_gracefully_except_under_NativeOnly));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var query = nativeOnly.Entities.Select(c => new { X = c.Name.ToUpper() });
            Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { X = c.Name.ToUpper() }).OrderBy(r => r.X).ToList();
        var driverResults = driver.Entities.Select(c => new { X = c.Name.ToUpper() }).OrderBy(r => r.X).ToList();

        Assert.Equal(driverResults, nativeResults);
        Assert.Equal(["ALICE", "BOB", "CAROL"], nativeResults.Select(r => r.X).ToArray());
    }

    // ── FIXED (EF-412/EF-356): mixed whole-entity + computed-arithmetic, once a silent wrong-data bug ─────
    //
    // Select(c => new { c, Total = c.Age * c.Score }) mixes a whole-entity leaf with a computed-arithmetic
    // leaf. This shape now has its own native route (NativeRoute.Projection, emitted as {"c": "$$ROOT"} in the
    // $project — see NativeProjectionBinder and MongoProjectionBindingExpressionVisitor), proven native under
    // NativeOnly by Mixed_whole_entity_and_computed_leaf_goes_native below.
    //
    // Historically (pre-EF-356, before this native route existed) this shape routed to the MIXED shaper
    // (MongoMixedProjectionBindingRemovingExpressionVisitor) via the default (non-native) projection-binding
    // walk, where the BinaryExpression's two operands (c.Age, c.Score) were each visited as ordinary
    // MemberExpressions against the SAME current projection member (no per-operand member push for a bare
    // arithmetic node) — so the second operand's binding silently overwrote the first's in the projection
    // mapping, and every row's "Total" came out as Score*Score instead of Age*Score. That bug was fixed as
    // EF-356 on the mixed-shaper path itself (independent of, and prior to, EF-412's native route). This test
    // now asserts the CORRECT values under the default (Native) mode and doubles as a regression pin for both
    // fixes: a future change to either the mixed shaper or the native route that reintroduced the clobber would
    // show up here as a wrong Total, not a thrown exception.
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value()
    {
        var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value));
        using var db = CreateContext(collection, [], MongoQueryMode.Native);

        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

        // Whole-entity fields materialize correctly...
        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        Assert.Equal([2, 20, 2], results.Select(r => r.c.Score).ToArray());

        // ...and so does the computed leaf beside them. This shape USED to come out as Score*Score rather
        // than Age*Score - a silent wrong-data bug (EF-356), fixed on the main-bound line: the whole-entity
        // leaf no longer clobbers the computed one's projection-member slot. Alice and Carol are what
        // discriminate the fix; Bob's row cannot, because Age == Score there makes both answers 400.
        Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
        Assert.Equal(14, results.Single(r => r.c.Name == "Alice").Total);
    }

    // EF-412 bind-side slice: the emit side (NativeProjectionBinder, task 1) now recognizes a whole-root-entity
    // leaf mixed with a computed sibling and routes it to Route == Projection (emitted as {"c": "$$ROOT"} in the
    // native $project). This test proves the BIND side (MongoProjectionBindingExpressionVisitor) no longer
    // declines that shape under NativeOnly with NativeTranslationNotSupportedException. The read side (Task 3)
    // may still be incomplete, so this test may still fail here with a DIFFERENT (read-side) error.
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_goes_native()
    {
        var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_goes_native));
        using var db = CreateContext(collection, [], MongoQueryMode.NativeOnly);

        // Must not throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
    }

    // EF-412 read-side slice. The emit + bind sides route this shape natively, which means the FALLBACK leg
    // has to keep working too: under explicit DriverLinq, MongoShapedQueryCompilingExpressionVisitor skips the
    // native Route == Projection branch and hands the MIXED removing visitor a WHOLE, un-projected document —
    // where the emitted "c" alias ($$ROOT) names no element. Without the read-side fix this case fails with
    // "Field 'c' required but not present in BsonDocument for a 'Customer'". This shape worked correctly on
    // DriverLinq before this slice, so a failure here is a regression, not a gap.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Mixed_whole_entity_and_computed_leaf_works_in_every_mode(MongoQueryMode mode)
    {
        var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_works_in_every_mode) + mode);
        using var db = CreateContext(collection, [], mode);

        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
    }

    // EF-412 LATE-FALLBACK leg. The shaper is built FIRST and native-vs-driver decided SECOND, so a query
    // routed native at translate time (Route == Projection) can still have TryBuildNativeFactory decline
    // MID-COMPILE — while the ALREADY alias-addressed native (non-mixed) removing visitor stays in place over
    // whatever the driver-LINQ bridge renders. A CAPTURED LOCAL in a string.StartsWith is the measured trigger
    // (the native renderer declines a parameterized regex term); a constant would never reach this leg and
    // would leave this test falsely green. This is the same mechanism Ef362OwnedHopArrayProjectionTests
    // documents, and the risk the EF-412 spike flagged as unverified for a "$$ROOT"-bound alias.
    //
    // Asserted under the DEFAULT Native mode (not DriverLinq): the point is that the default mode must not
    // return wrong data or throw on this combination. Values, not counts — the failure mode this pins is
    // "Field 'c' required but not present in BsonDocument", but a near-miss variant could equally have
    // returned a null entity or a misread Total.
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_behind_a_parameterized_where_reads_correct_values()
    {
        var (collection, logs) = SeedCustomers(
            nameof(Mixed_whole_entity_and_computed_leaf_behind_a_parameterized_where_reads_correct_values));

        var namePrefix = "A";

        // HALF THE DISCRIMINATOR, and it is not decoration. Without it this test would pass just as happily if
        // the shape went fully native, and would then prove nothing about the late-fallback leg at all.
        // NativeOnly forbids the fallback, so this throw is the proof that TryBuildNativeFactory declines
        // MID-COMPILE for this exact query (MQL has no parameterized-regex form — see
        // MongoQueryLanguageRenderer.RenderRegex) and therefore that the Native leg below genuinely executes
        // the driver-LINQ bridge underneath an already alias-addressed native shaper. MEASURED, not assumed.
        //
        // The OTHER half is the MQL assertion at the end of this test: this throw alone establishes only that
        // the query is ROUTED down the fallback leg, not that the shaper and the driver's rendering agree on
        // the alias — that is what the "c" : "$$ROOT" assertion pins, and it is the part that would catch a
        // renderer change on either side. Neither assertion is sufficient alone.
        using (var nativeOnly = CreateContext(collection, [], MongoQueryMode.NativeOnly))
        {
            var declined = nativeOnly.Entities
                .Where(c => c.Name.StartsWith(namePrefix))
                .Select(c => new { c, Total = c.Age * c.Score });
            Assert.Throws<NativeTranslationNotSupportedException>(() => declined.ToList());
        }

        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var results = db.Entities
            .Where(c => c.Name.StartsWith(namePrefix))
            .Select(c => new { c, Total = c.Age * c.Score })
            .OrderBy(r => r.c.Name)
            .ToList();

        var row = Assert.Single(results);
        Assert.Equal("Alice", row.c.Name);
        Assert.Equal(7, row.c.Age);
        Assert.Equal(2, row.c.Score);
        Assert.NotEqual(ObjectId.Empty, row.c.Id);
        Assert.Equal(14, row.Total);

        // WHY this passes with NO code change, recorded so the negative result is not re-derived from scratch.
        // The driver-LINQ bridge renders the SAME projection the native emit side would have — the alias is the
        // projection MEMBER name ("c") and the value is "$$ROOT" — so the alias-addressed native shaper's
        // doc["c"] read hits. That is precisely the harmless case ShouldStripBareProjectionOnFallback's remarks
        // describe: this leaf registers no ProjectionAliasTier.DocumentPath alias OVERRIDE (its alias already IS
        // the member name), so HasDocumentPathAliasOverride stays false, no late-fallback strip fires, and none
        // is needed. Contrast the EF-362 owned-hop array leaf, whose emit-side alias ("Home.Notes") is a
        // document path the driver would never pick — that is what makes the strip load-bearing there and inert
        // here. Asserted on the captured MQL so a future change to EITHER renderer that breaks that agreement
        // fails loudly here instead of silently returning a null entity.
        var mql = Mql(logs);
        Assert.Contains("\"c\" : \"$$ROOT\"", mql);
        Assert.Contains("$multiply", mql);
    }
}
