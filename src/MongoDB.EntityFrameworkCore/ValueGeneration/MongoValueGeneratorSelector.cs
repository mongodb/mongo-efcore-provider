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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// Implements <see cref="ValueGeneratorSelector"/> adding temporary ID
/// functionality for owned entity collections.
/// </summary>
public class MongoValueGeneratorSelector : ValueGeneratorSelector
{
    /// <summary>
    /// Create a new <see cref="MongoValueGeneratorSelector"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ValueGeneratorSelectorDependencies"/> to use.</param>
    public MongoValueGeneratorSelector(ValueGeneratorSelectorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override ValueGenerator? FindForType(IProperty property, IEntityType entityType, Type clrType)
    {
        // Generated unique integer identifiers for owned entities internal index
        if (entityType.IsOwned() && clrType == typeof(int) && property.IsShadowProperty())
        {
            return new OwnedEntityIndexValueGenerator();
        }

        // Base class generates Guid even if stored as string or binary
        return base.FindForType(property, entityType, clrType);
    }
}
