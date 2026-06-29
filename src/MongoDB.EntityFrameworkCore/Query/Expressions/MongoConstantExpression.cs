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
/// Represents a constant value in a MongoDB query expression tree.
/// An optional <see cref="ForSerialization"/> property provides the
/// <see cref="IProperty"/> context for serialization (used by the renderer).
/// </summary>
internal sealed class MongoConstantExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoConstantExpression"/> with the given value.
    /// </summary>
    /// <param name="value">The constant value.</param>
    /// <param name="forSerialization">
    /// Optional <see cref="IProperty"/> that provides serialization context for
    /// the renderer. May be <see langword="null"/> for untyped constants.
    /// </param>
    public MongoConstantExpression(object? value, IProperty? forSerialization)
    {
        Value = value;
        ForSerialization = forSerialization;
    }

    /// <summary>The constant value.</summary>
    public object? Value { get; }

    /// <summary>
    /// Optional property metadata used by the renderer to select the correct serializer.
    /// </summary>
    public IProperty? ForSerialization { get; }

    /// <inheritdoc />
    public override Type Type
        => Value?.GetType() ?? typeof(object);
}
