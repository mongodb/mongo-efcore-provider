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

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Provides the contextual information for a given query such as which client should execute it and using which dependencies.
/// </summary>
public class MongoQueryContext : QueryContext
{
    /// <summary>
    /// Create a <see cref="MongoQueryContext"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryContextDependencies"/> this context specifies.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> to use in executing this query.</param>
    public MongoQueryContext(
        QueryContextDependencies dependencies,
        IMongoClientWrapper mongoClient)
        : base(dependencies)
    {
        MongoClient = mongoClient;
    }

    /// <summary>
    /// The <see cref="IMongoClientWrapper"/> this query should be executed using.
    /// </summary>
    public virtual IMongoClientWrapper MongoClient { get; }

    public override InternalEntityEntry? TryGetEntry(IKey key, object[] keyValues, bool throwOnNullKey,
        [UnscopedRef] out bool hasNullKey)
    {
        return base.TryGetEntry(key, keyValues, throwOnNullKey, out hasNullKey);
    }
}
