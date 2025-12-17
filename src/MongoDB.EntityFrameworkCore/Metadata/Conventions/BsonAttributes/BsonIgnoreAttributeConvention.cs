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

using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;
#if EF8
using Microsoft.EntityFrameworkCore.Metadata.Internal;
#else
using Microsoft.EntityFrameworkCore.Internal;
#endif

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

/// <summary>
/// A convention that configures the element name for entity properties based on an applied <see cref="BsonElementAttribute" /> for
/// familiarity/compatibility with the Mongo C# Driver.
/// </summary>
public sealed class BsonIgnoreAttributeConvention : IEntityTypeAddedConvention, IComplexPropertyAddedConvention
{
    /// <summary>
    /// Creates a <see cref="BsonIgnoreAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public BsonIgnoreAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
        Dependencies = dependencies;
    }

    /// <summary>
    /// Dependencies for this convention.
    /// </summary>
    public ProviderConventionSetBuilderDependencies Dependencies { get; }

    /// <inheritdoc />
    public void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        var entityType = entityTypeBuilder.Metadata;
        var members = entityType.GetRuntimeProperties().Values.Cast<MemberInfo>()
            .Concat(entityType.GetRuntimeFields().Values);

        foreach (var member in members)
        {
            if (Attribute.IsDefined(member, typeof(BsonIgnoreAttribute), inherit: true))
            {
                entityTypeBuilder.Ignore(GetMemberName(member), fromDataAnnotation: true);
            }
        }
    }

    /// <inheritdoc />
    public void ProcessComplexPropertyAdded(
        IConventionComplexPropertyBuilder propertyBuilder,
        IConventionContext<IConventionComplexPropertyBuilder> context)
    {
        var complexType = propertyBuilder.Metadata.ComplexType;
        var members = complexType.GetRuntimeProperties().Values.Cast<MemberInfo>()
            .Concat(complexType.GetRuntimeFields().Values);

        foreach (var member in members)
        {
            if (Attribute.IsDefined(member, typeof(BsonIgnoreAttribute), inherit: true))
            {
                complexType.Builder.Ignore(GetMemberName(member), fromDataAnnotation: true);
            }
        }
    }

    private static string GetMemberName(MemberInfo member)
    {
        var name = member.Name;
        var index = member.Name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }
}
