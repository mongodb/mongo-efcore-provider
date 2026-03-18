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

using System.Globalization;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Storage.ValueConversion;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage.ValueConversion;

public class DecimalToDecimal128ConverterTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1.23")]
    [InlineData("-456.789")]
    [InlineData("79228162514264337593543950335")]
    public void Can_convert_decimal_to_Decimal128(string value)
    {
        var converter = new DecimalToDecimal128Converter();
        var input = decimal.Parse(value, CultureInfo.InvariantCulture);

        var result = converter.ConvertToProvider(input);

        Assert.Equal(new Decimal128(input), result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.23")]
    [InlineData("-456.789")]
    public void Can_convert_Decimal128_to_decimal(string value)
    {
        var converter = new DecimalToDecimal128Converter();
        var expected = decimal.Parse(value, CultureInfo.InvariantCulture);

        var result = converter.ConvertFromProvider(new Decimal128(expected));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Round_trips_decimal_through_Decimal128()
    {
        var converter = new DecimalToDecimal128Converter();
        var original = 123.456m;

        var asDecimal128 = (Decimal128)converter.ConvertToProvider(original)!;
        var backToDecimal = (decimal)converter.ConvertFromProvider(asDecimal128)!;

        Assert.Equal(original, backToDecimal);
    }

    [Fact]
    public void DefaultInfo_creates_valid_converter()
    {
        var info = DecimalToDecimal128Converter.DefaultInfo;
        var converter = info.Create();

        Assert.NotNull(converter);
        Assert.IsType<DecimalToDecimal128Converter>(converter);
    }
}
