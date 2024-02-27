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

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions.BsonAttributes;

/// <summary>
///  A convention that configures properties as required if they have the <see cref="BsonRequiredAttribute" /> applied.
/// </summary>
/// <remarks>
///     See <see href="https://aka.ms/efcore-docs-conventions">Model building conventions</see> for more information and examples.
/// </remarks>
public sealed class BsonRequiredPropertyAttributeConvention : PropertyAttributeConventionBase<BsonRequiredAttribute>,
    IComplexPropertyAddedConvention,
    IComplexPropertyFieldChangedConvention
{
    /// <summary>
    /// Creates a new instance of <see cref="BsonRequiredPropertyAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public BsonRequiredPropertyAttributeConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        BsonRequiredAttribute attribute,
        MemberInfo clrMember,
        IConventionContext context)
        => propertyBuilder.IsRequired(true, fromDataAnnotation: true);

    /// <inheritdoc />
    protected override void ProcessPropertyAdded(
        IConventionComplexPropertyBuilder propertyBuilder,
        BsonRequiredAttribute attribute,
        MemberInfo clrMember,
        IConventionContext context)
        => propertyBuilder.IsRequired(true, fromDataAnnotation: true);
}
