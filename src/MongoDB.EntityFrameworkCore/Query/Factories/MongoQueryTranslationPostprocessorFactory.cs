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
/// A factory for creating <see cref="MongoQueryTranslationPostprocessor" /> instances.
/// </summary>
public class MongoQueryTranslationPostprocessorFactory : IQueryTranslationPostprocessorFactory
{
    /// <summary>
    /// Create a <see cref="MongoQueryTranslationPostprocessorFactory"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryTranslationPostprocessorDependencies"/> passed to each created <see cref="MongoQueryTranslationPostprocessor" /> instance.</param>
    public MongoQueryTranslationPostprocessorFactory(
        QueryTranslationPostprocessorDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    /// The <see cref="QueryTranslationPostprocessorDependencies"/> passed to each <see cref="MongoQueryTranslationPostprocessor"/> created by this factory.
    /// </summary>
    protected virtual QueryTranslationPostprocessorDependencies Dependencies { get; }

    /// <summary>
    /// Create a new <see cref="MongoQueryTranslationPostprocessor"/> with the necessary dependencies.
    /// </summary>
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> to pass to the new <see cref="MongoQueryTranslationPostprocessorFactory"/>.</param>
    /// <returns>The newly created <see cref="MongoQueryTranslationPostprocessorFactory"/>.</returns>
    public virtual QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new MongoQueryTranslationPostprocessor(Dependencies, queryCompilationContext);
}
