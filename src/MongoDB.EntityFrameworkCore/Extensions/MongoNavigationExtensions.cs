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
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// MongoDB-specific extension methods for <see cref="IReadOnlyNavigation" />.
/// </summary>
public static class MongoNavigationExtensions
{
    /// <summary>
    /// Returns the encryption data key id used to encrypt the owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IForeignKey"/> to obtain the encryption data key id for.</param>
    /// <returns>The encryption data key id used to encrypt the property, or <see langword="null"/> if not set.</returns>
    public static Guid? GetDataEncryptionKeyId(this IReadOnlyForeignKey navigation)
        => (Guid?)navigation.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId)?.Value;

    /// <summary>
    /// Sets the encryption data key id used to encrypt the owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IMutableForeignKey"/> to set the encryption data key id for.</param>
    /// <param name="dataKeyId">The encryption data key id to set, or <see langword="null" /> to unset the value.</param>
    public static void SetDataEncryptionKeyId(this IMutableForeignKey navigation, Guid? dataKeyId)
        => navigation.SetOrRemoveAnnotation(MongoAnnotationNames.EncryptionDataKeyId, Check.NotEmpty(dataKeyId));

    /// <summary>
    /// Sets the encryption data key id used to encrypt the owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IConventionForeignKey"/> to set the encryption data key id for.</param>
    /// <param name="dataKeyId">The encryption data key id to set, or <see langword="null" /> to unset the value.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured encryption data key id, or <see langword="null"/> if it is not set.</returns>
    public static Guid? SetDataEncryptionKeyId(this IConventionForeignKey navigation, Guid? dataKeyId,
        bool fromDataAnnotation = false)
        => (Guid?)navigation
            .SetOrRemoveAnnotation(MongoAnnotationNames.EncryptionDataKeyId, Check.NotEmpty(dataKeyId), fromDataAnnotation)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource"/> for the encryption data key id when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IConventionForeignKey"/> to set the encryption data key id configuration source for.</param>
    /// <returns>The <see cref="ConfigurationSource"/> for the encryption data key id, or <see langword="null"/> if it is not set.</returns>
    public static ConfigurationSource? GetDataEncryptionKeyIdConfigurationSource(this IConventionForeignKey navigation)
        => navigation.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId)?.GetConfigurationSource();


    /// <summary>
    /// Returns the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IForeignKey"/> to obtain the encryption data key id for.</param>
    /// <returns>The <see cref="QueryableEncryptionType"/>, or <see langword="null"/> if not set.</returns>
    public static QueryableEncryptionType? GetQueryableEncryptionType(this IReadOnlyForeignKey navigation)
        => (QueryableEncryptionType?)navigation.FindAnnotation(MongoAnnotationNames.QueryableEncryptionType)?.Value;

    /// <summary>
    /// Sets the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IMutableForeignKey"/> to set the Queryable Encryption type for.</param>
    /// <param name="queryableEncryptionType">The <see cref="QueryableEncryptionType"/> specifying the type of Queryable Encryption to use, or <see langword="null" /> to unset the value.</param>
    public static void SetQueryableEncryptionType(this IMutableForeignKey navigation,
        QueryableEncryptionType? queryableEncryptionType)
        => navigation.SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionType,
            Check.IsDefinedOrNull(queryableEncryptionType));

    /// <summary>
    /// Sets the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IConventionForeignKey"/> to set the encryption data key id for.</param>
    /// <param name="queryableEncryptionType">The <see cref="QueryableEncryptionType"/> specifying the type of Queryable Encryption to use, or <see langword="null" /> to unset the value.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The <see cref="QueryableEncryptionType"/>, or <see langword="null"/> if not set.</returns>
    public static QueryableEncryptionType? SetQueryableEncryptionType(this IConventionForeignKey navigation,
        QueryableEncryptionType? queryableEncryptionType, bool fromDataAnnotation = false)
        => (QueryableEncryptionType?)navigation.SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionType,
            Check.IsDefinedOrNull(queryableEncryptionType), fromDataAnnotation)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the Queryable Encryption type for the owned entity when targeting MongoDB.
    /// </summary>
    /// <param name="navigation">The <see cref="IConventionForeignKey"/> to set the encryption data key id for.</param>
    /// <returns>The <see cref="QueryableEncryptionType"/>, or <see langword="null"/> if not set.</returns>
    public static ConfigurationSource? GetQueryableEncryptionTypeConfigurationSource(this IConventionForeignKey navigation)
        => navigation.FindAnnotation(MongoAnnotationNames.QueryableEncryptionType)?.GetConfigurationSource();

    /// <summary>
    /// Determine whether a navigation is embedded or not.
    /// </summary>
    /// <param name="navigation">The <see cref="IReadOnlyNavigation"/> to consider.</param>
    /// <returns>
    /// <see langword="true"/> if the navigation is embedded,
    /// <see langword="false"/> if it is not.
    /// </returns>
    public static bool IsEmbedded(this IReadOnlyNavigation navigation)
        => !navigation.IsOnDependent
           && !navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot();
}
