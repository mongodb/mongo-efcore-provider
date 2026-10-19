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
using System.Collections.Immutable;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>Where(e =&gt; localCollection.Contains(e.Member))</c> shapes EF's Northwind aggregate-operator suite exercises
/// beyond a plain List/HashSet/array: set-interface-typed parameters, an empty inline list, a tuple needle, and a
/// closed <c>Enumerable.Where</c> filter applied to the local collection.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeLocalCollectionContainsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int A { get; set; }
        public int B { get; set; }
        public Guid G { get; set; }
        public decimal D { get; set; }
        public DateTime T { get; set; }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Name = "a", A = 1, B = 2 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "b", A = 3, B = 4 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "c", A = 1, B = 4 },
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    /// <summary>Runs <paramref name="query"/> natively and against DriverLinq; returns the sorted Names.</summary>
    private List<string> Names(string name, Func<IQueryable<Row>, IQueryable<Row>> query)
    {
        var collection = Seed(name);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return query(db.Entities).Select(r => r.Name).AsEnumerable().Order().ToList();
        });
    }

    [Fact]
    public void IReadOnlySet_parameter_contains_goes_native()
    {
        IReadOnlySet<string> names = new HashSet<string> { "a", "c" };
        Assert.Equal(["a", "c"], Names(nameof(IReadOnlySet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void IImmutableSet_parameter_contains_goes_native()
    {
        IImmutableSet<string> names = ImmutableHashSet<string>.Empty.Add("b");
        Assert.Equal(["b"], Names(nameof(IImmutableSet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void ISet_parameter_contains_goes_native()
    {
        ISet<string> names = new HashSet<string> { "a" };
        Assert.Equal(["a"], Names(nameof(ISet_parameter_contains_goes_native), q => q.Where(r => names.Contains(r.Name))));
    }

    [Fact]
    public void Negated_contains_over_empty_inline_list_returns_every_row_natively()
        => Assert.Equal(["a", "b", "c"], Names(nameof(Negated_contains_over_empty_inline_list_returns_every_row_natively),
            q => q.Where(r => !new List<string>().Contains(r.Name))));

    [Fact]
    public void Contains_over_empty_inline_list_returns_no_rows_natively()
        => Assert.Empty(Names(nameof(Contains_over_empty_inline_list_returns_no_rows_natively),
            q => q.Where(r => new List<string>().Contains(r.Name))));

    [Fact]
    public void Tuple_parameter_array_contains_constructed_tuple_goes_native()
    {
        var pairs = new[] { Tuple.Create(1, 2), Tuple.Create(3, 4) };
        Assert.Equal(["a", "b"], Names(nameof(Tuple_parameter_array_contains_constructed_tuple_goes_native),
            q => q.Where(r => pairs.Contains(new Tuple<int, int>(r.A, r.B)))));
    }

    [Fact]
    public void Tuple_contains_is_positional_not_set_membership()
    {
        // (4, 1) must NOT match row c = (A: 1, B: 4): tuple equality is positional.
        var pairs = new[] { Tuple.Create(4, 1) };
        Assert.Empty(Names(nameof(Tuple_contains_is_positional_not_set_membership),
            q => q.Where(r => pairs.Contains(Tuple.Create(r.A, r.B)))));
    }

    [Fact]
    public void Filtered_inline_list_contains_goes_native()
        => Assert.Equal(["a"], Names(nameof(Filtered_inline_list_contains_goes_native),
            q => q.Where(r => new List<string?> { "a", null, "zzz" }.Where(n => n != null).Contains(r.Name))));

    // The same compiled query executed twice with a different captured value must see the new value.
    [Fact]
    public void Filtered_parameter_collection_is_re_evaluated_per_execution()
    {
        var collection = Seed(nameof(Filtered_parameter_collection_is_re_evaluated_per_execution));
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq })
        {
            var id = "a";
            using (var db = CreateContext(collection, mode))
            {
                Assert.Equal(["a"], db.Entities.Where(r => new List<string?> { "zzz", id }.Where(n => n != null).Contains(r.Name))
                    .Select(r => r.Name).ToList());
            }

            id = "b";
            using (var db = CreateContext(collection, mode))
            {
                Assert.Equal(["b"], db.Entities.Where(r => new List<string?> { "zzz", id }.Where(n => n != null).Contains(r.Name))
                    .Select(r => r.Name).ToList());
            }
        }
    }

    [Fact]
    public void Filter_capturing_another_variable_is_not_folded_and_declines_cleanly()
    {
        var excluded = "a";
        var names = new List<string> { "a", "b" };
        var collection = Seed(nameof(Filter_capturing_another_variable_is_not_folded_and_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Where(r => names.Where(n => n != excluded).Contains(r.Name)).Select(r => r.Name).ToList();
        });
        Assert.Equal(["b"], result);
    }

    // The tuple haystack is serialized by the driver's global serializer registry (MongoAggregationExpressionRenderer
    // .RenderInValues), while the needle reads each field as EF stored it — pin that they agree for the element types
    // whose representation is most likely to diverge (Guid, decimal, DateTime).
    [Fact]
    public void Tuple_contains_with_guid_decimal_and_datetime_elements_matches_stored_values_natively()
    {
        var pairs = new[] { Tuple.Create(Guid.Empty, 1) };
        var collection = Seed(nameof(Tuple_contains_with_guid_decimal_and_datetime_elements_matches_stored_values_natively));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var r0 = db.Entities.Where(r => pairs.Contains(Tuple.Create(r.G, r.A))).Select(r => r.Name).ToList();
        Assert.Equal(["a", "c"], r0.Order());
        var dPairs = new[] { Tuple.Create(0m, 1) };
        var r1 = db.Entities.Where(r => dPairs.Contains(Tuple.Create(r.D, r.A))).Select(r => r.Name).ToList();
        Assert.Equal(["a", "c"], r1.Order());
        var tPairs = new[] { Tuple.Create(default(DateTime), 1) };
        var r2 = db.Entities.Where(r => tPairs.Contains(Tuple.Create(r.T, r.A))).Select(r => r.Name).ToList();
        Assert.Equal(["a", "c"], r2.Order());
    }

    /// <summary>A user collection whose parameterless constructor is NOT empty.</summary>
    public class PrePopulatedNames : List<string>
    {
        public PrePopulatedNames() => Add("a");
    }

    // An argument-less `new X()` is only known to be empty for the BCL collection types; a user collection's
    // constructor can pre-populate it, so it must not be translated as an empty $nin.
    [Fact]
    public void Negated_contains_over_prepopulated_user_collection_does_not_treat_it_as_empty()
    {
        var collection = Seed(nameof(Negated_contains_over_prepopulated_user_collection_does_not_treat_it_as_empty));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        List<string>? names = null;
        var ex = Record.Exception(() => names = db.Entities.Where(r => !new PrePopulatedNames().Contains(r.Name))
            .Select(r => r.Name).ToList());
        if (ex is null)
            Assert.Equal(["b", "c"], names!.Order());
        else
            Assert.IsType<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(ex);
    }
}
