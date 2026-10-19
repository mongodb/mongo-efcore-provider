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

public class MongoExpressionTranslatorReplaceTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int Count { get; set; }
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        var entityType = db.Model.FindEntityType(typeof(Widget))!;
        return new MongoExpressionTranslator(entityType);
    }

    [Fact]
    public void Replace_strings_translates()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace("Sea", "Rea");

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var replace = Assert.IsType<MongoReplaceExpression>(result);
        Assert.Equal("Sea", Assert.IsType<MongoConstantExpression>(replace.Find).Value);
    }

    [Fact]
    public void Replace_chars_translates_to_one_char_strings()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace('S', 'R');

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var replace = Assert.IsType<MongoReplaceExpression>(result);
        Assert.Equal("R", Assert.IsType<MongoConstantExpression>(replace.Replacement).Value);
    }

    [Fact]
    public void Replace_with_StringComparison_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace("a", "b", StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }
}
