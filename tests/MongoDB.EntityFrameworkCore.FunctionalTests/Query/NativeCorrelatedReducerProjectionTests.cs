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
/// End-to-end coverage for a reference-collection-nav <c>First</c>/<c>FirstOrDefault</c> reduced to a scalar member
/// in a projection (<c>a.IdentificationMethods.FirstOrDefault().Method</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>FirstOrDefault().Member</c>: on no match, the left-outer <c>$unwind</c> nulls the lookup field and the dotted
/// <c>$project</c> read yields a missing field, which the generic alias read turns into <c>default(T)</c> for a
/// nullable type.
/// </para>
/// <para>
/// <c>First().Member</c> must throw on empty rather than return <c>default(T)</c>;
/// <c>MongoProjectionBindingRemovingExpressionVisitor</c> emits a check-and-throw for aliases matching a
/// <c>ThrowOnEmpty</c> leaf in <c>MongoQueryExpression.CorrelatedReducerLeaves</c>. That is only sound for a
/// non-nullable member (a matched row with a null member looks like no row), so
/// <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c> declines <c>First()</c> over a nullable member; the
/// <c>First()</c> tests therefore reduce to <c>Rank</c> (<c>int</c>).
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
    /// Enum member, i.e. a non-nullable value type, as in
    /// <c>BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default</c>. See the
    /// "nullable-widened" section below.
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

    // ── FirstOrDefault ───────────────────────────────────────────────────────────────────────────────────────────

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

        // NativeOnly succeeding proves this shape went native.
        Assert.Equal("Microchip", result.M);
    }

    /// <summary>
    /// No match: the dotted <c>$project</c> read through the nulled lookup field must produce a missing field, read
    /// back as null. Fails if MongoDB's <c>$project</c> semantics differ.
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

    // ── First: throw-on-empty ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void First_member_returns_correct_value_when_a_related_row_exists()
    {
        using var db = CreateContext(
            MongoQueryMode.NativeOnly, nameof(First_member_returns_correct_value_when_a_related_row_exists),
            out var withMethods, out _);

        // Reduces to Rank (non-nullable), since First() declines a nullable member (see class remarks).
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

    /// <summary>Throw-on-empty under the default <see cref="MongoQueryMode.Native"/> mode too, not just
    /// <c>NativeOnly</c>.</summary>
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

    // ── First() over a nullable-typed reduced member declines ────────────────────────────────────────────────────

    /// <summary>
    /// <c>First()</c> over a nullable member (<c>Method</c>) can't distinguish "no row" from "row with null
    /// Method", so it declines at translate time rather than wrongly throwing. No driver-LINQ fallback exists for
    /// this shape, so the decline surfaces as EF's "could not be translated".
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
    /// Control for <see cref="First_over_nullable_typed_member_declines"/>: <c>FirstOrDefault()</c> over the same
    /// nullable member still goes native and reads correctly.
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

    // ── Predicate / ordering / multi-document correctness ────────────────────────────────────────────────────────
    //
    // No driver-LINQ oracle exists for this shape, so expected values are computed by hand from the seed. Each
    // case seeds several candidates so a wrong pick gives a different answer.

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

        // Without the predicate, OrderBy(Rank) would pick "Microchip" (Rank 1); with it, the answer is "Tattoo"
        // (Rank 2), not "EarTag".
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

        // A correlation leak across documents (global rather than per-document first) gives both the same Method.
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

        // NativeOnly throws on any decline/fallback, so passing proves predicate+ordering went native.
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

    // ── FirstOrDefault() over a non-nullable value-type member ───────────────────────────────────────────────────
    //
    // EF's nav-expansion widens the member to Nullable<T> inside the inner Select and converts back to T at the
    // end, so the recognizer sees a Convert leaf rather than a MethodCallExpression. NativeOnly throughout.

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

        // 2 (Tattoo) is the highest rank; 1 (Microchip) is both the lowest and the positional first, so a
        // dropped $sort or a mis-resolved member would produce a different value.
        Assert.Equal(2, result.R);
    }

    /// <summary>
    /// Empty collection: <c>FirstOrDefault()</c> must yield <c>default(T)</c> (0 / zero enum), not throw.
    /// <para>
    /// For a non-nullable <c>T</c> the generic alias read throws on the missing field ("Document element 'R' is
    /// missing but required"), so
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.IsDefaultOnEmptyCorrelatedReducerLeaf</c> emits an
    /// absent-or-null → <c>default(T)</c> conditional; removing it makes this test throw.
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
    /// Populated and empty rows in one query: a per-document correlation failure would make the empty row read
    /// the populated row's value.
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
    /// Widened shape with <c>OrderBy</c> and a constant predicate; nests like the unwrapped shape, so the chain
    /// walk handles it once the two <c>Convert</c>s are peeled.
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

    // ── Sibling leaves: whole-root entity beside a reducer ───────────────────────────────────────────────────────
    //
    // `new { a, M = a.Nav.FirstOrDefault().Member }` is admitted (the reducer sets neither hasArrayLeaf nor
    // hasOwnedNavEntityLeaf). The leaves read from different places ($$ROOT vs `_lookup_<Nav>.<Member>`), so
    // one shadowing the other shows up as a wrong Name/Rank.

    /// <summary>
    /// Reducer leaf beside a whole-entity <c>$$ROOT</c> leaf, over the widened (non-nullable member) shape.
    /// No driver-LINQ fallback exists, so a decline would be a hard failure.
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
    /// Same pairing over the non-widened shape (<c>string</c> member, no <c>Convert</c> peel), which reaches the
    /// recognizer by a different path.
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
