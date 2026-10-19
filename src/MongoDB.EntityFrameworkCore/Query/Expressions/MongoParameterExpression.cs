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
    /// <param name="rawElementType">See <see cref="RawElementType"/>.</param>
    /// <param name="extractEntityKeyFromArrayElements">
    /// When <see langword="true"/>, the runtime value bound to <paramref name="name"/> is an ARRAY of WHOLE
    /// ENTITY instances (e.g. the collection side of <c>customers.Contains(c)</c>), not an array of the
    /// property's own values — <paramref name="forSerialization"/>'s <see cref="IPropertyBase.GetGetter"/>
    /// must be applied to EACH non-null element, per execution, before it is serialized; a
    /// <see langword="null"/> element passes through as a BSON null rather than being extracted. Mutually
    /// exclusive with <paramref name="extractFromEntityValue"/> and <paramref name="arrayElementIndex"/> — this
    /// is the per-ELEMENT analog of <see cref="ExtractFromEntityValue"/>, for the $in-values side of an
    /// entity-list <c>Contains</c> rather than a single entity-typed comparand. See the entity-list-Contains
    /// rewrite in <c>MongoExpressionTranslator.EntityEquality.cs</c>.
    /// </param>
    public MongoParameterExpression(
        string name, IProperty? forSerialization, bool extractFromEntityValue = false, int? arrayElementIndex = null,
        Type? rawElementType = null, bool extractEntityKeyFromArrayElements = false)
    {
        Name = name;
        ForSerialization = forSerialization;
        ExtractFromEntityValue = extractFromEntityValue;
        ArrayElementIndex = arrayElementIndex;
        RawElementType = rawElementType;
        ExtractEntityKeyFromArrayElements = extractEntityKeyFromArrayElements;
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
    /// When set, the runtime value bound to <see cref="Name"/> is an ARRAY or a TUPLE, and this is the
    /// constant index of the element actually compared — the element at this index must be extracted from
    /// the array/tuple, per execution, before it is serialized with <see cref="ForSerialization"/>'s
    /// serializer. Two distinct shapes produce this: <c>args[0]</c> where <c>args</c> is a compiled query's
    /// own array-typed parameter (see
    /// <see cref="NativeTranslation.NativeQueryParameter.TryGetParameterArrayElementIndex"/>), and a
    /// <c>Tuple.Create(...)</c> operand EF Core's parameter extraction funcletized into a single
    /// materialized-tuple parameter (see
    /// <see cref="NativeTranslation.MongoExpressionTranslator.TryDecomposeTupleOperand"/>).
    /// </summary>
    public int? ArrayElementIndex { get; }

    /// <summary>
    /// When <see cref="ForSerialization"/> is <see langword="null"/> (a property-less, COMPUTED-needle
    /// <c>Contains</c> collection — see <c>MongoExpressionTranslator.TranslateInValuesRaw</c>), the CLR type of
    /// the collection's elements, used by the renderer to pick a default (representation-less) element
    /// serializer instead of assuming <see cref="string"/>.
    /// </summary>
    public Type? RawElementType { get; }

    /// <summary>
    /// See the constructor parameter of the same name.
    /// </summary>
    public bool ExtractEntityKeyFromArrayElements { get; }

    /// <inheritdoc />
    public override Type Type
        => ForSerialization?.ClrType ?? typeof(object);
}
