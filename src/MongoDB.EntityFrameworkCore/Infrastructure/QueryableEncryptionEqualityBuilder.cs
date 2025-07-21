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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Methods to configure Queryable Encryption for a property configured as
/// equality-queryable.
/// </summary>
/// <param name="propertyBuilder">The <see cref="PropertyBuilder"/> these methods will apply to.</param>
public class QueryableEncryptionEqualityBuilder<T>(PropertyBuilder propertyBuilder)
{
    /// <summary>
    /// Sets the contention used by Queryable Encryption for this property in order
    /// to improve performance. Higher values improve performance of insert and update
    /// operations on low cardinality fields, but decrease find performance.
    /// </summary>
    /// <param name="contention">The contention to use, or <see langword="null"/> to unset and use the default of 8.</param>
    /// <returns>The <see cref="QueryableEncryptionEqualityBuilder{T}"/> to continue building this property.</returns>
    /// <remarks>
    /// This is intended for advanced users only. The default value is suitable for the
    /// majority of use cases, and should only be set if your use case requires it.
    /// </remarks>
    public QueryableEncryptionEqualityBuilder<T> WithContention(int? contention)
    {
        propertyBuilder.Metadata.SetQueryableEncryptionContention(contention);
        return this;
    }
}

