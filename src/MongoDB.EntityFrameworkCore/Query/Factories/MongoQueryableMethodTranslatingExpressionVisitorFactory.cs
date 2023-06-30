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
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Query.Factories;

/// <summary>
/// A factory for creating <see cref="MongoQueryableMethodTranslatingExpressionVisitor" /> instances.
/// </summary>
public class MongoQueryableMethodTranslatingExpressionVisitorFactory : IQueryableMethodTranslatingExpressionVisitorFactory
{
    /// <summary>
    /// Create a <see cref="MongoQueryableMethodTranslatingExpressionVisitorFactory"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryableMethodTranslatingExpressionVisitorDependencies"/> passed to each created <see cref="MongoQueryCompilationContext" /> instance.</param>
    public MongoQueryableMethodTranslatingExpressionVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    /// The <see cref="QueryableMethodTranslatingExpressionVisitorDependencies"/> passed to each <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/> created by this factory.
    /// </summary>
    protected virtual QueryableMethodTranslatingExpressionVisitorDependencies Dependencies { get; }

    /// <summary>
    /// Create a new <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/> with necessary dependencies.
    /// </summary>
    /// <param name="queryCompilationContext">The <see cref="MongoQueryCompilationContext"/> to pass to the new visitor.</param>
    /// <returns>The newly created <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/>.</returns>
    public virtual QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new MongoQueryableMethodTranslatingExpressionVisitor(Dependencies,
            (MongoQueryCompilationContext)queryCompilationContext);
}
