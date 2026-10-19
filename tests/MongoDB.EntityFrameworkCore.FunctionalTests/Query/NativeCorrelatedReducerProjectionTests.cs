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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// EF-449: end-to-end functional coverage for a reference-collection-nav <c>First</c>/<c>FirstOrDefault</c>
/// reduced to a scalar member inside a projection (<c>a.IdentificationMethods.FirstOrDefault().Method</c>) —
/// the first task in this feature's plan to prove the shape against a real MongoDB server rather than the
/// unit-tested translation pipeline in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <c>FirstOrDefault().Member</c> needs NO new read-side code: the <c>$lookup</c>'s sub-pipeline narrows to
/// 0-or-1 matched documents, the left-outer <c>$unwind</c> (<c>preserveNullAndEmptyArrays: true</c>) turns a
/// no-match into a <c>null</c> lookup field, and <c>"$_lookup_IdentificationMethods.Method"</c> on a
/// <c>null</c> parent evaluates to MISSING in a <c>$project</c> stage (confirmed empirically here, not merely
/// assumed — see <see cref="FirstOrDefault_over_empty_reference_collection_reads_as_default"/>'s own remarks) —
/// which the pre-existing generic alias read (<c>BsonBinding.GetElementValue&lt;T&gt;</c>) already turns into
/// <c>default(T)</c> for a nullable-typed read (a reference type like <c>string</c> here), matching
/// <c>FirstOrDefault()</c>'s own LINQ contract with no new code.
/// </para>
/// <para>
/// <c>First().Member</c> DOES need new read-side code: without it, the same missing-field read would silently
/// return <c>default(T)</c> too, which is wrong — <c>Enumerable.First()</c> must throw
/// <see cref="InvalidOperationException"/>("Sequence contains no elements") when the source is empty. The fix
/// (<c>MongoProjectionBindingRemovingExpressionVisitor</c>'s alias-read branch) checks the projection alias
/// against <c>MongoQueryExpression.CorrelatedReducerLeaves</c>: if the alias matches a leaf with
/// <c>ThrowOnEmpty == true</c>, it emits a check-and-throw around the raw alias read instead of the ordinary
/// unconditional read, keyed purely by alias so no other leaf kind is affected.
/// </para>
/// <para>
/// EF-449 FIX: that alias-only check is only sound for a NON-nullable reduced member — a matched row whose own
/// member happens to be null/absent is otherwise indistinguishable from "no related row" and <c>First()</c>
/// would incorrectly throw for a row that genuinely exists. <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c>
/// closes this by declining <c>First()</c> (never <c>FirstOrDefault()</c>) up front when the reduced member's own
/// type is nullable (<c>Nullable&lt;T&gt;</c> or a reference type, e.g. <c>string</c> — <c>Method</c> here) —
/// see <see cref="First_over_nullable_typed_member_declines"/> and
/// <see cref="FirstOrDefault_over_nullable_typed_member_still_works"/>. The <c>First()</c> tests below therefore
/// reduce to <c>Rank</c> (a non-nullable <c>int</c>), not <c>Method</c>.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeCorrelatedReducerProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Animal
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public System.Collections.Generic.List<IdentificationMethod> IdentificationMethods { get; set; } = null!;
    }

    /// <summary>
    /// The member kind the motivating spec test
    /// (<c>BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default</c>) reduces to:
    /// an enum, i.e. a NON-NULLABLE VALUE TYPE. See the "nullable-widened" section at the bottom of this file.
    /// </summary>
    private enum IdentificationKind
    {
        Unknown = 0,
        Implant = 1,
        Visual = 2
    }

    private class IdentificationMethod
    {
        public ObjectId Id { get; set; }
        public string Method { get; set; } = "";
        public int Rank { get; set; }
        public IdentificationKind Kind { get; set; }
        public ObjectId AnimalId { get; set; }
        public Animal Animal { get; set; } = null!;
    }

    private AnimalDbContext CreateContext(
        MongoQueryMode mode, string name, out ObjectId withMethods, out ObjectId withoutMethods,
        ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var animals = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "A" + suffix;
        var methods = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "M" + suffix;

        withMethods = ObjectId.GenerateNewId();
        withoutMethods = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<Animal>(animals).InsertMany(
        [
            new Animal { Id = withMethods, Name = "Rex" },
            new Animal { Id = withoutMethods, Name = "Fido" },
        ]);
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod
            {
                Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, Kind = IdentificationKind.Implant,
                AnimalId = withMethods
            },
            new IdentificationMethod
            {
                Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, Kind = IdentificationKind.Visual,
                AnimalId = withMethods
            },
        ]);

        return new AnimalDbContext(database, animals, methods, mode, loggerFactory);
    }

    // ── FirstOrDefault: no new read-side code, verified against real data ─────────────────────────────────────

    [Fact]
    public void FirstOrDefault_member_returns_correct_value_when_a_related_row_exists()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(FirstOrDefault_member_returns_correct_value_when_a_related_row_exists),
            out var withMethods, out _);

        var result = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method })
            .Single();

        // NativeOnly succeeding proves this shape genuinely went native rather than gracefully falling back.
        Assert.Equal("Microchip", result.M);
    }

    /// <summary>
    /// The empirical claim under test: when the $lookup's sub-pipeline matches nothing, the left-outer $unwind
    /// makes the lookup field null, and reading a dotted path through it ("$_lookup_IdentificationMethods.Method")
    /// in a $project stage produces a MISSING field on the projected document (not a present null) — which the
    /// existing generic alias read already turns into default(string) = null, matching FirstOrDefault()'s LINQ
    /// contract, with no new code. This must fail (return a thrown exception, a BSON error, or a non-null wrong
    /// value) if that empirical claim about MongoDB's $project semantics turns out to be false.
    /// </summary>
    [Fact]
    public void FirstOrDefault_over_empty_reference_collection_reads_as_default()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(FirstOrDefault_over_empty_reference_collection_reads_as_default),
            out _, out var withoutMethods);

        var result = db.Animals
            .Where(a => a.Id == withoutMethods)
            .Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Method })
            .Single();

        Assert.Null(result.M);
    }

    // ── First: new read-side code (throw-on-empty), verified against real data ────────────────────────────────

    [Fact]
    public void First_member_returns_correct_value_when_a_related_row_exists()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(First_member_returns_correct_value_when_a_related_row_exists),
            out var withMethods, out _);

        // Reduces to Rank (non-nullable int), not Method (string) — see the class remarks for why First()
        // declines a nullable-typed reduced member (EF-449 fix).
        var result = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, R = a.IdentificationMethods.OrderBy(m => m.Rank).First().Rank })
            .Single();

        Assert.Equal(1, result.R);
    }

    [Fact]
    public async Task First_over_empty_reference_collection_throws()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(First_over_empty_reference_collection_throws),
            out _, out var withoutMethods);

        var query = db.Animals
            .Where(a => a.Id == withoutMethods)
            .Select(a => new { a.Id, R = a.IdentificationMethods.First().Rank });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    /// <summary>Same throw-on-empty assertion under the default <see cref="MongoQueryMode.Native"/> gate too,
    /// so the behavior isn't accidentally specific to <c>NativeOnly</c>'s own error path.</summary>
    [Fact]
    public void First_over_empty_reference_collection_throws_under_default_native_mode()
    {
        using var db = CreateContext(
            MongoQueryMode.Native, nameof(First_over_empty_reference_collection_throws_under_default_native_mode),
            out _, out var withoutMethods);

        var query = db.Animals
            .Where(a => a.Id == withoutMethods)
            .Select(a => new { a.Id, R = a.IdentificationMethods.First().Rank });

        var ex = Assert.Throws<InvalidOperationException>(() => query.Single());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    // ── EF-449 bug fix: First() over a NULLABLE-typed reduced member declines instead of mis-throwing ─────────

    /// <summary>
    /// The bug this fix closes: <c>First()</c> reduced to <c>Method</c> (a <c>string</c>, i.e. nullable) cannot
    /// be told apart on the read side between "no related row" and "a related row exists but its Method is
    /// null/absent" — so <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c> now declines this shape
    /// entirely, up front at translate time. This whole leaf family has no driver-LINQ fallback oracle (a
    /// reference-collection-nav reduction is not a shape the C# driver's own LINQ v3 provider understands
    /// either), so the decline surfaces as EF Core's own generic "could not be translated" failure rather than
    /// admitting a leaf that could incorrectly throw "Sequence contains no elements" for a row that genuinely
    /// exists.
    /// </summary>
    [Fact]
    public void First_over_nullable_typed_member_declines()
    {
        using var db = CreateContext(
            MongoQueryMode.Native, nameof(First_over_nullable_typed_member_declines),
            out var withMethods, out _);

        var query = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, M = a.IdentificationMethods.First().Method });

        var ex = Assert.Throws<InvalidOperationException>(() => query.Single());
        Assert.Contains("could not be translated", ex.Message);
    }

    /// <summary>
    /// The discriminating control for <see cref="First_over_nullable_typed_member_declines"/>:
    /// <c>FirstOrDefault()</c> over the exact same nullable member (<c>Method</c>) is completely unaffected by
    /// the fix and still goes native and reads correctly — only <c>First()</c>'s ambiguous throw-on-empty case
    /// is narrowed.
    /// </summary>
    [Fact]
    public void FirstOrDefault_over_nullable_typed_member_still_works()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(FirstOrDefault_over_nullable_typed_member_still_works),
            out var withMethods, out _);

        var result = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method })
            .Single();

        Assert.Equal("Microchip", result.M);
    }

    // ── Task 7: predicate / ordering / multi-document correctness ─────────────────────────────────────────────
    //
    // No driver-LINQ oracle exists for this shape (confirmed across Tasks 1-6: it hard-fails under
    // MongoQueryMode.DriverLinq too, in the driver's own LINQ v3 provider), so these tests follow the file's
    // established pattern above: seed known rows with a known relationship, compute the expected value by hand
    // from that seed data, and assert the native (NativeOnly, to prove genuine native execution) result matches.
    // Each case seeds multiple candidate rows where picking the wrong one would produce a different, wrong
    // answer — not a single-candidate setup that would pass even with a broken predicate/sort.

    private AnimalDbContext CreateEmptyContext(MongoQueryMode mode, string name, out string animals, out string methods)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        animals = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "A" + suffix;
        methods = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "M" + suffix;
        return new AnimalDbContext(database, animals, methods, mode, null);
    }

    [Fact]
    public void FirstOrDefault_with_constant_predicate_returns_the_matching_row_not_the_positional_first()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(FirstOrDefault_with_constant_predicate_returns_the_matching_row_not_the_positional_first),
            out var animals, out var methods);

        var animalId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertOne(new Animal { Id = animalId, Name = "Rex" });
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "EarTag", Rank = 3, AnimalId = animalId },
        ]);

        var result = db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Method == "Tattoo")!.Method })
            .Single();

        // "Microchip" is the positional-first row; a broken predicate (or one silently ignored) would return
        // it instead of "Tattoo", which is the actual match.
        Assert.Equal("Tattoo", result.M);
    }

    [Fact]
    public void OrderBy_and_OrderByDescending_FirstOrDefault_pick_opposite_rows()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(OrderBy_and_OrderByDescending_FirstOrDefault_pick_opposite_rows),
            out var animals, out var methods);

        var animalId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertOne(new Animal { Id = animalId, Name = "Rex" });
        // Inserted out of rank order so a naive "positional first" read would be wrong either way.
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 3, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "EarTag", Rank = 1, AnimalId = animalId },
        ]);

        var ascending = db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new { a.Id, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method })
            .Single();

        var descending = db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new { a.Id, M = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault()!.Method })
            .Single();

        Assert.Equal("EarTag", ascending.M);   // Rank 1: lowest
        Assert.Equal("Microchip", descending.M); // Rank 3: highest
    }

    [Fact]
    public void OrderBy_combined_with_predicate_excludes_the_row_that_would_otherwise_sort_first()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(OrderBy_combined_with_predicate_excludes_the_row_that_would_otherwise_sort_first),
            out var animals, out var methods);

        var animalId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertOne(new Animal { Id = animalId, Name = "Rex" });
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "EarTag", Rank = 3, AnimalId = animalId },
        ]);

        // Without the predicate, OrderBy(Rank).FirstOrDefault() would pick "Microchip" (Rank 1). The predicate
        // excludes it, so the correct answer is the next-lowest-ranked matching row, "Tattoo" (Rank 2) — not
        // "EarTag", and not "Microchip" leaking through because the filter was silently ignored.
        var result = db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new
            {
                a.Id,
                M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault(m => m.Method != "Microchip")!.Method
            })
            .Single();

        Assert.Equal("Tattoo", result.M);
    }

    [Fact]
    public void Each_outer_document_picks_its_own_first_row_not_a_global_one()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(Each_outer_document_picks_its_own_first_row_not_a_global_one),
            out var animals, out var methods);

        var animal1 = ObjectId.GenerateNewId();
        var animal2 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertMany(
        [
            new Animal { Id = animal1, Name = "Rex" },
            new Animal { Id = animal2, Name = "Fido" },
        ]);
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, AnimalId = animal1 },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, AnimalId = animal1 },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "EarTag", Rank = 1, AnimalId = animal2 },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Collar", Rank = 2, AnimalId = animal2 },
        ]);

        var results = db.Animals
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method })
            .ToList();

        var rex = results.Single(r => r.Id == animal1);
        var fido = results.Single(r => r.Id == animal2);

        // If the correlation leaked across documents (e.g. a global "first" instead of a per-document one),
        // both would end up with the same Method value.
        Assert.Equal("Microchip", rex.M);
        Assert.Equal("EarTag", fido.M);
    }

    [Fact]
    public async Task Correlated_reducer_projection_with_predicate_and_ordering_succeeds_under_NativeOnly()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(Correlated_reducer_projection_with_predicate_and_ordering_succeeds_under_NativeOnly),
            out var animals, out var methods);

        var animalId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertOne(new Animal { Id = animalId, Name = "Rex" });
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, AnimalId = animalId },
            new IdentificationMethod { Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, AnimalId = animalId },
        ]);

        // NativeOnly throws NativeTranslationNotSupportedException on any decline/fallback, so a passing result
        // here proves the predicate+ordering combination genuinely goes native rather than passing by
        // coincidence via a fallback path.
        var result = await db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new
            {
                a.Id,
                M = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault(m => m.Method == "Tattoo")!.Method
            })
            .SingleAsync();

        Assert.Equal("Tattoo", result.M);
    }

    // ── EF-449 fix 2: FirstOrDefault() over a NON-NULLABLE VALUE-TYPE reduced member ───────────────────────────
    //
    // The shape the motivating spec test needs, and the one this file's original tests never exercised (they
    // reduced either a `string` via FirstOrDefault, or an `int` via First()). EF's nav-expansion represents "no
    // match" for a value-type FirstOrDefault() by WIDENING the reduced member to Nullable<T> inside the inner
    // Select and converting the reducer result back to the non-nullable T at the very end — so the recognizer
    // sees a UnaryExpression(Convert) leaf, not the MethodCallExpression it used to require. NativeOnly
    // throughout, so a pass proves genuine native execution rather than a graceful fallback.

    [Fact]
    public void FirstOrDefault_over_a_non_nullable_int_member_returns_the_correct_value()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(FirstOrDefault_over_a_non_nullable_int_member_returns_the_correct_value),
            out var withMethods, out _);

        var result = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, R = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault()!.Rank })
            .Single();

        // 2 (Tattoo) is the HIGHEST rank; 1 (Microchip) is both the lowest and the positional first, so a
        // dropped $sort or a mis-resolved member would produce a different value.
        Assert.Equal(2, result.R);
    }

    /// <summary>
    /// The empty-collection half: <c>FirstOrDefault()</c>'s LINQ contract is <c>default(T)</c>, which for a
    /// non-nullable value type is <c>0</c> / the zero-valued enum — NOT a throw and not a null-reference.
    /// <para>
    /// This test is the MUTATION EVIDENCE that the widened shape needs REAL read-side work. When no row matched,
    /// the reduced field is simply MISSING from the left-outer <c>$unwind</c>'s output — and for a NON-NULLABLE
    /// <c>T</c> the ordinary generic alias read does NOT yield <c>default(T)</c>, it THROWS
    /// ("Document element 'R' is missing but required"). So
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.IsDefaultOnEmptyCorrelatedReducerLeaf</c> recognizes
    /// this leaf kind by alias and emits an explicit absent-or-null → <c>default(T)</c> conditional ahead of the
    /// numeric-cast branch; reverting that branch reproduces the missing-element throw here. (An earlier version
    /// of this docstring claimed the generic read already yielded <c>default(T)</c> — disproven by exactly that
    /// mutation.)
    /// </para>
    /// </summary>
    [Fact]
    public void FirstOrDefault_over_a_non_nullable_int_member_reads_as_default_for_an_empty_collection()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly,
            nameof(FirstOrDefault_over_a_non_nullable_int_member_reads_as_default_for_an_empty_collection),
            out _, out var withoutMethods);

        var result = db.Animals
            .Where(a => a.Id == withoutMethods)
            .Select(a => new { a.Id, R = a.IdentificationMethods.FirstOrDefault()!.Rank })
            .Single();

        Assert.Equal(0, result.R);
    }

    [Fact]
    public void FirstOrDefault_over_an_enum_member_returns_the_correct_value()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(FirstOrDefault_over_an_enum_member_returns_the_correct_value),
            out var withMethods, out _);

        var result = db.Animals
            .Where(a => a.Id == withMethods)
            .Select(a => new { a.Id, K = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Kind })
            .Single();

        // Microchip (Rank 1) carries Implant; Tattoo (Rank 2) carries Visual — so picking the wrong row, or
        // reading the enum through the wrong representation, gives a different, detectably wrong answer.
        Assert.Equal(IdentificationKind.Implant, result.K);
    }

    [Fact]
    public void FirstOrDefault_over_an_enum_member_reads_as_default_for_an_empty_collection()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly,
            nameof(FirstOrDefault_over_an_enum_member_reads_as_default_for_an_empty_collection),
            out _, out var withoutMethods);

        var result = db.Animals
            .Where(a => a.Id == withoutMethods)
            .Select(a => new { a.Id, K = a.IdentificationMethods.FirstOrDefault()!.Kind })
            .Single();

        Assert.Equal(IdentificationKind.Unknown, result.K);
    }

    /// <summary>
    /// A single query returning BOTH a populated and an empty row, so the two cases are proven to coexist in one
    /// pipeline rather than only in separate single-document queries (a per-document correlation failure would
    /// otherwise be invisible: the empty row would read the populated row's value).
    /// </summary>
    [Fact]
    public async Task FirstOrDefault_over_a_value_type_member_mixes_populated_and_empty_rows_in_one_query()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly,
            nameof(FirstOrDefault_over_a_value_type_member_mixes_populated_and_empty_rows_in_one_query),
            out var withMethods, out var withoutMethods);

        // One leaf per query, deliberately: two reducer leaves over the SAME navigation collide on the
        // `_lookup_IdentificationMethods` alias and are declined by design (see
        // NativeCorrelatedReducerLeafTests.Two_leaves_over_the_same_navigation_decline).
        var ranks = await db.Animals
            .Select(a => new { a.Id, R = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Rank })
            .ToListAsync();
        var kinds = await db.Animals
            .Select(a => new { a.Id, K = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Kind })
            .ToListAsync();

        Assert.Equal(1, ranks.Single(r => r.Id == withMethods).R);
        Assert.Equal(0, ranks.Single(r => r.Id == withoutMethods).R);
        Assert.Equal(IdentificationKind.Implant, kinds.Single(r => r.Id == withMethods).K);
        Assert.Equal(IdentificationKind.Unknown, kinds.Single(r => r.Id == withoutMethods).K);
    }

    /// <summary>
    /// The widened shape with an <c>OrderBy</c> AND a constant predicate — MEASURED to nest identically to the
    /// unwrapped shape (<c>Where(fk).OrderBy(k).Where(pred).Select(m =&gt; Convert(m.Rank, int?)).FirstOrDefault()</c>),
    /// so the pre-existing chain walk handles it once the two wrapping <c>Convert</c>s are peeled.
    /// </summary>
    [Fact]
    public void FirstOrDefault_over_a_value_type_member_with_predicate_and_ordering_returns_the_correct_value()
    {
        using var db = CreateEmptyContext(
            MongoQueryMode.NativeOnly,
            nameof(FirstOrDefault_over_a_value_type_member_with_predicate_and_ordering_returns_the_correct_value),
            out var animals, out var methods);

        var animalId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Animal>(animals).InsertOne(new Animal { Id = animalId, Name = "Rex" });
        database.MongoDatabase.GetCollection<IdentificationMethod>(methods).InsertMany(
        [
            new IdentificationMethod
            {
                Id = ObjectId.GenerateNewId(), Method = "Microchip", Rank = 1, Kind = IdentificationKind.Implant,
                AnimalId = animalId
            },
            new IdentificationMethod
            {
                Id = ObjectId.GenerateNewId(), Method = "Tattoo", Rank = 2, Kind = IdentificationKind.Visual,
                AnimalId = animalId
            },
            new IdentificationMethod
            {
                Id = ObjectId.GenerateNewId(), Method = "EarTag", Rank = 3, Kind = IdentificationKind.Visual,
                AnimalId = animalId
            },
        ]);

        var result = db.Animals
            .Where(a => a.Id == animalId)
            .Select(a => new
            {
                a.Id,
                R = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault(m => m.Rank > 1)!.Rank
            })
            .Single();

        // Rank 1 sorts first but is excluded by the predicate; Rank 3 is the wrong end of the sort. Only the
        // correct predicate+sort combination yields 2.
        Assert.Equal(2, result.R);
    }

    // ── Sibling-leaf coverage: a WHOLE-ROOT-ENTITY leaf beside a reducer leaf ──────────────────────────────────
    //
    // `new { a, M = a.Nav.FirstOrDefault().Member }` — the `a` leaf is the WHOLE entity, projected as $$ROOT
    // (EF-412). Traced during EF-449's final review: this combination is ADMITTED, because the reducer leaf sets
    // neither `hasArrayLeaf` nor `hasOwnedNavEntityLeaf`, so the sibling-readability sweep those flags trigger
    // never runs over it. That the combination is admitted was a code-reading conclusion only, so it is proven
    // empirically here rather than assumed: the two leaves read from different places in the projected document
    // ($$ROOT for the entity, `_lookup_<Nav>.<Member>` for the reducer), and either one silently shadowing the
    // other would show up as a wrong Name/Rank below.

    /// <summary>
    /// The reducer leaf sits beside a whole-root-entity <c>$$ROOT</c> leaf, over the NULLABLE-widened
    /// (non-nullable value-type member) reducer shape. <c>NativeOnly</c>, so a pass proves the combination
    /// genuinely goes native rather than landing on a fallback — and this family has no driver-LINQ oracle to
    /// fall back TO, so a decline here would surface as a hard failure, not a silently different path.
    /// </summary>
    [Fact]
    public void Whole_root_entity_leaf_beside_a_reducer_leaf_reads_both_correctly()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(Whole_root_entity_leaf_beside_a_reducer_leaf_reads_both_correctly),
            out var withMethods, out var withoutMethods);

        var results = db.Animals
            .Select(a => new { a, R = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault()!.Rank })
            .ToList();

        var rex = results.Single(r => r.a.Id == withMethods);
        var fido = results.Single(r => r.a.Id == withoutMethods);

        // The whole-entity leaf materialized its own stored scalars off $$ROOT ...
        Assert.Equal("Rex", rex.a.Name);
        Assert.Equal("Fido", fido.a.Name);
        // ... and the reducer leaf read the correct per-document row (Tattoo, Rank 2, is the highest-ranked of
        // Rex's two methods; Fido has none, so FirstOrDefault's contract is default(int)).
        Assert.Equal(2, rex.R);
        Assert.Equal(0, fido.R);
    }

    /// <summary>
    /// The same sibling pairing over the NON-widened reducer shape (a <c>string</c> member, already nullable, so
    /// no <c>Convert</c> peel is involved) — the two shapes reach the leaf recognizer down different paths, so
    /// both are covered rather than assuming they behave alike once past it.
    /// </summary>
    [Fact]
    public async Task Whole_root_entity_leaf_beside_a_string_reducer_leaf_reads_both_correctly()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly,
            nameof(Whole_root_entity_leaf_beside_a_string_reducer_leaf_reads_both_correctly),
            out var withMethods, out var withoutMethods);

        var results = await db.Animals
            .Select(a => new { a, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method })
            .ToListAsync();

        var rex = results.Single(r => r.a.Id == withMethods);
        var fido = results.Single(r => r.a.Id == withoutMethods);

        Assert.Equal("Rex", rex.a.Name);
        Assert.Equal("Microchip", rex.M);
        Assert.Equal("Fido", fido.a.Name);
        Assert.Null(fido.M);
    }

    private class AnimalDbContext : DbContext
    {
        private readonly string _animals;
        private readonly string _methods;

        public AnimalDbContext(
            TemporaryDatabaseFixture database, string animals, string methods, MongoQueryMode mode,
            ILoggerFactory? loggerFactory)
            : base(Configure(database, mode, loggerFactory))
        {
            _animals = animals;
            _methods = methods;
        }

        private static DbContextOptions<AnimalDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<AnimalDbContext>()
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

        public DbSet<Animal> Animals { get; set; } = null!;
        public DbSet<IdentificationMethod> IdentificationMethods { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Animal>().ToCollection(_animals);
            modelBuilder.Entity<Animal>().HasMany(a => a.IdentificationMethods).WithOne(m => m.Animal)
                .HasForeignKey(m => m.AnimalId);
            modelBuilder.Entity<IdentificationMethod>().ToCollection(_methods);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
