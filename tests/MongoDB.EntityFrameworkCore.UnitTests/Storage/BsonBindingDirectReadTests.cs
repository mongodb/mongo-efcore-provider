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
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

// BsonBinding reads a value directly (no temporary document and reader) when it is stored exactly as the serializer writes it by default.
// The shortcut must be unobservable: every read here is compared with the driver's own BsonSerializationInfo.DeserializeValue, for
// the default case and for the cases that must still go through the serializer (another BSON type, a non-default representation).
public class BsonBindingDirectReadTests
{
    private static BsonDocument Doc(BsonValue value) => new("v", value);

    // What the driver does with the same element and serializer.
    private static T Reference<T>(BsonValue value, IBsonSerializer serializer)
        => (T)new BsonSerializationInfo("v", serializer, typeof(T)).DeserializeValue(value);

    private static void AssertSameAsDriver<T>(BsonValue value, IBsonSerializer? serializer = null)
    {
        serializer ??= BsonSerializerFactory.CreateTypeSerializer(typeof(T));

        T expected;
        try
        {
            expected = Reference<T>(value, serializer);
        }
        catch (Exception expectedException)
        {
            // The shortcut must not engage, so the serializer's exception surfaces unchanged.
            var actualException = Assert.ThrowsAny<Exception>(
                () => BsonBinding.GetKindAwareElementValue<T>(Doc(value), "v", serializer));
            Assert.Equal(expectedException.GetType(), actualException.GetType());
            return;
        }

        Assert.Equal(expected, BsonBinding.GetKindAwareElementValue<T>(Doc(value), "v", serializer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Int32_matches_the_driver(int value)
    {
        AssertSameAsDriver<int>(new BsonInt32(value));
        AssertSameAsDriver<int?>(new BsonInt32(value));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1_000_000_000_042L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Int64_matches_the_driver(long value)
    {
        AssertSameAsDriver<long>(new BsonInt64(value));
        AssertSameAsDriver<long?>(new BsonInt64(value));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.MaxValue)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Double_matches_the_driver(double value)
    {
        AssertSameAsDriver<double>(new BsonDouble(value));
        AssertSameAsDriver<double?>(new BsonDouble(value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Boolean_matches_the_driver(bool value)
    {
        AssertSameAsDriver<bool>(new BsonBoolean(value));
        AssertSameAsDriver<bool?>(new BsonBoolean(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("flat-42")]
    [InlineData("héllo ☃")]
    public void String_matches_the_driver(string value)
        => AssertSameAsDriver<string>(new BsonString(value));

    [Fact]
    public void ObjectId_matches_the_driver()
    {
        var id = ObjectId.GenerateNewId();
        AssertSameAsDriver<ObjectId>(new BsonObjectId(id));
        AssertSameAsDriver<ObjectId?>(new BsonObjectId(id));
        AssertSameAsDriver<ObjectId>(new BsonObjectId(ObjectId.Empty));
    }

    [Fact]
    public void Values_stored_as_another_bson_type_still_convert_through_the_serializer()
    {
        AssertSameAsDriver<int>(new BsonInt64(7));            // widening/narrowing the driver allows
        AssertSameAsDriver<int>(new BsonInt64(long.MaxValue)); // overflow: the driver throws
        AssertSameAsDriver<int>(new BsonDouble(3.0));
        AssertSameAsDriver<int>(new BsonDouble(3.5));          // truncation: the driver throws
        AssertSameAsDriver<long>(new BsonInt32(7));
        AssertSameAsDriver<double>(new BsonInt32(7));
        AssertSameAsDriver<double>(new BsonInt64(7));
        AssertSameAsDriver<bool>(new BsonInt32(1));
        AssertSameAsDriver<string>(new BsonInt32(7));          // the driver throws
        AssertSameAsDriver<string>(BsonSymbolTable.Lookup("sym"));
        AssertSameAsDriver<ObjectId>(new BsonString(ObjectId.GenerateNewId().ToString()));
    }

    [Fact]
    public void Non_default_representations_still_go_through_the_serializer()
    {
        // An int stored as a string, a long as a string, a string as an ObjectId, an ObjectId as a string: the bson type matches
        // the CLR type's default only when the serializer's representation says so.
        AssertSameAsDriver<int>(new BsonString("12"), new Int32Serializer(BsonType.String));
        AssertSameAsDriver<long>(new BsonString("12"), new Int64Serializer(BsonType.String));
        AssertSameAsDriver<double>(new BsonString("1.5"), new DoubleSerializer(BsonType.String));
        AssertSameAsDriver<int>(new BsonInt32(12), new Int32Serializer(BsonType.Int64)); // reads an int32 for an int64 representation
        AssertSameAsDriver<string>(
            new BsonObjectId(ObjectId.GenerateNewId()), new StringSerializer(BsonType.ObjectId));
        AssertSameAsDriver<ObjectId>(
            new BsonString(ObjectId.GenerateNewId().ToString()), new ObjectIdSerializer(BsonType.String));
        AssertSameAsDriver<bool?>(new BsonBoolean(true), new NullableSerializer<bool>(new BooleanSerializer()));
    }

    [Fact]
    public void Missing_and_null_elements_are_unaffected()
    {
        Assert.Equal(0, BsonBinding.GetElementValue<int?>(new BsonDocument("v", BsonNull.Value), "v") ?? 0);
        Assert.Null(BsonBinding.GetElementValue<int?>(new BsonDocument("other", 1), "v"));
        Assert.Throws<InvalidOperationException>(() => BsonBinding.GetElementValue<int>(new BsonDocument("other", 1), "v"));
    }
}
