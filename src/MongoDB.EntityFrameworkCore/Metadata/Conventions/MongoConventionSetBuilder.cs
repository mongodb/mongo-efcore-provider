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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// Creates the <see cref="ConventionSet" /> for the MongoDB provider.
/// </summary>
public class MongoConventionSetBuilder : ProviderConventionSetBuilder
{
    /// <summary>
    /// Creates a <see cref="MongoConventionSetBuilder" /> with the required dependencies.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this service.</param>
    public MongoConventionSetBuilder(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Builds and returns the convention set for the MongoDB provider.
    /// </summary>
    /// <returns>The convention set for the MongoDB provider.</returns>
    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();

        // New MongoDB-specific conventions
        conventionSet.Add(new CollectionAttributeConvention(Dependencies));
        conventionSet.Add(new CollectionNameFromDbSetConvention(Dependencies));

        // Convenience conventions for users familiar with EF
        conventionSet.Add(new ColumnAttributeConvention(Dependencies));
        conventionSet.Add(new TableAttributeConvention(Dependencies));

        // Convenience conventions for users familiar with the Mongo C# Driver
        conventionSet.Add(new BsonDateTimeOptionsAttributeConvention(Dependencies));
        conventionSet.Add(new BsonElementAttributeConvention(Dependencies));
        conventionSet.Add(new BsonIdPropertyAttributeConvention(Dependencies));
        conventionSet.Add(new BsonIgnoreAttributeConvention(Dependencies));
        conventionSet.Add(new BsonRequiredPropertyAttributeConvention(Dependencies));

        // Unsupported attributes on properties that should throw not supported
        // Note: Non-property level attributes are directly checked in MongoModelValidator.
        conventionSet.Add(new BsonDefaultValueAttributeConvention(Dependencies));
        conventionSet.Add(new BsonDictionaryOptionsAttributeConvention(Dependencies));
        conventionSet.Add(new BsonExtraElementsAttributeConvention(Dependencies));
        conventionSet.Add(new BsonGuidRepresentationAttributeConvention(Dependencies));
        conventionSet.Add(new BsonIgnoreIfDefaultAttributePropertyAttributeConvention(Dependencies));
        conventionSet.Add(new BsonIgnoreIfNullAttributePropertyAttributeConvention(Dependencies));
        conventionSet.Add(new BsonRepresentationAttributeConvention(Dependencies));
        conventionSet.Add(new BsonSerializationOptionsAttributeConvention(Dependencies));
        conventionSet.Add(new BsonSerializerPropertyConvention(Dependencies));
        conventionSet.Add(new BsonBsonTimeSpanOptionsPropertyAttributeConvention(Dependencies));

        // Replace default conventions with MongoDB-specific ones
        conventionSet.Replace<KeyDiscoveryConvention>(new PrimaryKeyDiscoveryConvention(Dependencies));
        conventionSet.Replace<ValueGenerationConvention>(new MongoValueGenerationConvention(Dependencies));
        conventionSet.Replace<RelationshipDiscoveryConvention>(new MongoRelationshipDiscoveryConvention(Dependencies));

        return conventionSet;
    }

    /// <summary>
    /// Call this method to build a <see cref="ConventionSet" /> for the MongoDB provider when using
    /// the <see cref="ModelBuilder" /> outside of <see cref="DbContext.OnModelCreating" />.
    /// </summary>
    /// <returns>The convention set.</returns>
    public static ConventionSet Build()
    {
        using var serviceScope = CreateServiceScope<DbContext>();
        using var context = serviceScope.ServiceProvider.GetRequiredService<DbContext>();
        return ConventionSet.CreateConventionSet(context);
    }

    /// <summary>
    /// Call this method to build a <see cref="ModelBuilder" /> for MongoDB outside of <see cref="DbContext.OnModelCreating" />.
    /// </summary>
    /// <remarks>
    /// Note that it is unusual to use this method. Consider using <see cref="DbContext" /> in the normal way instead.
    /// </remarks>
    /// <returns>The model builder with this convention set.</returns>
    public static ModelBuilder CreateModelBuilder()
    {
        using var serviceScope = CreateServiceScope<DbContext>();
        using var context = serviceScope.ServiceProvider.GetRequiredService<DbContext>();
        return new ModelBuilder(ConventionSet.CreateConventionSet(context), context.GetService<ModelDependencies>());
    }

    /// <summary>
    /// Call this method to build a <see cref="ModelBuilder" /> for MongoDB outside of <see cref="DbContext.OnModelCreating" />.
    /// </summary>
    /// <remarks>
    /// Note that it is unusual to use this method. Consider using <see cref="DbContext" /> in the normal way instead.
    /// </remarks>
    /// <returns>The model builder with this convention set.</returns>
    public static ModelBuilder CreateModelBuilder<T>() where T : DbContext
    {
        using var serviceScope = CreateServiceScope<T>();
        using var context = serviceScope.ServiceProvider.GetRequiredService<T>();
        return new ModelBuilder(ConventionSet.CreateConventionSet(context), context.GetService<ModelDependencies>());
    }

    private static IServiceScope CreateServiceScope<T>() where T : DbContext
    {
        var serviceProvider = new ServiceCollection()
            .AddEntityFrameworkMongoDB()
            .AddDbContext<T>(
                (p, o) =>
                    o.UseMongoDB("mongodb://localhost:27017", "_")
                        .UseInternalServiceProvider(p))
            .BuildServiceProvider();

        return serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope();
    }
}
