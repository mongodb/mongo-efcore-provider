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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extensions to specify MongoDB Queryable Encryption configuration for an EF Core model.
/// </summary>
public static class QueryableEncryptionBuilderExtensions
{
    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption without query ability.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<T> IsEncrypted<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.NotQueryable);
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return builder;
    }

    /// <summary>
    /// Configures an owned entity to be encrypted using MongoDB Queryable Encryption without query ability.
    /// </summary>
    /// <param name="builder">The <see cref="OwnedNavigationBuilder"/> for the owned entity being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="TOwnerEntity">The type that owns this object.</typeparam>
    /// <typeparam name="TDependentEntity">The type of the object that is owned.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static OwnedNavigationBuilder<TOwnerEntity, TDependentEntity> IsEncrypted<TOwnerEntity, TDependentEntity>(
        this OwnedNavigationBuilder<TOwnerEntity, TDependentEntity> builder,
        Guid dataKeyId) where TOwnerEntity : class where TDependentEntity : class
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.NotQueryable);
        builder.Metadata.SetDataEncryptionKeyId(dataKeyId);
        return builder;
    }

    /// <summary>
    /// Configures an owned entity to be encrypted using MongoDB Queryable Encryption without query ability.
    /// </summary>
    /// <param name="builder">The <see cref="OwnershipBuilder"/> for the owned entity being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="TOwnerEntity">The type that owns this object.</typeparam>
    /// <typeparam name="TDependentEntity">The type of the object that is owned.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static OwnershipBuilder<TOwnerEntity, TDependentEntity> IsEncrypted<TOwnerEntity, TDependentEntity>(
        this OwnershipBuilder<TOwnerEntity, TDependentEntity> builder,
        Guid dataKeyId) where TOwnerEntity : class where TDependentEntity : class
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.NotQueryable);
        builder.Metadata.SetDataEncryptionKeyId(dataKeyId);
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for equality queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <param name="equalityBuilder">The additional configuration for configuring equality options.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>
    /// <see cref="BsonType.Decimal128"/>, <see cref="BsonType.Double"/>, <see cref="BsonType.Document"/>,
    /// and <see cref="BsonType.Array"/>, storage types  are not permitted for equality encryption.
    /// </remarks>
    public static PropertyBuilder<T> IsEncryptedForEquality<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId,
        Action<QueryableEncryptionEqualityBuilder<T>>? equalityBuilder = null)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.Equality);
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        equalityBuilder?.Invoke(new QueryableEncryptionEqualityBuilder<T>(builder));
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <param name="minValue">The minimum permitted value for this range.</param>
    /// <param name="maxValue">The maximum permitted value for this range.</param>
    /// <param name="rangeBuilder">The additional configuration for configuring range options.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>
    /// Only <see cref="BsonType.Decimal128"/>, <see cref="BsonType.Double"/>, <see cref="BsonType.Int32"/>,
    /// and <see cref="BsonType.Int64"/> storage types are permitted for range encryption.
    /// </remarks>
    public static PropertyBuilder<T> IsEncryptedForRange<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId,
        T minValue,
        T maxValue,
        Action<QueryableEncryptionRangeBuilder<T>>? rangeBuilder = null)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.Range);
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        builder.Metadata.SetQueryableEncryptionRangeMin(minValue);
        builder.Metadata.SetQueryableEncryptionRangeMax(maxValue);
        rangeBuilder?.Invoke(new QueryableEncryptionRangeBuilder<T>(builder));
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <param name="minValue">The minimum permitted value for this range.</param>
    /// <param name="maxValue">The maximum permitted value for this range.</param>
    /// <param name="precision">The precision for this range (required for <see cref="BsonType.Decimal128"/>
    /// and <see cref="BsonType.Double"/> storage types.</param>
    /// <param name="rangeBuilder">The additional configuration for configuring range options.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>
    /// Only <see cref="BsonType.Decimal128"/>, <see cref="BsonType.Double"/>, <see cref="BsonType.Int32"/>,
    /// and <see cref="BsonType.Int64"/> storage types are permitted for range encryption.
    /// </remarks>
    public static PropertyBuilder<T> IsEncryptedForRange<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId,
        T minValue,
        T maxValue,
        int precision,
        Action<QueryableEncryptionRangeBuilder<T>>? rangeBuilder = null)
    {
        builder.Metadata.SetQueryableEncryptionPrecision(precision);
        rangeBuilder?.Invoke(new QueryableEncryptionRangeBuilder<T>(builder));
        return IsEncryptedForRange(builder, dataKeyId, minValue, maxValue);
    }
}
