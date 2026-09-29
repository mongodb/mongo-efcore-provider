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

public class MongoExpressionTranslatorCharArgumentTests
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

    [Theory]
    [InlineData("StartsWith", nameof(MongoRegexKind.StartsWith))]
    [InlineData("EndsWith", nameof(MongoRegexKind.EndsWith))]
    [InlineData("Contains", nameof(MongoRegexKind.Contains))]
    public void Char_constant_argument_becomes_a_one_char_string_term(string method, string kindName)
    {
        var kind = Enum.Parse<MongoRegexKind>(kindName);
        var w = Expression.Parameter(typeof(Widget), "w");
        var body = Expression.Call(
            Expression.Property(w, nameof(Widget.Text)),
            typeof(string).GetMethod(method, [typeof(char)])!,
            Expression.Constant('e'));

        Assert.True(BuildTranslator().TryTranslate(body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(kind, regex.Kind);
        Assert.Equal("e", Assert.IsType<MongoConstantExpression>(regex.Term).Value);
    }
}
