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
/// Represents a parameterized value placeholder in a MongoDB query expression tree.
/// Used to carry query-parameter references that will be resolved at execution time
/// (the B2 placeholder in the native query pipeline).
/// An optional <see cref="ForSerialization"/> provides the <see cref="IProperty"/>
/// context needed by the renderer.
/// </summary>
internal sealed class MongoParameterExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoParameterExpression"/> with the given name.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="forSerialization">
    /// Optional <see cref="IProperty"/> that provides serialization context for
    /// the renderer. May be <see langword="null"/> for untyped parameters.
    /// </param>
    public MongoParameterExpression(string name, IProperty? forSerialization)
    {
        Name = name;
        ForSerialization = forSerialization;
    }

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>
    /// Optional property metadata used by the renderer to select the correct serializer.
    /// </summary>
    public IProperty? ForSerialization { get; }

    /// <inheritdoc />
    public override Type Type
        => ForSerialization?.ClrType ?? typeof(object);
}
