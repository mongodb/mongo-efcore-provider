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
/// BCL <c>Enumerable.Sum</c> is checked (throws <see cref="System.OverflowException"/> the moment the
/// running total overflows). Mongo's <c>$sum</c> accumulator instead silently widens (int32 -&gt; int64 -&gt;
/// double), so <c>DeserializeScalar</c> can be asked to narrow a widened accumulator value back down to
/// TResult. These tests pin the accepted divergence: narrowing never throws, even though the returned value
/// does not reproduce BCL's per-element checked semantics.
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
        // A $sum over int32 fields that overflows int32 is widened by the server to int64; the widened
        // total here (4_000_000_000) is itself outside int's range. long -> int narrowing is a well-defined
        // (modulo 2^32) unchecked conversion, so the expected value can be pinned exactly.
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
        // A $sum over int64 fields that overflows int64 is widened by the server to double; a double this
        // far outside long's range would throw OverflowException via Convert.ChangeType. The exact returned
        // value is not part of the contract here (BCL Sum's per-element checked semantics are not
        // reproduced) — only that narrowing completes without throwing.
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
