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

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures the element name for entity properties by using a camel-case naming convention.
/// </summary>
public sealed class CamelCaseElementNameConvention : IPropertyAddedConvention, INavigationAddedConvention
{
    /// <summary>
    /// For every property that is added to the model set the element name to be the camel case
    /// version of the property name with symbols being removed and considered word separators.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property.</param>
    /// <param name="context">Additional information associated with convention execution.</param>
    public void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        IConventionContext<IConventionPropertyBuilder> context)
    {
        if (propertyBuilder.Metadata.Name == "_id" || propertyBuilder.Metadata.IsOwnedTypeOrdinalKey()) return;

        propertyBuilder.HasElementName(propertyBuilder.Metadata.Name.ToCamelCase(CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// For every navigation that is added to the model set the element name to be the camel case
    /// version of the navigation property name with symbols being removed and considered word separators.
    /// </summary>
    /// <param name="navigationBuilder">The builder for the navigation.</param>
    /// <param name="context">Additional information associated with convention execution.</param>
    public void ProcessNavigationAdded(
        IConventionNavigationBuilder navigationBuilder,
        IConventionContext<IConventionNavigationBuilder> context)
    {
        var name = navigationBuilder.Metadata.Name.ToCamelCase(CultureInfo.CurrentCulture);
        navigationBuilder.Metadata.TargetEntityType.SetAnnotation(MongoAnnotationNames.ElementName, name);
    }
}
