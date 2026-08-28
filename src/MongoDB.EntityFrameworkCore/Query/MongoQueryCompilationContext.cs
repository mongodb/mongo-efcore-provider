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

#if EF8 || EF9
    /// <summary>
    /// (EF8/EF9 only) Whether the ORIGINAL (pre-nav-expansion, pre-normalization) query tree for this
    /// compilation contains at least one raw <c>GroupJoin(...).SelectMany(...)</c> pair anywhere in it -
    /// populated by <see cref="MongoQueryTranslationPreprocessor"/> and consumed by
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsRecognizedGroupJoinFlattenLeftJoin</c> (EF-436)
    /// to decide whether THIS compilation's flattened <c>LeftJoin</c> calls may be admitted.
    /// </summary>
    /// <remarks>
    /// This is a per-QUERY gate, not a per-call one. A per-call check - matching a flattened <c>LeftJoin</c>
    /// call's own key-selector lambdas back to the specific <c>GroupJoin</c> pair that produced it, e.g. by
    /// reference identity - was tried and measured NOT to work: EF Core's nav-expansion reduction renames
    /// every join's key-selector parameters uniformly regardless of origin (a plain <c>c =&gt; c.CustomerID</c>
    /// the user wrote becomes <c>ti =&gt; ti.Outer.CustomerID</c> once expansion finishes), so no per-call
    /// structural or reference signal survives to distinguish a user-authored <c>GroupJoin</c>-flatten
    /// <c>LeftJoin</c> from EF's own internal optional-reference-navigation lowering, which emits the exact
    /// same shim <c>MethodInfo</c> directly. See <c>Query/AGENTS.md</c> for the accepted residual gap this
    /// per-query approximation leaves open.
    /// </remarks>
    internal bool HasGroupJoinFlattenPair { get; set; }
#endif

    /// <inheritdoc/>
    public override Func<QueryContext, TResult> CreateQueryExecutor<TResult>(Expression query)
    {
        OriginalExpression = query;
        return base.CreateQueryExecutor<TResult>(query);
    }
}
