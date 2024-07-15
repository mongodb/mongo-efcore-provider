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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

public class TransactionManagerTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public Guid _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void TransactionManager_throws_if_transaction_attempted()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateTemporaryCollection<SimpleEntity>());

        var ex = Assert.Throws<NotSupportedException>(() => db.Database.BeginTransaction());
        Assert.Contains("does not support transactions", ex.Message);
    }
}
