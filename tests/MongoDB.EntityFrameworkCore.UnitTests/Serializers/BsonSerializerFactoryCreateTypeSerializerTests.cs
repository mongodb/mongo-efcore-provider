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
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Serializers;

public class BsonSerializerFactoryCreateTypeSerializerTests
{
    [Fact]
    public void Anonymous_type_gets_class_map_serializer()
    {
        var type = new { A = 1, B = "x" }.GetType();

        AssertClassMapSerializer(type);
    }

    [Fact]
    public void Empty_anonymous_type_gets_class_map_serializer()
    {
        var type = new { }.GetType();

        AssertClassMapSerializer(type);
    }

    [Theory]
    [InlineData(typeof(PlainStruct))]
    [InlineData(typeof(PlainClass))]
    [InlineData(typeof(PlainRecord))]
    public void Concrete_poco_gets_class_map_serializer(Type type)
        => AssertClassMapSerializer(type);

    [Theory]
    [InlineData(typeof(IPlainInterface))]
    [InlineData(typeof(PlainAbstractClass))]
    public void Interface_or_abstract_type_is_not_supported(Type type)
        => Assert.Throws<NotSupportedException>(() => BsonSerializerFactory.CreateTypeSerializer(type));

    private static void AssertClassMapSerializer(Type type)
        => Assert.Equal(
            typeof(BsonClassMapSerializer<>).MakeGenericType(type),
            BsonSerializerFactory.CreateTypeSerializer(type).GetType());

    private struct PlainStruct
    {
        public int A { get; set; }
    }

    private class PlainClass
    {
        public int A { get; set; }
    }

    private record PlainRecord(int A);

    private interface IPlainInterface;

    private abstract class PlainAbstractClass;
}
