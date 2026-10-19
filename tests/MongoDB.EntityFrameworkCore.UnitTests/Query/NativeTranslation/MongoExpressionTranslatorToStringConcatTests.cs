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

public class MongoExpressionTranslatorToStringConcatTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int Count { get; set; }
        public double Ratio { get; set; }
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        return new MongoExpressionTranslator(db.Model.FindEntityType(typeof(Widget))!);
    }

    [Fact]
    public void Int_ToString_translates_to_toString_convert()
    {
        Expression<Func<Widget, string>> selector = w => w.Count.ToString();

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var convert = Assert.IsType<MongoConvertExpression>(result);
        Assert.Equal(typeof(string), convert.Type);
        Assert.IsType<MongoFieldExpression>(convert.Operand);
    }

    [Fact]
    public void Double_ToString_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Ratio.ToString();

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void Int_ToString_with_format_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Count.ToString("D4");

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void String_Concat_method_flattens_to_one_concat()
    {
        Expression<Func<Widget, string>> selector = w => string.Concat("A", "B", w.Text);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var concat = Assert.IsType<MongoConcatExpression>(result);
        Assert.Equal(3, concat.Operands.Count);
    }
}
