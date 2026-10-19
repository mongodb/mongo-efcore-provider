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
/// A query-parameter placeholder resolved at execution time. <see cref="ForSerialization"/>, when set, supplies the
/// serializer context for the renderer.
/// </summary>
internal sealed class MongoParameterExpression : MongoExpression
{
    /// <summary>Creates a <see cref="MongoParameterExpression"/>.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="forSerialization">Serializer context for the renderer; <see langword="null"/> if untyped.</param>
    /// <param name="extractFromEntityValue">
    /// The bound value is a whole entity (e.g. <c>c == local</c>), so <paramref name="forSerialization"/>'s getter
    /// must be applied to it per execution before serializing. See <c>MongoExpressionTranslator.EntityEquality.cs</c>.
    /// </param>
    /// <param name="arrayElementIndex">See <see cref="ArrayElementIndex"/>. Mutually exclusive with
    /// <paramref name="extractFromEntityValue"/>.</param>
    /// <param name="rawElementType">See <see cref="RawElementType"/>.</param>
    /// <param name="extractEntityKeyFromArrayElements">
    /// The bound value is an array of whole entities (e.g. <c>customers.Contains(c)</c>); the getter is applied to
    /// each non-null element, and null elements pass through as BSON null. Per-element analog of
    /// <paramref name="extractFromEntityValue"/>; mutually exclusive with it and <paramref name="arrayElementIndex"/>.
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

    /// <summary>Property metadata the renderer uses to select the serializer.</summary>
    public IProperty? ForSerialization { get; }

    /// <summary>See the constructor parameter of the same name.</summary>
    public bool ExtractFromEntityValue { get; }

    /// <summary>
    /// When set, the bound value is an array or tuple and this is the index of the element to extract per
    /// execution before serializing. Produced by <c>args[0]</c> on a compiled query's array parameter (see
    /// <see cref="NativeTranslation.NativeQueryParameter.TryGetParameterArrayElementIndex"/>) and by a funcletized
    /// <c>Tuple.Create(...)</c> operand (see
    /// <see cref="NativeTranslation.MongoExpressionTranslator.TryDecomposeTupleOperand"/>).
    /// </summary>
    public int? ArrayElementIndex { get; }

    /// <summary>
    /// Element CLR type for a property-less <c>Contains</c> collection (<see cref="ForSerialization"/> is null; see
    /// <c>MongoExpressionTranslator.TranslateInValuesRaw</c>), so the renderer picks a default element serializer
    /// instead of assuming <see cref="string"/>.
    /// </summary>
    public Type? RawElementType { get; }

    /// <summary>See the constructor parameter of the same name.</summary>
    public bool ExtractEntityKeyFromArrayElements { get; }

    /// <inheritdoc />
    public override Type Type
        => ForSerialization?.ClrType ?? typeof(object);
}
