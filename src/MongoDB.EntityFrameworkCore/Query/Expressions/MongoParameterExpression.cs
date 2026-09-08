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
    /// <param name="extractFromEntityValue">
    /// When <see langword="true"/>, the runtime value bound to <paramref name="name"/> is a WHOLE ENTITY
    /// instance (e.g. a captured local compared via <c>c == local</c>), not the property's own value —
    /// <paramref name="forSerialization"/>'s <see cref="IPropertyBase.GetGetter"/> must be applied to that
    /// instance, per execution, to obtain the actual value before serialization. See the entity-equality
    /// rewrite in <c>MongoExpressionTranslator.EntityEquality.cs</c>.
    /// </param>
    /// <param name="arrayElementIndex">See <see cref="ArrayElementIndex"/>. Mutually exclusive with
    /// <paramref name="extractFromEntityValue"/> — no node needs both.</param>
    public MongoParameterExpression(
        string name, IProperty? forSerialization, bool extractFromEntityValue = false, int? arrayElementIndex = null)
    {
        Name = name;
        ForSerialization = forSerialization;
        ExtractFromEntityValue = extractFromEntityValue;
        ArrayElementIndex = arrayElementIndex;
    }

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>
    /// Optional property metadata used by the renderer to select the correct serializer.
    /// </summary>
    public IProperty? ForSerialization { get; }

    /// <summary>
    /// See the constructor parameter of the same name.
    /// </summary>
    public bool ExtractFromEntityValue { get; }

    /// <summary>
    /// When set, the runtime value bound to <see cref="Name"/> is an ARRAY, and this is the constant index
    /// of the element actually compared (e.g. <c>args[0]</c> where <c>args</c> is a compiled query's own
    /// array-typed parameter) — the element at this index must be extracted from the array, per execution,
    /// before it is serialized with <see cref="ForSerialization"/>'s serializer. See
    /// <see cref="NativeTranslation.NativeQueryParameter.TryGetParameterArrayElementIndex"/>.
    /// </summary>
    public int? ArrayElementIndex { get; }

    /// <inheritdoc />
    public override Type Type
        => ForSerialization?.ClrType ?? typeof(object);
}
