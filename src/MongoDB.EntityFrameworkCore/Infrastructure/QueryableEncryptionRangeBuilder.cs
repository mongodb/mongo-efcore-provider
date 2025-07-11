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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Methods to configure Queryable Encryption for a property configured as
/// range-queryable.
/// </summary>
/// <param name="propertyBuilder">The <see cref="PropertyBuilder"/> these methods will apply to.</param>
public class QueryableEncryptionRangeBuilder<T>(PropertyBuilder propertyBuilder)
{
    /// <summary>
    /// Sets the sparsity used by Queryable Encryption for this property in order
    /// to improve performance. Low sparsity (dense indexing) improves query performance,
    /// but stores more documents in the encrypted metadata collections for each
    /// insert or update operation, causing greater storage overhead.
    /// </summary>
    /// <param name="sparsity">The sparsity to use from 1-4, or <see langref="null"/> to unset and use the default of 2.</param>
    /// <returns>The <see cref="QueryableEncryptionRangeBuilder{T}"/> to continue building this property.</returns>
    /// <remarks>
    /// This is intended for advanced users only. The default value is suitable for the
    /// majority of use cases, and should only be set if your use case requires it.
    /// </remarks>
    public QueryableEncryptionRangeBuilder<T> WithSparsity(int? sparsity)
    {
        if (sparsity is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(sparsity), sparsity, "Sparsity must be between 1 and 4.");

        propertyBuilder.Metadata.SetQueryableEncryptionSparsity(sparsity);
        return this;
    }

    /// <summary>
    /// Sets the precision used by Queryable Encryption for this property. This applies only to values stored
    /// as double or decimal in the BSON document and limits how many decimal places are taken into account
    /// when querying. Additional digits are dropped, not rounded.
    /// </summary>
    /// <param name="trimFactor">The precision to use from 1-4, or <see langref="null"/> to unset and use the default.</param>
    /// <returns>The <see cref="QueryableEncryptionRangeBuilder{T}"/> to continue building this property.</returns>
    /// <remarks>
    /// This is intended for advanced users only. The default value is suitable for the
    /// majority of use cases, and should only be set if your use case requires it.
    /// </remarks>
    public QueryableEncryptionRangeBuilder<T> WithPrecision(int? trimFactor)
    {
        propertyBuilder.Metadata.SetQueryableEncryptionPrecision(trimFactor);
        return this;
    }

    /// <summary>
    /// Sets the trim factor used by Queryable Encryption for this property. A higher trim factor increases the
    /// throughput of concurrent inserts and updates at the cost of slowing down some range read operations.
    /// </summary>
    /// <param name="trimFactor">The trim factor to use, or <see langref="null"/> to unset and use the default of 6.</param>
    /// <returns>The <see cref="QueryableEncryptionRangeBuilder{T}"/> to continue building this property.</returns>
    /// <remarks>
    /// This is intended for advanced users only. The default value is suitable for the
    /// majority of use cases, and should only be set if your use case requires it.
    /// </remarks>
    public QueryableEncryptionRangeBuilder<T> WithTrimFactor(int? trimFactor)
    {
        propertyBuilder.Metadata.SetQueryableEncryptionTrimFactor(trimFactor);
        return this;
    }

    /// <summary>
    /// Sets the contention used by Queryable Encryption for this property in order
    /// to improve performance. Higher values improve performance of insert and update
    /// operations on low cardinality fields, but decrease find performance.
    /// </summary>
    /// <param name="contention">The contention to use, or <see langref="null"/> to unset and use the default of 8.</param>
    /// <returns>The <see cref="QueryableEncryptionRangeBuilder{T}"/> to continue building this property.</returns>
    /// <remarks>
    /// This is intended for advanced users only. The default value is suitable for the
    /// majority of use cases, and should only be set if your use case requires it.
    /// </remarks>
    public QueryableEncryptionRangeBuilder<T> WithContention(int? contention)
    {
        propertyBuilder.Metadata.SetQueryableEncryptionContention(contention);
        return this;
    }
}
