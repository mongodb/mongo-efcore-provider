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

namespace MongoDB.EntityFrameworkCore.Metadata.Search;

/// <summary>
/// Numeric storage representation for MongoDB search <c>number</c> fields.
/// </summary>
/// <remarks>
/// Atlas Search indexes numeric values using either a 64-bit signed integer or a 64-bit IEEE 754
/// double-precision floating-point format. Choose the representation that matches your data to
/// balance precision and range for filtering, sorting, and aggregations.
/// For background on Atlas Search numeric field mappings, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number/"/>.
/// </remarks>
public enum SearchNumberRepresentation
{
    /// <summary>
    /// 64-bit signed integer (<c>int64</c>): exact integer values with full 64-bit range.
    /// Use when you require precise integer comparisons and ordering without floating-point rounding.
    /// </summary>
    Int64,
    /// <summary>
    /// 64-bit IEEE 754 double (<c>double</c>): floating-point values that support fractions and a wide
    /// dynamic range. Useful for measurements and decimals, with the usual floating-point precision trade-offs.
    /// </summary>
    Double
}
