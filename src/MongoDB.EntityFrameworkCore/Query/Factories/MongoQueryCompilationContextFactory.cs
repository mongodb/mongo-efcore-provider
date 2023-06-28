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

namespace MongoDB.EntityFrameworkCore.Query.Factories;

/// <summary>
/// A factory for creating <see cref="MongoQueryCompilationContext" /> instances.
/// </summary>
public class MongoQueryCompilationContextFactory : IQueryCompilationContextFactory
{
    public MongoQueryCompilationContextFactory(QueryCompilationContextDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    /// The <see cref="QueryCompilationContextDependencies"/> to be passed on to each
    /// <see cref="MongoQueryCompilationContext"/> created by this factory.
    /// </summary>
    protected virtual QueryCompilationContextDependencies Dependencies { get; }

    /// <summary>
    /// Create a new <see cref="MongoQueryCompilationContext"/> with the given dependencies.
    /// </summary>
    /// <param name="async">Whether the <see cref="MongoQueryCompilationContext"/> will process
    /// an asynchronous query or not.</param>
    /// <returns></returns>
    public virtual QueryCompilationContext Create(bool async)
        => new MongoQueryCompilationContext(Dependencies, async);
}
