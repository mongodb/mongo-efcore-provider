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
/// A factory for creating <see cref="MongoShapedQueryCompilingExpressionVisitor" /> instances.
/// </summary>
public class MongoShapedQueryCompilingExpressionVisitorFactory : IShapedQueryCompilingExpressionVisitorFactory
{
    /// <summary>
    /// Create a <see cref="MongoShapedQueryCompilingExpressionVisitorFactory"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> passed to each created <see cref="MongoShapedQueryCompilingExpressionVisitor" /> instance.</param>
    public MongoShapedQueryCompilingExpressionVisitorFactory(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    /// The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> passed to each <see cref="MongoShapedQueryCompilingExpressionVisitor"/> created by this factory.
    /// </summary>
    protected virtual ShapedQueryCompilingExpressionVisitorDependencies Dependencies { get; }

    /// <summary>
    /// Create a new <see cref="MongoShapedQueryCompilingExpressionVisitor"/> with necessary dependencies.
    /// </summary>
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> passed to each newly created <see cref="MongoShapedQueryCompilingExpressionVisitor"/>.</param>
    /// <returns>The newly created <see cref="MongoShapedQueryCompilingExpressionVisitor"/>.</returns>
    public virtual ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext) =>
        new MongoShapedQueryCompilingExpressionVisitor(Dependencies, (MongoQueryCompilationContext)queryCompilationContext);
}
