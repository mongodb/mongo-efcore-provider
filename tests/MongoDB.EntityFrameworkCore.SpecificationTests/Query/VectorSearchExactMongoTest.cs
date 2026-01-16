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

[MongoCondition(MongoCondition.IsAtlas)]
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
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Doubles", "limit" : 4, "index" : "DoublesIndex", "exact" : true, "queryVector" : [0.33000000000000002, -0.52000000000000002] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_Memory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "MemoryFloats", "limit" : 4, "index" : "MemoryFloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_Memory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "MemoryDoubles", "limit" : 4, "index" : "MemoryDoublesIndex", "exact" : true, "queryVector" : [0.33000000000000002, -0.52000000000000002] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "ReadOnlyMemoryFloats", "limit" : 4, "index" : "ReadOnlyMemoryFloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "ReadOnlyMemoryDoubles", "limit" : 4, "index" : "ReadOnlyMemoryDoublesIndex", "exact" : true, "queryVector" : [0.33000000000000002, -0.52000000000000002] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryFloats", "limit" : 4, "index" : "BinaryFloatsIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryBytes", "limit" : 4, "index" : "BinaryBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinarySBytes", "limit" : 4, "index" : "BinarySBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryMemoryFloats", "limit" : 4, "index" : "BinaryMemoryFloatsIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryMemoryBytes", "limit" : 4, "index" : "BinaryMemoryBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryMemorySBytes", "limit" : 4, "index" : "BinaryMemorySBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryReadOnlyMemoryFloats", "limit" : 4, "index" : "BinaryReadOnlyMemoryFloatsIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryReadOnlyMemoryBytes", "limit" : 4, "index" : "BinaryReadOnlyMemoryBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "BinaryReadOnlyMemorySBytes", "limit" : 4, "index" : "BinaryReadOnlyMemorySBytesIndex", "exact" : true, "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_floats_before_where(bool async)
    {
        await base.VectorSearch_floats_before_where(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "is_published" : true } }
""");
    }

    public override async Task VectorSearch_with_projection(bool async)
    {
        await base.VectorSearch_with_projection(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "_v" : "$Author", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_on_nested_reference(bool async, bool specifyIndex)
    {
        await base.VectorSearch_on_nested_reference(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsVectorIndex", "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_bool_pre_filter(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "filter" : { "is_published" : true }, "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Doubles", "limit" : 4, "index" : "DoublesIndex", "filter" : { "$and" : [{ "comments" : "Froody" }, { "$or" : [{ "Pages" : { "$gt" : 500 } }, { "is_published" : true }] }] }, "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_bool_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Preface.Floats", "limit" : 4, "index" : "FloatsVectorIndex", "filter" : { "Preface.Published" : true }, "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Preface.Floats", "limit" : 4, "index" : "FloatsVectorIndex", "filter" : { "$and" : [{ "Preface.PrefaceComments" : "Froody" }, { "$or" : [{ "Preface.Pages" : { "$gt" : 500 } }, { "Preface.Published" : true }] }] }, "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_projection_of_score(bool async)
    {
        await base.VectorSearch_with_projection_of_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Author" : "$Author", "Score" : "$__score", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_with_projection_of_entity_and_score(bool async)
    {
        await base.VectorSearch_with_projection_of_entity_and_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Book" : "$$ROOT", "Score" : "$__score", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_with_projection_of_constructed_entity_and_score(bool async)
    {
        await base.VectorSearch_with_projection_of_constructed_entity_and_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Book" : { "_id" : "$_id", "Title" : "$Title", "Author" : "$Author", "Isbn" : "$Isbn", "Comments" : "$comments", "Pages" : "$Pages", "IsPublished" : "$is_published" }, "Score" : "$__score", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_logs_for_zero_results(bool async)
    {
        var expectedQuery =
            """
            Books.aggregate([{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "FloatsWithNoData", "limit" : 4, "numCandidates" : 40, "index" : "FloatsWithNoDataIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }])
            """;

        await VectorSearchZeroResults(async, expectedQuery);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "FloatsWithNoData", "limit" : 4, "numCandidates" : 40, "index" : "FloatsWithNoDataIndex", "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_projection_of_score_using_EF_Property(bool async)
    {
        await base.VectorSearch_with_projection_of_score_using_EF_Property(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "index" : "FloatsIndex", "exact" : true, "queryVector" : [0.33000001311302185, -0.51999998092651367] } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Author" : "$Author", "Score" : "$__score", "_id" : 0 } }
""");
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_throws_if_num_candidates_set_for_exact_search(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };
        var queryable = context.Set<Book>().VectorSearch(e => e.Preface.Floats, inputVector, limit: 4, new(NumberOfCandidates: 10, Exact: true));

        Assert.Contains(
            "The option 'Exact' is set to 'true' on a call to 'VectorQuery', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set.",
            (await Assert.ThrowsAsync<InvalidOperationException>(
                async () => _ = async ? await queryable.ToListAsync() : queryable.ToList())).Message);
    }

    public class VectorSearchExactFixture : VectorSearchFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("VectorSearchExact");

        public override bool Exact
            => true;
    }
}
