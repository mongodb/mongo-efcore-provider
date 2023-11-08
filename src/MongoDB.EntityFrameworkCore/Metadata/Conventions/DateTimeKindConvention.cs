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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that set <see cref="DateTimeKind"/> for <see cref="DateTime"/> properties.
/// </summary>
public class DateTimeKindConvention : IPropertyAddedConvention
{
    /// <summary>
    /// Creates a <see cref="DateTimeKindConvention"/>.
    /// </summary>
    /// <param name="dateTimeKind"><see cref="DateTimeKind"/> to use</param>
    public DateTimeKindConvention(DateTimeKind dateTimeKind)
    {
        DateTimeKind = dateTimeKind;
    }

    /// <summary>
    /// <see cref="DateTimeKind"/> to use.
    /// </summary>
    protected DateTimeKind DateTimeKind { get; }

    /// <summary>
    /// For every property of <see cref="DateTime"/> type that is added to the model set the configured <see cref="DateTimeKind"/>.
    /// </summary>
    /// <param name="propertyBuilder"></param>
    /// <param name="context"></param>
    public void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        IConventionContext<IConventionPropertyBuilder> context)
    {
        var clrType = propertyBuilder.Metadata.ClrType;
        if (clrType.IsNullableValueType())
        {
            clrType = Nullable.GetUnderlyingType(clrType);
        }

        if (clrType != typeof(DateTime))
        {
            return;
        }

        propertyBuilder.HasDateTimeKind(DateTimeKind);
    }
}
