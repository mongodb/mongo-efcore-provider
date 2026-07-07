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
/// Tests for the native-translation logical query IR, <see cref="MongoSelectDefinition"/> (the
/// "MongoSelectExpression" of the EF-323 design), composed into <see cref="MongoQueryExpression"/>
/// via its <see cref="MongoQueryExpression.Select"/> property.
/// </summary>
public class MongoSelectDefinitionTests
{
    private class StubEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static MongoSelectDefinition TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType).Select;
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
