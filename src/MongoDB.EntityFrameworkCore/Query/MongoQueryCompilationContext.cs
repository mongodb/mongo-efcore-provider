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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Query;

/// <inheritdoc />
public class MongoQueryCompilationContext(QueryCompilationContextDependencies dependencies, bool async, MongoQueryMode queryMode)
    : QueryCompilationContext(dependencies, async)
{
    /// <summary>
    /// Creates a <see cref="MongoQueryCompilationContext"/> using the default <see cref="MongoQueryMode.Native"/> query mode.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryCompilationContextDependencies"/> this context depends upon.</param>
    /// <param name="async">Whether the query is asynchronous.</param>
    public MongoQueryCompilationContext(QueryCompilationContextDependencies dependencies, bool async)
        : this(dependencies, async, MongoQueryMode.Native)
    {
    }

    /// <summary>
    /// The original expression that was passed to the query translator.
    /// </summary>
    public Expression? OriginalExpression { get; internal set; }

    /// <summary>
    /// The <see cref="MongoQueryMode"/> that controls how LINQ queries are translated for this compilation.
    /// </summary>
    public MongoQueryMode QueryMode { get; } = queryMode;

    /// <inheritdoc/>
    public override Func<QueryContext, TResult> CreateQueryExecutor<TResult>(Expression query)
    {
        OriginalExpression = query;
        return base.CreateQueryExecutor<TResult>(query);
    }
}
