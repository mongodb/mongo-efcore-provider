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
/// Needed by <em>deferred</em> stage slots, whose BSON shape (not just parameter values) depends on runtime
/// state. <see cref="ParameterValues"/> is already bridged across EF versions by the caller, so no
/// version-conditional code is needed here or in <see cref="MongoPipelineFactory"/>.
/// </remarks>
/// <param name="ParameterValues">The named parameter values for this execution.</param>
/// <param name="SerializerFactory">The serializer factory for this <c>DbContext</c>.</param>
/// <param name="QueryLogger">For deferred slots that raise a diagnostic (e.g. a missing vector index).</param>
/// <param name="AdditionalState">
/// Handed to <c>MongoExecutableQuery.AdditionalState</c>; deferred slots may record entries for the executor.
/// </param>
internal readonly record struct MongoNativeBuildContext(
    IReadOnlyDictionary<string, object?> ParameterValues,
    BsonSerializerFactory SerializerFactory,
    IDiagnosticsLogger<DbLoggerCategory.Query> QueryLogger,
    IDictionary<string, object> AdditionalState);
