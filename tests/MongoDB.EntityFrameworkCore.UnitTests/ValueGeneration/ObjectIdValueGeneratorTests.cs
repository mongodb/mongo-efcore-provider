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
using MongoDB.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.UnitTests.ValueGeneration;

public class ObjectIdValueGeneratorTests
{
    [Fact]
    public void Generates_non_temporary_ObjectIds()
    {
        var generator = new ObjectIdValueGenerator();
        Assert.False(generator.GeneratesTemporaryValues);
    }

    [Fact]
    public void Next_generates_unique_ObjectId()
    {
        const int loops = 100;
        var generator = new ObjectIdValueGenerator();
        var values = new HashSet<ObjectId>(loops);

        for (int i = 0; i < loops; i++)
            values.Add(generator.Next(null!));

        Assert.Equal(loops, values.Count);
    }
}
