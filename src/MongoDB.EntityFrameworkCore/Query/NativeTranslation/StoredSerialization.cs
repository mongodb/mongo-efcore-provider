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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Whether two properties store equal CLR values as the same BSON, so the server can compare one property's stored
/// values with the other's and either property's serializer can read both back.
/// </summary>
/// <remarks>
/// <para>
/// The single rule shared by every caller that combines two properties' stored values: the anonymous-key join's
/// per-member pairs (<c>MongoQueryableMethodTranslatingExpressionVisitor.TryResolveAnonymousKeyJoinProperties</c>)
/// and the projected set op's per-alias operands (<c>OperandSerializationsMatch</c>). Callers keep their own
/// pre-checks (the join's scalar-only rule; the set op's both-default shortcut, computed values and TimeOfDay).
/// </para>
/// <para>
/// Mirrors what <see cref="Serializers.BsonSerializerFactory"/> builds for a property: its type mapping's converter
/// (wrapped in a <c>ValueConverterSerializer</c>), then its <c>BsonRepresentation</c> on the CLR or provider type.
/// An enum stored as a string on one side and an int on the other, or <c>[BsonRepresentation(String)]</c> on one side
/// only, never matches in the database.
/// </para>
/// </remarks>
internal static class StoredSerialization
{
    /// <summary>
    /// Whether <paramref name="property1"/> and <paramref name="property2"/> are stored identically: equal
    /// <see cref="MongoPropertyExtensions.GetBsonRepresentation"/> (both none, or equal), equal configured provider CLR
    /// type, and equivalent effective converters (<see cref="ConvertersEquivalent"/>).
    /// </summary>
    internal static bool StoredAlike(IProperty property1, IProperty property2)
        => property1.GetBsonRepresentation() == property2.GetBsonRepresentation()
           && property1.GetProviderClrType() == property2.GetProviderClrType()
           && ConvertersEquivalent(EffectiveConverter(property1), EffectiveConverter(property2));

    /// <summary>
    /// The converter <see cref="Serializers.BsonSerializerFactory"/> wraps: the type mapping's, which also covers one
    /// derived from the provider type alone (<c>HasConversion&lt;string&gt;()</c>) that <c>GetValueConverter()</c>
    /// doesn't report. A finalized model always has a type mapping; the configured converter is only a fallback.
    /// </summary>
    private static ValueConverter? EffectiveConverter(IProperty property)
        => property.FindTypeMapping()?.Converter ?? property.GetValueConverter();

    /// <summary>
    /// Whether two converters encode every value identically: both absent, the same instance, or the same converter
    /// type with the same provider type and structurally equal to- and from-provider expressions.
    /// </summary>
    /// <remarks>
    /// EF value converters have no equality of their own, but their encoding is their expressions: constructor
    /// arguments that choose the encoding appear in them as constants (<c>BoolToStringConverter("N", "Y")</c> vs
    /// <c>("F", "T")</c>) or as captured closure objects, which compare by reference, so two lambdas capturing a local
    /// variable (and two converters built around a captured <c>Encoding</c>) are never equal even when they would
    /// encode alike. A lambda converter written the same way on both properties, or two instances of a converter whose
    /// encoding is fixed by its type (<c>EnumToStringConverter&lt;&gt;</c>, the provider's ObjectId converters), are equal.
    /// </remarks>
    internal static bool ConvertersEquivalent(ValueConverter? converter1, ValueConverter? converter2)
        => (converter1, converter2) switch
        {
            (null, null) => true,
            ({ } c1, { } c2) when ReferenceEquals(c1, c2) => true,
            ({ } c1, { } c2) => c1.GetType() == c2.GetType()
                                && c1.ProviderClrType == c2.ProviderClrType
                                && ExpressionEqualityComparer.Instance.Equals(
                                    c1.ConvertToProviderExpression, c2.ConvertToProviderExpression)
                                && ExpressionEqualityComparer.Instance.Equals(
                                    c1.ConvertFromProviderExpression, c2.ConvertFromProviderExpression),
            _ => false
        };
}
