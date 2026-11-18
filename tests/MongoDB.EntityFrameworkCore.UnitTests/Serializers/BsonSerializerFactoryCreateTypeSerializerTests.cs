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
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
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

    [Fact]
    public void Generic_non_enumerable_class_gets_class_map_serializer()
        => AssertClassMapSerializer(typeof(GenericPoco<int>));

    [Fact]
    public void Generic_non_enumerable_class_is_not_a_supported_collection_type()
        => Assert.False(BsonSerializerFactory.IsSupportedCollectionType(typeof(GenericPoco<int>)));

    [Theory]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(IEnumerable<int>))]
    [InlineData(typeof(Memory<int>))]
    [InlineData(typeof(ReadOnlyMemory<int>))]
    public void Collection_types_are_supported_collection_types(Type type)
        => Assert.True(BsonSerializerFactory.IsSupportedCollectionType(type));

    [Theory]
    [InlineData(typeof((int, string)))]
    [InlineData(typeof(Tuple<int, string>))]
    public void Tuple_types_are_not_supported(Type type)
        => Assert.Throws<NotSupportedException>(() => BsonSerializerFactory.CreateTypeSerializer(type));

    [Fact]
    public void Class_map_creation_does_not_register_a_class_map_in_the_driver()
    {
        BsonSerializerFactory.CreateTypeSerializer(typeof(UnregisteredPoco));

        Assert.False(BsonClassMap.IsClassMapRegistered(typeof(UnregisteredPoco)));
        BsonClassMap.RegisterClassMap<UnregisteredPoco>(cm => cm.AutoMap());
    }

    [Fact]
    public void Time_of_day_serializer_reads_milliseconds_as_TimeSpan()
    {
        var serializer = BsonSerializerFactory.CreateTimeOfDaySerializer(typeof(TimeSpan));

        Assert.Same(BsonSerializerFactory.TimeOfDayMillisecondsSerializer, serializer);
    }

    [Fact]
    public void Time_of_day_serializer_wraps_nullable_TimeSpan()
    {
        var serializer = BsonSerializerFactory.CreateTimeOfDaySerializer(typeof(TimeSpan?));

        Assert.IsType<NullableSerializer<TimeSpan>>(serializer);
    }

    [Fact]
    public void Time_of_day_serializer_rejects_other_types()
        => Assert.Throws<ArgumentException>(() => BsonSerializerFactory.CreateTimeOfDaySerializer(typeof(int)));

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

    private class GenericPoco<T>
    {
        public T A { get; set; }
    }

    private class UnregisteredPoco
    {
        public int A { get; set; }
    }

    private interface IPlainInterface;

    private abstract class PlainAbstractClass;
}
