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

using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoSetOperationRouteTests
{
    private static MongoSelectDefinition OperandSelect()
        => new();

    [Fact]
    public void SetOperation_keeps_WholeEntity_route()
    {
        var select = new MongoSelectDefinition
        {
            SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, OperandSelect(), "customers"),
            IsSetOp = true
        };
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
    }

    [Fact]
    public void IsSetOp_marks_HasTerminalOperator()
    {
        var select = new MongoSelectDefinition { IsSetOp = true };
        Assert.True(select.HasTerminalOperator);
    }

    [Fact]
    public void No_terminal_operator_by_default()
        => Assert.False(new MongoSelectDefinition().HasTerminalOperator);

    [Fact]
    public void SetOperation_holds_kind_operand_and_collection()
    {
        var operand = OperandSelect();
        var setOp = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "orders");
        Assert.Equal(MongoSetOperationKind.Concat, setOp.Kind);
        Assert.Same(operand, setOp.OperandSelect);
        Assert.Equal("orders", setOp.OperandCollectionName);
    }

    [Fact]
    public void UnwindSource_marks_HasTerminalOperator()
    {
        var select = new MongoSelectDefinition { UnwindSource = MongoUnwindSource.Owned("Items", innerEntityType: null!) };
        Assert.True(select.HasTerminalOperator);
    }
}
