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
internal sealed class NativeTranslationNotSupportedException : Exception
{
    public NativeTranslationNotSupportedException(string message) : base(message)
    {
    }
}