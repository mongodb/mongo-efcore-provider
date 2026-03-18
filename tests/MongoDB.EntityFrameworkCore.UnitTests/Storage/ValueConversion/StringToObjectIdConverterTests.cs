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

public class StringToObjectIdConverterTests
{
    [Fact]
    public void Can_convert_string_to_ObjectId()
    {
        var converter = new StringToObjectIdConverter();

        var result = converter.ConvertToProvider("507f191e810c19729de860ea");

        Assert.Equal(ObjectId.Parse("507f191e810c19729de860ea"), result);
    }

    [Fact]
    public void Can_convert_ObjectId_to_string()
    {
        var converter = new StringToObjectIdConverter();
        var objectId = ObjectId.Parse("507f191e810c19729de860ea");

        var result = converter.ConvertFromProvider(objectId);

        Assert.Equal("507f191e810c19729de860ea", result);
    }

    [Fact]
    public void Round_trips_string_through_ObjectId()
    {
        var converter = new StringToObjectIdConverter();
        var original = ObjectId.GenerateNewId().ToString();

        var asObjectId = (ObjectId)converter.ConvertToProvider(original)!;
        var backToString = (string)converter.ConvertFromProvider(asObjectId)!;

        Assert.Equal(original, backToString);
    }

    [Fact]
    public void DefaultInfo_creates_valid_converter()
    {
        var info = StringToObjectIdConverter.DefaultInfo;
        var converter = info.Create();

        Assert.NotNull(converter);
        Assert.IsType<StringToObjectIdConverter>(converter);
    }
}
