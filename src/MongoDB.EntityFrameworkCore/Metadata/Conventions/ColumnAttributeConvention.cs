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

using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures the element name for entity properties based on an applied
/// <see cref="ColumnAttribute" /> for familiarity/compatibility with other EF Core providers.
/// </summary>
public class ColumnAttributeConvention :
    PropertyAttributeConventionBase<ColumnAttribute>,
    INavigationAddedConvention
{
    /// <summary>
    /// Creates a <see cref="CollectionAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public ColumnAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// For every property added to the model that has a <see cref="ColumnAttribute"/>
    /// use the specified name as an annotation to configure the element name used in BSON documents.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="clrMember">The member that has the attribute.</param>
    /// <param name="context">Additional information associated with convention execution.</param>
    protected override void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        ColumnAttribute attribute,
        MemberInfo clrMember,
        IConventionContext context)
    {
        if (!string.IsNullOrWhiteSpace(attribute.Name))
        {
            propertyBuilder.HasElementName(attribute.Name, fromDataAnnotation: true);
        }

        if (!string.IsNullOrWhiteSpace(attribute.TypeName))
        {
            Dependencies.Logger.ColumnAttributeWithTypeUsed(propertyBuilder.Metadata); // Will throw by default
        }
    }

    /// <summary>
    /// For every navigation added to the model that is an owned entity with a <see cref="ColumnAttribute"/>
    /// use the specified element name as an annotation to configure the element name used in the BSON documents.
    /// </summary>
    /// <param name="navigationBuilder">The builder for the navigation.</param>
    /// <param name="context">Additional information associated with convention execution.</param>
    public void ProcessNavigationAdded(
        IConventionNavigationBuilder navigationBuilder,
        IConventionContext<IConventionNavigationBuilder> context)
    {
        var meta = navigationBuilder.Metadata;
        var attribute = meta.PropertyInfo?.GetCustomAttributes().OfType<ColumnAttribute>().FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(attribute?.Name) && meta.TargetEntityType.IsOwned())
        {
            meta.TargetEntityType.SetContainingElementName(attribute.Name, fromDataAnnotation: true);
        }
    }
}
