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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Model extension methods for MongoDB-specific metadata.
/// </summary>
public static class MongoDbModelExtensions
{
    /// <summary>
    /// Returns the encryption data key id used as the default for encrypted properties in the model.
    /// </summary>
    /// <param name="model">The model to get the default encryption data key id for.</param>
    /// <returns>The encryption data key id used as the default for encrypted properties in the model, or <see langword="null"/> if not set.</returns>
    public static Guid? GetEncryptionDataKeyId(this IReadOnlyModel model)
        => model is RuntimeModel
            ? throw new InvalidOperationException(CoreStrings.RuntimeModelMissingData)
            : (Guid?)model[MongoAnnotationNames.EncryptionDataKeyId];

    /// <summary>
    /// Sets the encryption data key id used as the default for encrypted properties in the model.
    /// </summary>
    /// <param name="model">The model to set the default encryption data key id on.</param>
    /// <param name="dataKeyId">The encryption data key id to use as the default for encrypted properties in this model.</param>
    public static void SetEncryptionDataKeyId(this IMutableModel model, Guid? dataKeyId)
        => model.SetOrRemoveAnnotation(MongoAnnotationNames.EncryptionDataKeyId, Check.NotEmpty(dataKeyId));

    /// <summary>
    /// Sets the encryption data key id used as the default for encrypted properties in the model.
    /// </summary>
    /// <param name="model">The model to set the default encryption data key id on.</param>
    /// <param name="dataKeyId">The encryption data key id to use as the default for encrypted properties in this model.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static Guid? SetEncryptionDataKeyId(
        this IConventionModel model,
        Guid? dataKeyId,
        bool fromDataAnnotation = false)
        => (Guid?)model.SetOrRemoveAnnotation(
            MongoAnnotationNames.EncryptionDataKeyId,
            Check.NotEmpty(dataKeyId),
            fromDataAnnotation)?.Value;

    /// <summary>
    /// Returns the <see cref="ConfigurationSource" /> for the encryption data key id used as the default for encrypted properties in the model.
    /// </summary>
    /// <param name="model">The model to get the default encryption data key id for.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for the data key id used as the default for encrypted properties in the model.</returns>
    public static ConfigurationSource? GetEncryptionDataKeyIdConfigurationSource(this IConventionModel model)
        => model.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId)?.GetConfigurationSource();
}

