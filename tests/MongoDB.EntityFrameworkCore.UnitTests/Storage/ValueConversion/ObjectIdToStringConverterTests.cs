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
using MongoDB.EntityFrameworkCore.Storage.ValueConversion;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage.ValueConversion;

public class ObjectIdToStringConverterTests
{
    [Fact]
    public void Can_convert_ObjectId_to_string()
    {
        var converter = new ObjectIdToStringConverter();
        var objectId = ObjectId.Parse("507f1f77bcf86cd799439011");

        var result = converter.ConvertToProvider(objectId);

        Assert.Equal("507f1f77bcf86cd799439011", result);
    }

    [Fact]
    public void Can_convert_string_to_ObjectId()
    {
        var converter = new ObjectIdToStringConverter();

        var result = converter.ConvertFromProvider("507f1f77bcf86cd799439011");

        Assert.Equal(ObjectId.Parse("507f1f77bcf86cd799439011"), result);
    }

    [Fact]
    public void Round_trips_ObjectId_through_string()
    {
        var converter = new ObjectIdToStringConverter();
        var original = ObjectId.GenerateNewId();

        var asString = (string)converter.ConvertToProvider(original)!;
        var backToObjectId = (ObjectId)converter.ConvertFromProvider(asString)!;

        Assert.Equal(original, backToObjectId);
    }

    [Fact]
    public void DefaultInfo_creates_valid_converter()
    {
        var info = ObjectIdToStringConverter.DefaultInfo;
        var converter = info.Create();

        Assert.NotNull(converter);
        Assert.IsType<ObjectIdToStringConverter>(converter);
    }
}
