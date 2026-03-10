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
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class CollectionShaperExpressionTests
{
    [Fact]
    public void Constructor_sets_properties()
    {
        var projection = Expression.Constant(1);
        var innerShaper = Expression.Constant("shaper");

        var expression = new CollectionShaperExpression(projection, innerShaper, null, typeof(int));

        Assert.Same(projection, expression.Projection);
        Assert.Same(innerShaper, expression.InnerShaper);
        Assert.Null(expression.Navigation);
        Assert.Equal(typeof(int), expression.ElementType);
    }

    [Fact]
    public void NodeType_is_Extension()
    {
        var expression = new CollectionShaperExpression(
            Expression.Constant(1), Expression.Constant(2), null, typeof(int));

        Assert.Equal(ExpressionType.Extension, expression.NodeType);
    }

    [Fact]
    public void Type_returns_list_of_element_type_when_no_navigation()
    {
        var expression = new CollectionShaperExpression(
            Expression.Constant(1), Expression.Constant(2), null, typeof(string));

        Assert.Equal(typeof(List<string>), expression.Type);
    }

    [Fact]
    public void Print_outputs_expected_format()
    {
        var projection = Expression.Constant(42);
        var innerShaper = Expression.Constant("inner");
        var expression = new CollectionShaperExpression(projection, innerShaper, null, typeof(int));

        var printer = new ExpressionPrinter();
        ((IPrintableExpression)expression).Print(printer);

        var output = printer.ToString();
        Assert.Contains("CollectionShaper:", output);
    }

    [Fact]
    public void Update_returns_same_instance_when_unchanged()
    {
        var projection = Expression.Constant(1);
        var innerShaper = Expression.Constant(2);
        var expression = new CollectionShaperExpression(projection, innerShaper, null, typeof(int));

        var updated = expression.Update(projection, innerShaper);

        Assert.Same(expression, updated);
    }

    [Fact]
    public void Update_returns_new_instance_when_projection_changed()
    {
        var projection = Expression.Constant(1);
        var innerShaper = Expression.Constant(2);
        var expression = new CollectionShaperExpression(projection, innerShaper, null, typeof(int));

        var newProjection = Expression.Constant(99);
        var updated = expression.Update(newProjection, innerShaper);

        Assert.NotSame(expression, updated);
        Assert.Same(newProjection, updated.Projection);
    }
}
