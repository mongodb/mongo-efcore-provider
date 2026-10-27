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

using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

// The EF-337 cases of RepresentedPropertyPushdownTestsBase, spread over several classes. xUnit runs the cases of a class
// one after another, and this suite has thousands of them, so each class below is a separate parallel unit with its own
// fixture (and so its own database).

/// <summary>The smaller theories, sharing one class.</summary>
public class RepresentedPropertyPushdownTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    [Theory]
    [MemberData(nameof(EqualityCases))]
    public void Equality_over_any_storage_keeps_working(string query, Storage storage, MongoQueryMode mode)
        => Equality_over_any_storage_keeps_workingCore(query, storage, mode);

    [Theory]
    [MemberData(nameof(TaggedCases))]
    public void Order_sensitive_query_over_ObjectId_or_enum_representation(string query, EnumStorage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_ObjectId_or_enum_representationCore(query, storage, mode);

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Owned_element_comparison_over_string_represented_property_is_converted_or_declined(MongoQueryMode mode)
        => Owned_element_comparison_over_string_represented_property_is_converted_or_declinedCore(mode);

    [Theory]
    [MemberData(nameof(NarrowCases))]
    public void Narrowing_casting_converter(string query, MongoQueryMode mode)
        => Narrowing_casting_converterCore(query, mode);

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Identity_representation_is_not_refused(MongoQueryMode mode)
        => Identity_representation_is_not_refusedCore(mode);
}

public class RepresentedPropertyPushdownRefusedTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void Order_sensitive_query_over_non_order_preserving_storage_is_refused(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_non_order_preserving_storage_is_refusedCore(query, storage, mode);
}

public class RepresentedPropertyPushdownInt64RepresentationTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.Int64Representation);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownDoubleRepresentationTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.DoubleRepresentation);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownDecimal128RepresentationTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.Decimal128Representation);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownCastingConverterToLongTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.CastingConverterToLong);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownCastingConverterToDoubleTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.CastingConverterToDouble);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownStringRepresentationTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.StringRepresentation);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownStringConverterTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => ExactOrConvertedCasesFor(Storage.StringConverter);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Order_sensitive_query_over_exact_or_converted_storage_is_correct(string query, Storage storage, MongoQueryMode mode)
        => Order_sensitive_query_over_exact_or_converted_storage_is_correctCore(query, storage, mode);
}

public class RepresentedPropertyPushdownFlagAsStringTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => RichCasesFor(FlagStorage.FlagAsString);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Stored_value_members_truthiness_and_primitive_collections(
        string query, bool refused, FlagStorage flag, MongoQueryMode mode)
        => Stored_value_members_truthiness_and_primitive_collectionsCore(query, refused, flag, mode);
}

public class RepresentedPropertyPushdownFlagInvertedTests(TemporaryDatabaseFixture database)
    : RepresentedPropertyPushdownTestsBase(database), IClassFixture<TemporaryDatabaseFixture>
{
    public static IEnumerable<object[]> Cases()
        => RichCasesFor(FlagStorage.FlagInverted);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Stored_value_members_truthiness_and_primitive_collections(
        string query, bool refused, FlagStorage flag, MongoQueryMode mode)
        => Stored_value_members_truthiness_and_primitive_collectionsCore(query, refused, flag, mode);
}
