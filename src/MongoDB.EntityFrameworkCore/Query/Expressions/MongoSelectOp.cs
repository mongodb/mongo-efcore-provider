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
/// One filter/sort/page operation in a <see cref="MongoSelectDefinition"/>'s ordered pipeline. Emitted verbatim,
/// so list order is stage order; this is how non-canonical Skip/Take (Take-before-Skip, repeated paging) is
/// represented.
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
/// A whole-document <c>Distinct()</c> over a non-projected source. The row shape is unchanged, so unlike a
/// projected Distinct (<see cref="MongoSelectDefinition.Grouping"/>) it is just another ordered op, lowering to the
/// <c>$group{_id:"$$ROOT"}</c> + <c>$replaceRoot</c> dedup also used by <c>Union</c>.
/// </summary>
/// <param name="KeepOnlyField">
/// For a Distinct over a bare join Inner leaf: the join's <c>_lookup_&lt;Nav&gt;</c> field, the only part of the
/// flattened document that is the result entity. The dedup first projects the document down to it.
/// </param>
/// <param name="ExcludeField">
/// For a Distinct over a bare join Outer leaf: the join's <c>_lookup_&lt;Nav&gt;</c> field, which is not part of
/// the result entity. The dedup first unsets it, so an outer row matched by several inner rows comes back once.
/// </param>
internal sealed record MongoDistinctOp(string? KeepOnlyField = null, string? ExcludeField = null) : MongoSelectOp;
