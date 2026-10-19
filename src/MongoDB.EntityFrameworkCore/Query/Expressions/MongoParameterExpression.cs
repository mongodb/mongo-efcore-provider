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
    /// <param name="forSerialization">The property supplying the serializer context, if any.</param>
    /// <param name="extractFromEntityValue">
    /// The bound value is a whole entity (e.g. <c>c == local</c>), so <paramref name="forSerialization"/>'s getter
    /// must be applied to it per execution before serializing. See <c>MongoExpressionTranslator.EntityEquality.cs</c>.
    /// </param>
    /// <param name="arrayElementIndex">See <see cref="ArrayElementIndex"/>. Mutually exclusive with
    /// <paramref name="extractFromEntityValue"/>.</param>
    /// <param name="rawElementType">The raw element type of the bound value, if any.</param>
    /// <param name="extractEntityKeyFromArrayElements">
    /// The bound value is an array of whole entities (e.g. <c>customers.Contains(c)</c>); the getter is applied to
    /// each non-null element, and null elements pass through as BSON null. Per-element analog of
    /// <paramref name="extractFromEntityValue"/>; mutually exclusive with it and <paramref name="arrayElementIndex"/>.
    /// </param>
    /// <param name="valueType">The type of the bound value, if different from the property's.</param>
    /// <param name="runtimeEvaluator">Computes the value from the parameter values at execution time.</param>
    /// <param name="explicitSerializer">A serializer to use instead of one derived from <paramref name="forSerialization"/>.</param>
    public MongoParameterExpression(
        string name, IProperty? forSerialization, bool extractFromEntityValue = false, int? arrayElementIndex = null,
        Type? rawElementType = null, bool extractEntityKeyFromArrayElements = false, Type? valueType = null,
        Func<IReadOnlyDictionary<string, object?>, object?>? runtimeEvaluator = null,
        MongoDB.Bson.Serialization.IBsonSerializer? explicitSerializer = null)
    {
        Name = name;
        ForSerialization = forSerialization;
        ExtractFromEntityValue = extractFromEntityValue;
        ArrayElementIndex = arrayElementIndex;
        RawElementType = rawElementType;
        ExtractEntityKeyFromArrayElements = extractEntityKeyFromArrayElements;
        ValueType = valueType;
        RuntimeEvaluator = runtimeEvaluator;
        ExplicitSerializer = explicitSerializer;
    }

    public string Name { get; }

    /// <summary>Property metadata the renderer uses to select the serializer.</summary>
    public IProperty? ForSerialization { get; }

    /// <summary>See the constructor parameter of the same name.</summary>
    public bool ExtractFromEntityValue { get; }

    /// <summary>
    /// When set, the bound value is an array or tuple and this is the index of the element to extract per execution
    /// (<c>args[0]</c> on a compiled query's array parameter, or a funcletized <c>Tuple.Create(...)</c> operand).
    /// </summary>
    public int? ArrayElementIndex { get; }

    /// <summary>
    /// Element CLR type for a property-less <c>Contains</c> collection, so the renderer picks a default element
    /// serializer instead of assuming <see cref="string"/>.
    /// </summary>
    public Type? RawElementType { get; }

    /// <summary>See the constructor parameter of the same name.</summary>
    public bool ExtractEntityKeyFromArrayElements { get; }

    /// <summary>
    /// The CLR type of the bound LINQ parameter, when known. Only a nullability hint: serialization and
    /// <see cref="Type"/> still come from <see cref="ForSerialization"/>.
    /// </summary>
    public Type? ValueType { get; }

    /// <summary>
    /// When set, <see cref="Name"/> is not an EF query parameter: the value is computed by this delegate once per
    /// <c>MongoPipelineFactory.Build</c>. Used for a subtree holding a clock member (<see cref="RuntimeClock"/>),
    /// which must never be baked into the cached template.
    /// </summary>
    public Func<IReadOnlyDictionary<string, object?>, object?>? RuntimeEvaluator { get; }

    /// <summary>
    /// The serializer for a value that belongs to no <see cref="IProperty"/> (a dictionary entry). Takes precedence
    /// over <see cref="ForSerialization"/>.
    /// </summary>
    public MongoDB.Bson.Serialization.IBsonSerializer? ExplicitSerializer { get; }

    /// <inheritdoc />
    /// <remarks>
    /// A property-less runtime-evaluated value reports its evaluated CLR type, as the constant it replaces did.
    /// </remarks>
    public override Type Type
        => ForSerialization?.ClrType ?? (RuntimeEvaluator is not null ? ValueType : null) ?? typeof(object);
}
