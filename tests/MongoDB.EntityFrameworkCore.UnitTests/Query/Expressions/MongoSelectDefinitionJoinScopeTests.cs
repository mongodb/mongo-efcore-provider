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

using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class MongoSelectDefinitionJoinScopeTests
{
    private class Outer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Inner
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    [Fact]
    public void HasTerminalOperator_stays_false_when_only_a_join_scope_is_set()
    {
        // JoinScope is metadata recorded for every eligible join, including Include-shaped ones; counting it
        // as a terminal would block reference-Include confirmation.
        var select = new MongoSelectDefinition();
        Assert.False(select.HasTerminalOperator);

        var (outerEntityType, innerEntityType) = BuildTwoEntityTypes();
        select.JoinScope = new MongoJoinScope(
            outerEntityType, [new MongoJoinScopeLevel(innerEntityType, innerPrefix: "_lookup_Orders", isLeftOuter: false)]);

        Assert.False(select.HasTerminalOperator);
    }

    private static (IEntityType, IEntityType) BuildTwoEntityTypes()
    {
        using var db = SingleEntityDbContext.Create<Outer>();
        using var dbInner = SingleEntityDbContext.Create<Inner>();
        var outerType = db.Model.FindEntityType(typeof(Outer))!;
        var innerType = dbInner.Model.FindEntityType(typeof(Inner))!;
        return (outerType, innerType);
    }
}
