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
/// Numeric arithmetic computed projection leaves bound into <c>$project</c> via
/// <see cref="MongoExpressionTranslator.TryTranslateValue"/>/<c>NativeProjectionBinder</c> (comparison operands are
/// in <see cref="NativeExprComparisonTests"/>). Each shape is proven native under <see cref="MongoQueryMode.NativeOnly"/>,
/// checked for Native/driver-LINQ parity, and checked for the expected MQL operator. Unrepresentable leaves must fall
/// back correctly and throw under <c>NativeOnly</c>.
///
/// Also covers a whole-root-entity leaf mixed with computed/scalar/count siblings
/// (<c>Select(c => new { c, Total = c.Age * c.Score })</c>), which goes native via <c>NativeRoute.Projection</c>
/// (distinct from bare <c>Select(c => c)</c>'s <c>NativeRoute.WholeEntity</c>), plus its late-fallback and
/// driver-LINQ legs.
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

    // ── Entity type + fixture for the owned-collection-count sibling variation ───────────────────────

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

        // NativeOnly proves the $project went native; parity alone wouldn't, since a silent fallback gives identical
        // results.
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

    // ── Whole-root-entity leaf mixed with a sibling ──────────────────────────────────────────────────

    // Entity leaf + plain scalar member sibling: the Route == Projection branch mustn't depend on the sibling being
    // a BinaryExpression. All three modes: under DriverLinq, Select.Projection is populated too, so the
    // IsWholeRootEntityAlias null-out in MongoProjectionBindingRemovingExpressionVisitor is load-bearing.
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

    // Entity leaf + owned-collection .Count sibling: the whole-entity and count-leaf branches in one NewExpression.
    // DriverLinq is excluded; see the _still_fails_under_explicit_DriverLinq test below.
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

    // Known gap (EF-443): `new { c, PostCount = c.Posts.Count }` has never worked under explicit DriverLinq. The
    // mixed removing visitor now fails at compile time (InvalidOperationException, looking up a "PostCount" model
    // property) rather than with a per-row NRE. Asserts only that it fails, so a fix makes this test fail: then
    // delete it and add DriverLinq to the theory above.
    [Fact]
    public void Mixed_whole_entity_and_owned_collection_count_leaf_still_fails_under_explicit_DriverLinq()
    {
        var (collection, logs) = SeedCustomersWithPosts(
            nameof(Mixed_whole_entity_and_owned_collection_count_leaf_still_fails_under_explicit_DriverLinq));
        using var db = CreateContext(collection, logs, MongoQueryMode.DriverLinq, CustomerWithPostsModel);

        Assert.ThrowsAny<Exception>(
            () => db.Entities.Select(c => new { c, PostCount = c.Posts.Count }).OrderBy(r => r.c.Name).ToList());
    }

    // Degenerate `new { c }` (Route == Projection) side by side with bare `Select(c => c)` (Route == WholeEntity), so
    // a change conflating the two routes breaks one of them. All three modes, since the $$ROOT alias reaches the
    // mixed removing visitor under DriverLinq too.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Wrapped_and_bare_whole_entity_leaves_both_go_native_and_work_in_every_mode(MongoQueryMode mode)
    {
        var (collection, logs) = SeedCustomers(
            nameof(Wrapped_and_bare_whole_entity_leaves_both_go_native_and_work_in_every_mode) + mode);

        // Wrapped shape: Route == Projection.
        using (var wrapped = CreateContext(collection, logs, mode))
        {
            var results = wrapped.Entities.Select(c => new { c }).OrderBy(r => r.c.Name).ToList();

            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
            Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        }

        // Bare shape: Route == WholeEntity.
        using (var bare = CreateContext(collection, [], mode))
        {
            var results = bare.Entities.OrderBy(c => c.Name).ToList();

            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(c => c.Name).ToArray());
            Assert.Equal([7, 20, -7], results.Select(c => c.Age).ToArray());
        }
    }

    // Entity leaf + computed sibling ordered by the computed sibling, catching any coupling of native routing to
    // OrderBy-by-entity-member (which the other mixed tests use).
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

    // Integral division translates to MongoBinaryOperator.IntegerDivide ($trunc of $divide). The seed is
    // deliberately not evenly divisible (a non-integral $divide result can't deserialize into an int); each
    // expected value is one only truncate-toward-zero gives: 7/2 -> 3, 20/3 -> 6, -7/2 -> -3 (floor would be -4).
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

        // NativeOnly forbids fallback, so success proves the shape went native.
        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
                .ToList().OrderBy(r => r.Name).ToList();

            Assert.Equal([3, 6, -3], results.Select(r => r.X).ToArray());
            Assert.Contains("$trunc", Mql(logs));
        }

        // Not compared against driver-LINQ, which emits raw $divide and throws on non-integral quotients (the
        // released-behavior bug fixed on the native path).
        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        Assert.Equal([3, 6, -3],
            native.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
                .ToList().OrderBy(r => r.Name).Select(r => r.X).ToArray());
    }

    // ── String-method leaf (Split) has no native translation: graceful fallback except under NativeOnly.
    // (Concatenation is native; see NativeStringConcatTests.)

    [Fact]
    public void String_method_call_projection_falls_back_gracefully_except_under_NativeOnly()
    {
        var (collection, logs) = SeedCustomers(nameof(String_method_call_projection_falls_back_gracefully_except_under_NativeOnly));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var query = nativeOnly.Entities.Select(c => new { X = c.Name.Split('o', StringSplitOptions.None)[0] });
            Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { X = c.Name.Split('o', StringSplitOptions.None)[0] })
            .OrderBy(r => r.X).ToList();
        var driverResults = driver.Entities.Select(c => new { X = c.Name.Split('o', StringSplitOptions.None)[0] })
            .OrderBy(r => r.X).ToList();

        Assert.Equal(driverResults, nativeResults);
        Assert.Equal(["Alice", "B", "Car"], nativeResults.Select(r => r.X).ToArray());
    }

    // ── Mixed whole-entity + computed-arithmetic ───────────────────────────────────────────────────────
    //
    // On the mixed-shaper path, both operands of `c.Age * c.Score` once bound to the same projection member, so
    // Total came out as Score*Score (EF-356). Pins correct values under default Native mode against regressions in
    // either the mixed shaper or the native route (see Mixed_whole_entity_and_computed_leaf_goes_native).
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value()
    {
        var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value));
        using var db = CreateContext(collection, [], MongoQueryMode.Native);

        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        Assert.Equal([2, 20, 2], results.Select(r => r.c.Score).ToArray());

        // Alice and Carol discriminate the Score*Score clobber; Bob can't (Age == Score).
        Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
        Assert.Equal(14, results.Single(r => r.c.Name == "Alice").Total);
    }

    // Bind side (MongoProjectionBindingExpressionVisitor) must not decline the whole-entity + computed shape under
    // NativeOnly; it's emitted as {"c": "$$ROOT"} with Route == Projection.
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

    // Under explicit DriverLinq the mixed removing visitor gets a whole, un-projected document where the "c" ($$ROOT)
    // alias names no element; without the read-side handling this fails with "Field 'c' required but not present".
    // This worked on DriverLinq before, so a failure here is a regression.
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

    // Whole-entity + computed leaf with a genuine query parameter (parameterized StartsWith, now natively
    // representable), under Native and NativeOnly.
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_behind_a_parameterized_where_reads_correct_values()
    {
        var (collection, logs) = SeedCustomers(
            nameof(Mixed_whole_entity_and_computed_leaf_behind_a_parameterized_where_reads_correct_values));

        var namePrefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(collection, [], MongoQueryMode.NativeOnly))
        {
            var nativeOnlyResults = nativeOnly.Entities
                .Where(c => c.Name.StartsWith(namePrefix))
                .Select(c => new { c, Total = c.Age * c.Score })
                .ToList();

            var nativeOnlyRow = Assert.Single(nativeOnlyResults);
            Assert.Equal("Alice", nativeOnlyRow.c.Name);
            Assert.Equal(14, nativeOnlyRow.Total);
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

        // Asserted on the captured MQL so a future regression in either the whole-entity leaf's "$$ROOT" alias
        // or the computed sibling's arithmetic rendering fails loudly here instead of silently returning a
        // null entity or a misread Total.
        var mql = Mql(logs);
        Assert.Contains("\"c\" : \"$$ROOT\"", mql);
        Assert.Contains("$multiply", mql);
    }
}
