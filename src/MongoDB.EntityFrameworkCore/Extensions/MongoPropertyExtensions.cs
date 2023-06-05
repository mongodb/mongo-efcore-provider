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

using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Property extension methods for MongoDB metadata.
/// </summary>
public static class MongoPropertyExtensions
{
    /// <summary>
    /// Returns the field name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the field name for.</param>
    /// <returns>Returns the field name that the property is mapped to.</returns>
    public static string GetFieldName(this IReadOnlyProperty property)
        => (string?)property[MongoAnnotationNames.FieldName]
           ?? GetDefaultFieldName(property);

    private static string GetDefaultFieldName(IReadOnlyProperty property) => property.Name;

    /// <summary>
    ///  Sets the field name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to obtain the field name for.</param>
    /// <param name="name">The name of the field that should be used.</param>
    public static void SetFieldName(this IMutableProperty property, string? name)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.FieldName, name);

    /// <summary>
    ///  Sets the field name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="name">The name to set.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static string? SetFieldName(
        this IConventionProperty property,
        string? name,
        bool fromDataAnnotation = false)
        => (string?)property.SetOrRemoveAnnotation(MongoAnnotationNames.FieldName, name, fromDataAnnotation)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> the field name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the field name for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the field name was specified by for this property.
    /// </returns>
    public static ConfigurationSource? GetFieldNameConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.FieldName)?.GetConfigurationSource();
}
