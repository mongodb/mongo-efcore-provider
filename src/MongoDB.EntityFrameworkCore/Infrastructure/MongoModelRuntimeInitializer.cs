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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Initializes a <see cref="IModel" /> with the runtime dependencies.
/// </summary>
/// <param name="dependencies"></param>
public class MongoModelRuntimeInitializer(ModelRuntimeInitializerDependencies dependencies)
    : ModelRuntimeInitializer(dependencies)
{
    private static readonly Dictionary<Type, IDiscriminatorConvention>? DiscriminatorConventionDictionary =
        typeof(BsonSerializer).GetField("__discriminatorConventions", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dictionary<Type, IDiscriminatorConvention>;

    /// <summary>
    /// Validates and initializes the given model with runtime dependencies.
    /// </summary>
    /// <remarks>
    /// Specifically, this method initializes the MongoDB C# Driver with the necessary
    /// configuration to support the model and MongoDB EF Core provider.
    /// </remarks>
    /// <param name="model">The model to initialize.</param>
    /// <param name="designTime">Whether the model should contain design-time configuration.</param>
    /// <param name="validationLogger">The validation logger.</param>
    /// <returns>The initialized model.</returns>
    public override IModel Initialize(IModel model, bool designTime = true,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation>? validationLogger = null)
    {
        model = base.Initialize(model, designTime, validationLogger);

        ConfigureDriverConvention();
        SetupTypeDiscriminators(model);

        return model;
    }

    private static void ConfigureDriverConvention()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618 // Type or member is obsolete
    }

    private static void SetupTypeDiscriminators(IModel model)
    {
        if (DiscriminatorConventionDictionary == null)
        {
            throw new InvalidOperationException("Unable to access MongoDB C# Driver discriminator conventions.");
        }

        var discriminatorProperties = new HashSet<IReadOnlyProperty>();
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var discriminatorProperty = entityType.FindDiscriminatorProperty();
            if (discriminatorProperty != null)
            {
                discriminatorProperties.Add(discriminatorProperty);
            }
        }

        foreach (var discriminatorProperty in discriminatorProperties)
        {
            var entityType = (IReadOnlyEntityType)discriminatorProperty.DeclaringType;
            DiscriminatorConventionDictionary.TryAdd(entityType.ClrType, new MongoEFDiscriminator(entityType));
        }
    }
}
