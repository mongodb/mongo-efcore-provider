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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests for the native-translation logical-slot additions on <see cref="MongoQueryExpression"/>.
/// These slots are the "MongoSelectExpression" described in the EF-323 design; they are implemented
/// in-place on <see cref="MongoQueryExpression"/> to avoid churning the QMTEV / shaper / factory
/// plumbing (controller decision).
/// </summary>
public class MongoSelectExpressionTests
{
    private class StubEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static MongoQueryExpression TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType);
    }

    [Fact]
    public void AddPredicateConjunct_ANDs_into_a_single_predicate()
    {
        var select = TestSelect();
        var a = new MongoConstantExpression(true, null);
        var b = new MongoConstantExpression(true, null);

        select.AddPredicateConjunct(a);
        select.AddPredicateConjunct(b);

        var binary = Assert.IsType<MongoBinaryExpression>(select.Predicate);
        Assert.Equal(MongoBinaryOperator.AndAlso, binary.Operator);
    }

    [Fact]
    public void New_select_is_native_representable_by_default()
        => Assert.True(TestSelect().IsNativeRepresentable);
}
