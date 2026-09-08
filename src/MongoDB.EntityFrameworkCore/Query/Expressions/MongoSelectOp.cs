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

using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// One filter/sort/page operation in a <see cref="MongoSelectDefinition"/>'s ordered pipeline. The list of
/// these is emitted verbatim by the lowerer, so their order IS the emitted stage order — this is what lets
/// the native path represent non-canonical Skip/Take (operator-after-paging, Take-before-Skip, repeated
/// paging). Dialect-neutral logical IR (holds <see cref="MongoExpression"/>, never BSON), like
/// <see cref="MongoOrdering"/> / <see cref="MongoGrouping"/>.
/// </summary>
internal abstract record MongoSelectOp;

/// <summary>A <c>$match</c> predicate.</summary>
internal sealed record MongoMatchOp(MongoExpression Predicate) : MongoSelectOp;

/// <summary>A <c>$sort</c> over one or more orderings.</summary>
internal sealed record MongoSortOp(IReadOnlyList<MongoOrdering> Orderings) : MongoSelectOp;

/// <summary>A <c>$skip</c> offset.</summary>
internal sealed record MongoSkipOp(MongoExpression Count) : MongoSelectOp;

/// <summary>A <c>$limit</c> cap.</summary>
internal sealed record MongoLimitOp(MongoExpression Count) : MongoSelectOp;

/// <summary>
/// A whole-document <c>Distinct()</c> over a NON-projected source (EF-322: no preceding <c>Select</c> has
/// populated <see cref="MongoSelectDefinition.Projection"/>). Unlike a projected <c>Distinct()</c>
/// (<see cref="MongoSelectDefinition.Grouping"/>/<see cref="MongoSelectDefinition.PostGroupOps"/>, which need
/// field-by-field key extraction because the row shape has already been narrowed to specific aliases), a
/// whole-entity row's shape never changes, so this needs no special post-terminal handling at all — it is
/// just another entry in the ordinary ordered op list (<see cref="MongoSelectDefinition.PipelineOps"/> /
/// <see cref="MongoSelectDefinition.TrailingOps"/>), lowering to the generic <c>$group{_id:"$$ROOT"}</c> +
/// <c>$replaceRoot</c> dedup pattern already used for <c>Union</c>'s own dedup
/// (<c>MongoPipelineFactory.RenderUnionWith</c>). A marker record — carries no data of its own.
/// </summary>
internal sealed record MongoDistinctOp : MongoSelectOp;
