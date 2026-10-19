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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Row-independent projection leaves (closed <c>new</c>, <c>new { }</c>, captured values the <c>$literal</c> path can't
/// render, a <c>MemberInit</c> whose constructor arguments are parameters) are evaluated client-side by the shaper
/// and never projected (<c>NativeProjectionBinder.IsRowIndependentLeaf</c>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is projected for such a leaf, so no server operator may read it. The shaper evaluates it with this
/// execution's value for every row, so a set operation over such a select would show source1's value on source2's
/// rows, and dedup would compare only the server-projected members. Those decline
/// (<c>MongoSelectDefinition.HasClientEvaluatedProjectionLeaf</c>). A <c>Distinct</c> stays native: the value is the
/// same for every row of one execution, so deduplicating by the server-projected members alone is exact, and an
/// all-client projection deduplicates to one row.
/// </para>
/// <para>
/// When every leaf is client-evaluated, a constant sentinel is projected so the route stays a projection (one row
/// per document) rather than collapsing to a whole-entity fetch. A constant, not <c>_id</c>, so a <c>Distinct</c>
/// over it yields one row rather than one per document.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeClientEvaluatedLeafTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int N { get; set; }
    }

    public class Dto
    {
        public Dto()
        {
        }

        public Dto(string value)
        {
            Value = value;
        }

        public string? Value { get; }
        public string? Label { get; set; }
        public Dto? Nested { get; set; }
    }

    public class Item
    {
        public int X { get; set; }
    }

    private static readonly (string Label, int N)[] Rows = [("a", 1), ("b", 2), ("c", 3), ("a", 4)];

    // ── Row-independent leaves go native ────────────────────────────────────────────────────────────

    [Fact]
    public void Closed_new_DateTime_leaf_beside_a_field_goes_native()
    {
        var collection = Seed(nameof(Closed_new_DateTime_leaf_beside_a_field_goes_native));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { x.Label, A = new DateTime() })
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.A.Ticks}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|0", "a|0", "b|0", "c|0"], results);
    }

    [Fact]
    public void All_client_closed_new_DateTime_keeps_one_row_per_document()
    {
        var collection = Seed(nameof(All_client_closed_new_DateTime_keeps_one_row_per_document));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Where(x => x.Label == "a")
                .Select(x => new { A = new DateTime(2020, 1, 2) })
                .AsEnumerable()
                .Select(r => r.A.ToString("yyyy-MM-dd"))
                .ToList();
        });

        Assert.Equal(["2020-01-02", "2020-01-02"], results);
    }

    [Fact]
    public void Boxed_closed_new_leaf_is_evaluated_in_place()
    {
        var collection = Seed(nameof(Boxed_closed_new_leaf_is_evaluated_in_place));

        // EF keeps the Convert over the unevaluated `new`; the read side must evaluate it rather than bind the Convert
        // to an alias the $project never emits.
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { x.Label, O = (object)new DateTime(2020, 1, 2) })
                .AsEnumerable()
                .Select(r => $"{r.Label}|{((DateTime)r.O).ToString("yyyy-MM-dd")}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|2020-01-02", "a|2020-01-02", "b|2020-01-02", "c|2020-01-02"], results);
    }

    [Fact]
    public void Converted_list_initializer_leaf_is_evaluated_in_place()
    {
        var collection = Seed(nameof(Converted_list_initializer_leaf_is_evaluated_in_place));
        var item = new Item { X = 7 };

        // EF keeps a ListInit over a captured object, and a Convert over it. Unlike a Convert over a `new`, the read
        // side's numeric-cast arm would claim it and bind it to an alias the $project never emits (a null list).
        //
        // Hand-computed oracle for the boxed form: driver-LINQ is not one, as it throws serializing the captured
        // List<Item> through ObjectSerializer (the type isn't in its allowed-types list).
        List<string> RunBoxed(MongoQueryMode mode)
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { x.Label, O = (object)new List<Item> { item } })
                .AsEnumerable()
                .Select(r => $"{r.Label}|{((List<Item>)r.O).Single().X}")
                .OrderBy(s => s)
                .ToList();
        }

        Assert.Equal(["a|7", "a|7", "b|7", "c|7"], RunBoxed(MongoQueryMode.NativeOnly));
        Assert.Equal(["a|7", "a|7", "b|7", "c|7"], RunBoxed(MongoQueryMode.Native));

        var upcast = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { x.Label, O = (IEnumerable<DateTime>)new List<DateTime> { new DateTime() } })
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.O.Single().Ticks}")
                .OrderBy(s => s)
                .ToList();
        });
        Assert.Equal(["a|0", "a|0", "b|0", "c|0"], upcast);
    }

    [Fact]
    public void Empty_anonymous_type_keeps_one_row_per_document()
    {
        var collection = Seed(nameof(Empty_anonymous_type_keeps_one_row_per_document));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { })
                .AsEnumerable()
                .Select(r => r.ToString()!)
                .ToList();
        });

        Assert.Equal(Rows.Length, results.Count);
    }

    [Fact]
    public void Captured_anonymous_type_closure_is_evaluated_client_side()
    {
        var collection = Seed(nameof(Captured_anonymous_type_closure_is_evaluated_client_side));

        foreach (var flag in new[] { false, true })
        {
            var closure = new { B = flag, S = "s" };
            var results = NativeModeAssert.NativeAndParity(mode =>
            {
                using var context = CreateContext(collection, mode);
                return context.Entities.AsNoTracking()
                    .Select(x => new { f = closure })
                    .AsEnumerable()
                    .Select(r => $"{r.f.B}|{r.f.S}")
                    .ToList();
            });

            Assert.Equal(Enumerable.Repeat($"{flag}|s", Rows.Length), results);
        }
    }

    [Fact]
    public void Captured_anonymous_type_closure_beside_a_field_is_evaluated_client_side()
    {
        var collection = Seed(nameof(Captured_anonymous_type_closure_beside_a_field_is_evaluated_client_side));

        foreach (var flag in new[] { false, true })
        {
            var closure = new { B = flag };
            var results = NativeModeAssert.NativeAndParity(mode =>
            {
                using var context = CreateContext(collection, mode);
                return context.Entities.AsNoTracking()
                    .Select(x => new { f = closure, x.Label })
                    .AsEnumerable()
                    .Select(r => $"{r.Label}|{r.f.B}")
                    .OrderBy(s => s)
                    .ToList();
            });

            Assert.Equal([$"a|{flag}", $"a|{flag}", $"b|{flag}", $"c|{flag}"], results);
        }
    }

    [Fact]
    public void Captured_list_of_objects_is_evaluated_client_side()
    {
        var collection = Seed(nameof(Captured_list_of_objects_is_evaluated_client_side));
        var items = new List<Item> { new() { X = 7 }, new() { X = 8 } };

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { x.Label, Items = items })
                .AsEnumerable()
                .Select(r => $"{r.Label}|{string.Join(",", r.Items.Select(i => i.X))}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|7,8", "a|7,8", "b|7,8", "c|7,8"], results);
    }

    [Fact]
    public void MemberInit_with_parameter_only_constructor_arguments_goes_native()
    {
        var collection = Seed(nameof(MemberInit_with_parameter_only_constructor_arguments_goes_native));
        var value = "random";

        List<string> Run(MongoQueryMode mode)
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new Dto(value) { Label = x.Label, Nested = new Dto(value) })
                .AsEnumerable()
                .Select(r => $"{r.Value}|{r.Label}|{r.Nested?.Value}|{r.Nested?.Label}")
                .OrderBy(s => s)
                .ToList();
        }

        // Hand-computed oracle (what in-memory LINQ answers): both constructions receive `value`. Driver-LINQ is not an
        // oracle here: it renders `Nested` as a $literal document and deserializes it, and the get-only Value
        // property doesn't round-trip, so it answers Nested.Value = null.
        string[] expected = ["random|a|random|", "random|a|random|", "random|b|random|", "random|c|random|"];
        Assert.Equal(expected, Run(MongoQueryMode.NativeOnly));
        Assert.Equal(expected, Run(MongoQueryMode.Native));
    }

    // ── Distinct over a client-evaluated leaf ───────────────────────────────────────────────────────

    [Fact]
    public void Distinct_over_an_all_client_projection_returns_one_row()
    {
        var collection = Seed(nameof(Distinct_over_an_all_client_projection_returns_one_row));
        var closure = new { B = true };

        // One row is correct: the value is the same for every row of one execution.
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closure })
                .Distinct()
                .AsEnumerable()
                .Select(r => r.f.B)
                .ToList();
        });

        Assert.Equal([true], results);
    }

    [Fact]
    public void Distinct_over_a_client_leaf_beside_a_field_dedups_by_the_field()
    {
        var collection = Seed(nameof(Distinct_over_a_client_leaf_beside_a_field_dedups_by_the_field));
        var closure = new { B = true };

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closure, x.Label })
                .Distinct()
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.f.B}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|True", "b|True", "c|True"], results);
    }

    // ── Operators that would read a client-evaluated leaf decline ──────────────────────────────────

    [Fact]
    public void Union_over_client_evaluated_leaves_declines()
    {
        var collection = Seed(nameof(Union_over_client_evaluated_leaves_declines));
        var closureA = new { B = true };
        var closureB = new { B = false };

        // The shaper would evaluate closureA on source2's rows too, and the dedup would compare Label only: native
        // would return 3 rows, all True, where the right answer is 6 (each Label once per closure).
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closureA, x.Label })
                .Union(context.Entities.AsNoTracking().Select(x => new { f = closureB, x.Label }))
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.f.B}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|False", "a|True", "b|False", "b|True", "c|False", "c|True"], results);
    }

    [Fact]
    public void Union_of_Distincts_over_client_evaluated_leaves_declines()
    {
        var collection = Seed(nameof(Union_of_Distincts_over_client_evaluated_leaves_declines));
        var closureA = new { B = true };
        var closureB = new { B = false };

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closureA, x.Label })
                .Distinct()
                .Union(context.Entities.AsNoTracking().Select(x => new { f = closureB, x.Label }).Distinct())
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.f.B}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a|False", "a|True", "b|False", "b|True", "c|False", "c|True"], results);
    }

    [Fact]
    public void Concat_over_client_evaluated_leaves_declines()
    {
        var collection = Seed(nameof(Concat_over_client_evaluated_leaves_declines));
        var closureA = new { B = true };
        var closureB = new { B = false };

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closureA, x.Label })
                .Concat(context.Entities.AsNoTracking().Select(x => new { f = closureB, x.Label }))
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.f.B}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(
            ["a|False", "a|False", "a|True", "a|True", "b|False", "b|True", "c|False", "c|True"], results);
    }

    // EF's navigation expansion composes a Where/OrderBy written after the Select into the source, substituting the
    // member (`p.f.B` becomes the captured `closure.B`), so no server operator reads the client leaf's alias; the
    // predicate/key over the captured value itself declines. Pinned so that stays a clean decline.

    [Fact]
    public void Where_over_a_client_evaluated_leaf_declines()
    {
        var collection = Seed(nameof(Where_over_a_client_evaluated_leaf_declines));
        var closure = new { B = true };

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closure, x.Label })
                .Where(p => p.f.B)
                .AsEnumerable()
                .Select(r => r.Label)
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["a", "a", "b", "c"], results);
    }

    [Fact]
    public void OrderBy_over_a_client_evaluated_leaf_declines()
    {
        var collection = Seed(nameof(OrderBy_over_a_client_evaluated_leaf_declines));
        var closure = new { K = 5 };

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closure, x.Label })
                .OrderBy(p => p.f.K)
                .ThenBy(p => p.Label)
                .AsEnumerable()
                .Select(r => r.Label)
                .ToList();
        });

        Assert.Equal(["a", "a", "b", "c"], results);
    }

    // A captured TimeSpan is a value type BsonValue.Create can't render, so `x => ts` is an all-client bare body:
    // only the sentinel is projected. (A closed `new DateTime(y, m, d)` body is not: it goes native as a positional
    // constructor over $literal arguments.)

    [Fact]
    public void All_client_bare_closure_keeps_one_row_per_document()
    {
        var collection = Seed(nameof(All_client_bare_closure_keeps_one_row_per_document));
        var ts = TimeSpan.FromHours(3);

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking().Select(x => ts).ToList();
        });

        Assert.Equal(Enumerable.Repeat(ts, Rows.Length), results);
    }

    [Fact]
    public void Max_after_Distinct_over_an_all_client_projection_declines()
    {
        var collection = Seed(nameof(Max_after_Distinct_over_an_all_client_projection_declines));
        var ts = TimeSpan.FromHours(3);

        // The Distinct's only key is the sentinel, so its terminal aggregate would reduce the constant (and fail to
        // read `true` as a TimeSpan) rather than the client value.
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return new List<TimeSpan> { context.Entities.AsNoTracking().Select(x => ts).Distinct().Max() };
        });

        Assert.Equal([ts], results);
    }

    [Fact]
    public void Max_over_an_all_client_projection_declines()
    {
        var collection = Seed(nameof(Max_over_an_all_client_projection_declines));
        var ts = TimeSpan.FromHours(3);

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return new List<TimeSpan> { context.Entities.AsNoTracking().Select(x => ts).Max() };
        });

        Assert.Equal([ts], results);
    }

    [Fact]
    public void Where_after_Distinct_over_an_all_client_projection_declines()
    {
        var collection = Seed(nameof(Where_after_Distinct_over_an_all_client_projection_declines));
        var ts = TimeSpan.FromHours(3);
        var low = TimeSpan.FromHours(1);

        // After the Distinct the Where runs over the $group output, whose only key is the sentinel.
        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking().Select(x => ts).Distinct().Where(t => t > low).ToList();
        });

        Assert.Equal([ts], results);
    }

    [Fact]
    public void OrderBy_after_Distinct_over_a_client_leaf_declines()
    {
        var collection = Seed(nameof(OrderBy_after_Distinct_over_a_client_leaf_declines));
        var closure = new { K = 5 };

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .Select(x => new { f = closure, x.Label })
                .Distinct()
                .OrderBy(p => p.f.K)
                .ThenBy(p => p.Label)
                .AsEnumerable()
                .Select(r => r.Label)
                .ToList();
        });

        Assert.Equal(["a", "b", "c"], results);
    }

    [Fact]
    public void Paging_and_First_over_a_client_leaf_stay_native()
    {
        var collection = Seed(nameof(Paging_and_First_over_a_client_leaf_stay_native));
        var closure = new { B = true };

        var paged = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            return context.Entities.AsNoTracking()
                .OrderBy(x => x.N)
                .Select(x => new { f = closure, x.Label })
                .Skip(1)
                .Take(2)
                .AsEnumerable()
                .Select(r => $"{r.Label}|{r.f.B}")
                .ToList();
        });
        Assert.Equal(["b|True", "c|True"], paged);

        var first = NativeModeAssert.NativeAndParity(mode =>
        {
            using var context = CreateContext(collection, mode);
            var row = context.Entities.AsNoTracking()
                .OrderBy(x => x.N)
                .Select(x => new { f = closure })
                .First();
            return new List<bool> { row.f.B };
        });
        Assert.Equal([true], first);
    }

    // ── Seed and helpers ────────────────────────────────────────────────────────────────────────────

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(Rows.Select(r => new Row { Label = r.Label, N = r.N }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
