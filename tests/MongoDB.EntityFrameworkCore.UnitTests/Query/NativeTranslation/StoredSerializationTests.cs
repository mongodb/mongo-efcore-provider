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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="StoredSerialization.ConvertersEquivalent"/>: two converters count as storing alike only when their
/// encodings are provably the same. The rule is structural, so these pin both directions: converter types whose
/// encoding is fixed by the type stay equivalent across instances (they were a hand-kept allow-list before), and
/// constructor arguments or captured variables that choose the encoding keep two instances apart.
/// </summary>
public class StoredSerializationTests
{
    private enum Kind { A, B }

    // Every converter type the former StatelessConverterTypes allow-list admitted: two separately constructed
    // instances must still compare equivalent, or anonymous-key joins over them would regress to declining.
    public static TheoryData<string, Func<ValueConverter>> FormerlyAllowListedConverters => new()
    {
        { "BoolToZeroOneConverter<int>", () => new BoolToZeroOneConverter<int>() },
        { "BytesToStringConverter", () => new BytesToStringConverter() },
        { "CastingConverter<int, long>", () => new CastingConverter<int, long>() },
        { "CharToStringConverter", () => new CharToStringConverter() },
        { "DateOnlyToStringConverter", () => new DateOnlyToStringConverter() },
        { "DateTimeOffsetToBinaryConverter", () => new DateTimeOffsetToBinaryConverter() },
        { "DateTimeOffsetToBytesConverter", () => new DateTimeOffsetToBytesConverter() },
        { "DateTimeOffsetToStringConverter", () => new DateTimeOffsetToStringConverter() },
        { "DateTimeToBinaryConverter", () => new DateTimeToBinaryConverter() },
        { "DateTimeToStringConverter", () => new DateTimeToStringConverter() },
        { "DateTimeToTicksConverter", () => new DateTimeToTicksConverter() },
        { "EnumToNumberConverter<Kind, int>", () => new EnumToNumberConverter<Kind, int>() },
        { "EnumToStringConverter<Kind>", () => new EnumToStringConverter<Kind>() },
        { "GuidToBytesConverter", () => new GuidToBytesConverter() },
        { "GuidToStringConverter", () => new GuidToStringConverter() },
        { "IPAddressToBytesConverter", () => new IPAddressToBytesConverter() },
        { "IPAddressToStringConverter", () => new IPAddressToStringConverter() },
        { "NumberToBytesConverter<int>", () => new NumberToBytesConverter<int>() },
        { "NumberToStringConverter<int>", () => new NumberToStringConverter<int>() },
        { "PhysicalAddressToBytesConverter", () => new PhysicalAddressToBytesConverter() },
        { "PhysicalAddressToStringConverter", () => new PhysicalAddressToStringConverter() },
        { "StringToBoolConverter", () => new StringToBoolConverter() },
        { "StringToCharConverter", () => new StringToCharConverter() },
        { "StringToDateOnlyConverter", () => new StringToDateOnlyConverter() },
        { "StringToDateTimeConverter", () => new StringToDateTimeConverter() },
        { "StringToDateTimeOffsetConverter", () => new StringToDateTimeOffsetConverter() },
        { "StringToEnumConverter<Kind>", () => new StringToEnumConverter<Kind>() },
        { "StringToGuidConverter", () => new StringToGuidConverter() },
        { "StringToNumberConverter<int>", () => new StringToNumberConverter<int>() },
        { "StringToTimeOnlyConverter", () => new StringToTimeOnlyConverter() },
        { "StringToTimeSpanConverter", () => new StringToTimeSpanConverter() },
        { "StringToUriConverter", () => new StringToUriConverter() },
        { "TimeOnlyToStringConverter", () => new TimeOnlyToStringConverter() },
        { "TimeOnlyToTicksConverter", () => new TimeOnlyToTicksConverter() },
        { "TimeSpanToStringConverter", () => new TimeSpanToStringConverter() },
        { "TimeSpanToTicksConverter", () => new TimeSpanToTicksConverter() },
        { "UriToStringConverter", () => new UriToStringConverter() },
        { "StringToObjectIdConverter", () => new StringToObjectIdConverter() },
        { "ObjectIdToStringConverter", () => new ObjectIdToStringConverter() },
        { "Decimal128ToDecimalConverter", () => new Decimal128ToDecimalConverter() },
        { "DecimalToDecimal128Converter", () => new DecimalToDecimal128Converter() },
    };

    [Theory]
    [MemberData(nameof(FormerlyAllowListedConverters))]
    public void Two_instances_of_a_type_fixed_converter_are_equivalent(string name, Func<ValueConverter> create)
    {
        var first = create();
        var second = create();

        Assert.NotSame(first, second);
        Assert.True(StoredSerialization.ConvertersEquivalent(first, second), name);
    }

    [Fact]
    public void BoolToStringConverters_with_different_values_differ_and_their_constants_are_in_the_trees()
    {
        // The structural rule is only safe if the constructor arguments that choose the encoding are visible in the
        // expressions: verify the to-provider trees carry "Y"/"N" and "T"/"F" as constants.
        var yesNo = new BoolToStringConverter("N", "Y");
        var trueFalse = new BoolToStringConverter("F", "T");

        Assert.Equal(yesNo.GetType(), trueFalse.GetType());
        Assert.Equal(yesNo.ProviderClrType, trueFalse.ProviderClrType);
        Assert.Contains("Y", Constants(yesNo.ConvertToProviderExpression));
        Assert.Contains("N", Constants(yesNo.ConvertToProviderExpression));
        Assert.Contains("T", Constants(trueFalse.ConvertToProviderExpression));
        Assert.Contains("F", Constants(trueFalse.ConvertToProviderExpression));
        Assert.Equal("Y", yesNo.ConvertToProvider(true));
        Assert.Equal("T", trueFalse.ConvertToProvider(true));

        Assert.False(StoredSerialization.ConvertersEquivalent(yesNo, trueFalse));
        Assert.False(StoredSerialization.ConvertersEquivalent(trueFalse, yesNo));
    }

    [Fact]
    public void Separately_written_identical_non_capturing_lambda_converters_are_equivalent()
    {
        var first = new ValueConverter<int, string>(v => (v + 100).ToString(), v => int.Parse(v) - 100);
        var second = new ValueConverter<int, string>(v => (v + 100).ToString(), v => int.Parse(v) - 100);

        Assert.True(StoredSerialization.ConvertersEquivalent(first, second));
    }

    [Fact]
    public void Lambda_converters_that_differ_in_either_direction_are_not_equivalent()
    {
        var baseline = new ValueConverter<int, string>(v => (v + 100).ToString(), v => int.Parse(v) - 100);
        var toDiffers = new ValueConverter<int, string>(v => (v + 200).ToString(), v => int.Parse(v) - 100);
        var fromDiffers = new ValueConverter<int, string>(v => (v + 100).ToString(), v => int.Parse(v) - 200);

        Assert.False(StoredSerialization.ConvertersEquivalent(baseline, toDiffers));
        Assert.False(StoredSerialization.ConvertersEquivalent(baseline, fromDiffers));
    }

    [Fact]
    public void Lambda_converters_capturing_a_local_are_not_equivalent_even_with_equal_values()
    {
        // Each Prefixed call captures `prefix` in its own closure object; the expressions reference that object as a
        // constant, which compares by reference. Equal captured values, same encoding, still not provably equal.
        var first = Prefixed("c");
        var second = Prefixed("c");

        Assert.Equal(first.ConvertToProvider(7), second.ConvertToProvider(7));
        Assert.False(StoredSerialization.ConvertersEquivalent(first, second));

        static ValueConverter<int, string> Prefixed(string prefix)
            => new(v => prefix + v, v => int.Parse(v.Substring(prefix.Length)));
    }

    [Fact]
    public void A_converter_against_none_and_different_converter_types_are_not_equivalent()
    {
        Assert.True(StoredSerialization.ConvertersEquivalent(null, null));
        Assert.False(StoredSerialization.ConvertersEquivalent(new EnumToStringConverter<Kind>(), null));
        Assert.False(StoredSerialization.ConvertersEquivalent(null, new EnumToStringConverter<Kind>()));
        Assert.False(StoredSerialization.ConvertersEquivalent(
            new EnumToStringConverter<Kind>(), new EnumToNumberConverter<Kind, int>()));
        // Same generic definition, different provider type.
        Assert.False(StoredSerialization.ConvertersEquivalent(
            new EnumToNumberConverter<Kind, int>(), new EnumToNumberConverter<Kind, long>()));
    }

    [Fact]
    public void The_same_instance_is_equivalent()
    {
        var converter = new BoolToStringConverter("N", "Y");
        Assert.True(StoredSerialization.ConvertersEquivalent(converter, converter));
    }

    private static List<object?> Constants(Expression expression)
    {
        var collector = new ConstantCollector();
        collector.Visit(expression);
        return collector.Values;
    }

    private sealed class ConstantCollector : ExpressionVisitor
    {
        public List<object?> Values { get; } = [];

        protected override Expression VisitConstant(ConstantExpression node)
        {
            Values.Add(node.Value);
            return base.VisitConstant(node);
        }
    }
}
