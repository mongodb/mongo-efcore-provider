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

using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

[XUnitCollection("SerializationTests")]
public abstract class BaseSerializationTests : IClassFixture<TemporaryDatabaseFixture>
{
    protected readonly TemporaryDatabaseFixture TempDatabase;

    protected BaseSerializationTests(TemporaryDatabaseFixture tempDatabase)
    {
        TempDatabase = tempDatabase;
    }

    protected class BaseIdEntity
    {
        public ObjectId id { get; set; }
    }

    protected IMongoCollection<T> SetupIdOnlyCollection<T>([CallerMemberName] string? methodName = null)
    {
        var collection = TempDatabase.CreateTemporaryCollection<BaseIdEntity>(methodName);
        collection.WriteTestDocs(new[] {new BaseIdEntity()});
        return TempDatabase.MongoDatabase.GetCollection<T>(collection.CollectionNamespace.CollectionName);
    }
}
