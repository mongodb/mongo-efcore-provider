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
/// Native Union/Concat of bare scalar operands whose projection aliases differ: a column reads by its document path
/// (<c>_id</c>, <c>N</c>) while a computed value, a constant or a grouped aggregate reads by <c>_v</c>. The second
/// operand is re-aliased to the first's alias, since every combined row is read through the first operand's shaper.
/// Anonymous/DTO operands stay strict (<c>ProjectionShapesMatch</c>).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSetOperationProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Entity
    {
        public int Id { get; set; }
        public int N { get; set; }
    }

    // (Id, N): (1, 10), (2, 20), (3, 1), (4, 10). N + 1 = {11, 21, 2, 11}; N * 2 = {20, 40, 2, 20}; counts by N = {2, 1, 1}.
    private IMongoCollection<Entity> Seed(string name)
        => Seed(name, (1, 10), (2, 20), (3, 1), (4, 10));

    private IMongoCollection<Entity> Seed(string name, params (int Id, int N)[] rows)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany(rows.Select(r => new BsonDocument { { "_id", r.Id }, { "N", r.N } }));
        return database.MongoDatabase.GetCollection<Entity>(collectionName);
    }

    // (Id, N): (1, 8), (2, 20). A constant 8 on the first operand equals row 1's N, so Union must hold 8 once while
    // Concat keeps every copy.
    private IMongoCollection<Entity> SeedWithAnEight(string name)
        => Seed(name, (1, 8), (2, 20));

    private static List<T> RunSorted<T>(
        IMongoCollection<Entity> collection, Func<SingleEntityDbContext<Entity>, IQueryable<T>> query, IComparer<T>? comparer = null)
        => NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return query(db).ToList().OrderBy(v => v, comparer ?? Comparer<T>.Default).ToList();
        });

    private static List<T> DeclinesSorted<T>(
        IMongoCollection<Entity> collection, Func<SingleEntityDbContext<Entity>, IQueryable<T>> query)
        => NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return query(db).ToList().OrderBy(v => v).ToList();
        });

    private static SingleEntityDbContext<Entity> CreateContext(
        IMongoCollection<Entity> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                if (logs != null)
                {
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                }

                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private List<int> Run(string name, Func<SingleEntityDbContext<Entity>, IQueryable<int>> query)
    {
        var collection = Seed(name);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return query(db).ToList().OrderBy(v => v).ToList();
        });
    }

    [Fact]
    public void Union_of_key_column_and_computed_scalar_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 11, 21],
            Run(nameof(Union_of_key_column_and_computed_scalar_goes_native),
                db => db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => x.N + 1))));

    [Fact]
    public void Union_of_computed_scalar_and_key_column_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 11, 21],
            Run(nameof(Union_of_computed_scalar_and_key_column_goes_native),
                db => db.Entities.Select(x => x.N + 1).Union(db.Entities.Select(x => x.Id))));

    [Fact]
    public void Concat_of_key_column_and_computed_scalar_keeps_duplicates()
        => Assert.Equal(
            [1, 2, 2, 3, 4, 20, 20, 40],
            Run(nameof(Concat_of_key_column_and_computed_scalar_keeps_duplicates),
                db => db.Entities.Select(x => x.Id).Concat(db.Entities.Select(x => x.N * 2))));

    [Fact]
    public void Union_of_non_key_column_and_computed_scalar_goes_native()
        => Assert.Equal(
            [1, 10, 20, 101, 102, 103, 104],
            Run(nameof(Union_of_non_key_column_and_computed_scalar_goes_native),
                db => db.Entities.Select(x => x.N).Union(db.Entities.Select(x => x.Id + 100))));

    [Fact]
    public void Union_of_two_different_columns_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 10, 20],
            Run(nameof(Union_of_two_different_columns_goes_native),
                db => db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => x.N))));

    [Fact]
    public void Union_of_column_and_constant_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 7],
            Run(nameof(Union_of_column_and_constant_goes_native),
                db => db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => 7))));

    [Fact]
    public void Concat_of_column_and_grouped_count_goes_native()
        => Assert.Equal(
            [1, 1, 1, 2, 2, 3, 4],
            Run(nameof(Concat_of_column_and_grouped_count_goes_native),
                db => db.Entities.Select(x => x.Id).Concat(db.Entities.GroupBy(x => x.N).Select(g => g.Count()))));

    [Fact]
    public void Union_of_grouped_count_and_column_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4],
            Run(nameof(Union_of_grouped_count_and_column_goes_native),
                db => db.Entities.GroupBy(x => x.N).Select(g => g.Count()).Union(db.Entities.Select(x => x.Id))));

    [Fact]
    public void Union_of_column_and_computed_scalar_re_aliases_the_second_operand()
    {
        var collection = Seed(nameof(Union_of_column_and_computed_scalar_re_aliases_the_second_operand));
        var logs = new List<string>();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => x.N + 1)).ToList();

        Assert.Equal([1, 2, 3, 4, 11, 21], result.OrderBy(v => v));
        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains("\"$unionWith\"", mql);
        Assert.Contains("{ \"$project\" : { \"_id\" : { \"$add\" : [\"$N\", 1] } } }", mql);
        Assert.DoesNotContain("\"_v\"", mql);
    }

    // Anonymous operands alias each member by its name, so they already match and need no re-aliasing (the bare-scalar
    // alignment is never reached for a construction shaper; see NativeSetOperationAliasAlignmentTests in the unit
    // tests). Pinned so a change to either side's alias derivation shows up here.
    [Fact]
    public void Union_of_two_member_anonymous_operands_goes_native()
    {
        var collection = Seed(nameof(Union_of_two_member_anonymous_operands_goes_native));
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Select(x => new { x.Id, x.N })
                .Union(db.Entities.Select(x => new { Id = x.N + 1, N = x.Id }))
                .ToList().Select(v => v.Id + ":" + v.N).OrderBy(s => s, StringComparer.Ordinal).ToList();
        });

        Assert.Equal(["11:1", "11:4", "1:10", "21:2", "2:20", "2:3", "3:1", "4:10"], results);
    }

    [Fact]
    public void Union_of_single_member_anonymous_operands_goes_native()
    {
        var collection = Seed(nameof(Union_of_single_member_anonymous_operands_goes_native));
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Select(x => new { x.Id })
                .Union(db.Entities.Select(x => new { Id = x.N + 1 }))
                .ToList().Select(v => v.Id).OrderBy(v => v).ToList();
        });

        Assert.Equal([1, 2, 3, 4, 11, 21], results);
    }

    // The second operand's post-Distinct filter reads its own flattened alias (N), so re-aliasing its projection would
    // make the filter match nothing; such an operand declines.
    [Fact]
    public void Union_with_filtered_distinct_operand_of_a_different_alias_declines()
    {
        var collection = Seed(nameof(Union_with_filtered_distinct_operand_of_a_different_alias_declines));
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Select(x => x.Id)
                .Concat(db.Entities.Select(x => x.N).Distinct().Where(v => v > 5))
                .ToList().OrderBy(v => v).ToList();
        });

        Assert.Equal([1, 2, 3, 4, 10, 20], results);
    }

    // A value-converted column's stored representation ("c5") differs from a computed value's, and every combined row is
    // read through the first operand's shaper, so the pair declines at translation instead of failing (or misreading)
    // at materialization. There is no parity leg: the driver-LINQ fallback reads the combined rows with a single
    // serializer too, and fails on "c5" the same way.
    public class Coded
    {
        public int Id { get; set; }
        public int Code { get; set; }
        public int N { get; set; }
    }

    private SingleEntityDbContext<Coded> CreateCodedContext(string name, MongoQueryMode mode)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertMany([
            new BsonDocument { { "_id", 1 }, { "Code", "c5" }, { "N", 10 } },
            new BsonDocument { { "_id", 2 }, { "Code", "c6" }, { "N", 20 } },
        ]);

        return SingleEntityDbContext.Create(
            database.MongoDatabase.GetCollection<Coded>(collectionName),
            modelBuilderAction: mb => mb.Entity<Coded>().Property(x => x.Code)
                .HasConversion(v => "c" + v, v => int.Parse(v.Substring(1))),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private void AssertCodedSetOperationDeclines(string name, Func<SingleEntityDbContext<Coded>, IQueryable<int>> query)
    {
        using var db = CreateCodedContext(name, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => query(db).ToList());
    }

    // Converted column on the second operand.
    [Fact]
    public void Union_of_value_converted_column_and_computed_scalar_declines()
        => AssertCodedSetOperationDeclines(nameof(Union_of_value_converted_column_and_computed_scalar_declines),
            db => db.Entities.Select(x => x.N + 1).Union(db.Entities.Select(x => x.Code)));

    // Converted column on the first operand.
    [Fact]
    public void Union_of_computed_scalar_after_a_value_converted_column_declines()
        => AssertCodedSetOperationDeclines(nameof(Union_of_computed_scalar_after_a_value_converted_column_declines),
            db => db.Entities.Select(x => x.Code).Union(db.Entities.Select(x => x.N + 1)));

    // A grouped operand's flattening projection reads the $group output, so the converted field is reached only through
    // an accumulator operand: here the filtered count's condition. (A group key, Min/Max/Sum or Distinct over the
    // converted field already declines before the set op, so it can't reach this gate.) Paired with the key column so
    // the aliases differ (`_v` vs `_id`); a `_v`/`_v` pair is admitted by ProjectionShapesMatch without re-aliasing.
    // The decline is deliberately conservative, not required for correctness: the converter is applied to the
    // comparison literal and the count is a server-computed int, so native would answer this correctly. It keeps the
    // accumulator conjunct of ReadsOnlyDefaultSerializedFields mutation-provable.
    [Fact]
    public void Union_with_a_grouped_accumulator_over_a_value_converted_column_declines()
    {
        AssertCodedSetOperationDeclines(nameof(Union_with_a_grouped_accumulator_over_a_value_converted_column_declines) + "1",
            db => db.Entities.Select(x => x.Id).Union(db.Entities.GroupBy(x => x.N).Select(g => g.Count(x => x.Code > 5))));
        AssertCodedSetOperationDeclines(nameof(Union_with_a_grouped_accumulator_over_a_value_converted_column_declines) + "2",
            db => db.Entities.GroupBy(x => x.N).Select(g => g.Count(x => x.Code > 5)).Union(db.Entities.Select(x => x.Id)));
    }

    // A constant or parameter on the FIRST operand. Its standalone shaper bakes the value in instead of reading it, and
    // that shaper reads every combined row, so the leaf is rebound to read the `$literal`-projected `_v` per document
    // (CanRebindConstantLeafToDocument). Without the rebind source2's rows would all read back as source1's value.

    [Fact]
    public void Union_of_constant_and_column_holds_a_matching_value_once()
        => Assert.Equal(
            [8, 20],
            RunSorted(SeedWithAnEight(nameof(Union_of_constant_and_column_holds_a_matching_value_once)),
                db => db.Entities.Select(x => 8).Union(db.Entities.Select(x => x.N))));

    [Fact]
    public void Concat_of_constant_and_column_keeps_every_copy_of_a_matching_value()
        => Assert.Equal(
            [8, 8, 8, 20],
            RunSorted(SeedWithAnEight(nameof(Concat_of_constant_and_column_keeps_every_copy_of_a_matching_value)),
                db => db.Entities.Select(x => 8).Concat(db.Entities.Select(x => x.N))));

    // One constant row (filtered first operand) and one matching column row: Concat holds two 8s, Union one.
    [Fact]
    public void Filtered_constant_operand_against_a_matching_column_dedups_only_under_Union()
    {
        var collection = SeedWithAnEight(nameof(Filtered_constant_operand_against_a_matching_column_dedups_only_under_Union));
        Assert.Equal(
            [8, 8, 20],
            RunSorted(collection, db => db.Entities.Where(x => x.Id == 1).Select(x => 8).Concat(db.Entities.Select(x => x.N))));
        Assert.Equal(
            [8, 20],
            RunSorted(collection, db => db.Entities.Where(x => x.Id == 1).Select(x => 8).Union(db.Entities.Select(x => x.N))));
    }

    [Fact]
    public void Union_of_constant_and_key_column_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 8],
            Run(nameof(Union_of_constant_and_key_column_goes_native),
                db => db.Entities.Select(x => 8).Union(db.Entities.Select(x => x.Id))));

    [Fact]
    public void Union_of_constant_and_computed_scalar_goes_native()
        => Assert.Equal(
            [2, 8, 11, 21],
            Run(nameof(Union_of_constant_and_computed_scalar_goes_native),
                db => db.Entities.Select(x => 8).Union(db.Entities.Select(x => x.N + 1))));

    [Fact]
    public void Union_of_constant_and_grouped_count_goes_native()
        => Assert.Equal(
            [1, 2, 8],
            Run(nameof(Union_of_constant_and_grouped_count_goes_native),
                db => db.Entities.Select(x => 8).Union(db.Entities.GroupBy(x => x.N).Select(g => g.Count()))));

    [Fact]
    public void Union_of_two_constants_goes_native()
        => Assert.Equal(
            [8, 9],
            Run(nameof(Union_of_two_constants_goes_native),
                db => db.Entities.Select(x => 8).Union(db.Entities.Select(x => 9))));

    [Fact]
    public void Concat_of_string_constant_and_null_string_constant_goes_native()
    {
        var collection = SeedWithAnEight(nameof(Concat_of_string_constant_and_null_string_constant_goes_native));
        Assert.Equal(
            [null, null, "A", "A"],
            RunSorted(collection, db => db.Entities.Select(x => "A").Concat(db.Entities.Select(x => (string?)null)),
                StringComparer.Ordinal));
        Assert.Equal(
            [null, "A"],
            RunSorted(collection, db => db.Entities.Select(x => "A").Union(db.Entities.Select(x => (string?)null)),
                StringComparer.Ordinal));
    }

    // A null string constant on the first operand reads back as null on its rows and as the value on source2's rows.
    [Fact]
    public void Concat_of_null_string_constant_and_string_constant_goes_native()
        => Assert.Equal(
            [null, null, "A", "A"],
            RunSorted(SeedWithAnEight(nameof(Concat_of_null_string_constant_and_string_constant_goes_native)),
                db => db.Entities.Select(x => (string?)null).Concat(db.Entities.Select(x => "A")), StringComparer.Ordinal));

    // A string constant that looks like a field path must stay a literal on the first operand too.
    [Fact]
    public void Concat_of_dollar_string_constant_and_string_constant_goes_native()
        => Assert.Equal(
            ["$N", "$N", "B", "B"],
            RunSorted(SeedWithAnEight(nameof(Concat_of_dollar_string_constant_and_string_constant_goes_native)),
                db => db.Entities.Select(x => "$N").Concat(db.Entities.Select(x => "B")), StringComparer.Ordinal));

    [Fact]
    public void Union_of_long_double_and_bool_constants_and_columns_goes_native()
    {
        var collection = SeedWithAnEight(nameof(Union_of_long_double_and_bool_constants_and_columns_goes_native));
        Assert.Equal([8L, 20L], RunSorted(collection, db => db.Entities.Select(x => 8L).Union(db.Entities.Select(x => (long)x.N))));
        Assert.Equal([0.5, 4.0, 10.0], RunSorted(collection, db => db.Entities.Select(x => 0.5).Union(db.Entities.Select(x => x.N / 2.0))));
        Assert.Equal([false, true], RunSorted(collection, db => db.Entities.Select(x => true).Union(db.Entities.Select(x => x.N > 10))));
    }

    // A captured variable is a parameter leaf: rendered per execution, so a second execution with a different value
    // (same compiled query) reads back the new value.
    [Fact]
    public void Union_of_parameter_and_column_goes_native_and_reads_the_current_value()
    {
        var collection = SeedWithAnEight(nameof(Union_of_parameter_and_column_goes_native_and_reads_the_current_value));
        var p = 8;
        Assert.Equal([8, 20], RunSorted(collection, db => db.Entities.Select(x => p).Union(db.Entities.Select(x => x.N))));
        p = 30;
        Assert.Equal([8, 20, 30], RunSorted(collection, db => db.Entities.Select(x => p).Union(db.Entities.Select(x => x.N))));
        Assert.Equal([8, 20, 30, 30], RunSorted(collection, db => db.Entities.Select(x => p).Concat(db.Entities.Select(x => x.N))));
    }

    [Fact]
    public void Union_of_constant_and_column_reads_the_first_operand_from_the_document()
    {
        var collection = SeedWithAnEight(nameof(Union_of_constant_and_column_reads_the_first_operand_from_the_document));
        var logs = new List<string>();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Select(x => 8).Union(db.Entities.Select(x => x.N)).ToList();

        Assert.Equal([8, 20], result.OrderBy(v => v));
        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains("{ \"$project\" : { \"_v\" : { \"$literal\" : 8 }, \"_id\" : 0 } }", mql);
        Assert.Contains("\"$unionWith\"", mql);
        Assert.Contains("{ \"$project\" : { \"_v\" : \"$N\", \"_id\" : 0 } }", mql);
    }

    // Intersect/Except have no driver-LINQ oracle (they hard-fail on the fallback), so these are NativeOnly with hand
    // oracles. The difference stage groups on $$ROOT after each operand's $project, so the operands must share an
    // alias: without the re-alias `{_id: 2}` never equals `{_v: 2}` (Intersect would be empty, Except all of source1).
    // Ids {1, 2, 3, 4}; N + 1 = {11, 21, 2, 11}.
    private static List<T> RunNativeOnlySorted<T>(IMongoCollection<Entity> collection, Func<SingleEntityDbContext<Entity>, IQueryable<T>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        return query(db).ToList().OrderBy(v => v).ToList();
    }

    [Fact]
    public void Intersect_and_Except_of_key_column_and_computed_scalar_re_align_the_second_operand()
    {
        var collection = Seed(nameof(Intersect_and_Except_of_key_column_and_computed_scalar_re_align_the_second_operand));
        Assert.Equal([2], RunNativeOnlySorted(collection, db => db.Entities.Select(x => x.Id).Intersect(db.Entities.Select(x => x.N + 1))));
        Assert.Equal([1, 3, 4], RunNativeOnlySorted(collection, db => db.Entities.Select(x => x.Id).Except(db.Entities.Select(x => x.N + 1))));
    }

    // A constant first operand against a column: (Id, N) = (1, 8), (2, 20). The constant is `_v`, N is re-aliased to
    // it. Only source1's rows come out of a set difference, so a baked-in constant would still read 8 there; the
    // rebind isn't what these discriminate, the re-alias is (without it 8 never matches N's 8).
    [Fact]
    public void Intersect_and_Except_of_constant_and_column_compare_the_constant_with_each_row()
    {
        var collection = SeedWithAnEight(nameof(Intersect_and_Except_of_constant_and_column_compare_the_constant_with_each_row));
        Assert.Equal([8], RunNativeOnlySorted(collection, db => db.Entities.Select(x => 8).Intersect(db.Entities.Select(x => x.N))));
        Assert.Equal([], RunNativeOnlySorted(collection, db => db.Entities.Select(x => 8).Except(db.Entities.Select(x => x.N))));
        Assert.Equal([], RunNativeOnlySorted(collection, db => db.Entities.Select(x => 8).Intersect(db.Entities.Select(x => x.Id))));
        Assert.Equal([8], RunNativeOnlySorted(collection, db => db.Entities.Select(x => 8).Except(db.Entities.Select(x => x.Id))));
    }

    // A projected Distinct as the second operand is re-aliased too (its flattened `N` becomes `_id`). N = {10, 20, 1, 10}.
    [Fact]
    public void Union_of_key_column_and_distinct_column_goes_native()
        => Assert.Equal(
            [1, 2, 3, 4, 10, 20],
            Run(nameof(Union_of_key_column_and_distinct_column_goes_native),
                db => db.Entities.Select(x => x.Id).Union(db.Entities.Select(x => x.N).Distinct())));

    // A projected Distinct between the constant Select and the set operation moves the constant into its key parts
    // and projects an element ref, but keeps the Select's shaper with the constant baked in. Unless the gate sees
    // through the Distinct (HasShaperUnsafeConstantLeaf), every combined row, source2's too, reads back as the
    // constant ([8, 8] for the first test). Nothing rebinds it, so these decline.
    [Fact]
    public void Union_of_distinct_constant_and_column_declines()
        => Assert.Equal(
            [8, 20],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_distinct_constant_and_column_declines)),
                db => db.Entities.Select(x => 8).Distinct().Union(db.Entities.Select(x => x.N))));

    [Fact]
    public void Concat_of_distinct_constant_and_column_declines()
        => Assert.Equal(
            [8, 8, 20],
            DeclinesSorted(SeedWithAnEight(nameof(Concat_of_distinct_constant_and_column_declines)),
                db => db.Entities.Select(x => 8).Distinct().Concat(db.Entities.Select(x => x.N))));

    [Fact]
    public void Union_of_distinct_constant_and_constant_declines()
        => Assert.Equal(
            [8, 9],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_distinct_constant_and_constant_declines)),
                db => db.Entities.Select(x => 8).Distinct().Union(db.Entities.Select(x => 9))));

    [Fact]
    public void Union_of_distinct_parameter_and_column_declines()
    {
        var p = 8;
        Assert.Equal(
            [8, 20],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_distinct_parameter_and_column_declines)),
                db => db.Entities.Select(x => p).Distinct().Union(db.Entities.Select(x => x.N))));
    }

    [Fact]
    public void Union_of_distinct_anonymous_operand_with_a_constant_member_declines()
    {
        var collection = SeedWithAnEight(nameof(Union_of_distinct_anonymous_operand_with_a_constant_member_declines));
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Select(x => new { A = 8, x.N }).Distinct()
                .Union(db.Entities.Select(x => new { A = x.Id, N = 7 }))
                .ToList().Select(v => v.A + ":" + v.N).OrderBy(s => s, StringComparer.Ordinal).ToList();
        });

        Assert.Equal(["1:7", "2:7", "8:20", "8:8"], results);
    }

    // A grouped constant result on source1: the server-side pipeline is right, but the rebind binds a plain projection
    // member that the grouped shaper doesn't read (it threw "Document element '_v0' is missing but required"), so
    // CanRebindConstantLeafToDocument requires an ungrouped select. As source2 it is read through source1's shaper and
    // goes native.
    [Fact]
    public void Union_of_grouped_constant_result_and_column_declines()
        => Assert.Equal(
            [1, 8, 10, 20],
            DeclinesSorted(Seed(nameof(Union_of_grouped_constant_result_and_column_declines)),
                db => db.Entities.GroupBy(x => x.N).Select(g => 8).Union(db.Entities.Select(x => x.N))));

    [Fact]
    public void Union_of_column_and_grouped_constant_result_goes_native()
        => Assert.Equal(
            [1, 8, 10, 20],
            Run(nameof(Union_of_column_and_grouped_constant_result_goes_native),
                db => db.Entities.Select(x => x.N).Union(db.Entities.GroupBy(x => x.N).Select(g => 8))));

    // A real GroupBy's constant key is read back from `_id` by the grouped shaper, so the Distinct key-part check is
    // scoped to IsDistinct and this stays native.
    [Fact]
    public void Union_of_grouped_constant_key_and_anonymous_column_operand_goes_native()
    {
        var collection = Seed(nameof(Union_of_grouped_constant_key_and_anonymous_column_operand_goes_native));
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.GroupBy(x => 8).Select(g => new { A = g.Key, C = g.Count() })
                .Union(db.Entities.Select(x => new { A = x.Id, C = x.N }))
                .ToList().Select(v => v.A + ":" + v.C).OrderBy(s => s, StringComparer.Ordinal).ToList();
        });

        Assert.Equal(["1:10", "2:20", "3:1", "4:10", "8:4"], results);
    }

    // A constant member of a grouped construction is baked into its shaper like a plain one.
    [Fact]
    public void Union_of_grouped_anonymous_operand_with_a_constant_member_declines()
    {
        var collection = Seed(nameof(Union_of_grouped_anonymous_operand_with_a_constant_member_declines));
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.GroupBy(x => x.N).Select(g => new { A = 8, C = g.Count() })
                .Union(db.Entities.Select(x => new { A = x.Id, C = x.N }))
                .ToList().Select(v => v.A + ":" + v.C).OrderBy(s => s, StringComparer.Ordinal).ToList();
        });

        Assert.Equal(["1:10", "2:20", "3:1", "4:10", "8:1", "8:2"], results);
    }

    // Round-trip-unsafe constant types stay out of scope: a constant DateTime reads back as UTC-kind, a Guid depends on
    // the representation, an enum on its storage type. They decline rather than rebind.
    public enum Color
    {
        Red,
        Green,
    }

    // Captured, so each is a bare parameter leaf (an inline `new DateTime(2020, 1, 1)` binds as three ctor arguments).
    [Fact]
    public void Union_of_DateTime_parameters_declines()
    {
        var d1 = new DateTime(2020, 1, 1);
        var d2 = new DateTime(2021, 1, 1);
        Assert.Equal(
            [d1, d2],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_DateTime_parameters_declines)),
                db => db.Entities.Select(x => d1).Union(db.Entities.Select(x => d2))));
    }

    // Pins the decline, not the allow-list: a Guid leaf declines before CanRebindConstantLeafToDocument decides (it
    // still declines with both the allow-list and the render probe removed).
    [Fact]
    public void Union_of_Guid_constants_declines()
    {
        var g1 = new Guid("00000000-0000-0000-0000-000000000001");
        var g2 = new Guid("00000000-0000-0000-0000-000000000002");
        Assert.Equal(
            [g1, g2],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_Guid_constants_declines)),
                db => db.Entities.Select(x => g1).Union(db.Entities.Select(x => g2))));
    }

    [Fact]
    public void Union_of_enum_constants_declines()
        => Assert.Equal(
            [Color.Red, Color.Green],
            DeclinesSorted(SeedWithAnEight(nameof(Union_of_enum_constants_declines)),
                db => db.Entities.Select(x => Color.Red).Union(db.Entities.Select(x => Color.Green))));

    // A constant inside a construction is read by member name from a baked-in shaper; only a bare leaf is rebound.
    [Fact]
    public void Union_of_anonymous_operands_with_a_constant_member_declines()
    {
        var collection = SeedWithAnEight(nameof(Union_of_anonymous_operands_with_a_constant_member_declines));
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Select(x => new { A = 8, x.N })
                .Union(db.Entities.Select(x => new { A = x.Id, N = 7 }))
                .ToList().Select(v => v.A + ":" + v.N).OrderBy(s => s, StringComparer.Ordinal).ToList();
        });

        Assert.Equal(["1:7", "2:7", "8:20", "8:8"], results);
    }

    // One projection, but a construction shaper. NativeOnly only: the driver-LINQ fallback fails on this shape too
    // (FormatException "Expected element name to be '_v', not 'A'"), so there is no parity oracle.
    [Fact]
    public void Union_of_single_member_anonymous_operands_with_a_constant_member_declines()
    {
        var collection = SeedWithAnEight(nameof(Union_of_single_member_anonymous_operands_with_a_constant_member_declines));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => new { A = 8 }).Union(db.Entities.Select(x => new { A = x.N })).ToList());
    }

    // A constructor call binds its arguments as separate projections (`_ctorArg0` = the constant 'a', `_ctorArg1` = N)
    // and a construction shaper of type string. The constant first argument must not be mistaken for a bare leaf:
    // rebinding would read 'a' as the whole string. NativeOnly only: the driver-LINQ fallback can't translate
    // `new string(char, int)`.
    [Fact]
    public void Union_of_string_constructor_operands_with_a_constant_argument_declines()
    {
        var collection = SeedWithAnEight(nameof(Union_of_string_constructor_operands_with_a_constant_argument_declines));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => new string('a', x.N)).Union(db.Entities.Select(x => new string('b', x.Id))).ToList());
    }

    // A per-row client-evaluated leaf is not a server-rendered constant/parameter; it never reaches the rebind.
    // NativeOnly only: driver-LINQ can't translate the client method either ("Expression not supported").
    private static int ClientTwice(int n) => n * 2;

    [Fact]
    public void Union_with_a_client_evaluated_first_operand_declines()
    {
        var collection = SeedWithAnEight(nameof(Union_with_a_client_evaluated_first_operand_declines));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => ClientTwice(x.N)).Union(db.Entities.Select(x => x.N)).ToList());
    }
}
