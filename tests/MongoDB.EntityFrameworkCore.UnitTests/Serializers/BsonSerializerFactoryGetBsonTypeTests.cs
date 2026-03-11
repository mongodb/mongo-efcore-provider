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
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Serializers;

public class BsonSerializerFactoryGetBsonTypeTests
{
    [Theory]
    [InlineData(typeof(bool), BsonType.Boolean)]
    [InlineData(typeof(byte), BsonType.Int32)]
    [InlineData(typeof(char), BsonType.Int32)]
    [InlineData(typeof(DateTime), BsonType.DateTime)]
    [InlineData(typeof(DateTimeOffset), BsonType.Document)]
    [InlineData(typeof(DateOnly), BsonType.DateTime)]
    [InlineData(typeof(TimeOnly), BsonType.Int64)]
    [InlineData(typeof(decimal), BsonType.Decimal128)]
    [InlineData(typeof(double), BsonType.Double)]
    [InlineData(typeof(Guid), BsonType.Binary)]
    [InlineData(typeof(short), BsonType.Int32)]
    [InlineData(typeof(int), BsonType.Int32)]
    [InlineData(typeof(long), BsonType.Int64)]
    [InlineData(typeof(ObjectId), BsonType.ObjectId)]
    [InlineData(typeof(TimeSpan), BsonType.String)]
    [InlineData(typeof(sbyte), BsonType.Int32)]
    [InlineData(typeof(float), BsonType.Double)]
    [InlineData(typeof(string), BsonType.String)]
    [InlineData(typeof(ushort), BsonType.Int32)]
    [InlineData(typeof(uint), BsonType.Int32)]
    [InlineData(typeof(ulong), BsonType.Int64)]
    [InlineData(typeof(Decimal128), BsonType.Decimal128)]
    public void Primitive_types_map_to_expected_BsonType(Type clrType, BsonType expected)
    {
        Assert.Equal(expected, BsonSerializerFactory.GetBsonType(clrType));
    }

    [Fact]
    public void ByteArray_maps_to_Binary()
    {
        Assert.Equal(BsonType.Binary, BsonSerializerFactory.GetBsonType(typeof(byte[])));
    }

    [Fact]
    public void Int_enum_maps_to_Int32()
    {
        Assert.Equal(BsonType.Int32, BsonSerializerFactory.GetBsonType(typeof(IntEnum)));
    }

    [Fact]
    public void Long_enum_maps_to_Int64()
    {
        Assert.Equal(BsonType.Int64, BsonSerializerFactory.GetBsonType(typeof(LongEnum)));
    }

    [Fact]
    public void Short_enum_maps_to_Int32()
    {
        Assert.Equal(BsonType.Int32, BsonSerializerFactory.GetBsonType(typeof(ShortEnum)));
    }

    [Fact]
    public void Array_maps_to_Array()
    {
        Assert.Equal(BsonType.Array, BsonSerializerFactory.GetBsonType(typeof(int[])));
    }

    [Fact]
    public void Nullable_int_maps_to_Int32()
    {
        Assert.Equal(BsonType.Int32, BsonSerializerFactory.GetBsonType(typeof(int?)));
    }

    [Fact]
    public void Nullable_decimal_maps_to_Decimal128()
    {
        Assert.Equal(BsonType.Decimal128, BsonSerializerFactory.GetBsonType(typeof(decimal?)));
    }

    [Fact]
    public void Dictionary_maps_to_Document()
    {
        Assert.Equal(BsonType.Document, BsonSerializerFactory.GetBsonType(typeof(Dictionary<string, int>)));
    }

    [Fact]
    public void Generic_list_maps_to_Array()
    {
        Assert.Equal(BsonType.Array, BsonSerializerFactory.GetBsonType(typeof(List<int>)));
    }

    [Fact]
    public void Unknown_class_maps_to_Document()
    {
        Assert.Equal(BsonType.Document, BsonSerializerFactory.GetBsonType(typeof(BsonSerializerFactoryGetBsonTypeTests)));
    }

    private enum IntEnum { A, B }
    private enum LongEnum : long { A, B }
    private enum ShortEnum : short { A, B }
}
