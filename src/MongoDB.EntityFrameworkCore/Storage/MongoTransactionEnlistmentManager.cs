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
using System.Transactions;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Class to throw if ambient transactions such as those provided by
/// <see cref="Transaction.Current"/> are attempted.
/// </summary>
public class MongoTransactionEnlistmentManager : ITransactionEnlistmentManager
{
    /// <inheritdoc />
    public void EnlistTransaction(Transaction? transaction)
        => throw new NotSupportedException(
            "The MongoDB EF Provider does not support ambient transactions. Consider explicit (Database.BeginTransaction) or implicit (DbContext.SaveChanges) transactions instead.");

    /// <inheritdoc />
    public Transaction? EnlistedTransaction
        => null;
}
