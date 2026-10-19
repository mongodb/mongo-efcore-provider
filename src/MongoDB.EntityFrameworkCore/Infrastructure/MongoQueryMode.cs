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

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>Selects how the provider translates LINQ queries to MongoDB.</summary>
public enum MongoQueryMode
{
    /// <summary>Native translation when representable; driver-LINQ fallback otherwise. (Default.)</summary>
    Native,

    /// <summary>Always use the driver's LINQ provider (the pre-rebuild behavior).</summary>
    DriverLinq,

    /// <summary>Native translation only; throw at compile time on an un-representable query (diagnostic).</summary>
    NativeOnly
}
