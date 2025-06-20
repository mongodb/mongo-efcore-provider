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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Extensions to specify MongoDB Queryable Encryption configuration for an EF Core model.
/// </summary>
public static class QueryableEncryptionBuilderExtensions
{
    /// <summary>
    /// Configure the MongoDB Queryable Encryption default encryption data key id for a given model.
    /// </summary>
    /// <param name="modelBuilder">The <see cref="ModelBuilder"/> for the model being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <exception cref="ArgumentException">If the <paramref name="dataKeyId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <remarks>The specified encryption data key id will be used for any property to be encrypted unless it is overridden
    /// at property or entity level.</remarks>
    public static ModelBuilder EncryptionDefaults(
        this ModelBuilder modelBuilder,
        Guid dataKeyId)
    {
        modelBuilder.Model.SetEncryptionDataKeyId(dataKeyId);
        return modelBuilder;
    }

    /// <summary>
    /// Configure the MongoDB Queryable Encryption default encryption data key id for a given model.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <exception cref="ArgumentException">If the <paramref name="dataKeyId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <remarks>The specified encryption data key id will be used for any property to be encrypted unless it is overridden
    /// at property level.</remarks>
    public static EntityTypeBuilder<TEntity> EncryptionDefaults<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Guid dataKeyId) where TEntity:class
    {
        entityTypeBuilder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
    public static PropertyBuilder<T> IsEncrypted<T>(
        this PropertyBuilder<T> builder)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.NotQueryable);
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption but with no property-level query support.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<T> IsEncrypted<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId)
    {
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return IsEncrypted(builder);
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
    public static PropertyBuilder<T> IsEncryptedForRange<T>(
        this PropertyBuilder<T> builder)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.Range);
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
    public static PropertyBuilder<T> IsEncryptedForRange<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId)
    {
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return IsEncryptedForRange(builder);
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="minValue">The minimum permitted value for this range.</param>
    /// <param name="maxValue">The maximum permitted value for this range.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
public static PropertyBuilder<T> IsEncryptedForRange<T>(
    this PropertyBuilder<T> builder,
    T minValue,
    T maxValue)
{
    builder.Metadata.SetQueryableEncryptionRangeMin(minValue);
    builder.Metadata.SetQueryableEncryptionRangeMax(maxValue);
    return IsEncryptedForRange(builder);
}

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for range queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <param name="minValue">The minimum permitted value for this range.</param>
    /// <param name="maxValue">The maximum permitted value for this range.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<T> IsEncryptedForRange<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId,
        T minValue,
        T maxValue)
    {
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return IsEncryptedForRange(builder, minValue, maxValue);
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for equality queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
    public static PropertyBuilder<T> IsEncryptedForEquality<T>(
        this PropertyBuilder<T> builder)
    {
        builder.Metadata.SetQueryableEncryptionType(QueryableEncryptionType.Equality);
        return builder;
    }

    /// <summary>
    /// Configures a property to be encrypted using MongoDB Queryable Encryption with support for equality queries.
    /// </summary>
    /// <param name="builder">The <see cref="PropertyBuilder"/> for the property being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="T">The type of property being configured.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<T> IsEncryptedForEquality<T>(
        this PropertyBuilder<T> builder,
        Guid dataKeyId)
    {
        builder.Metadata.SetEncryptionDataKeyId(dataKeyId);
        return IsEncryptedForEquality(builder);
    }

    /// <summary>
    /// Configures an owned entity object to be encrypted using MongoDB Queryable Encryption.
    /// </summary>
    /// <param name="builder">The <see cref="OwnedNavigationBuilder"/> for the owned entity being configured.</param>
    /// <typeparam name="TOwner">The type that owns this object.</typeparam>
    /// <typeparam name="TDependent">The type of the object that is owned.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    /// <remarks>Will attempt to use an encryption data key id specified at entity level. If that is not available, then it will
    /// attempt to use one specified at model level. If that is also unavailable, then will throw at model validation
    /// time.</remarks>
    public static OwnedNavigationBuilder<TOwner, TDependent> IsEncrypted<TOwner, TDependent>(
        this OwnedNavigationBuilder<TOwner, TDependent> builder) where TOwner : class where TDependent : class
    {
        builder.Metadata.SetAnnotation(MongoAnnotationNames.QueryableEncryptionType, QueryableEncryptionType.NotQueryable);
        return builder;
    }

    /// <summary>
    /// Configures an owned entity object to be encrypted using MongoDB Queryable Encryption.
    /// </summary>
    /// <param name="builder">The <see cref="OwnedNavigationBuilder"/> for the owned entity being configured.</param>
    /// <param name="dataKeyId">The <see cref="Guid"/> with the encryption data key id to be used.</param>
    /// <typeparam name="TOwner">The type that owns this object.</typeparam>
    /// <typeparam name="TDependent">The type of the object that is owned.</typeparam>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static OwnedNavigationBuilder<TOwner, TDependent> IsEncrypted<TOwner, TDependent>(
        this OwnedNavigationBuilder<TOwner, TDependent> builder, Guid dataKeyId) where TOwner : class where TDependent : class
    {
        builder.Metadata.SetAnnotation(MongoAnnotationNames.EncryptionDataKeyId, dataKeyId);
        return IsEncrypted(builder);
    }
}
