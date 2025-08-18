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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extensions to <see cref="IQueryable"/> for the MongoDB EF Core Provider.
/// </summary>
/// <remarks>
/// Some of these are duplicates of what is exposed in the MongoDB C# Driver extensions. They are exposed here
/// to avoid conflicts with the Async overloads present in the MongoDB C# Driver extensions/namespace versions
/// that conflict with EF Core.
/// </remarks>
public static class MongoQueryableExtensions
{
    /// <summary>
    /// Appends a $vectorSearch stage to an <see cref="IQueryable{T}"/> LINQ pipeline.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements of <paramref name="source" />.</typeparam>
    /// <typeparam name="TProperty">The type of the vector property to search.</typeparam>
    /// <param name="source">The <see cref="IQueryable{T}"/> LINQ pipeline to append to.</param>
    /// <param name="property">The property containing the vectors in the source.</param>
    /// <param name="queryVector">The vector to search with - typically an array of floats.</param>
    /// <param name="limit">The number of items to limit the vector search to.</param>
    /// <param name="options">An optional <see cref="VectorSearchOptions{TDocument}"/> containing additional filters, index names etc.</param>
    /// <returns>
    /// The <see cref="IQueryable{TSource}"/> with the $vectorSearch stage appended.
    /// </returns>
    public static IQueryable<TSource> VectorSearch<TSource, TProperty>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TProperty>> property,
        QueryVector queryVector,
        int limit,
        VectorSearchOptions? options = null)
    {
        options ??= new();

        var visitor = new EntityRootFindingVisitor();
        visitor.Visit(source.Expression);

        if (visitor.RootExpression == null)
        {
            throw new InvalidOperationException($"Cannot execute a VectorSearch on a non-EF Core IQueryable.");
        }

        var members = GetMemberAccess<MemberInfo>(property);
        var entityType = visitor.RootExpression.EntityType;
        var memberMetadata = entityType?.FindMember(members[0].Name);

        if (memberMetadata == null)
        {
            throw new InvalidOperationException(
                $"Could not create a vector query for '{typeof(TSource).ShortDisplayName()}.{members[0].Name}'. Make sure the entity type is included in the EF model and that the property or field is mapped.");
        }

        foreach (var memberInfo in members.Skip(1))
        {
            memberMetadata = (memberMetadata as INavigation)?.TargetEntityType.FindMember(memberInfo.Name);
        }

        var vectorIndexesInModel = memberMetadata?.DeclaringType.ContainingEntityType
            .GetIndexes().Where(i => i.GetVectorIndexOptions() != null && i.Properties[0] == memberMetadata).ToList();

        if (options.Value.IndexName == null)
        {
            // Index to use was not specified in the query. Throw if there is anything but one index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because there are no vector indexes defined for this property in the EF model. " +
                    "Use 'HasIndex' on the EF model builder to define an index. ");
            }

            if (vectorIndexesInModel.Count > 1)
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because multiple vector indexes are defined for this property in the EF model. " +
                    "Specify the index to use in the call to 'VectorSearch'.");
            }

            // There is only one index and none was specified, so use that index.
            options = options.Value with { IndexName = vectorIndexesInModel[0].Name };
        }
        else
        {
            // Index to use was specified in the query. Throw if it doesn't match any index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because vector index '{options.Value.IndexName}' was not defined in the EF Core model. " +
                    "Use 'HasIndex' on the EF model builder to specify the index, or disable this warning if you have created your MongoDB indexes outside of EF Core.");
            }

            if (vectorIndexesInModel.All(i => i.Name != options.Value.IndexName))
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because vector index '{options.Value.IndexName}' was not defined in the EF Core model. " +
                    "Vector query searches must use one of the indexes defined on the EF model.");
            }
            // Index name in query already matches, so just continue.
        }

        return source.AppendStage(PipelineStageDefinitionBuilder
            .VectorSearch(property, queryVector, limit, new() { IndexName = options.Value.IndexName, NumberOfCandidates = options.Value.NumberOfCandidates }));
    }

    private sealed class EntityRootFindingVisitor : ExpressionVisitor
    {
        public EntityQueryRootExpression? RootExpression { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            if (node is EntityQueryRootExpression entityQueryRootExpression)
            {
                RootExpression = entityQueryRootExpression;
            }
            return base.Visit(node);
        }
    }

    private static IReadOnlyList<TMemberInfo> GetMemberAccess<TMemberInfo>(this LambdaExpression memberAccessExpression)
        where TMemberInfo : MemberInfo
    {
        var members = memberAccessExpression.Parameters[0].MatchMemberAccess<TMemberInfo>(memberAccessExpression.Body);

        if (members is null)
        {
            throw new ArgumentException(
                $"The expression '{memberAccessExpression}' is not a valid member access expression. The expression should represent a simple property or field access: 't => t.MyProperty'.");
        }
        return members;
    }

    private static IReadOnlyList<TMemberInfo>? MatchMemberAccess<TMemberInfo>(
        this Expression parameterExpression,
        Expression memberAccessExpression)
        where TMemberInfo : MemberInfo
    {
        var memberInfos = new List<TMemberInfo>();
        var unwrappedExpression = RemoveTypeAs(RemoveConvert(memberAccessExpression));
        do
        {
            var memberExpression = unwrappedExpression as MemberExpression;
            if (!(memberExpression?.Member is TMemberInfo memberInfo))
            {
                return null;
            }
            memberInfos.Insert(0, memberInfo);
            unwrappedExpression = RemoveTypeAs(RemoveConvert(memberExpression.Expression));
        }
        while (unwrappedExpression != parameterExpression);

        return memberInfos;
    }

    [return: NotNullIfNotNull(nameof(expression))]
    private static Expression? RemoveTypeAs(this Expression? expression)
    {
        while (expression?.NodeType == ExpressionType.TypeAs)
        {
            expression = ((UnaryExpression)RemoveConvert(expression)).Operand;
        }

        return expression;
    }

    [return: NotNullIfNotNull(nameof(expression))]
    private static Expression? RemoveConvert(Expression? expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
            ? RemoveConvert(unaryExpression.Operand)
            : expression;

}
