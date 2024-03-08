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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// Abstract class to register unsupported attributes on properties as <see cref="MongoAnnotationNames.NotSupportedAttributes"/>
/// allowing <see cref="MongoModelValidator"/> to throw when encountered.
/// </summary>
/// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> conventions depend upon.</param>
/// <typeparam name="T">The type of <see cref="Attribute"/> to register as unsupported.</typeparam>
public abstract class NotSupportedPropertyAttributeConvention<T>(ProviderConventionSetBuilderDependencies dependencies)
    : PropertyAttributeConventionBase<T>(dependencies) where T : Attribute
{
    /// <inheritdoc />
    protected override void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        T attribute,
        MemberInfo clrMember,
        IConventionContext context)
    {
        var meta = propertyBuilder.Metadata;
        meta.SetAnnotation(MongoAnnotationNames.NotSupportedAttributes, attribute);
    }
}
