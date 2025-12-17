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

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extension methods for <see cref="PropertiesConfigurationBuilder" />.
/// </summary>
public static class MongoPropertiesConfigurationBuilderExtensions
{
    /// <summary>
    /// Configures the BSON representation that the properties are stored as when targeting MongoDB.
    /// </summary>
    /// <param name="propertiesConfigurationBuilder">The builder for the properties being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store these properties as.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertiesConfigurationBuilder HaveBsonRepresentation(
        this PropertiesConfigurationBuilder propertiesConfigurationBuilder,
        BsonType bsonType,
        bool? allowOverflow = null,
        bool? allowTruncation = null)
    {
        var representation = new BsonRepresentationConfiguration(bsonType, allowOverflow, allowTruncation);
        propertiesConfigurationBuilder.HaveAnnotation(MongoAnnotationNames.BsonRepresentation, representation.ToDictionary());

        return propertiesConfigurationBuilder;
    }

    /// <summary>
    /// Configures the BSON representation that the properties are stored as when targeting MongoDB.
    /// </summary>
    /// <typeparam name="TProperty">The type of the properties being configured.</typeparam>
    /// <param name="propertiesConfigurationBuilder">The builder for the properties being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store these properties as.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertiesConfigurationBuilder<TProperty> HaveBsonRepresentation<TProperty>(
        this PropertiesConfigurationBuilder<TProperty> propertiesConfigurationBuilder,
        BsonType bsonType,
        bool? allowOverflow = null,
        bool? allowTruncation = null)
        => (PropertiesConfigurationBuilder<TProperty>)HaveBsonRepresentation((PropertiesConfigurationBuilder)propertiesConfigurationBuilder, bsonType, allowOverflow, allowTruncation);

    /// <summary>
    /// Configures the <see cref="DateTimeKind"/> that <see cref="DateTime"/> and <see cref="DateTimeOffset"/> properties are stored as when targeting MongoDB.
    /// </summary>
    /// <param name="propertiesConfigurationBuilder">The builder for the properties being configured.</param>
    /// <param name="dateTimeKind">The <see cref="DateTimeKind"/> to store these properties as.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertiesConfigurationBuilder HaveDateTimeKind(
        this PropertiesConfigurationBuilder propertiesConfigurationBuilder,
        DateTimeKind dateTimeKind)
    {
        propertiesConfigurationBuilder.HaveAnnotation(MongoAnnotationNames.DateTimeKind, dateTimeKind);

        return propertiesConfigurationBuilder;
    }
}
