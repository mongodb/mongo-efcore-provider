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

using System.Reflection;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

/// <summary>
/// BCL <c>Enumerable.Sum</c> is checked, but <c>$sum</c> silently widens (int32 -&gt; int64 -&gt; double), so
/// <c>DeserializeScalar</c> may narrow a widened value back to TResult. Pins the accepted divergence: narrowing
/// never throws, though it doesn't reproduce BCL's checked semantics.
/// </summary>
public class DeserializeScalarOverflowTests
{
    private static readonly MethodInfo DeserializeScalarDefinition = typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetMethod("DeserializeScalar", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static TResult DeserializeScalar<TResult>(BsonValue value)
    {
        var doc = new BsonDocument("v", value);
        var method = DeserializeScalarDefinition.MakeGenericMethod(typeof(TResult));
        return (TResult)method.Invoke(null, [doc])!;
    }

    [Fact]
    public void Sum_int_narrowing_from_widened_int64_does_not_throw()
    {
        // An int32 $sum overflow widens to int64; long -> int narrowing is unchecked (modulo 2^32), so the
        // value can be pinned exactly.
        long widened = 4_000_000_000L;
        var result = DeserializeScalar<int>(new BsonInt64(widened));

        Assert.Equal(unchecked((int)widened), result);
    }

    [Fact]
    public void Sum_int_narrowing_from_in_range_int64_round_trips()
    {
        var result = DeserializeScalar<int>(new BsonInt64(42L));

        Assert.Equal(42, result);
    }

    [Fact]
    public void Sum_long_narrowing_from_widened_double_does_not_throw()
    {
        // An int64 $sum overflow widens to double; Convert.ChangeType would throw for this value. Only
        // "doesn't throw" is pinned, not the returned value.
        var exception = Record.Exception(() => DeserializeScalar<long>(new BsonDouble(1e20)));

        Assert.Null(exception);
    }

    [Fact]
    public void Sum_long_narrowing_from_in_range_double_round_trips_with_precision_loss_accepted()
    {
        var result = DeserializeScalar<long>(new BsonDouble(1e17));

        Assert.Equal((long)1e17, result);
    }
}
