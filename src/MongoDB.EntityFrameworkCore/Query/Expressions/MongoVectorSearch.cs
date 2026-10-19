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
/// It holds the RAW argument nodes for the query vector, the limit and the options rather than
/// pre-extracted values, mirroring what the driver-LINQ bridge's own <c>ParamValue&lt;T&gt;</c> does: each is
/// an EF query parameter (or, defensively, a constant) whose value is only known per execution. They are
/// resolved at Build time through
/// <see cref="NativeTranslation.NativeQueryParameter.TryGetQueryParameterName"/>, which is where the
/// EF8/EF9-vs-EF10 query-parameter-node difference is encapsulated — that helper is the whole reason no
/// version-conditional compilation appears anywhere on this path.
/// </para>
/// <para>
/// The pre-filter, by contrast, IS translated at compile time (it is a predicate the native translator
/// either supports or declines), and any parameter inside it is recorded as an ordinary placeholder
/// sentinel in the shared <c>PlaceholderTable</c>, so it substitutes in the same pass as every other stage.
/// </para>
/// <para>
/// <see cref="PropertyLambda"/> is kept as an EF <see cref="LambdaExpression"/> rather than a resolved
/// element path because the driver's own <c>PipelineStageDefinitionBuilder.VectorSearch</c> takes an
/// <c>Expression&lt;Func&lt;TDoc, TField&gt;&gt;</c> and derives the document path from it — which is how a
/// nested selector such as <c>e =&gt; e.Preface.Floats</c> renders as <c>"Preface.Floats"</c> for free, and
/// is why this feature needs no MQL path-rendering logic of its own.
/// </para>
/// </remarks>
/// <param name="EntityType">
/// The entity type the vector search is rooted on (the query's own collection entity type). Carried here
/// because the deferred stage builder needs it for member resolution, the driver's generic builder call and
/// the entity serializer used to render the stage — and <c>MongoPipelineFactory</c> itself is deliberately
/// free of any model/entity-type knowledge.
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
