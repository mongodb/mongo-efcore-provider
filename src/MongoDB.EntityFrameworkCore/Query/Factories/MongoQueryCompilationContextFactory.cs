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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Query.Factories;

/// <summary>
/// A factory for creating <see cref="MongoQueryCompilationContext" /> instances.
/// </summary>
public class MongoQueryCompilationContextFactory : IQueryCompilationContextFactory
{
    private readonly MongoQueryMode _queryMode;

    /// <summary>
    /// Create a <see cref="MongoQueryContextFactory"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryCompilationContextDependencies"/> passed to each created <see cref="MongoQueryCompilationContext" /> instance.</param>
    /// <param name="dbContextOptions">The <see cref="IDbContextOptions"/> used to resolve the configured <see cref="MongoQueryMode"/>.</param>
    public MongoQueryCompilationContextFactory(
        QueryCompilationContextDependencies dependencies,
        IDbContextOptions dbContextOptions)
    {
        Dependencies = dependencies;
        _queryMode = dbContextOptions.FindExtension<MongoOptionsExtension>()?.QueryMode ?? MongoQueryMode.Native;
    }

    /// <summary>
    /// The <see cref="QueryCompilationContextDependencies"/> passed to each <see cref="MongoQueryCompilationContext"/> created by this factory.
    /// </summary>
    protected virtual QueryCompilationContextDependencies Dependencies { get; }

    /// <summary>
    /// Create a new <see cref="MongoQueryCompilationContext"/> with the necessary dependencies.
    /// </summary>
    /// <param name="async"><see langword="true"/> if the query to process is asynchronous, <see langword="false"/> if it is synchronous.</param>
    /// <returns>The newly created <see cref="MongoQueryCompilationContext"/>.</returns>
    public virtual QueryCompilationContext Create(bool async)
        => new MongoQueryCompilationContext(Dependencies, async, _queryMode);
}
