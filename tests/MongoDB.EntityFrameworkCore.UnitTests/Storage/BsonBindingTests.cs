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
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public class BsonBindingTests
{
    [Fact]
    public void Read_element_returns_value()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = BsonBinding.GetElementValue<int>(document, "property");

        Assert.Equal(12, value);
    }

    [Fact]
    public void Read_missing_element_throws()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var ex = Assert.Throws<InvalidOperationException>(() => BsonBinding.GetElementValue<int>(document, "missedElementName"));

        Assert.Contains("missedElementName", ex.Message);
    }

    [Fact]
    public void Read_missing_nullable_element_returns_default()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = BsonBinding.GetElementValue<int?>(document, "missedElementName");

        Assert.Null(value);
    }
}
