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

using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public class MongoTransactionManagerTests
{
    [Fact]
    public async Task Does_not_support_transactions()
    {
        var transactionManager = new MongoTransactionManager();

        Assert.Throws<NotSupportedException>(() => transactionManager.BeginTransaction());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await transactionManager.BeginTransactionAsync());

        Assert.Throws<NotSupportedException>(() => transactionManager.CommitTransaction());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await transactionManager.CommitTransactionAsync());

        Assert.Throws<NotSupportedException>(() => transactionManager.RollbackTransaction());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await transactionManager.RollbackTransactionAsync());

        Assert.Null(transactionManager.CurrentTransaction);
        Assert.Null(transactionManager.EnlistedTransaction);

        Assert.Throws<NotSupportedException>(() => transactionManager.EnlistTransaction(null));

        transactionManager.ResetState();
        await transactionManager.ResetStateAsync();

        Assert.Null(transactionManager.CurrentTransaction);
        Assert.Null(transactionManager.EnlistedTransaction);
    }
}
