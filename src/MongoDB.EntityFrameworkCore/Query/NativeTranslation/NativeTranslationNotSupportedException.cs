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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Thrown by the native MQL translator when it encounters a query shape it does not yet support.
/// Under <see cref="MongoDB.EntityFrameworkCore.Infrastructure.MongoQueryMode.Native"/> the compile-time
/// gate catches this and falls back to the driver-LINQ path; under
/// <see cref="MongoDB.EntityFrameworkCore.Infrastructure.MongoQueryMode.NativeOnly"/> it propagates,
/// surfacing the unsupported query shape to the caller.
/// </summary>
/// <remarks>
/// <b>Visibility is an OPEN question, tracked as EF-420.</b> This type is <c>internal</c>, yet under
/// <see cref="MongoDB.EntityFrameworkCore.Infrastructure.MongoQueryMode.NativeOnly"/> it is the exception a
/// USER sees and would reasonably want to catch by type. Making it public is a public-API decision (see the
/// versioning rubric in the repo's AGENTS.md), so it is ticketed rather than changed here. Nothing blocks
/// merging on it; EF-420 exists so the decision is on record rather than defaulted by silence.
/// </remarks>
internal sealed class NativeTranslationNotSupportedException : Exception
{
    public NativeTranslationNotSupportedException(string message) : base(message)
    {
    }
}