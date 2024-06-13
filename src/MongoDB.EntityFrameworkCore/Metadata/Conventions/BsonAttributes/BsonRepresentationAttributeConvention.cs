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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

/// <summary>
/// A convention that configures the element name for entity properties based on an applied <see cref="BsonElementAttribute" /> for
/// familiarity with the Mongo C# Driver.
/// </summary>
public sealed class BsonRepresentationAttributeConvention : PropertyAttributeConventionBase<BsonRepresentationAttribute>
{
    /// <summary>
    /// Creates a <see cref="BsonRepresentationAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public BsonRepresentationAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        BsonRepresentationAttribute attribute,
        MemberInfo clrMember,
        IConventionContext context)
    {
        if (!Enum.IsDefined(attribute.Representation))
        {
            throw new NotSupportedException(
                $"Property '{clrMember.DeclaringType.ShortDisplayName()}.{clrMember.Name}' has {nameof(BsonRepresentationAttribute)} of {attribute.Representation} which a not supported type.");
        }

        propertyBuilder.HasBsonRepresentation(attribute.Representation, attribute.AllowOverflow, attribute.AllowTruncation);
    }
}
