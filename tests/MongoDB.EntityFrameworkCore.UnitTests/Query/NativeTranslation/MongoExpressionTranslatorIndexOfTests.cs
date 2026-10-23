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

public class MongoExpressionTranslatorIndexOfTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        return new MongoExpressionTranslator(db.Model.FindEntityType(typeof(Widget))!);
    }

    [Fact]
    public void IndexOf_char_translates_with_a_one_char_string_needle()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf('e');

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var indexOf = Assert.IsType<MongoStringIndexOfExpression>(result);
        Assert.Equal("e", Assert.IsType<MongoConstantExpression>(indexOf.Needle).Value);
        Assert.Null(indexOf.Start);
    }

    [Fact]
    public void IndexOf_with_start_index_carries_the_start_operand()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf("e", 2);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var indexOf = Assert.IsType<MongoStringIndexOfExpression>(result);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(indexOf.Start).Value);
    }

    [Fact]
    public void IndexOf_with_StringComparison_declines()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf("e", StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }
}
