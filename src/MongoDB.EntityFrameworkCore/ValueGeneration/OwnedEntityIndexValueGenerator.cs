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
/// Generates a unique sequential <see cref="int"/> to used by owned entity
/// collections ensuring internal key uniqueness within EF tracking.
/// The values are not actually persisted.
/// </summary>
internal class OwnedEntityIndexValueGenerator : ValueGenerator<int>
{
    private int _current = int.MinValue;

    /// <summary>
    /// Generates a unique sequential <see langref="int"/>.
    /// </summary>
    /// <param name="entry">The <see cref="EntityEntry"/> this <see langref="int"/>
    /// will be used by.</param>
    /// <returns>A unique <see langref="int"/>.</returns>
    public override int Next(EntityEntry entry) =>
        Interlocked.Increment(ref _current);

    /// <summary>
    /// Always <see langref="false"/> as this generator is only used for permanent values.
    /// </summary>
    public override bool GeneratesTemporaryValues
        => false;
}
