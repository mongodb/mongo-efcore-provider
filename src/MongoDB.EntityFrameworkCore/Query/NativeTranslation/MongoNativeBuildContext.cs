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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The per-execution state a native pipeline needs while it is being built, handed to
/// <see cref="MongoPipelineFactory.Build(in MongoNativeBuildContext)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Most of a native pipeline is rendered once, at compile time, into an immutable template whose only
/// per-execution variability is parameter <em>values</em> — for those, the parameter dictionary alone is
/// enough (see <see cref="MongoPipelineFactory.Build(IReadOnlyDictionary{string, object})"/>). A
/// <em>deferred</em> stage slot is one whose BSON <em>shape</em> — not merely its values — depends on
/// runtime state, so the stage document has to be constructed at execution time. This record carries
/// everything such a slot may need.
/// </para>
/// <para>
/// No EF-version-conditional code appears here or in <see cref="MongoPipelineFactory"/>:
/// <see cref="ParameterValues"/> is already bridged by the caller (EF10's
/// <c>QueryContext.Parameters</c> vs. EF8/EF9's <c>QueryContext.ParameterValues</c>).
/// </para>
/// </remarks>
/// <param name="ParameterValues">
/// The named parameter values for this execution, already bridged to a version-agnostic dictionary.
/// </param>
/// <param name="SerializerFactory">The serializer factory for this <c>DbContext</c>.</param>
/// <param name="QueryLogger">
/// The query logger, for a deferred slot that raises a diagnostic (e.g. a missing vector index) while
/// it builds.
/// </param>
/// <param name="AdditionalState">
/// The mutable state dictionary that will be handed to <c>MongoExecutableQuery.AdditionalState</c>; a
/// deferred slot may record entries here for later use by the executor.
/// </param>
internal readonly record struct MongoNativeBuildContext(
    IReadOnlyDictionary<string, object?> ParameterValues,
    BsonSerializerFactory SerializerFactory,
    IDiagnosticsLogger<DbLoggerCategory.Query> QueryLogger,
    IDictionary<string, object> AdditionalState);
