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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A constant value, optionally with the <see cref="IProperty"/> whose serializer the renderer should use.
/// </summary>
internal sealed class MongoConstantExpression : MongoExpression
{
    public MongoConstantExpression(object? value, IProperty? forSerialization)
    {
        Value = value;
        ForSerialization = forSerialization;
    }

    public object? Value { get; }

    /// <summary>Property whose serializer renders the value; <see langword="null"/> for untyped constants.</summary>
    public IProperty? ForSerialization { get; }

    /// <inheritdoc />
    public override Type Type
        => Value?.GetType() ?? typeof(object);
}
