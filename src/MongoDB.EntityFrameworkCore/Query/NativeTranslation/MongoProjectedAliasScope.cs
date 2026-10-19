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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The output schema of a keyed <c>GroupBy(key).Select(...)</c>'s flattening <c>$project</c>, as seen by a lambda
/// composed after it (a post-group <c>Where</c>, or a terminal aggregate's predicate/selector). See
/// <see cref="MongoExpressionTranslator.ProjectedAliasScope"/>.
/// </summary>
/// <param name="Parameter">
/// The lambda parameter naming a flattened row. Matched by reference identity, never by name.
/// </param>
/// <param name="Projections">
/// The flatten <c>$project</c>'s entries (<see cref="MongoSelectDefinition.Projection"/>); each alias is a top-level
/// field once it has run.
/// </param>
internal sealed record MongoProjectedAliasScope(ParameterExpression Parameter, IReadOnlyList<MongoProjection> Projections);
