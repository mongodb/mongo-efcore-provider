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
using System.Reflection;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

/// <summary>
/// BCL <c>Enumerable.Sum</c> is checked, but <c>$sum</c> silently widens (int32 -&gt; int64 -&gt; double), so
/// <c>DeserializeScalar</c> narrows a widened value back to TResult with checked arithmetic: a total that doesn't fit
/// throws <see cref="OverflowException"/> like BCL's Sum, and one that fits round-trips.
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
    public void Sum_int_narrowing_from_widened_int64_throws_OverflowException()
    {
        Assert.IsType<OverflowException>(
            Assert.Throws<TargetInvocationException>(() => DeserializeScalar<int>(new BsonInt64(4_000_000_000L))).InnerException);
    }

    [Fact]
    public void Sum_int_narrowing_from_in_range_int64_round_trips()
    {
        var result = DeserializeScalar<int>(new BsonInt64(42L));

        Assert.Equal(42, result);
    }

    [Fact]
    public void Sum_long_narrowing_from_widened_double_throws_OverflowException()
    {
        Assert.IsType<OverflowException>(
            Assert.Throws<TargetInvocationException>(() => DeserializeScalar<long>(new BsonDouble(1e20))).InnerException);
    }

    [Fact]
    public void Sum_long_narrowing_from_in_range_double_round_trips_with_precision_loss_accepted()
    {
        var result = DeserializeScalar<long>(new BsonDouble(1e17));

        Assert.Equal((long)1e17, result);
    }
}
