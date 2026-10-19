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
using System.Collections.Generic;
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
    /// <param name="valueType">See <see cref="ValueType"/>.</param>
    /// <param name="runtimeEvaluator">See <see cref="RuntimeEvaluator"/>.</param>
    public MongoParameterExpression(
        string name, IProperty? forSerialization, bool extractFromEntityValue = false, int? arrayElementIndex = null,
        Type? rawElementType = null, bool extractEntityKeyFromArrayElements = false, Type? valueType = null,
        Func<IReadOnlyDictionary<string, object?>, object?>? runtimeEvaluator = null)
    {
        Name = name;
        ForSerialization = forSerialization;
        ExtractFromEntityValue = extractFromEntityValue;
        ArrayElementIndex = arrayElementIndex;
        RawElementType = rawElementType;
        ExtractEntityKeyFromArrayElements = extractEntityKeyFromArrayElements;
        ValueType = valueType;
        RuntimeEvaluator = runtimeEvaluator;
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

    /// <summary>
    /// The CLR type of the LINQ query parameter the value is bound from, when known; <see langword="null"/> otherwise.
    /// Only a nullability hint (a non-nullable value type is never bound to null): serialization and <see cref="Type"/>
    /// still come from <see cref="ForSerialization"/>.
    /// </summary>
    public Type? ValueType { get; }

    /// <summary>
    /// When set, <see cref="Name"/> is not an EF query parameter: the value is computed by this delegate (over the
    /// execution's EF query-parameter values) once per <c>MongoPipelineFactory.Build</c>. Used for a closed subtree
    /// holding a clock member (<see cref="RuntimeClock"/>), which must never be baked into the cached template.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>, object?>? RuntimeEvaluator { get; }

    /// <inheritdoc />
    /// <remarks>
    /// A property-less runtime-evaluated value reports its evaluated CLR type, as the constant it replaces did
    /// (e.g. so a <c>$dateAdd</c> over <c>DateTime.UtcNow</c> stays <c>DateTime</c>-typed).
    /// </remarks>
    public override Type Type
        => ForSerialization?.ClrType ?? (RuntimeEvaluator is not null ? ValueType : null) ?? typeof(object);
}
