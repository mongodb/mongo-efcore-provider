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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// A row of the native one-pass cursor of a tracked query: the entity as constructed while the driver deserialized the batch, plus the
/// tracking still to be done when the enumerator reaches it. <see cref="Deferred"/> is <see langword="null"/> when there is none, or the
/// <see cref="MongoSimpleTracking"/> or <see cref="MongoPendingTracking"/> to replay. (A row that failed to materialize is an empty slot;
/// see <see cref="MongoQueryContext.RowFailed"/>.)
/// </summary>
internal readonly struct MongoMaterializedRow<TEntity>(TEntity? instance, object? deferred)
{
    public TEntity? Instance { get; } = instance;

    public object? Deferred { get; } = deferred;
}
