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

using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTests
{
    [Fact]
    public void Binary_node_exposes_operator_and_operands()
    {
        var left = new MongoConstantExpression(1, forSerialization: null);
        var right = new MongoConstantExpression(2, forSerialization: null);
        var bin = new MongoBinaryExpression(MongoBinaryOperator.LessThan, left, right);

        Assert.Equal(MongoBinaryOperator.LessThan, bin.Operator);
        Assert.Same(left, bin.Left);
        Assert.Same(right, bin.Right);
        Assert.Equal(ExpressionType.Extension, bin.NodeType);
    }

    [Fact]
    public void Ordering_carries_key_and_direction()
    {
        var key = new MongoConstantExpression(0, null);
        var ordering = new MongoOrdering(key, Ascending: false);
        Assert.Same(key, ordering.KeySelector);
        Assert.False(ordering.Ascending);
    }
}
