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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

internal static class ExtensionMethods
{
    private static int NextInt32(this Random random)
        => random.Next(0, 1 << 4) << 28 | random.Next(0, 1 << 28);

    // From John Skeet's answer at https://stackoverflow.com/questions/609501/generating-a-random-decimal-in-c-sharp
    public static decimal NextDecimal(this Random random)
        => new(random.NextInt32(),
            random.NextInt32(),
            random.NextInt32(),
            random.Next(2) == 1,
            (byte)random.Next(29));

    public static byte NextByte(this Random random)
        => (byte)random.Next(0, byte.MaxValue);

    public static short NextInt16(this Random random)
        => (short)random.Next(1, short.MaxValue);

    public static string ToExpectedPrecision(this DateTime dateTime)
        =>  dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff");

}
