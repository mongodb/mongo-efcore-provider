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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class VectorSearchExactMongoTest : VectorSearchMongoTestBase,
    IClassFixture<VectorSearchExactMongoTest.VectorSearchExactFixture>
{
    public VectorSearchExactMongoTest(VectorSearchExactFixture fixture)
        : base(fixture)
    {
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task VectorSearch_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "Doubles", "limit" : 4, "index" : "DoublesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_Memory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "MemoryFloats", "limit" : 4, "index" : "MemoryFloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_Memory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "MemoryDoubles", "limit" : 4, "index" : "MemoryDoublesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "ReadOnlyMemoryFloats", "limit" : 4, "index" : "ReadOnlyMemoryFloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "ReadOnlyMemoryDoubles", "limit" : 4, "index" : "ReadOnlyMemoryDoublesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryFloats", "limit" : 4, "index" : "BinaryFloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_bytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryBytes", "limit" : 4, "index" : "BinaryBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_sbytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinarySBytes", "limit" : 4, "index" : "BinarySBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryMemoryFloats", "limit" : 4, "index" : "BinaryMemoryFloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryMemoryBytes", "limit" : 4, "index" : "BinaryMemoryBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryMemorySBytes", "limit" : 4, "index" : "BinaryMemorySBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryFloats", "limit" : 4, "index" : "BinaryReadOnlyMemoryFloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryBytes", "limit" : 4, "index" : "BinaryReadOnlyMemoryBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemorySBytes", "limit" : 4, "index" : "BinaryReadOnlyMemorySBytesIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_floats_after_where(bool async)
    {
        await base.VectorSearch_floats_after_where(async);

        AssertMql(
            """
Books.{ "$match" : { "IsPublished" : true } }, { "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true } }
""");
    }

    public override async Task VectorSearch_floats_before_where(bool async)
    {
        await base.VectorSearch_floats_before_where(async);

        AssertMql();
    }

    public override async Task VectorSearch_on_nested_reference(bool async, bool specifyIndex)
    {
        await base.VectorSearch_on_nested_reference(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsVectorIndex" } }
""");
    }

    public override void VectorSearch_throws_for_driver_IQueryable()
    {
        base.VectorSearch_throws_for_driver_IQueryable();

        AssertMql();
    }

    public override void VectorSearch_throws_for_L2O_IQueryable()
    {
        base.VectorSearch_throws_for_L2O_IQueryable();

        AssertMql();
    }

    public override async Task VectorSearch_with_bool_pre_filter(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "filter" : { "IsPublished" : true }, "exact" : true } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Doubles", "limit" : 4, "index" : "DoublesIndex", "filter" : { "$and" : [{ "Comments" : "Froody" }, { "$or" : [{ "Pages" : { "$gt" : 500 } }, { "IsPublished" : true }] }] }, "exact" : true } }
""");
    }

    public override async Task VectorSearch_with_bool_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "index" : "FloatsVectorIndex", "filter" : { "Preface.IsPublished" : true }, "exact" : true } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "index" : "FloatsVectorIndex", "filter" : { "$and" : [{ "Preface.Comments" : "Froody" }, { "$or" : [{ "Preface.Pages" : { "$gt" : 500 } }, { "Preface.IsPublished" : true }] }] }, "exact" : true } }
""");
    }

    [ConditionalFact]
    public virtual async Task VectorSearch_throws_if_num_candidates_set_for_exact_search()
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        Assert.Contains(
            "The option 'Exact' is set to 'true', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set.",
            Assert.Throws<ArgumentException>(() => context.Set<Book>().VectorSearch(e => e.Preface.Floats, inputVector, limit: 4, new(NumberOfCandidates: 10, Exact: true))).Message);
    }

    public class VectorSearchExactFixture : VectorSearchFixtureBase
    {
        protected override string StoreName { get; } = TestServer.Atlas.GetUniqueDatabaseName("VectorSearchExact");
        public override bool Exact => true;
    }
}
