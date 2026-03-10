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
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Core.Misc;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Mapping;

public class BuiltInDataTypesMongoTest(BuiltInDataTypesMongoTest.BuiltInDataTypesMongoFixture fixture)
    : BuiltInDataTypesTestBase<BuiltInDataTypesMongoTest.BuiltInDataTypesMongoFixture>(fixture)
{
    [ConditionalTheory(Skip = "Enum casting issue EF-215"), InlineData(false), InlineData(true)]
    public override Task Can_filter_projection_with_captured_enum_variable(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Enum casting issue EF-215"), InlineData(false), InlineData(true)]
    public override Task Can_filter_projection_with_inline_enum_variable(bool _)
        => Task.CompletedTask;

    #if !EF8
    [ConditionalFact(Skip = "Include issue EF-117")]
    public override Task Can_insert_and_read_back_with_string_key()
        => Task.CompletedTask;

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override Task Can_read_back_bool_mapped_as_int_through_navigation()
        => Task.CompletedTask;

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override Task Can_read_back_mapped_enum_from_collection_first_or_default()
        => Task.CompletedTask;

    [ConditionalFact(Skip = "Call ToString on DateTimeOffset EF-217")]
    public override Task Object_to_string_conversion()
        => Task.CompletedTask;

    [ConditionalFact(Skip = "Projecting DateTimeOffset members EF-218")]
    public override Task Optional_datetime_reading_null_from_database()
        => Task.CompletedTask;
    #else
    [ConditionalFact(Skip = "Include issue EF-117")]
    public override void Can_insert_and_read_back_with_string_key()
    {
    }

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Can_read_back_bool_mapped_as_int_through_navigation()
    {
    }

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Can_read_back_mapped_enum_from_collection_first_or_default()
    {
    }

    [ConditionalFact(Skip = "Call ToString on DateTimeOffset EF-217")]
    public override void Object_to_string_conversion()
    {
    }

    [ConditionalFact(Skip = "Projecting DateTimeOffset members EF-218")]
    public override void Optional_datetime_reading_null_from_database()
    {
    }
    #endif

    public class BuiltInDataTypesMongoFixture : BuiltInDataTypesFixtureBase
    {
        private ITestStoreFactory? _testStoreFactory;

        protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("BuiltInDataTypes");

        public TestServer TestServer { get; private set; }

        public override async Task InitializeAsync()
        {
            TestServer = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
            _testStoreFactory = new MongoTestStoreFactory(TestServer);

            await base.InitializeAsync();
        }

        public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
            => base.AddOptions(builder.ConfigureWarnings(w => w.Ignore(MongoEventId.ColumnAttributeWithTypeUsed)));

        protected override ITestStoreFactory TestStoreFactory
            => _testStoreFactory!;

        public override bool StrictEquality
            => true;

        public override int IntegerPrecision
            => 53;

        public override bool SupportsAnsi
            => false;

        public override bool SupportsUnicodeToAnsiConversion
            => false;

        public override bool SupportsLargeStringComparisons
            => true;

        public override bool SupportsBinaryKeys
            => false;

        public override bool SupportsDecimalComparisons
            => true;

        public override DateTime DefaultDateTime
            => new();

        public override bool PreservesDateTimeKind
            => false;
    }
}
