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

using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Factories;

/// <summary>
/// A factory for creating <see cref="MongoQueryContext" /> instances.
/// </summary>
public class MongoQueryContextFactory : IQueryContextFactory
{
    /// <summary>
    /// Create a <see cref="MongoQueryContextFactory"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryContextDependencies"/> passed to each created <see cref="MongoQueryContext" /> instance.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> passed to each created <see cref="MongoQueryContext" /> instance.</param>
    public MongoQueryContextFactory(
        QueryContextDependencies dependencies,
        IMongoClientWrapper mongoClient)
    {
        Dependencies = dependencies;
        Client = mongoClient;
    }

    /// <summary>
    /// The <see cref="QueryContextDependencies"/> passed to each created <see cref="MongoQueryContext"/>.
    /// </summary>
    protected virtual QueryContextDependencies Dependencies { get; }

    /// <summary>
    /// Create a <see cref="MongoQueryContext"/>.
    /// </summary>
    /// <returns>A new <see cref="MongoQueryContext"/>.</returns>
    public virtual QueryContext Create() => new MongoQueryContext(Dependencies, Client);

    /// <summary>
    /// The <see cref="IMongoClientWrapper"/> passed to each created <see cref="MongoQueryContext"/>.
    /// </summary>
    public IMongoClientWrapper Client { get; }
}
