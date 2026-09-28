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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Builds the driver's <c>$vectorSearch</c> stage for a <c>MongoQueryableExtensions.VectorSearch</c> call.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the driver-LINQ bridge and the native translator so both run the same validation, index resolution,
/// diagnostics and driver call, and can't drift on observable exceptions and warnings.
/// </para>
/// <para>
/// <see cref="Resolve"/> has no reflection, so its exceptions (notably the <c>Exact</c>+<c>NumberOfCandidates</c>
/// <see cref="InvalidOperationException"/>) surface unwrapped. Moving them into the reflection-invoked generic
/// would wrap them in <see cref="TargetInvocationException"/> on both paths.
/// </para>
/// <para>
/// Don't add <see cref="BindingFlags.DoNotWrapExceptions"/> to <see cref="CreateStage"/>'s <c>Invoke</c>: a
/// non-positive <c>limit</c> surfacing as <see cref="TargetInvocationException"/> wrapping
/// <see cref="ArgumentOutOfRangeException"/> (<c>Parameter 'limit'</c>) is shipped behavior. Likewise, don't make
/// <c>MongoPipelineFactory.ValidatePagingStages</c> inspect <c>$vectorSearch</c>; that would throw the
/// <c>Take(0)</c>-style <c>Parameter 'count'</c> exception instead and create a divergence.
/// </para>
/// </remarks>
internal static class VectorSearchStageBuilder
{
    /// <summary>
    /// Resolves the target member and index, applies the <c>Exact</c>+<c>NumberOfCandidates</c> guard, and logs
    /// <c>VectorSearchNeedsIndex</c> when the requested index isn't in the model. Must stay reflection-free (see
    /// type remarks).
    /// </summary>
    /// <param name="entityType">
    /// The root entity type, or <see langword="null" /> when the source CLR type isn't mapped (member resolution
    /// then throws using <paramref name="sourceType" />).
    /// </param>
    /// <param name="sourceType">Used only for the error message when <paramref name="entityType" /> is null.</param>
    /// <param name="propertyLambda">The property selector, already unquoted.</param>
    /// <param name="options">The query's <see cref="VectorQueryOptions"/>, if any.</param>
    /// <param name="queryLogger">Logger for the <c>VectorSearchNeedsIndex</c> warning.</param>
    /// <returns>The resolved member and the options with the index name filled in.</returns>
    internal static ResolvedVectorSearch Resolve(
        IEntityType? entityType,
        Type sourceType,
        LambdaExpression propertyLambda,
        VectorQueryOptions? options,
        IDiagnosticsLogger<DbLoggerCategory.Query> queryLogger)
    {
        var concreteOptions = options ?? new();

        if (concreteOptions is { NumberOfCandidates: not null, Exact: true })
        {
            throw new InvalidOperationException(
                "The option 'Exact' is set to 'true' on a call to 'VectorQuery', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set. Either 'NumberOfCandidates' or 'Exact' can be set, but not both.");
        }

        var members = propertyLambda.GetMemberAccess<MemberInfo>();
        var memberMetadata = entityType?.FindMember(members[0].Name);

        if (memberMetadata == null)
        {
            throw new InvalidOperationException(
                $"Could not create a vector query for '{(entityType?.ClrType ?? sourceType).ShortDisplayName()}.{members[0].Name}'. Make sure the entity type is included in the EF Core model and that the property or field is mapped.");
        }

        foreach (var memberInfo in members.Skip(1))
        {
            memberMetadata = (memberMetadata as INavigation)?.TargetEntityType.FindMember(memberInfo.Name);
        }

        var vectorIndexesInModel = memberMetadata?.DeclaringType.ContainingEntityType
            .GetIndexes().Where(i => i.GetVectorIndexOptions() != null && i.Properties[0] == memberMetadata).ToList();

        if (concreteOptions.IndexName == null)
        {
            // Index to use was not specified in the query. Throw or warn if there is anything but one index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
            {
                ThrowForBadOptions(
                    "the vector index for this query could not be found. Use 'HasIndex' on the EF model builder to specify the index, or " +
                    "specify the index name in the call to 'VectorQuery' if indexes are being managed outside of EF Core.");
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
            // Index to use was specified in the query. Throw or warn if it doesn't match any index in the model.
            if (vectorIndexesInModel == null || vectorIndexesInModel.All(i => i.Name != concreteOptions.IndexName))
            {
                queryLogger.VectorSearchNeedsIndex((IProperty)memberMetadata!);
            }
            // Index name in query already matches, so just continue.
        }

        return new ResolvedVectorSearch(memberMetadata!, concreteOptions);

        void ThrowForBadOptions(string reason)
        {
            throw new InvalidOperationException(
                $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because {reason}");
        }
    }

    /// <summary>
    /// Builds the driver's <c>$vectorSearch</c> <c>PipelineStageDefinition&lt;T, T&gt;</c> via reflection with default
    /// <see cref="BindingFlags"/>, so a bad <c>limit</c> is wrapped identically on both paths.
    /// </summary>
    /// <param name="entityType">The root entity type.</param>
    /// <param name="propertyLambda">The property selector, already unquoted.</param>
    /// <param name="resolved">The result of <see cref="Resolve"/>.</param>
    /// <param name="filterDefinition">
    /// The pre-filter, or <see langword="null" />: an <c>ExpressionFilterDefinition&lt;T&gt;</c> on the driver-LINQ
    /// bridge, a <c>BsonDocumentFilterDefinition&lt;T&gt;</c> on the native path.
    /// </param>
    /// <param name="queryVector">The query vector.</param>
    /// <param name="limit">The limit; non-positive values are rejected by the driver inside the reflection call.</param>
    /// <returns>The driver's pipeline stage definition, untyped.</returns>
    internal static object CreateStage(
        IEntityType entityType,
        LambdaExpression propertyLambda,
        ResolvedVectorSearch resolved,
        object? filterDefinition,
        QueryVector queryVector,
        int limit)
    {
        var searchOptionsType = typeof(VectorSearchOptions<>).MakeGenericType(entityType.ClrType);
        var searchOptions = Activator.CreateInstance(searchOptionsType)!;

        searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.IndexName))!.SetValue(searchOptions,
            resolved.Options.IndexName);
        searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.NumberOfCandidates))!.SetValue(searchOptions,
            resolved.Options.NumberOfCandidates);
        searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Exact))!.SetValue(searchOptions,
            resolved.Options.Exact);

        if (filterDefinition != null)
        {
            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Filter))!.SetValue(searchOptions,
                filterDefinition);
        }

        return typeof(PipelineStageDefinitionBuilder)
            .GetTypeInfo().GetDeclaredMethods(nameof(PipelineStageDefinitionBuilder.VectorSearch))
            .Single(mi =>
                mi.GetParameters()[0].ParameterType.IsGenericType
                && mi.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>))
            .MakeGenericMethod(entityType.ClrType, resolved.Member.ClrType)
            .Invoke(null, [propertyLambda, queryVector, limit, searchOptions])!;
    }

    /// <summary>
    /// Renders a stage from <see cref="CreateStage"/> to a <see cref="BsonDocument"/>. Native path only; the
    /// driver-LINQ bridge lets the driver render it via <c>MongoQueryable.AppendStage</c>.
    /// </summary>
    /// <param name="stage">The stage returned by <see cref="CreateStage"/>.</param>
    /// <param name="entityType">The root entity type.</param>
    /// <param name="entitySerializer">The provider's serializer for <paramref name="entityType"/>.</param>
    /// <returns>The rendered <c>$vectorSearch</c> stage document.</returns>
    internal static BsonDocument RenderStage(object stage, IEntityType entityType, IBsonSerializer entitySerializer)
        => (BsonDocument)RenderStageMethodInfo
            .MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [stage, entitySerializer])!;

    private static readonly MethodInfo RenderStageMethodInfo =
        typeof(VectorSearchStageBuilder).GetMethod(nameof(RenderStageCore), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static BsonDocument RenderStageCore<TDocument>(
        PipelineStageDefinition<TDocument, TDocument> stage,
        IBsonSerializer<TDocument> entitySerializer)
        => stage.Render(new RenderArgs<TDocument>(entitySerializer, BsonSerializer.SerializerRegistry)).Document;

    /// <summary>
    /// The outcome of <see cref="Resolve"/>.
    /// </summary>
    /// <param name="Member">The mapped member the query vector is compared against.</param>
    /// <param name="Options">The query options, with <see cref="VectorQueryOptions.IndexName"/> always set.</param>
    internal readonly record struct ResolvedVectorSearch(IPropertyBase Member, VectorQueryOptions Options);
}
