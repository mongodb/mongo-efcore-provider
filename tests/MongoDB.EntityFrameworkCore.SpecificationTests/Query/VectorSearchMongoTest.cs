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
public class VectorSearchMongoTest : VectorSearchMongoTestBase, IClassFixture<VectorSearchMongoTest.VectorSearchFixture>
{
    public VectorSearchMongoTest(VectorSearchFixture fixture)
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
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "Doubles", "limit" : 4, "numCandidates" : 40, "index" : "DoublesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_Memory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "MemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "MemoryFloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_Memory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_Memory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "MemoryDoubles", "limit" : 4, "numCandidates" : 40, "index" : "MemoryDoublesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "ReadOnlyMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "ReadOnlyMemoryFloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_ReadOnlyMemory_doubles(bool async, bool specifyIndex)
    {
        await base.VectorSearch_ReadOnlyMemory_doubles(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "ReadOnlyMemoryDoubles", "limit" : 4, "numCandidates" : 40, "index" : "ReadOnlyMemoryDoublesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryFloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinarySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinarySBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemoryFloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryMemoryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemoryBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_Memory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_Memory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryMemorySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemorySBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_floats_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_floats_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemoryFloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_bytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_bytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemoryBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_binary_sbytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await base.VectorSearch_binary_sbytes_as_ReadOnlyMemory(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemorySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemorySBytesIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_floats_before_where(bool async)
    {
        await base.VectorSearch_floats_before_where(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "is_published" : true } }
""");
    }

    public override async Task VectorSearch_with_projection(bool async)
    {
        await base.VectorSearch_with_projection(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "_v" : "$Author", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_on_nested_reference(bool async, bool specifyIndex)
    {
        await base.VectorSearch_on_nested_reference(async, specifyIndex);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsVectorIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_bool_pre_filter(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex", "filter" : { "is_published" : true } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Doubles", "limit" : 4, "numCandidates" : 40, "index" : "DoublesIndex", "filter" : { "$and" : [{ "comments" : "Froody" }, { "$or" : [{ "Pages" : { "$gt" : 500 } }, { "is_published" : true }] }] } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_bool_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_bool_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsVectorIndex", "filter" : { "Preface.Published" : true } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_complex_pre_filter_on_nested_reference(bool async)
    {
        await base.VectorSearch_with_complex_pre_filter_on_nested_reference(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsVectorIndex", "filter" : { "$and" : [{ "Preface.PrefaceComments" : "Froody" }, { "$or" : [{ "Preface.Pages" : { "$gt" : 500 } }, { "Preface.Published" : true }] }] } } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_projection_of_score(bool async)
    {
        await base.VectorSearch_with_projection_of_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Author" : "$Author", "Score" : "$__score", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_with_projection_of_entity_and_score(bool async)
    {
        await base.VectorSearch_with_projection_of_entity_and_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Book" : "$$ROOT", "Score" : "$__score", "_id" : 0 } }
""");
    }

    public override async Task VectorSearch_with_projection_of_constructed_entity_and_score(bool async)
    {
        await base.VectorSearch_with_projection_of_constructed_entity_and_score(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Book" : { "_id" : "$_id", "Title" : "$Title", "Author" : "$Author", "Isbn" : "$Isbn", "Comments" : "$comments", "Pages" : "$Pages", "IsPublished" : "$is_published" }, "Score" : "$__score", "_id" : 0 } }
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
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "FloatsWithNoData", "limit" : 4, "numCandidates" : 40, "index" : "FloatsWithNoDataIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public override async Task VectorSearch_with_projection_of_score_using_EF_Property(bool async)
    {
        await base.VectorSearch_with_projection_of_score_using_EF_Property(async);

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }, { "$match" : { "$or" : [{ "Title" : { "$regularExpression" : { "pattern" : "Action", "options" : "s" } } }, { "Title" : { "$regularExpression" : { "pattern" : "DbContext", "options" : "s" } } }] } }, { "$project" : { "Author" : "$Author", "Score" : "$__score", "_id" : 0 } }
""");
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_num_candidates(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 20, new("FloatsIndex", NumberOfCandidates: 20));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 20, "numCandidates" : 20, "index" : "FloatsIndex" } }, { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } }
""");
    }

    public class VectorSearchFixture : VectorSearchFixtureBase
    {
        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("BuiltInDataTypes");

        public override bool Exact
            => false;
    }
}
