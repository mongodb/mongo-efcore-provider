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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The streaming materializer (<c>MongoStreamingEntityMaterializerRewriter</c>) must match driver-LINQ for
/// schema-drifted documents: a missing required non-nullable scalar throws <see cref="InvalidOperationException"/>
/// (as <c>BsonBinding</c> does); an explicit BSON <c>null</c> on a non-nullable value property materializes
/// <c>default(T)</c>. Queries use <c>.ToList()</c> because scalar-cardinality operators such as <c>.Single()</c>
/// are never streaming-eligible and would silently take the DOM path.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMaterializerNullabilityTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Scored
    {
        public ObjectId Id { get; set; }
        public int Score { get; set; }       // required non-nullable scalar
        public int? Bonus { get; set; }       // nullable scalar
    }

    private class WithOwned
    {
        public ObjectId Id { get; set; }
        public Stats Stats { get; set; } = null!;
    }

    private class Stats
    {
        public int Score { get; set; }        // required non-nullable scalar on owned sub-document
    }

    private class Named
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";      // required non-nullable REFERENCE scalar
        public List<int> Tags { get; set; } = [];   // required non-nullable REFERENCE scalar (collection)
    }

    private class StringKeyed
    {
        public string Id { get; set; } = "";       // string primary key: never null
        public string? Label { get; set; }          // nullable, non-key string
    }

    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Missing_required_scalar_throws_under_native_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() } });

        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.ToList());
        }

        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
        }
    }

    [Fact]
    public void Explicit_null_on_non_nullable_scalar_materializes_default_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", BsonNull.Value } });

        int driverScore;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverScore = driver.Entities.ToList().Single().Score;
        }

        int nativeScore;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeScore = native.Entities.ToList().Single().Score;
        }

        Assert.Equal(default, driverScore);
        Assert.Equal(driverScore, nativeScore);
    }

    [Fact]
    public void Explicit_null_on_non_nullable_scalar_goes_native()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", BsonNull.Value } });

        using var native = CreateContext(collection, MongoQueryMode.NativeOnly);
        var score = native.Entities.ToList().Single().Score;
        Assert.Equal(default, score);
    }

    [Fact]
    public void Explicit_null_on_nullable_scalar_materializes_null_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", 7 }, { "Bonus", BsonNull.Value } });

        int? driverBonus;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverBonus = driver.Entities.ToList().Single().Bonus;
        }

        int? nativeBonus;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeBonus = native.Entities.ToList().Single().Bonus;
        }

        Assert.Null(driverBonus);
        Assert.Equal(driverBonus, nativeBonus);
    }

    // Explicit BSON null on a non-nullable reference-typed scalar must throw, not leave a required member null.

    [Theory]
    [InlineData("Name")]
    [InlineData("Tags")]
    public void Explicit_null_on_non_nullable_reference_scalar_throws_under_native_matching_driver(string element)
    {
        var collection = database.CreateCollection<Named>(values: [element]);
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Every other required property is valid, so the materializer reaches `element` rather than throwing
        // "missing" on a sibling first.
        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Name", "a name" },
            { "Tags", new BsonArray([1, 2, 3]) }
        };
        doc[element] = BsonNull.Value;
        raw.InsertOne(doc);

        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.ToList());
        }

        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var nativeOnlyEx = Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
            Assert.Equal(driverEx.Message, nativeOnlyEx.Message);
        }
    }

    [Fact]
    public void Complete_document_materializes_correctly_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        collection.InsertOne(new Scored { Id = ObjectId.GenerateNewId(), Score = 42, Bonus = 5 });

        Scored driverEntity;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEntity = driver.Entities.ToList().Single();
        }

        Scored nativeEntity;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEntity = native.Entities.ToList().Single();
        }

        Assert.Equal(42, driverEntity.Score);
        Assert.Equal(driverEntity.Score, nativeEntity.Score);
        Assert.Equal(driverEntity.Bonus, nativeEntity.Bonus);
    }

    // Owned single-reference whole-entity queries go native, and the streaming materializer enforces RequiredPresence
    // for owned children too (BuildFillLoop(child)). Parity with driver-LINQ holds whichever path each side takes.

    [Fact]
    public void Missing_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Stats", new BsonDocument() }
        });

        Action<ModelBuilder> model = mb => mb.Entity<WithOwned>().OwnsOne(e => e.Stats);

        Exception? driverEx = Record.Exception(() =>
        {
            using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, model);
            _ = driver.Entities.ToList();
        });

        Exception? nativeEx = Record.Exception(() =>
        {
            using var native = CreateContext(collection, MongoQueryMode.Native, model);
            _ = native.Entities.ToList();
        });

        Assert.Equal(driverEx?.GetType(), nativeEx?.GetType());

        // Under NativeOnly this must throw InvalidOperationException from the streaming materializer, not NativeTranslationNotSupportedException.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
        }
    }

    [Fact]
    public void Explicit_null_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Stats", new BsonDocument { { "Score", BsonNull.Value } } }
        });

        Action<ModelBuilder> model = mb => mb.Entity<WithOwned>().OwnsOne(e => e.Stats);

        int? driverScore = null;
        Exception? driverEx = Record.Exception(() =>
        {
            using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, model);
            driverScore = driver.Entities.ToList().Single().Stats.Score;
        });

        int? nativeScore = null;
        Exception? nativeEx = Record.Exception(() =>
        {
            using var native = CreateContext(collection, MongoQueryMode.Native, model);
            nativeScore = native.Entities.ToList().Single().Stats.Score;
        });

        Assert.Equal(driverEx?.GetType(), nativeEx?.GetType());
        Assert.Equal(driverScore, nativeScore);

        // Under NativeOnly a present-but-null required owned scalar materializes to default(T), matching the driver-LINQ oracle.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            var nativeOnlyScore = nativeOnly.Entities.ToList().Single().Stats.Score;
            Assert.Equal(driverScore, nativeOnlyScore);
        }
    }

    // A primary-key string is never null, so its Length is not null behind the non-nullable int it is read as.
    [Fact]
    public void String_primary_key_length_expression_projection_goes_native()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            return context.Entities.OrderBy(e => e.Id).Select(e => new { e.Id, L = e.Id.Length + 5 }).ToList();
        });

        Assert.Equal([7, 10], result.Select(r => r.L));
    }

    // Control for the PK exemption: a nullable non-key string may be null, so its Length + 5 is read as int? and a null
    // throws EF's "Nullable object must have a value." (never 0 + 5). Over non-null data it reads the exact values.
    [Fact]
    public void Nullable_non_key_string_length_expression_projection_goes_native_and_throws_on_null()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = "yy" }]);
        var nullCollection = database.CreateCollection<StringKeyed>(nameof(Nullable_non_key_string_length_expression_projection_goes_native_and_throws_on_null) + "_null");
        nullCollection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            return context.Entities.OrderBy(e => e.Id).Select(e => new { e.Id, L = e.Label!.Length + 5 }).ToList();
        });
        Assert.Equal([6, 7], result.Select(r => r.L));

        AssertThrowsNullableObjectMustHaveAValue(mode =>
        {
            using var context = CreateContext(nullCollection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            _ = context.Entities.OrderBy(e => e.Id).Select(e => new { e.Id, L = e.Label!.Length + 5 }).ToList();
        });
    }

    // EF's own message for reading a null through Nullable<T>.Value, which is what EF relational throws for a null
    // behind a non-nullable projected type (upstream Non_nullable_property_through_optional_navigation).
    private const string NullableObjectMustHaveAValue = "Nullable object must have a value.";

    // Native (default) and NativeOnly both run the native pipeline and throw EF's exception. DriverLinq is not asserted:
    // it sends an unguarded $strLenCP/$indexOfCP, which fails server-side on a null string.
    private static void AssertThrowsNullableObjectMustHaveAValue(Action<MongoQueryMode> run)
    {
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            var exception = Assert.Throws<InvalidOperationException>(() => run(mode));
            Assert.Equal(NullableObjectMustHaveAValue, exception.Message);
        }
    }

    // Each shape reads a null-propagated scalar (Length/IndexOf over a nullable string) behind a non-nullable type. The
    // result is widened to long client-side, after ToList, so one theory covers int and long leaves.
    private static List<long> RunNullableStringScalarShape(
        IMongoCollection<StringKeyed> collection, MongoQueryMode mode, string shape)
    {
        using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
        var ordered = context.Entities.OrderBy(e => e.Id);
        return shape switch
        {
            "Length" => ordered.Select(e => new { e.Label!.Length }).ToList().Select(r => (long)r.Length).ToList(),
            "LongLength" => ordered.Select(e => new { L = (long)e.Label!.Length }).ToList().Select(r => r.L).ToList(),
            "IndexOf" => ordered.Select(e => new { I = e.Label!.IndexOf("") }).ToList().Select(r => (long)r.I).ToList(),
            "IndexOfChar" => ordered.Select(e => new { I = e.Label!.IndexOf("y") }).ToList().Select(r => (long)r.I).ToList(),
            "BareLength" => ordered.Select(e => e.Label!.Length).ToList().Select(l => (long)l).ToList(),
            "BareLongLength" => ordered.Select(e => (long)e.Label!.Length).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }

    [Theory]
    [InlineData("Length", new long[] { 1, 2 })]
    [InlineData("LongLength", new long[] { 1, 2 })]
    [InlineData("IndexOf", new long[] { 0, 0 })]
    [InlineData("IndexOfChar", new long[] { -1, 0 })]
    [InlineData("BareLength", new long[] { 1, 2 })]
    [InlineData("BareLongLength", new long[] { 1, 2 })]
    public void Nullable_string_scalar_behind_non_nullable_type_reads_exact_values_natively(string shape, long[] expected)
    {
        var collection = database.CreateCollection<StringKeyed>(values: [shape]);
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = "yy" }]);

        var result = NativeModeAssert.NativeAndParity(mode => RunNullableStringScalarShape(collection, mode, shape));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Length")]
    [InlineData("LongLength")]
    [InlineData("IndexOf")]
    [InlineData("IndexOfChar")]
    [InlineData("BareLength")]
    [InlineData("BareLongLength")]
    public void Nullable_string_scalar_behind_non_nullable_type_throws_on_null_row(string shape)
    {
        var collection = database.CreateCollection<StringKeyed>(values: [shape]);
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        AssertThrowsNullableObjectMustHaveAValue(mode => RunNullableStringScalarShape(collection, mode, shape));
    }

    // $max/$min skip a null operand and Math.Sign's $switch orders a null below 0, so the server answers a non-null
    // value where EF throws (the null row here would read Max(null, 5) as 5); no read of the value can see the null, so
    // these decline instead of throwing on null. Only the NativeOnly decline is asserted: driver-LINQ can't translate
    // the anonymous Math.Max/Min member ("unable to determine which serializer"), so there is no fallback oracle.
    [Theory]
    [InlineData("Max")]
    [InlineData("Min")]
    [InlineData("Sign")]
    public void Null_absorbing_math_over_nullable_string_length_declines(string function)
    {
        var collection = database.CreateCollection<StringKeyed>(values: [function]);
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        using var context = CreateContext(collection, MongoQueryMode.NativeOnly, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
        var ordered = context.Entities.OrderBy(e => e.Id);
        Assert.Throws<NativeTranslationNotSupportedException>(() => function switch
        {
            "Max" => ordered.Select(e => new { V = Math.Max(e.Label!.Length, e.Id.Length) }).ToList().Select(r => r.V).ToList(),
            "Min" => ordered.Select(e => new { V = Math.Min(e.Label!.Length, e.Id.Length) }).ToList().Select(r => r.V).ToList(),
            _ => ordered.Select(e => new { V = Math.Sign(e.Label!.Length) }).ToList().Select(r => r.V).ToList()
        });
    }

    // A nullable cast reads the null as null and stays native (no throw): the flag is only for non-nullable reads.
    [Fact]
    public void Nullable_cast_of_nullable_string_length_reads_null()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        using var context = CreateContext(collection, MongoQueryMode.NativeOnly, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
        var result = context.Entities.OrderBy(e => e.Id).Select(e => new { L = (int?)e.Label!.Length }).ToList();

        Assert.Equal([1, null], result.Select(r => r.L));
    }

    // Distinct over the flagged projection re-reads it from the $group key; the flag must survive that rewrite, or a
    // null row reads as 0.
    [Fact]
    public void Distinct_over_nullable_string_length_projection_throws_on_null_row()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        AssertThrowsNullableObjectMustHaveAValue(mode =>
        {
            using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            _ = context.Entities.Select(e => new { e.Label!.Length }).Distinct().ToList();
        });
    }

    // A projected set-op operand's flag lives on the operand's own Select; source1's shaper reads every combined row,
    // so it must honour source2's flag too, or source2's null row reads as 0.
    [Fact]
    public void Union_operand_nullable_string_length_throws_on_null_row()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = null }]);

        AssertThrowsNullableObjectMustHaveAValue(mode =>
        {
            using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            _ = context.Entities.Select(e => new { L = e.Id.Length })
                .Union(context.Entities.Select(e => new { L = e.Label!.Length }))
                .ToList();
        });
    }

    // Min/Max/Average over a flagged bare projection reduce an all-null input to null, which the scalar-aggregate
    // reader would read as 0 where EF throws; they decline. Driver-LINQ has no oracle (its unguarded $strLenCP fails
    // server-side on null), so only the NativeOnly decline is asserted.
    [Theory]
    [InlineData("Max")]
    [InlineData("Min")]
    [InlineData("Average")]
    public void Min_max_average_over_nullable_string_length_projection_decline(string op)
    {
        var collection = database.CreateCollection<StringKeyed>(values: [op]);
        collection.InsertMany([new StringKeyed { Id = "ab", Label = null }, new StringKeyed { Id = "abcde", Label = null }]);

        using var context = CreateContext(collection, MongoQueryMode.NativeOnly, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
        var lengths = context.Entities.Select(e => e.Label!.Length);
        Assert.Throws<NativeTranslationNotSupportedException>(() => op switch
        {
            "Max" => (double)lengths.Max(),
            "Min" => lengths.Min(),
            _ => lengths.Average()
        });
    }

    // Sum skips a null as EF's SUM does (COALESCE to 0), so it stays native and agrees with the oracle.
    [Fact]
    public void Sum_over_nullable_string_length_projection_goes_native()
    {
        var collection = database.CreateCollection<StringKeyed>();
        collection.InsertMany([new StringKeyed { Id = "ab", Label = "x" }, new StringKeyed { Id = "abcde", Label = "yy" }]);

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode, mb => mb.Entity<StringKeyed>().HasKey(e => e.Id));
            return new List<int> { context.Entities.Select(e => e.Label!.Length).Sum() };
        });

        Assert.Equal([3], result);
    }

    private class JoinOwner
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private class JoinPet
    {
        public string Id { get; set; } = "";       // string primary key on the INNER side of a left join
        public string OwnerId { get; set; } = "";
    }

    private class JoinDbContext(
        TemporaryDatabaseFixture database, string owners, string pets, MongoQueryMode mode)
        : DbContext(new DbContextOptionsBuilder<JoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UncachedModelKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<JoinOwner> Owners { get; set; } = null!;
        public DbSet<JoinPet> Pets { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<JoinOwner>().ToCollection(owners);
            modelBuilder.Entity<JoinPet>().ToCollection(pets);
        }

        private sealed class UncachedModelKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => System.Threading.Interlocked.Increment(ref _count);
        }
    }

    // Control for the PK exemption: it is for the root document's own key only. The inner side of a left-outer join
    // (GroupJoin + DefaultIfEmpty) is missing for an unmatched row even though the property is a key, so its Length
    // behind a non-nullable int is read as int? and an unmatched row throws EF's "Nullable object must have a value."
    // (EF relational's answer too), never 0 + 5. With every owner matched it reads the exact values, with parity.
    [Fact]
    public void Left_join_inner_string_primary_key_length_expression_throws_on_unmatched_row()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owners = "jo" + suffix;
        var pets = "jp" + suffix;
        database.MongoDatabase.GetCollection<JoinOwner>(owners).InsertMany(
            [new JoinOwner { Id = "o1", Name = "A" }, new JoinOwner { Id = "o2", Name = "B" }]);
        database.MongoDatabase.GetCollection<JoinPet>(pets).InsertMany(
            [new JoinPet { Id = "pet1", OwnerId = "o1" }, new JoinPet { Id = "pet22", OwnerId = "o2" }]);

        List<(string Name, int L)> Run(MongoQueryMode mode)
        {
            using var context = new JoinDbContext(database, owners, pets, mode);
            return context.Owners
                .GroupJoin(context.Pets, o => o.Id, p => p.OwnerId, (o, ps) => new { o, ps })
                .SelectMany(x => x.ps.DefaultIfEmpty(), (x, p) => new { x.o.Name, L = p!.Id.Length + 5 })
                .OrderBy(r => r.Name)
                .ToList()
                .Select(r => (r.Name, r.L))
                .ToList();
        }

        var result = NativeModeAssert.NativeAndParity(Run);
        Assert.Equal([9, 10], result.Select(r => r.L));

        // An owner with no pet: its inner side is missing.
        database.MongoDatabase.GetCollection<JoinOwner>(owners).InsertOne(new JoinOwner { Id = "o3", Name = "C" });
        AssertThrowsNullableObjectMustHaveAValue(mode => Run(mode));
    }
}
