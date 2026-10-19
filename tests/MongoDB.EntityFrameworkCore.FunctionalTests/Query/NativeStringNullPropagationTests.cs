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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native string operators over a null or missing receiver answer what EF answers: a member-style call on a null
/// receiver propagates null (<c>S.Substring(0, 1)</c>, <c>S.Length</c>, <c>S.IndexOf("b")</c> are null), and
/// string concatenation treats a null operand as <c>""</c>. On the server, <c>$substrCP</c> answers <c>""</c>,
/// <c>$strLenCP</c> is an error and <c>$concat</c> answers null, so the renderer null-guards them. NativeOnly
/// against a hand oracle: driver-LINQ answers the server's values, and C# throws on the null receiver.
/// </summary>
/// <remarks>
/// Rows: <c>null</c> (S stored as BSON null), <c>missing</c> (no S element), <c>value</c> (S = "Abc"). Results are
/// listed in Label order: missing, null, value.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeStringNullPropagationTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? S { get; set; }
        public string? T { get; set; }
        public string? U { get; set; }
    }

    [Fact]
    public void Substring_of_null_or_missing_is_null()
        => AssertProjection(x => x.S!.Substring(0, 1), [null, null, "A"]);

    [Fact]
    public void To_end_Substring_of_null_or_missing_is_null()
        => AssertProjection(x => x.S!.Substring(1), [null, null, "bc"]);

    [Fact]
    public void Trim_of_null_or_missing_is_null()
        => AssertProjection(x => x.S!.Trim(), [null, null, "Abc"]);

    [Fact]
    public void Replace_of_null_or_missing_is_null()
        => AssertProjection(x => x.S!.Replace("b", "z"), [null, null, "Azc"]);

    [Fact]
    public void Nullable_Length_of_null_or_missing_is_null()
        => AssertProjection(x => (int?)x.S!.Length, [null, null, 3]);

    [Fact]
    public void Nullable_IndexOf_of_null_or_missing_is_null()
        => AssertProjection(x => (int?)x.S!.IndexOf("b"), [null, null, 1]);

    [Fact]
    public void IndexOf_a_null_or_missing_needle_is_null()
        // A null $indexOfCP needle is a server error; EF answers null. The "value" row's T is "b".
        => AssertProjection(x => (int?)"Abc".IndexOf(x.T!), [null, null, 1]);

    [Fact]
    public void Concat_treats_null_or_missing_operand_as_empty()
        => AssertProjection(x => x.S + "x", ["x", "x", "Abcx"]);

    [Fact]
    public void Concat_method_treats_null_or_missing_operand_as_empty()
        // A filter: a projected string.Concat(...) call isn't a native projection leaf.
        => AssertFilter(x => string.Concat("<", x.S, ">") == "<>", ["missing", "null"]);

    [Fact]
    public void Concat_of_two_nullable_fields_treats_each_as_empty()
        => AssertProjection(x => x.S + x.T, ["", "", "Abcb"]);

    [Fact]
    public void Concat_over_a_null_propagating_operator_treats_its_null_as_empty()
        => AssertProjection(x => x.S!.Substring(0, 1) + "x", ["x", "x", "Ax"]);

    [Fact]
    public void Substring_equality_filter_is_unchanged()
        => AssertFilter(x => x.S!.Substring(0, 1) == "A", ["value"]);

    [Fact]
    public void Substring_inequality_filter_is_unchanged()
        // EF's C# null semantics: null != "A" is true.
        => AssertFilter(x => x.S!.Substring(0, 1) != "A", ["missing", "null"]);

    [Fact]
    public void Substring_equals_null_filter_matches_null_and_missing()
        => AssertFilter(x => x.S!.Substring(0, 1) == null, ["missing", "null"]);

    [Fact]
    public void Length_relational_filter_excludes_null_and_missing()
        // $strLenCP was a server error; lifted null < 5 is false.
        => AssertFilter(x => x.S!.Length < 5, ["value"]);

    [Fact]
    public void IndexOf_relational_filter_excludes_null_and_missing()
        // $indexOfCP propagates null, and $lt orders null below every number; lifted null < 5 is false.
        => AssertFilter(x => x.S!.IndexOf("b") < 5, ["value"]);

    [Fact]
    public void Concat_equality_filter_treats_null_as_empty()
        => AssertFilter(x => x.S + "x" == "x", ["missing", "null"]);

    [Fact]
    public void Compare_of_two_nullable_fields_equates_missing_with_null()
        // string.Compare(null, null) is 0; $cmp orders missing below null, so the missing row needs $ifNull.
        => AssertFilter(x => string.Compare(x.S, x.U) == 0, ["missing", "null"]);

    // `<string call> ?? constant`: the server computes the whole $ifNull, so the read side must not re-apply the call
    // over the coalesced value ("none".Substring(0, 1) is "n").
    [Fact]
    public void Coalesced_Substring_projection_reads_the_server_value()
        => AssertProjection(x => x.S!.Substring(0, 1) ?? "none", ["none", "none", "A"]);

    [Fact]
    public void Coalesced_to_end_Substring_projection_reads_the_server_value()
        => AssertProjection(x => x.S!.Substring(1) ?? "none", ["none", "none", "bc"]);

    [Fact]
    public void Coalesced_Replace_projection_reads_the_server_value()
        => AssertProjection(x => x.S!.Replace("n", "X") ?? "none", ["none", "none", "Abc"]);

    [Fact]
    public void Coalesced_Substring_member_projection_reads_the_server_value()
    {
        using var db = CreateContext(Seed(nameof(Coalesced_Substring_member_projection_reads_the_server_value)), MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["missing:none", "null:none", "value:A"],
            db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new { x.Label, V = x.S!.Substring(0, 1) ?? "none" })
                .AsEnumerable().Select(x => x.Label + ":" + x.V).ToList());
    }

    [Fact]
    public void Coalesced_field_projection_is_unchanged()
        => AssertProjection(x => x.S ?? "none", ["none", "none", "Abc"]);

    [Fact]
    public void Conditional_over_Substring_projection_reads_the_server_value()
        => AssertProjection(x => x.S == null ? "none" : x.S.Substring(0, 1), ["none", "none", "A"]);

    // A non-nullable Length/IndexOf read of a possibly-null string would silently answer 0 for a null/missing row
    // (EF throws), so it declines; the nullable-cast spelling stays native (see Nullable_Length_of_null_or_missing_is_null).
    [Fact]
    public void Non_nullable_Length_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => x.S!.Length).ToList());

    [Fact]
    public void Non_nullable_Length_member_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => new { L = x.S!.Length }).ToList());

    [Fact]
    public void Non_nullable_IndexOf_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => x.S!.IndexOf("b")).ToList());

    [Fact]
    public void Non_nullable_arithmetic_over_Length_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => x.S!.Length + 1).ToList());

    [Fact]
    public void Non_nullable_Length_group_key_declines()
        => AssertDeclines(db => db.Entities.GroupBy(x => x.S!.Length).Select(g => new { g.Key, C = g.Count() }).ToList());

    [Fact]
    public void Non_nullable_Length_max_accumulator_declines()
        => AssertDeclines(db => db.Entities.GroupBy(x => x.Label).Select(g => new { g.Key, M = g.Max(x => x.S!.Length) }).ToList());

    [Fact]
    public void Non_nullable_IndexOf_pushed_list_declines()
        => AssertDeclines(db => db.Entities.GroupBy(x => x.Label).Select(g => new { g.Key, L = g.Select(x => x.S!.IndexOf("b")).ToList() }).ToList());

    // A declined Length leaf beside a client-side case mapping (no driver-LINQ push-down) is read from the whole
    // document: correct for a value, and loud (like EF) for a null.
    [Fact]
    public void Declined_Length_beside_a_case_mapping_is_computed_client_side()
    {
        using var db = CreateContext(Seed(nameof(Declined_Length_beside_a_case_mapping_is_computed_client_side)), MongoQueryMode.Native);
        Assert.Equal(
            ["ABC:3"],
            db.Entities.Where(x => x.Label == "value").Select(x => new { U = x.S!.ToUpper(), L = x.S!.Length }).AsEnumerable()
                .Select(x => x.U + ":" + x.L).ToList());
        Assert.ThrowsAny<Exception>(
            () => db.Entities.Where(x => x.Label == "null").Select(x => new { U = x.S!.ToUpper(), L = x.S!.Length }).ToList());
    }

    [Fact]
    public void Declined_computed_Length_beside_a_case_mapping_is_computed_client_side()
    {
        using var db = CreateContext(Seed(nameof(Declined_computed_Length_beside_a_case_mapping_is_computed_client_side)), MongoQueryMode.Native);
        Assert.Equal(
            ["ABC:3:4"],
            db.Entities.Where(x => x.Label == "value").Select(x => new { U = x.S!.ToUpper(), T = x.S!.Trim().Length, P = x.S!.Length + 1 })
                .AsEnumerable().Select(x => x.U + ":" + x.T + ":" + x.P).ToList());
        Assert.ThrowsAny<Exception>(
            () => db.Entities.Where(x => x.Label == "null").Select(x => new { U = x.S!.ToUpper(), T = x.S!.Trim().Length }).ToList());
        Assert.ThrowsAny<Exception>(
            () => db.Entities.Where(x => x.Label == "missing").Select(x => new { U = x.S!.ToUpper(), P = x.S!.Length + 1 }).ToList());
    }

    [Fact]
    public void Non_nullable_conditional_Length_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => x.Label != "" ? x.S!.Length : -1).ToList());

    [Fact]
    public void Non_nullable_conditional_Length_member_projection_declines()
        => AssertDeclines(db => db.Entities.Select(x => new { L = x.Label != "" ? x.S!.Length : -1 }).ToList());

    [Fact]
    public void Non_nullable_conditional_Length_group_key_declines()
        => AssertDeclines(db => db.Entities.GroupBy(x => x.Label != "" ? x.S!.Length : 0).Select(g => new { g.Key, C = g.Count() }).ToList());

    [Fact]
    public void Non_nullable_conditional_Length_max_accumulator_declines()
        => AssertDeclines(db => db.Entities.GroupBy(x => x.Label).Select(g => new { g.Key, M = g.Max(x => x.Label != "" ? x.S!.Length : 0) }).ToList());

    [Fact]
    public void Terminal_Max_of_non_nullable_Length_declines()
        => AssertDeclines(db => db.Entities.Max(x => x.S!.Length));

    [Fact]
    public void Terminal_Min_of_non_nullable_Length_declines()
        => AssertDeclines(db => db.Entities.Min(x => x.S!.Length));

    [Fact]
    public void Terminal_Average_of_non_nullable_Length_declines()
        => AssertDeclines(db => db.Entities.Average(x => x.S!.Length));

    [Fact]
    public void Terminal_Sum_of_Length_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Terminal_Sum_of_Length_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(3, db.Entities.Sum(x => x.S!.Length));
    }

    [Fact]
    public void Terminal_Max_of_nullable_Length_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Terminal_Max_of_nullable_Length_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(3, db.Entities.Max(x => (int?)x.S!.Length));
    }

    [Fact]
    public void Length_ToString_of_null_or_missing_is_null()
        => AssertProjection(x => x.S!.Length.ToString(), [null, null, "3"]);

    // The standard null-guard idiom: the Length branch never sees a null, so the non-nullable read stays native. The
    // guard must test the same stored value (structurally), or the read still declines.
    [Fact]
    public void Null_guarded_Length_projection_stays_native()
        => AssertProjection(x => x.S == null ? -1 : x.S.Length, [-1, -1, 3]);

    [Fact]
    public void Not_null_guarded_Length_member_projection_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Not_null_guarded_Length_member_projection_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal([-1, -1, 3], db.Entities.OrderBy(x => x.Label).Select(x => new { L = x.S != null ? x.S.Length : -1 }).AsEnumerable().Select(x => x.L).ToList());
    }

    [Fact]
    public void IsNullOrEmpty_guarded_Length_with_label_projection_stays_native()
    {
        using var db = CreateContext(Seed(nameof(IsNullOrEmpty_guarded_Length_with_label_projection_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["missing:0", "null:0", "value:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, L = string.IsNullOrEmpty(x.S) ? 0 : x.S.Length })
                .AsEnumerable().Select(x => x.Label + ":" + x.L).ToList());
    }

    [Fact]
    public void Null_guarded_Length_terminal_Max_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Null_guarded_Length_terminal_Max_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(3, db.Entities.Max(x => x.S == null ? 0 : x.S.Length));
    }

    [Fact]
    public void Null_guarded_Length_max_accumulator_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Null_guarded_Length_max_accumulator_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal([3], db.Entities.GroupBy(x => x.Label != "").Select(g => g.Max(x => x.S == null ? 0 : x.S.Length)).ToList());
    }

    // Beside a client-side case mapping the projection is read from whole documents in both modes, and a leaf computing
    // a Length is re-evaluated client-side as written. The released driver-LINQ provider (v10.0.4) answers the null and
    // value rows the same, and fails the missing row on the server ($eq: [missing, null] is false).
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Null_guarded_Length_beside_a_case_mapping_stays_correct(string mode)
    {
        using var db = CreateContext(Seed(nameof(Null_guarded_Length_beside_a_case_mapping_stays_correct) + mode), Enum.Parse<MongoQueryMode>(mode));
        Assert.Equal(
            ["missing::-1", "null::-1", "value:ABC:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = x.S == null ? -1 : x.S.Length })
                .AsEnumerable().Select(x => x.Label + ":" + x.U + ":" + x.L).ToList());
        Assert.Equal(
            ["missing::-1", "null::-1", "value:ABC:4"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = x.S == null ? -1 : x.S.Length + 1 })
                .AsEnumerable().Select(x => x.Label + ":" + x.U + ":" + x.L).ToList());
    }

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Coalesced_Length_beside_a_case_mapping_is_computed_client_side(string mode)
    {
        using var db = CreateContext(Seed(nameof(Coalesced_Length_beside_a_case_mapping_is_computed_client_side) + mode), Enum.Parse<MongoQueryMode>(mode));
        Assert.Equal(
            ["missing::0", "null::0", "value:ABC:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (x.S ?? "").Length })
                .AsEnumerable().Select(x => x.Label + ":" + x.U + ":" + x.L).ToList());
    }

    // A nullable cast answers null when a document read its operand null-propagates from is null, wherever the cast sits:
    // around the member's binding, inside the leaf, or over a string operator whose receiver is the null read.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Nullable_cast_Length_beside_a_case_mapping_is_null_when_a_read_is_null(string mode)
    {
        using var db = CreateContext(SeedWithIndependentNulls(nameof(Nullable_cast_Length_beside_a_case_mapping_is_null_when_a_read_is_null) + mode), Enum.Parse<MongoQueryMode>(mode));
        // Rows: missing, null, sNull (S null, T "b"), tNull (S "Abc", T null), value (S "Abc", T "b").
        Assert.Equal(
            ["missing:<null>", "null:<null>", "sNull:<null>", "tNull:<null>", "value:4"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)(x.S!.Length + x.T!.Length) })
                .AsEnumerable().Select(x => x.Label + ":" + Show(x.L)).ToList());
        Assert.Equal(
            ["missing:<null>", "null:<null>", "sNull:<null>", "tNull:3", "value:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)x.S!.Trim().Length })
                .AsEnumerable().Select(x => x.Label + ":" + Show(x.L)).ToList());
        Assert.Equal(
            ["missing:<null>", "null:<null>", "sNull:<null>", "tNull:2", "value:2"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)x.S!.Substring(1).Length })
                .AsEnumerable().Select(x => x.Label + ":" + Show(x.L)).ToList());
        Assert.Equal(
            ["missing:-1", "null:-1", "sNull:-1", "tNull:3", "value:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)x.S!.Length ?? -1 })
                .AsEnumerable().Select(x => x.Label + ":" + x.L).ToList());
        // Only T's null propagates through the conditional's arm; S's null is decided by the guard.
        Assert.Equal(
            ["missing:<null>", "null:<null>", "sNull:-1", "tNull:<null>", "value:4"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)((x.S == null ? -2 : x.S.Length) + x.T!.Length) })
                .AsEnumerable().Select(x => x.Label + ":" + Show(x.L)).ToList());
        Assert.Equal(
            ["missing:<null>", "null:<null>", "sNull:<null>", "tNull:3", "value:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = x.S!.Length.ToString() })
                .AsEnumerable().Select(x => x.Label + ":" + (x.L ?? "<null>")).ToList());
        // Concatenation treats a null operand as "", so its reads force no null.
        Assert.Equal(
            ["missing:0", "null:0", "sNull:1", "tNull:3", "value:4"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)(x.S + x.T).Length })
                .AsEnumerable().Select(x => x.Label + ":" + Show(x.L)).ToList());
    }

    // Each operand of a computed Length leaf read from whole documents must read its own value, not the last one bound.
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Computed_Length_leaf_read_from_whole_documents_reads_every_operand(string mode)
    {
        using var db = CreateContext(Seed(nameof(Computed_Length_leaf_read_from_whole_documents_reads_every_operand) + mode), Enum.Parse<MongoQueryMode>(mode));
        var value = db.Entities.Where(x => x.Label == "value");
        Assert.Equal(
            ["ABC:3:3:6"],
            value.Select(x => new
                {
                    U = x.S!.ToUpper(),
                    A = x.S!.Length + (x.T!.Length > 1 ? 100 : 0),
                    B = x.S!.Length > x.T!.Length ? x.S.Length : x.T.Length,
                    C = (x.T!.Length > 1 ? 100 : 0) + x.S!.Length * 2
                })
                .AsEnumerable().Select(x => x.U + ":" + x.A + ":" + x.B + ":" + x.C).ToList());
        Assert.Equal(
            ["value:3"],
            value.Select(x => new { E = x, L = x.S!.Length + (x.T!.Length > 1 ? 100 : 0) }).AsEnumerable()
                .Select(x => x.E.Label + ":" + x.L).ToList());
    }

    [Fact]
    public void Length_guarded_by_a_different_field_declines()
        => AssertDeclines(db => db.Entities.Select(x => x.T == null ? -1 : x.S!.Length).ToList());

    // A true `a && b` proves both; a false one proves neither, so the Length arm may see a null S (the sNull row).
    [Fact]
    public void Length_guarded_by_a_conjunction_declines()
    {
        using var db = CreateContext(
            SeedWithIndependentNulls(nameof(Length_guarded_by_a_conjunction_declines)), MongoQueryMode.NativeOnly);
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => x.S == null && x.T == null ? -1 : x.S!.Length).ToList());
    }

    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Nullable_Length_beside_a_case_mapping_is_null_for_null_and_missing(string mode)
    {
        using var db = CreateContext(Seed(nameof(Nullable_Length_beside_a_case_mapping_is_null_for_null_and_missing) + mode), Enum.Parse<MongoQueryMode>(mode));
        Assert.Equal(
            ["missing::<null>", "null::<null>", "value:ABC:3"],
            db.Entities.OrderBy(x => x.Label).Select(x => new { x.Label, U = x.S!.ToUpper(), L = (int?)x.S!.Length })
                .AsEnumerable().Select(x => x.Label + ":" + x.U + ":" + (x.L?.ToString() ?? "<null>")).ToList());
    }

    [Fact]
    public void Nullable_Length_group_key_stays_native()
    {
        using var db = CreateContext(Seed(nameof(Nullable_Length_group_key_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["3:1", "<null>:2"],
            db.Entities.GroupBy(x => (int?)x.S!.Length).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => (x.Key?.ToString() ?? "<null>") + ":" + x.C).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Length_sum_accumulator_stays_native()
    {
        // $sum skips the null, as EF's SUM does.
        using var db = CreateContext(Seed(nameof(Length_sum_accumulator_stays_native)), MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["missing:0", "null:0", "value:3"],
            db.Entities.GroupBy(x => x.Label).Select(g => new { g.Key, T = g.Sum(x => x.S!.Length) }).AsEnumerable()
                .Select(x => x.Key + ":" + x.T).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    private void AssertDeclines<T>(Func<SingleEntityDbContext<Row>, T> query, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var db = CreateContext(Seed(name), MongoQueryMode.NativeOnly);
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(() => query(db));
    }

    // FirstOrDefault()/LastOrDefault() over a null/missing receiver: every comparison answers as C#/EF compare a null
    // with a char (`<`, `==` false; `!=` and a negation true); only the empty string equals '\0'. The projected value
    // keeps the '\0' of an empty string. Rows: empty (""), missing, null, value ("Abc").
    [Fact]
    public void FirstOrDefault_less_than_excludes_null_and_missing()
        => AssertCharFilter(x => x.S!.FirstOrDefault() < 'B', ["empty", "value"]);

    [Fact]
    public void FirstOrDefault_flipped_greater_than_excludes_null_and_missing()
        => AssertCharFilter(x => 'B' > x.S!.FirstOrDefault(), ["empty", "value"]);

    [Fact]
    public void FirstOrDefault_less_than_or_equal_to_NUL_matches_only_empty()
        => AssertCharFilter(x => x.S!.FirstOrDefault() <= '\0', ["empty"]);

    [Fact]
    public void FirstOrDefault_greater_than_or_equal_excludes_null_and_missing()
        => AssertCharFilter(x => x.S!.FirstOrDefault() >= 'A', ["value"]);

    [Fact]
    public void Negated_FirstOrDefault_less_than_matches_exactly_the_complement()
        => AssertCharFilter(x => !(x.S!.FirstOrDefault() < 'B'), ["missing", "null"]);

    [Fact]
    public void LastOrDefault_less_than_excludes_null_and_missing()
        => AssertCharFilter(x => x.S!.LastOrDefault() < 'd', ["empty", "value"]);

    [Fact]
    public void FirstOrDefault_equality_is_unchanged()
        => AssertCharFilter(x => x.S!.FirstOrDefault() == 'A', ["value"]);

    [Fact]
    public void FirstOrDefault_inequality_is_unchanged()
        => AssertCharFilter(x => x.S!.FirstOrDefault() != 'A', ["empty", "missing", "null"]);

    [Fact]
    public void FirstOrDefault_equals_NUL_matches_only_empty()
        => AssertCharFilter(x => x.S!.FirstOrDefault() == '\0', ["empty"]);

    [Fact]
    public void FirstOrDefault_not_equals_NUL_matches_null_missing_and_value()
        => AssertCharFilter(x => x.S!.FirstOrDefault() != '\0', ["missing", "null", "value"]);

    [Fact]
    public void Flipped_LastOrDefault_equals_NUL_matches_only_empty()
        => AssertCharFilter(x => '\0' == x.S!.LastOrDefault(), ["empty"]);

    [Fact]
    public void Negated_FirstOrDefault_equals_NUL_matches_exactly_the_complement()
        => AssertCharFilter(x => !(x.S!.FirstOrDefault() == '\0'), ["missing", "null", "value"]);

    [Fact]
    public void FirstOrDefault_projection_still_reads_NUL_for_null_and_missing()
    {
        using var db = CreateContext(SeedWithEmpty(nameof(FirstOrDefault_projection_still_reads_NUL_for_null_and_missing)), MongoQueryMode.NativeOnly);
        Assert.Equal(['\0', '\0', '\0', 'A'], db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.S!.FirstOrDefault()).ToList());
    }

    private void AssertCharFilter(Expression<Func<Row, bool>> predicate, string[] expected, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var db = CreateContext(SeedWithEmpty(name), MongoQueryMode.NativeOnly);
        Assert.Equal(expected, db.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    private static string Show(int? value) => value?.ToString() ?? "<null>";

    private IMongoCollection<Row> SeedWithIndependentNulls(string name)
    {
        var collection = Seed(name);
        collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "sNull" }, { "S", BsonNull.Value }, { "T", "b" }, { "U", "z" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "tNull" }, { "S", "Abc" }, { "T", BsonNull.Value }, { "U", "z" } }
        ]);
        return collection;
    }

    private IMongoCollection<Row> SeedWithEmpty(string name)
    {
        var collection = Seed(name);
        collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "empty" }, { "S", "" }, { "T", "" }, { "U", "" } });
        return collection;
    }

    private void AssertProjection<T>(Expression<Func<Row, T>> selector, T[] expected, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var db = CreateContext(Seed(name), MongoQueryMode.NativeOnly);
        Assert.Equal(expected, db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(selector).ToList());
    }

    private void AssertFilter(Expression<Func<Row, bool>> predicate, string[] expected, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var db = CreateContext(Seed(name), MongoQueryMode.NativeOnly);
        Assert.Equal(expected, db.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "null" }, { "S", BsonNull.Value }, { "T", BsonNull.Value }, { "U", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing" }, { "U", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "value" }, { "S", "Abc" }, { "T", "b" }, { "U", "z" } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(collectionName);
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
