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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Tests.Query.Expressions;

public static class BsonElementBindingExpressionTests
{
    [Theory]
    [InlineData("some-name", typeof(string))]
    [InlineData("aDifferentName", typeof(ObjectId))]
    public static void Can_set_properties_from_constructor(string elementName, Type type)
    {
        var actual = new BsonElementBindingExpression(elementName, type);

        Assert.Equal(elementName, actual.ElementName);
        Assert.Equal(type, actual.Type);
    }
}
