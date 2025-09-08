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
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using ExpressionVisitor = System.Linq.Expressions.ExpressionVisitor;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// LINQ extension methods over <see cref="IQueryable"/> for the MongoDB EF Core Provider. Note that these methods can only
/// be used with the <see href="https://www.mongodb.com/docs/entity-framework/current/">MongoDB EF Core Provider</see>. They
/// cannot be used directly with the MongoDB C# driver.
/// </summary>
public static class MongoQueryableExtensions
{
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
        this IQueryable<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        QueryVector queryVector,
        int limit,
        VectorQueryOptions? options = null)
        => source.VectorSearch(property, null!, queryVector, limit, options);

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
        this IQueryable<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        Expression<Func<TSource, bool>> preFilter,
        QueryVector queryVector,
        int limit,
        VectorQueryOptions? options = null)
    {
        Check.IsEfQueryProvider(source);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(queryVector);

        var concreteOptions = options ?? new();

        if (concreteOptions is { NumberOfCandidates: not null, Exact: true })
        {
            throw new ArgumentException(
                "The option 'Exact' is set to 'true', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set. Either 'NumberOfCandidates' or 'Exact' can be set, but not both.");
        }

        var members = property.GetMemberAccess<MemberInfo>();
        var entityType = EntityRootFindingVisitor.FindRoot(source.Expression).EntityType;
        var memberMetadata = entityType.FindMember(members[0].Name);

        if (memberMetadata == null)
        {
            throw new InvalidOperationException(
                $"Could not create a vector query for '{typeof(TSource).ShortDisplayName()}.{members[0].Name}'. Make sure the entity type is included in the EF Core model and that the property or field is mapped.");
        }

        var path = memberMetadata.Name;
        foreach (var memberInfo in members.Skip(1))
        {
            memberMetadata = (memberMetadata as INavigation)?.TargetEntityType.FindMember(memberInfo.Name);
            path += $".{memberInfo.Name}";
        }

        var vectorIndexesInModel = memberMetadata?.DeclaringType.ContainingEntityType
            .GetIndexes().Where(i => i.GetVectorIndexOptions() != null && i.Properties[0] == memberMetadata).ToList();

        if (concreteOptions.IndexName == null)
        {
            // Index to use was not specified in the query. Throw if there is anything but one index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
            {
                ThrowForBadOptions(
                    "there are no vector indexes defined for this property in the EF Core model. Use 'HasIndex' on the EF model builder to define an index.");
            }

            if (vectorIndexesInModel!.Count > 1)
            {
                ThrowForBadOptions(
                    "multiple vector indexes are defined for this property in the EF Core model. Specify the index to use in the call to 'VectorSearch'.");
            }

            // There is only one index and none was specified, so use that index.
            concreteOptions = concreteOptions with { IndexName = vectorIndexesInModel[0].Name };
        }
        else
        {
            // Index to use was specified in the query. Throw if it doesn't match any index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
            {
                ThrowForBadOptions(
                    $"vector index '{concreteOptions.IndexName}' was not defined in the EF Core model. Use 'HasIndex' on the EF model builder to specify the index, or disable this warning if you have created your MongoDB indexes outside of EF Core.");
            }

            if (vectorIndexesInModel!.All(i => i.Name != concreteOptions.IndexName))
            {
                ThrowForBadOptions(
                    $"vector index '{concreteOptions.IndexName}' was not defined in the EF Core model. Vector query searches must use one of the indexes defined on the EF Core model.");
            }
            // Index name in query already matches, so just continue.
        }

        var searchOptions = new VectorSearchOptions<TSource>
        {
            IndexName = concreteOptions.IndexName,
            NumberOfCandidates = concreteOptions.NumberOfCandidates,
            Exact = concreteOptions.Exact,
        };

        if (preFilter != null!)
        {
            searchOptions.Filter = preFilter;
        }

        return source.AppendStage(PipelineStageDefinitionBuilder.VectorSearch(property, queryVector, limit, searchOptions));

        void ThrowForBadOptions(string reason)
        {
            throw new InvalidOperationException(
                $"A vector query for '{entityType.DisplayName()}.{members[0].Name}' could not be executed because {reason}");
        }
    }

    private sealed class EntityRootFindingVisitor : ExpressionVisitor
    {
        public static EntityQueryRootExpression FindRoot(Expression? node)
        {
            var visitor = new EntityRootFindingVisitor();
            visitor.Visit(node);

            Debug.Assert(visitor.RootExpression != null, "EF Core query has null root expression.");

            return visitor.RootExpression;
        }

        private EntityRootFindingVisitor()
        {
        }

        private EntityQueryRootExpression? RootExpression { get; set; }

        public override Expression? Visit(Expression? node)
        {
            if (node is EntityQueryRootExpression entityQueryRootExpression)
            {
                RootExpression = entityQueryRootExpression;
            }
            return base.Visit(node);
        }
    }
}
