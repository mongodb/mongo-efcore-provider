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
/// A computed projection leaf read from whole documents (the mixed reader: beside a client-side case mapping, an entity,
/// or a leaf that declines natively) is re-evaluated client-side over its own document reads, so every operand reads
/// its own value. Binding it operand by operand registers each operand under the same member, and every operand then
/// reads as the last one bound (<c>S.IndexOf(T)</c> as <c>T.IndexOf(T)</c>).
/// </summary>
/// <remarks>
/// Hand oracle: C# over the stored values, except that a member access or instance call on a null string answers null
/// where its result is nullable (EF's null propagation) and throws where it is not (as EF throws reading a null into a
/// non-nullable value). Rows: missing (no elements), null (all null), sNull (S null), tNull (T null) and value
/// (S = "Abc", T = "b"); each row is queried on its own, and <see cref="Throws"/> marks a row that must throw.
/// </remarks>
[XUnitCollection("QueryTests")]
public class MixedReaderClientLeafTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private const string Throws = "<throws>";

    public class Inner
    {
        public string? W { get; set; }
    }

    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? S { get; set; }
        public string? T { get; set; }
        public string? E { get; set; }
        public string? C { get; set; }
        public Inner? O { get; set; }
        public int? I1 { get; set; }
        public int? I2 { get; set; }
    }

    public class CtorDto(string? name, int n)
    {
        public int Len { get; } = name?.Length ?? -1;
        public int N { get; } = n;
        public string? Name { get; set; }
    }

    // The `name` parameter initializes both Name (which an initializer may also assign) and First.
    public class NameDto(string? name, string? other)
    {
        public string? Name { get; set; } = name;
        public string? First { get; } = name;
        public string? Other { get; } = other;
    }

    public class Dto
    {
        public string? U { get; set; }
        public int I { get; set; }
    }

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void IndexOf_of_another_field_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S!.IndexOf(x.T!) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            [Throws, Throws, Throws, Throws, "ABC:1"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Nullable_IndexOf_of_another_field_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = (int?)x.S!.IndexOf(x.T!) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:<null>", "<null>:<null>", "<null>:<null>", "ABC:<null>", "ABC:1"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Concatenation_with_a_case_mapped_field_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S + x.T!.ToLower() })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:", "<null>:", "<null>:b", "ABC:Abc", "ABC:Abcb"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Concat_method_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = string.Concat(x.S, x.T) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:", "<null>:", "<null>:b", "ABC:Abc", "ABC:Abcb"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Compare_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = string.Compare(x.S, x.T) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            ["<null>:0", "<null>:0", "<null>:-1", "ABC:1", "ABC:-1"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Replace_with_another_field_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S!.Replace("b", x.T) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:<null>", "<null>:<null>", "<null>:<null>", "ABC:Ac", "ABC:Abc"]);

    // Natively these are computed on the server, but string.Compare declines, so beside a case mapping they read from
    // whole documents in every mode.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void StartsWith_another_field_read_from_whole_documents(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), F = string.Compare(x.S, x.T), V = x.S!.StartsWith(x.T!) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.F + ":" + x.V),
            [Throws, Throws, Throws, Throws, "ABC:-1:False"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Substring_at_another_fields_length_read_from_whole_documents(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), F = string.Compare(x.S, x.T), V = x.S!.Substring(x.T!.Length) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.F + ":" + Show(x.V)),
            ["<null>:0:<null>", "<null>:0:<null>", "<null>:-1:<null>", "ABC:1:<null>", "ABC:-1:bc"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Math_Max_of_two_lengths_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = Math.Max(x.S!.Length, x.T!.Length) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            [Throws, Throws, Throws, Throws, "ABC:3"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Equality_of_two_fields_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S == x.T })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            ["<null>:True", "<null>:True", "<null>:False", "ABC:False", "ABC:False"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Coalesce_and_conditional_over_two_fields_beside_a_case_mapping(string mode)
    {
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S ?? x.T })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:<null>", "<null>:<null>", "<null>:b", "ABC:Abc", "ABC:Abc"]);
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S == null ? x.T : x.S })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.V)),
            ["<null>:<null>", "<null>:<null>", "<null>:b", "ABC:Abc", "ABC:Abc"]);
    }

    // A string instance call on a null receiver answers null, as in EF, rather than throwing.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Null_propagating_string_calls_beside_an_entity(string mode)
    {
        AssertRows(mode, q => q.Select(x => new { R = x, V = x.S!.Trim() + x.T })
                .AsEnumerable().Select(x => x.R.Label + ":" + Show(x.V)),
            ["missing:", "null:", "sNull:b", "tNull:Abc", "value:Abcb"]);
        AssertRows(mode, q => q.Select(x => new { R = x, V = x.S!.Replace("b", "x") })
                .AsEnumerable().Select(x => x.R.Label + ":" + Show(x.V)),
            ["missing:<null>", "null:<null>", "sNull:<null>", "tNull:Axc", "value:Axc"]);
    }

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void KeyValuePair_of_a_case_mapping_and_a_computed_length(string mode)
        => AssertRows(mode, q => q.Select(x => new KeyValuePair<string, int>(x.S!.ToUpper(), x.S!.Length * 10 + x.T!.Length))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.Value),
            [Throws, Throws, Throws, Throws, "ABC:31"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void KeyValuePair_of_two_fields_beside_an_entity(string mode)
        => AssertRows(mode, q => q.Select(x => new { R = x, K = new KeyValuePair<string?, string?>(x.S, x.T) })
                .AsEnumerable().Select(x => x.R.Label + ":" + Show(x.K.Key) + "/" + Show(x.K.Value)),
            ["missing:<null>/<null>", "null:<null>/<null>", "sNull:<null>/b", "tNull:Abc/<null>", "value:Abc/b"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Nested_anonymous_member_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), N = new { V = string.Concat(x.S, x.T), I = x.S!.IndexOf(x.T!) } })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.N.V + ":" + x.N.I),
            [Throws, Throws, Throws, Throws, "ABC:Abcb:1"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Dto_member_init_beside_a_case_mapping(string mode)
        => AssertRows(mode, q => q.Select(x => new Dto { U = x.S!.ToUpper(), I = x.S!.IndexOf(x.T!) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.I),
            [Throws, Throws, Throws, Throws, "ABC:1"]);

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Entity_beside_a_multi_read_leaf(string mode)
    {
        AssertRows(mode, q => q.Select(x => new { R = x, V = x.S!.IndexOf(x.T!) })
                .AsEnumerable().Select(x => x.R.Label + ":" + x.V),
            [Throws, Throws, Throws, Throws, "value:1"]);
        AssertRows(mode, q => q.Select(x => new { R = x, V = string.Concat(x.S, x.T) })
                .AsEnumerable().Select(x => x.R.Label + ":" + x.V),
            ["missing:", "null:", "sNull:b", "tNull:Abc", "value:Abcb"]);
    }

    // Each operand is read through its own property: an owned navigation's document, a configured element name, and
    // a value converter (C is stored as "pre:" + value).
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Owned_renamed_and_converted_operands_beside_a_case_mapping(string mode)
    {
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = string.Concat(x.S, x.O!.W) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            ["<null>:", "<null>:", "<null>:b", "ABC:Abc", "ABC:Abcb"]);
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = string.Concat(x.S, x.E) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            ["<null>:", "<null>:", "<null>:b", "ABC:Abc", "ABC:Abcb"]);
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = string.Concat(x.S, x.C) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V),
            ["<null>:", "<null>:", "<null>:b", "ABC:Abc", "ABC:Abcb"]);
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), V = x.S!.IndexOf(x.O!.W!), W = x.S!.IndexOf(x.E!), Y = x.S!.IndexOf(x.C!) })
                .AsEnumerable().Select(x => Show(x.U) + ":" + x.V + ":" + x.W + ":" + x.Y),
            [Throws, Throws, Throws, Throws, "ABC:1:1:1"]);
    }

    // The constructor's `name` parameter and the initializer both name the Name member, so the arguments can't be
    // bound per member; bound under the one enclosing member, each would read as the last one bound (Len as the
    // length of T, or the Compare result read as a string). The mixed reader declines instead, for every row.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Constructor_argument_and_initializer_naming_the_same_member(string mode)
    {
        AssertEveryRowDeclinesConstruction(mode, q => q.Select(x => new CtorDto(x.S!.ToUpper(), string.Compare(x.S, x.T)) { Name = x.T })
                .AsEnumerable().Select(x => x.Len + ":" + x.N + ":" + Show(x.Name)),
            nameof(CtorDto));
        AssertEveryRowDeclinesConstruction(mode, q => q.Select(x => new { R = x, D = new CtorDto(x.S!.ToUpper(), string.Compare(x.S, x.T)) { Name = x.T } })
                .AsEnumerable().Select(x => x.D.Len + ":" + x.D.N + ":" + Show(x.D.Name)),
            nameof(CtorDto));
    }

    // Same-typed arguments: bound under the one enclosing member, First would silently read T.ToUpper() ("B" where C#
    // answers "ABC").
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Same_typed_constructor_arguments_and_initializer_naming_the_same_member(string mode)
    {
        AssertEveryRowDeclinesConstruction(mode, q => q.Select(x => new NameDto(x.S!.ToUpper(), x.T!.ToUpper()) { Name = x.T })
                .AsEnumerable().Select(x => Show(x.First) + ":" + Show(x.Name) + ":" + Show(x.Other)),
            nameof(NameDto));
        AssertEveryRowDeclinesConstruction(mode, q => q.Select(x => new { R = x, D = new NameDto(x.S!.ToUpper(), x.T!.ToUpper()) { Name = x.T } })
                .AsEnumerable().Select(x => Show(x.D.First) + ":" + Show(x.D.Name) + ":" + Show(x.D.Other)),
            nameof(NameDto));
    }

    // A Select that an operator composes over (Distinct, Union) is retained, so the driver returns projected documents
    // rather than whole ones; a leaf can't be re-evaluated over them, and must not read the missing S as null. C# answers
    // ["<null>:<null>", "<null>:<null>", "b:<null>", "<null>:bc", "b:bc"]; until that is reachable (EF-460) every row
    // must fail translation rather than answer anything: the mixed reader looks V up as an entity property.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Computed_leaf_under_a_retained_pushed_down_Select(string mode)
    {
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.S!.Substring(1) }).Distinct()
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.S!.Substring(1) })
                .Union(q.Select(x => new { R = x.O, V = x.S!.Substring(1) }))
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
    }

    // EF-460: a concatenation or arithmetic leaf under the same retained Select used to be re-evaluated over the
    // projected documents' missing S/T/I1/I2 and answer silently wrong values ("<null>:" for every row, where C# answers
    // "b:b", "<null>:Abc", "b:Abcb"). It now fails translation like the Substring leaf above. Reading the projected V by
    // name instead is not an option: the driver's $concat answers null where C# treats a null operand as empty.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Concat_and_arithmetic_leaf_under_a_retained_pushed_down_Select(string mode)
    {
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.S + x.T }).Distinct()
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.S + x.T })
                .Union(q.Select(x => new { R = x.O, V = x.S + x.T }))
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.I1 + x.I2 }).Distinct()
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
        AssertEveryRowThrows(mode, q => q.Select(x => new { R = x.O, V = x.I1 * 2 + x.I2 })
                .Union(q.Select(x => new { R = x.O, V = x.I1 * 2 + x.I2 }))
                .AsEnumerable().Select(x => Show(x.R?.W) + ":" + Show(x.V)));
    }

    private void AssertEveryRowThrows(
        string mode,
        Func<IQueryable<Row>, IEnumerable<string>> query,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var collection = Seed(name + mode + Guid.NewGuid().ToString("N")[..4]);
        foreach (var label in new[] { "missing", "null", "sNull", "tNull", "value" })
        {
            using var db = CreateContext(collection, Enum.Parse<MongoQueryMode>(mode));
            var exception = Assert.Throws<InvalidOperationException>(
                () => query(db.Entities.AsNoTracking().Where(x => x.Label == label)).ToList());
            Assert.Equal(
                "The property 'Row.V' could not be found. Ensure that the property exists and has been included in the model.",
                exception.Message);
        }
    }

    private static string Show(string? value) => value ?? "<null>";

    private static string Show(int? value) => value?.ToString() ?? "<null>";

    private void AssertRows(
        string mode,
        Func<IQueryable<Row>, IEnumerable<string>> query,
        string[] expected,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var collection = Seed(name + mode);
        var labels = new[] { "missing", "null", "sNull", "tNull", "value" };
        var actual = new List<string>();
        foreach (var label in labels)
        {
            using var db = CreateContext(collection, Enum.Parse<MongoQueryMode>(mode));
            try
            {
                actual.Add(string.Join(",", query(db.Entities.AsNoTracking().Where(x => x.Label == label)).ToList()));
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                actual.Add(Throws);
            }
        }

        Assert.Equal(expected, actual);
    }

    // Every row must throw the mixed reader's decline for a construction whose arguments can't be bound per member.
    private void AssertEveryRowDeclinesConstruction(
        string mode,
        Func<IQueryable<Row>, IEnumerable<string>> query,
        string constructedTypeName,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var collection = Seed(name + mode + Guid.NewGuid().ToString("N")[..4]);
        foreach (var label in new[] { "missing", "null", "sNull", "tNull", "value" })
        {
            using var db = CreateContext(collection, Enum.Parse<MongoQueryMode>(mode));
            var exception = Assert.Throws<InvalidOperationException>(
                () => query(db.Entities.AsNoTracking().Where(x => x.Label == label)).ToList());
            Assert.StartsWith(
                $"The projection constructs '{constructedTypeName}' from arguments that can't each be read from the document",
                exception.Message);
        }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing" } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", "null" }, { "S", BsonNull.Value }, { "T", BsonNull.Value },
                { "e_t", BsonNull.Value }, { "C", BsonNull.Value }, { "O", new BsonDocument("W", BsonNull.Value) }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", "sNull" }, { "S", BsonNull.Value }, { "T", "b" }, { "I1", BsonNull.Value }, { "I2", 5 },
                { "e_t", "b" }, { "C", "pre:b" }, { "O", new BsonDocument("W", "b") }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", "tNull" }, { "S", "Abc" }, { "T", BsonNull.Value }, { "I1", 4 }, { "I2", BsonNull.Value },
                { "e_t", BsonNull.Value }, { "C", BsonNull.Value }, { "O", new BsonDocument("W", BsonNull.Value) }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", "value" }, { "S", "Abc" }, { "T", "b" }, { "I1", 2 }, { "I2", 3 },
                { "e_t", "b" }, { "C", "pre:b" }, { "O", new BsonDocument("W", "b") }
            }
        ]);
        return database.MongoDatabase.GetCollection<Row>(collectionName);
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<Row>().Property(x => x.E).HasElementName("e_t");
                mb.Entity<Row>().Property(x => x.C)
                    .HasConversion(v => v == null ? null : "pre:" + v, v => v == null ? null : v.Substring(4));
                mb.Entity<Row>().OwnsOne(x => x.O);
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
