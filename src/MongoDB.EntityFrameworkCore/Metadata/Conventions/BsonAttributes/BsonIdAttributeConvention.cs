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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

/// <summary>
/// A convention that configures the element name for entity properties based on an applied <see cref="BsonIdAttribute" /> for
/// familiarity with the Mongo C# Driver.
/// </summary>
public sealed class BsonIdPropertyAttributeConvention : PropertyAttributeConventionBase<BsonIdAttribute>
{
    /// <summary>
    /// Creates a new instance of <see cref="BsonIdPropertyAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public BsonIdPropertyAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc/>
    protected override void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        BsonIdAttribute attribute,
        MemberInfo clrMember,
        IConventionContext context)
    {
        if (propertyBuilder.Metadata.DeclaringType is EntityType)
        {
            // Rely on PrimaryKeyDiscoveryConvention to map "_id" as primary key
            propertyBuilder.HasElementName("_id", fromDataAnnotation: true);
        }
        else
        {
            var property = propertyBuilder.Metadata;
            var member = property.GetIdentifyingMemberInfo();
            if (member != null && Attribute.IsDefined(member, typeof(BsonIdAttribute), inherit: true))
            {
                throw new InvalidOperationException(
                    CoreStrings.AttributeNotOnEntityTypeProperty("BsonId", property.DeclaringType.DisplayName(), property.Name));
            }
        }
    }
}
