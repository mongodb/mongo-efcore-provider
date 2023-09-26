﻿/* Copyright 2023-present MongoDB Inc.
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

using System.Threading;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// Creates an auto-incrementing number used by owned entity collections to ensure
/// key uniqueness within EF tracking.
/// </summary>
internal class OwnedEntityIndexValueGenerator : ValueGenerator<int>
{
    private int _current = int.MinValue;

    /// <inheritdoc/>
    public override int Next(EntityEntry entry) =>
        Interlocked.Increment(ref _current);

    /// <inheritdoc/>
    public override bool GeneratesTemporaryValues
        => false;
}
