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

using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that ensures embedded objects are correctly configured as owned entities.
/// </summary>
public class MongoRelationshipDiscoveryConvention : RelationshipDiscoveryConvention
{
    /// <summary>
    /// Creates a <see cref="MongoRelationshipDiscoveryConvention"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="MongoRelationshipDiscoveryConvention"/> required by this convention.</param>
    public MongoRelationshipDiscoveryConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override bool? ShouldBeOwned(Type targetType, IConventionModel model)
        => ShouldBeOwnedType(targetType, model);

    /// <summary>
    /// Determine if a given entity type should be added as an owned entity if not already in the model.
    /// </summary>
    /// <param name="targetType">Target entity <see cref="Type"/>.</param>
    /// <param name="model">The <see cref="IConventionModel"/> being built.</param>
    /// <returns><see langword="true"/> if the type is to be owned, <see langword="false"/> otherwise.</returns>
    public static bool ShouldBeOwnedType(
        Type targetType,
        IConventionModel model)
        => !targetType.IsGenericType
           || targetType == typeof(Dictionary<string, object>)
           || targetType.GetInterface(typeof(IEnumerable<>).Name) == null;
}
