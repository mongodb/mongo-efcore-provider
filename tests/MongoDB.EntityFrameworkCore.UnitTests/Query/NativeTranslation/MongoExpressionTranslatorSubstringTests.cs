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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorSubstringTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void Substring_two_args_translates()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Substring(1, 2);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var substring = Assert.IsType<MongoSubstringExpression>(result);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(substring.Start).Value);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(substring.Length).Value);
    }

    [Fact]
    public void Substring_one_arg_has_null_length()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Substring(1);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        Assert.Null(Assert.IsType<MongoSubstringExpression>(result).Length);
    }
}
