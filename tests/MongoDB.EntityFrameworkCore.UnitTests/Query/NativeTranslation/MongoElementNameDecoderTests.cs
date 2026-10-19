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
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoElementNameDecoderTests
{
    // Distinct instances, so a returned reference proves the decoder handed back its own canonical string rather than allocating.
    private static string Copy(string value) => new(value.AsSpan());

    private static List<string> ReadNames(IBsonReader reader, INameDecoder decoder)
    {
        var names = new List<string>();
        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            names.Add(reader.ReadName(decoder));
            reader.SkipValue();
        }

        reader.ReadEndDocument();
        return names;
    }

    private static BsonBinaryReader CreateReader(BsonDocument document)
        => new(new MemoryStream(document.ToBson()));

    [Fact]
    public void A_mapped_name_is_returned_as_the_canonical_instance_without_allocating_a_string()
    {
        var prop1 = Copy("Prop1");
        var prop10 = Copy("Prop10");
        var decoder = new MongoElementNameDecoder([prop1, prop10, Copy("_id")]);

        using var reader = CreateReader(new BsonDocument { { "Prop10", 1 }, { "Prop1", 2 } });
        var names = ReadNames(reader, decoder);

        Assert.Same(prop10, names[0]);
        Assert.Same(prop1, names[1]);
    }

    [Fact]
    public void Names_that_are_not_mapped_are_still_decoded_in_full()
    {
        // A prefix of a mapped name, an extension of one, an unrelated name and a non-ASCII one: none may be mistaken for a mapped
        // name or truncated, and the stream must be left positioned correctly for the value that follows.
        var decoder = new MongoElementNameDecoder([Copy("Prop1"), Copy("Prop10")]);

        using var reader = CreateReader(
            new BsonDocument { { "Prop", 1 }, { "Prop100", 2 }, { "Other", 3 }, { "Prop1é", 4 }, { "Prop1", 5 }, { "", 6 } });
        var names = ReadNames(reader, decoder);

        Assert.Equal(["Prop", "Prop100", "Other", "Prop1é", "Prop1", ""], names);
    }

    [Fact]
    public void An_empty_decoder_decodes_every_name()
    {
        var decoder = new MongoElementNameDecoder([]);

        using var reader = CreateReader(new BsonDocument { { "A", 1 }, { "B", 2 } });

        Assert.Equal(["A", "B"], ReadNames(reader, decoder));
    }

    [Fact]
    public void One_decoder_can_serve_many_documents()
    {
        var a = Copy("A");
        var decoder = new MongoElementNameDecoder([a]);

        for (var i = 0; i < 3; i++)
        {
            using var reader = CreateReader(new BsonDocument { { "A", i }, { "Z", i } });
            var names = ReadNames(reader, decoder);
            Assert.Same(a, names[0]);
            Assert.Equal("Z", names[1]);
        }
    }

    [Fact]
    public void Duplicate_names_are_tolerated()
    {
        var decoder = new MongoElementNameDecoder([Copy("A"), Copy("A")]);

        using var reader = CreateReader(new BsonDocument { { "A", 1 } });

        Assert.Equal(["A"], ReadNames(reader, decoder));
    }
}
