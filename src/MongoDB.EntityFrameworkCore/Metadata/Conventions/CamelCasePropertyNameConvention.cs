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
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures the element name for entity properties by using the camelcase naming convention.
/// </summary>
public class CamelCasePropertyNameConvention : IPropertyAddedConvention
{
    /// <summary>
    /// Creates a <see cref="CamelCasePropertyNameConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public CamelCasePropertyNameConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    ///  Dependencies this convention uses.
    /// </summary>
    protected virtual ProviderConventionSetBuilderDependencies Dependencies { get; }

    /// <summary>
    /// For every property that is added to the model set the element name to be the camel case
    /// version of the property name with symbols being removed and considered word separators.
    /// </summary>
    /// <param name="propertyBuilder"></param>
    /// <param name="context"></param>
    public void ProcessPropertyAdded(IConventionPropertyBuilder propertyBuilder,
        IConventionContext<IConventionPropertyBuilder> context)
    {
        if (propertyBuilder.Metadata.Name == "_id") return;

        propertyBuilder.HasElementName(propertyBuilder.Metadata.Name.ToCamelCase(CultureInfo.CurrentCulture));
    }
}
