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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using Microsoft.EntityFrameworkCore;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// Wraps an EF Core query so it can be typed as <see cref="IRootQueryable{TEntity}"/>. All behavior is the wrapped query's.
/// </summary>
internal sealed class RootQueryable<TEntity>(IQueryable<TEntity> inner) : IRootQueryable<TEntity>, IAsyncEnumerable<TEntity>
    where TEntity : class
{
    public Type ElementType => inner.ElementType;

    public Expression Expression => inner.Expression;

    public IQueryProvider Provider => inner.Provider;

    public IEnumerator<TEntity> GetEnumerator() => inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IAsyncEnumerator<TEntity> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => ((IAsyncEnumerable<TEntity>)inner).GetAsyncEnumerator(cancellationToken);
}
