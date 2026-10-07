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

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Serializers;

/// <summary>
/// <see cref="BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames"/>: whether a whole-value alias read of a
/// native sub-document keyed by member names reads each member back (the gate for a server-side ternary over
/// constructions; see <c>NativeProjectionBinder.HasMisreadDocumentConstructionBranch</c>).
/// </summary>
public class BsonSerializerFactoryReadsMembersByNameTests
{
    public class NameOnly
    {
        public string Text { get; set; } = "";
    }

    // The driver's conventions map a member named Id to "_id".
    public class WithId
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class Renamed
    {
        [BsonElement("t")]
        public string Text { get; set; } = "";
    }

    public struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    // Two members mapped to one element name: building the class map throws.
    public class DuplicateElement
    {
        [BsonElement("a")]
        public string First { get; set; } = "";

        [BsonElement("a")]
        public string Second { get; set; } = "";
    }

    [Fact]
    public void Members_read_by_their_own_names()
        => Assert.True(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(NameOnly), ["Text"]));

    [Fact]
    public void Nullable_struct_reads_through_its_underlying_type()
        => Assert.True(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(Point?), ["X", "Y"]));

    [Fact]
    public void Id_mapped_to_underscore_id_is_a_misread()
        => Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(WithId), ["Id", "Name"]));

    [Fact]
    public void Renamed_element_is_a_misread()
        => Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(Renamed), ["Text"]));

    [Fact]
    public void Unmapped_member_is_a_misread()
        => Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(NameOnly), ["Missing"]));

    [Fact]
    public void Class_map_that_fails_to_build_declines_instead_of_throwing()
        => Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(DuplicateElement), ["First"]));

    [Fact]
    public void Non_document_types_are_a_misread()
    {
        Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(string), ["Length"]));
        Assert.False(BsonSerializerFactory.ReadsMembersFromElementsOfTheirOwnNames(typeof(int[]), ["Length"]));
    }
}
