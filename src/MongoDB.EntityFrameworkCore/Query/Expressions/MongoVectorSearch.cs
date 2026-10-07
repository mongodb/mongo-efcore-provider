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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The native-translation IR for a <c>MongoQueryableExtensions.VectorSearch</c> call — the payload of
/// <see cref="MongoSelectDefinition.VectorSearch"/>.
/// </summary>
/// <remarks>
/// <para>
/// The query vector, limit and options are kept as raw argument nodes (EF query parameters, or defensively
/// constants) and resolved per execution via
/// <see cref="NativeTranslation.NativeQueryParameter.TryGetQueryParameterName"/>, which hides the EF8/EF9-vs-EF10 parameter-node difference. The pre-filter is translated at compile time and its
/// parameters go into the shared <c>PlaceholderTable</c>.
/// </para>
/// <para>
/// <see cref="PropertyLambda"/> stays a <see cref="LambdaExpression"/> because the driver's
/// <c>PipelineStageDefinitionBuilder.VectorSearch</c> derives the document path from it (e.g.
/// <c>e =&gt; e.Preface.Floats</c> → <c>"Preface.Floats"</c>).
/// </para>
/// </remarks>
/// <param name="EntityType">
/// The root entity type, needed by the deferred stage builder for member resolution and serialization, since
/// <c>MongoPipelineFactory</c> has no model knowledge.
/// </param>
/// <param name="PropertyLambda">The vector property selector — <c>Arguments[1]</c>, unwrapped from its quote.</param>
/// <param name="PreFilter">
/// The pre-filter (<c>Arguments[2]</c>) translated to native IR at compile time, or <see langword="null"/>
/// when the query has none.
/// </param>
/// <param name="QueryVectorArgument">The query-vector argument node — <c>Arguments[3]</c>.</param>
/// <param name="LimitArgument">The limit argument node — <c>Arguments[4]</c>.</param>
/// <param name="OptionsArgument">The options argument node — <c>Arguments[5]</c>.</param>
internal sealed record MongoVectorSearch(
    IEntityType EntityType,
    LambdaExpression PropertyLambda,
    MongoExpression? PreFilter,
    Expression QueryVectorArgument,
    Expression LimitArgument,
    Expression OptionsArgument);
