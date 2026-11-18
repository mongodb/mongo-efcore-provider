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
/// values with the other's and either property's serializer can read both back. DateTimeKind read-back is checked
/// separately (<see cref="NativeDateTimeKindReadBack"/>), and BinaryVector storage isn't covered.
/// </summary>
/// <remarks>
/// Shared by the anonymous-key join's per-member pairs (<c>TryResolveAnonymousKeyJoinProperties</c>) and the projected set
/// op's per-alias operands (<c>OperandSerializationsMatch</c>); callers keep their own pre-checks. Mirrors what
/// <see cref="Serializers.BsonSerializerFactory"/> builds: the type mapping's converter, then <c>BsonRepresentation</c>. An
/// enum stored as a string on one side and an int on the other never matches in the database.
/// </remarks>
internal static class StoredSerialization
{
    // The key is read back from _id through a generic CLR-type serializer (no backing IProperty), which only reproduces default/identity serialization; a value
    // converter or non-default BsonRepresentation would throw or return the raw stored value, so such keys fall back. Accumulator operands are held to the same
    // rule ($sum/$max reduce the stored form). Also used by TranslateOfType's discriminator guard and as the strict half of StoredOrdering.PreservesClrOrdering (EF-337).
    internal static bool HasDefaultKeySerialization(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() == null;

    /// <summary>
    /// Equal <see cref="MongoPropertyExtensions.GetBsonRepresentation"/> and equivalent effective converters. The configured
    /// provider-type annotation isn't compared: the serializer reads only the effective converter, representation and CLR type.
    /// </summary>
    internal static bool StoredAlike(IProperty property1, IProperty property2)
        => property1.GetBsonRepresentation() == property2.GetBsonRepresentation()
           && ConvertersEquivalent(EffectiveConverter(property1), EffectiveConverter(property2));

    /// <summary>
    /// The converter <see cref="Serializers.BsonSerializerFactory"/> wraps: the type mapping's, which also covers one derived
    /// from the provider type alone (<c>HasConversion&lt;string&gt;()</c>) that <c>GetValueConverter()</c> doesn't report.
    /// </summary>
    private static ValueConverter? EffectiveConverter(IProperty property)
        => property.FindTypeMapping()?.Converter ?? property.GetValueConverter();

    /// <summary>
    /// Whether two converters encode every value identically: both absent, the same instance, or the same converter
    /// type with the same provider type and structurally equal to- and from-provider expressions.
    /// </summary>
    /// <remarks>
    /// EF value converters have no equality; their encoding is their expressions, whose constants or captured closures choose
    /// it (<c>BoolToStringConverter("N", "Y")</c> vs <c>("F", "T")</c>). Closures compare by reference, so separately created
    /// closures decline even when they would encode alike. Assumes behaviour is fully described by the expressions: a subclass
    /// overriding <c>ConvertToProviderTyped</c> with outside state, or a captured constant with a lax <c>Equals</c>, defeats it.
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
