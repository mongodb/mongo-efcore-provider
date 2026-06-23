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

// On EF8 the provider does not implement ExecuteDelete/ExecuteUpdate (the whole bulk path is #if !EF8),
// so EF Core reports the operation as untranslatable. This pins that clean-failure boundary. On EF9+ the
// feature is implemented and exercised by ExecuteDeleteTests / ExecuteUpdateTests instead.
#if EF8

using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class BulkOperationsUnsupportedOnEf8Tests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class SimpleEntity
    {
        public ObjectId _id { get; set; }
    }

    [Fact]
    public void ExecuteDelete_throws_translation_failure()
    {
        using var db = SingleEntityDbContext.Create(database.CreateCollection<SimpleEntity>());

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.ExecuteDelete());
        Assert.Contains("could not be translated", ex.Message);
    }
}

#endif
