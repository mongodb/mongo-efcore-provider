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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// LINQ extension methods over <see cref="IQueryable"/> for the MongoDB EF Core Provider. Note that these methods can only
/// be used with the <see href="https://www.mongodb.com/docs/entity-framework/current/">MongoDB EF Core Provider</see>. They
/// cannot be used directly with the MongoDB C# driver.
/// </summary>
public static class MongoQueryableExtensions
{
    private static readonly MethodInfo VectorSearchMethodInfo
        = typeof(MongoQueryableExtensions)
            .GetTypeInfo().GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(VectorSearch));

    internal static bool IsVectorSearch(this MethodCallExpression methodCallExpression) =>
        methodCallExpression.Method.DeclaringType == typeof(MongoQueryableExtensions)
        && methodCallExpression.Method.IsGenericMethod
        && methodCallExpression.Method.GetGenericMethodDefinition() is var genericMethod
        && genericMethod == VectorSearchMethodInfo;

    /// <summary>
    /// Adds an MongoDB Atlas Vector Search to this LINQ query. This method must be called at the root of an EF query
    /// against MongoDB, except that a <see cref="System.Linq.Queryable.Where{T}(IQueryable{T},Expression{Func{T,bool}})"/>
    /// clause can be used to add a pre-query filter.
    /// </summary>
    /// <remarks>
    /// Note that MongoDB Atlas Vector Search can only be used with MongoDB Atlas, not with other MongoDB configurations.
    /// </remarks>
    /// <typeparam name="TSource">The type of the elements of <paramref name="source" />.</typeparam>
    /// <typeparam name="TProperty">The type of the vector property to search.</typeparam>
    /// <param name="source">The <see cref="IQueryable{T}"/> LINQ expression from EF Core.</param>
    /// <param name="property">The model property mapped to the BSON property containing vectors in the database.</param>
    /// <param name="queryVector">The vector to search with.</param>
    /// <param name="limit">The number of items to limit the vector search to.</param>
    /// <param name="options">An optional <see cref="VectorQueryOptions"/> with options for the search, including the specific index name to use.</param>
    /// <returns>An  <see cref="IQueryable{TSource}"/> that will perform a vector search when executed.</returns>
    public static IQueryable<TSource> VectorSearch<TSource, TProperty>(
        this DbSet<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        QueryVector queryVector,
        int limit,
        VectorQueryOptions? options = null)
        where TSource : class
        => VectorSearch((IQueryable<TSource>)source, property, null, queryVector, limit, options);

    /// <summary>
    /// Adds an MongoDB Atlas Vector Search to this LINQ query. This method must be called at the root of an EF Core query
    /// against MongoDB, except that a <see cref="System.Linq.Queryable.Where{T}(IQueryable{T},Expression{Func{T,bool}})"/>
    /// clause can be used to add a pre-query filter.
    /// </summary>
    /// <remarks>
    /// Note that MongoDB Atlas Vector Search can only be used with MongoDB Atlas, not with other MongoDB configurations.
    /// </remarks>
    /// <typeparam name="TSource">The type of the elements of <paramref name="source" />.</typeparam>
    /// <typeparam name="TProperty">The type of the vector property to search.</typeparam>
    /// <param name="source">The <see cref="IQueryable{T}"/> LINQ expression from EF Core.</param>
    /// <param name="property">The model property mapped to the BSON property containing vectors in the database.</param>
    /// <param name="preFilter">A predicate used to filter out documents before starting vector search.</param>
    /// <param name="queryVector">The vector to search with.</param>
    /// <param name="limit">The number of items to limit the vector search to.</param>
    /// <param name="options">An optional <see cref="VectorQueryOptions"/> with options for the search, including the specific index name to use.</param>
    /// <returns>An  <see cref="IQueryable{TSource}"/> that will perform a vector search when executed.</returns>
    public static IQueryable<TSource> VectorSearch<TSource, TProperty>(
        this DbSet<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        Expression<Func<TSource, bool>>? preFilter,
        QueryVector queryVector,
        int limit,
        VectorQueryOptions? options = null)
        where TSource : class
        => VectorSearch((IQueryable<TSource>)source, property, preFilter, queryVector, limit, options);

    private static IQueryable<TSource> VectorSearch<TSource, TProperty>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        Expression<Func<TSource, bool>>? preFilter,
        QueryVector queryVector,
        int limit,
        VectorQueryOptions? options = null)
        where TSource : class
    {
        Check.IsEfQueryProvider(source);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(queryVector);

        // Place the call in the tree so we can validate arguments later with more context and loggers available.
        return source.Provider.CreateQuery<TSource>(
            Expression.Call(
                instance: null,
                method: VectorSearchMethodInfo.MakeGenericMethod(typeof(TSource), typeof(TProperty)),
                arguments:
                [
                    source.Expression,
                    property,
                    preFilter == null ? Expression.Constant(null, typeof(Expression<Func<TSource, bool>>)) : preFilter,
                    Expression.Constant(queryVector),
                    Expression.Constant(limit),
                    Expression.Constant(options, typeof(VectorQueryOptions?))
                ]));
    }
}
