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

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// Generates unique <see cref="ObjectId"/> values. This is the default generator
/// for MongoDB `_id` <see cref="ObjectId"/> fields. These generated values persist
/// to the server instead of using server-generated values and round-tripping.
/// </summary>
internal class ObjectIdValueGenerator : ValueGenerator<ObjectId>
{
    /// <summary>
    /// Generates a unique <see cref="ObjectId"/>.
    /// </summary>
    /// <param name="entry">The <see cref="EntityEntry"/> this <see cref="ObjectId"/>
    /// will be used by.</param>
    /// <returns>A unique <see cref="ObjectId"/>.</returns>
    public override ObjectId Next(EntityEntry entry)
        => ObjectId.GenerateNewId();

    /// <summary>
    /// Always <see langword="false"/> as this generator is only used for permanent values.
    /// </summary>
    public override bool GeneratesTemporaryValues
        => false;
}
