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

namespace MongoDB.EntityFrameworkCore.UnitTests;

public class ExpressionExtensionMethodsTests
{
    [Fact]
    public void RemoveTypeAs_strips_TypeAs_expression()
    {
        var param = Expression.Parameter(typeof(object), "x");
        var typeAs = Expression.TypeAs(param, typeof(string));

        var result = ExpressionExtensionMethods.RemoveTypeAs(typeAs);

        Assert.Same(param, result);
    }

    [Fact]
    public void RemoveTypeAs_returns_same_for_non_TypeAs()
    {
        var constant = Expression.Constant(42);

        var result = ExpressionExtensionMethods.RemoveTypeAs(constant);

        Assert.Same(constant, result);
    }

    [Fact]
    public void RemoveTypeAs_returns_null_for_null()
    {
        Assert.Null(ExpressionExtensionMethods.RemoveTypeAs(null));
    }

    [Fact]
    public void RemoveConvert_strips_Convert_expression()
    {
        var constant = Expression.Constant(42);
        var convert = Expression.Convert(constant, typeof(long));

        var result = ExpressionExtensionMethods.RemoveConvert(convert);

        Assert.Same(constant, result);
    }

    [Fact]
    public void RemoveConvert_strips_nested_converts()
    {
        var constant = Expression.Constant((short)42);
        var convert1 = Expression.Convert(constant, typeof(int));
        var convert2 = Expression.Convert(convert1, typeof(long));

        var result = ExpressionExtensionMethods.RemoveConvert(convert2);

        Assert.Same(constant, result);
    }

    [Fact]
    public void GetConstantValue_returns_value()
    {
        var expr = Expression.Constant("hello");

        var result = expr.GetConstantValue<string>();

        Assert.Equal("hello", result);
    }

    [Fact]
    public void UnwrapLambdaFromQuote_returns_lambda_from_quote()
    {
        Expression<System.Func<int, bool>> lambda = x => x > 0;
        var quoted = Expression.Quote(lambda);

        var result = ExpressionExtensionMethods.UnwrapLambdaFromQuote(quoted);

        Assert.Same(lambda, result);
    }

    [Fact]
    public void UnwrapLambdaFromQuote_returns_same_if_not_quote()
    {
        Expression<System.Func<int, bool>> lambda = x => x > 0;

        var result = ExpressionExtensionMethods.UnwrapLambdaFromQuote(lambda);

        Assert.Same(lambda, result);
    }
}
