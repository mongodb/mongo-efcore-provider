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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace (extensions should be in the EF Core namespace for discovery)
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

    // One marker method per mode rather than a mode argument: EF turns a constant argument into a query parameter, which
    // would hide the mode from the compiled-query cache key. The method identity is part of the key.
    private static readonly Dictionary<MethodInfo, MongoQueryProvider> QueryProviderMarkers
        = typeof(MongoQueryableExtensions)
            .GetTypeInfo().GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(mi => mi.Name.StartsWith("QueryProviderMarker", StringComparison.Ordinal))
            .ToDictionary(mi => mi, mi => Enum.Parse<MongoQueryProvider>(mi.Name["QueryProviderMarker".Length..]));

    internal static bool TryGetWithQueryProvider(this MethodCallExpression methodCallExpression, out MongoQueryProvider queryProvider)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(MongoQueryableExtensions)
            && method.IsGenericMethod
            && QueryProviderMarkers.TryGetValue(method.GetGenericMethodDefinition(), out queryProvider))
        {
            return true;
        }

        queryProvider = default;
        return false;
    }

    internal static bool IsVectorSearch(this MethodCallExpression methodCallExpression) =>
        methodCallExpression.Method.DeclaringType == typeof(MongoQueryableExtensions)
        && methodCallExpression.Method.IsGenericMethod
        && methodCallExpression.Method.GetGenericMethodDefinition() is var genericMethod
        && genericMethod == VectorSearchMethodInfo;

    /// <summary>
    /// Walks a captured <see cref="System.Linq.Queryable"/> method chain through each call's source argument
    /// looking for a <c>VectorSearch</c> call (at the root, optionally under a single pre-<c>Where</c>).
    /// </summary>
    /// <remarks>
    /// Shared by the compile-time gate and the method translator's set-operation checks. Answered from the
    /// captured chain, not from a flag on the select tree.
    /// </remarks>
    internal static bool ContainsVectorSearch(this Expression? captured)
    {
        while (captured is MethodCallExpression call)
        {
            if (call.IsVectorSearch())
            {
                return true;
            }

            captured = call.Arguments.Count > 0 ? call.Arguments[0] : null;
        }

        return false;
    }

    /// <summary>
    /// Adds a MongoDB Atlas Vector Search to this LINQ query. This method must be called at the root of an EF Core query
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
    /// Adds a MongoDB Atlas Vector Search to this LINQ query. This method must be called at the root of an EF Core query
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

    /// <summary>
    /// Sets the <see cref="MongoQueryProvider"/> used to translate this query, overriding the mode configured for the context
    /// with <see cref="MongoDbContextOptionsBuilder.UseQueryProvider"/>, in either direction. For example, a query can run through
    /// the driver's LINQ provider while the rest of the context uses native translation, or can require native translation
    /// with <see cref="MongoQueryProvider.NativeOnly"/> while the rest of the context allows fallback.
    /// </summary>
    /// <remarks>
    /// This method is only available on a <see cref="DbSet{TEntity}"/>, so it is applied at the root of the query. If a query
    /// has several roots, such as a join, <c>WithQueryProvider</c> can be used on more than one of them only if
    /// they specify the same mode.
    /// </remarks>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <param name="source">The <see cref="DbSet{TEntity}"/> at the root of the query.</param>
    /// <param name="queryProvider">The <see cref="MongoQueryProvider"/> to use for this query.</param>
    /// <returns>An <see cref="IQueryable{TEntity}"/> that will be translated using <paramref name="queryProvider"/>.</returns>
    public static IQueryable<TEntity> WithQueryProvider<TEntity>(this DbSet<TEntity> source, MongoQueryProvider queryProvider)
        where TEntity : class
        => WithQueryProvider((IQueryable<TEntity>)source, queryProvider);

    private static IQueryable<TEntity> WithQueryProvider<TEntity>(this IQueryable<TEntity> source, MongoQueryProvider queryProvider)
        where TEntity : class
    {
        Check.IsEfQueryProvider(source);

        var marker = queryProvider switch
        {
            MongoQueryProvider.Native => nameof(QueryProviderMarkerNative),
            MongoQueryProvider.DriverLinq => nameof(QueryProviderMarkerDriverLinq),
            MongoQueryProvider.NativeOnly => nameof(QueryProviderMarkerNativeOnly),
            _ => throw new ArgumentOutOfRangeException(nameof(queryProvider), queryProvider, null)
        };

        var method = typeof(MongoQueryableExtensions)
            .GetMethod(marker, BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(TEntity));

        // Place the marker in the tree; it is consumed when the query is compiled.
        return source.Provider.CreateQuery<TEntity>(Expression.Call(instance: null, method: method, source.Expression));
    }

    private static IQueryable<TEntity> QueryProviderMarkerNative<TEntity>(this IQueryable<TEntity> source) => Marker(source);

    private static IQueryable<TEntity> QueryProviderMarkerDriverLinq<TEntity>(this IQueryable<TEntity> source) => Marker(source);

    private static IQueryable<TEntity> QueryProviderMarkerNativeOnly<TEntity>(this IQueryable<TEntity> source) => Marker(source);

    private static IQueryable<TEntity> Marker<TEntity>(IQueryable<TEntity> source)
        => throw new InvalidOperationException($"{nameof(WithQueryProvider)} can only be used in a query run by the MongoDB EF Core provider.");
}
