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

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoElementNameDecoder.ReadIndex"/>: the fill loop dispatches on the position of each element name among the mapped names, so the
/// index must agree with <see cref="MongoElementNameDecoder.DistinctNames"/> for a mapped name and be -1 for anything else.
/// </summary>
public class MongoElementNameDecoderIndexTests
{
    private static List<int> ReadIndexes(IBsonReader reader, MongoElementNameDecoder decoder)
    {
        var indexes = new List<int>();
        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            indexes.Add(MongoElementNameDecoder.ReadIndex(reader, decoder));
            reader.SkipValue();
        }

        reader.ReadEndDocument();
        return indexes;
    }

    private static List<int> ReadBinary(BsonDocument document, params string[] mapped)
    {
        using var reader = new BsonBinaryReader(new MemoryStream(document.ToBson()));
        return ReadIndexes(reader, new MongoElementNameDecoder(mapped));
    }

    [Fact]
    public void A_mapped_name_reads_as_its_position_among_the_distinct_names()
        => Assert.Equal(
            [2, 0, 1],
            ReadBinary(new BsonDocument { { "c", 1 }, { "a", 2 }, { "b", 3 } }, "a", "b", "c"));

    [Fact]
    public void An_unmapped_name_reads_as_minus_one()
        => Assert.Equal(
            [0, -1, 1, -1],
            ReadBinary(new BsonDocument { { "a", 1 }, { "zzz", 2 }, { "b", 3 }, { "_id", 4 } }, "a", "b"));

    [Fact]
    public void A_prefix_or_extension_of_a_mapped_name_is_unmapped()
        => Assert.Equal(
            [-1, 0, -1],
            ReadBinary(new BsonDocument { { "Pro", 1 }, { "Prop", 2 }, { "Property", 3 } }, "Prop"));

    [Fact]
    public void A_name_mapped_twice_has_the_index_of_its_first_occurrence()
    {
        Assert.Equal(["a", "b"], MongoElementNameDecoder.DistinctNames(["a", "b", "a"]));
        Assert.Equal([0, 1], ReadBinary(new BsonDocument { { "a", 1 }, { "b", 2 } }, "a", "b", "a"));
    }

    [Fact]
    public void A_reader_that_gives_the_name_to_inform_instead_of_decode_is_matched_by_name()
    {
        using var reader = new BsonDocumentReader(new BsonDocument { { "b", 1 }, { "x", 2 }, { "a", 3 } });
        Assert.Equal([1, -1, 0], ReadIndexes(reader, new MongoElementNameDecoder(["a", "b"])));
    }

    [Fact]
    public void The_index_of_one_thread_is_not_seen_by_another()
    {
        var failures = 0;
        var decoder = new MongoElementNameDecoder(Enumerable.Range(0, 20).Select(i => "n" + i));
        var document = new BsonDocument(Enumerable.Range(0, 20).Select(i => new BsonElement("n" + i, i)));
        var bytes = document.ToBson();

        Parallel.For(0, 8, _ =>
        {
            for (var round = 0; round < 200; round++)
            {
                using var reader = new BsonBinaryReader(new MemoryStream(bytes));
                if (!ReadIndexes(reader, decoder).SequenceEqual(Enumerable.Range(0, 20)))
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        Assert.Equal(0, failures);
    }
}
