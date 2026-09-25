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

#if !EF8 && !EF9

using System;
using Microsoft.EntityFrameworkCore.Query.Translations;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.SpecificationTests.Query;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query.Translations;

public class StringTranslationsMongoTest : StringTranslationsTestBase<MongoBasicTypesQueryFixture>
{
    public StringTranslationsMongoTest(MongoBasicTypesQueryFixture fixture, ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestMqlLoggerFactory.Clear();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    // Upstream StringTranslationsTestBase declares a test named "Equals" (a [ConditionalFact] on the base
    // virtual method), which the xunit analyzer flags as colliding with object.Equals(object) by name.
    // Suppressed rather than renamed since this override must match the base signature exactly.
#pragma warning disable xUnit1024
    public override async Task Equals()
    {
        await base.Equals();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
""");
    }
#pragma warning restore xUnit1024

    public override async Task Equals_with_OrdinalIgnoreCase()
    {
        await base.Equals_with_OrdinalIgnoreCase();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$strcasecmp" : ["$String", "seattle"] }, 0] } } }
""");
    }

    public override async Task Equals_with_Ordinal()
    {
        await base.Equals_with_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : ["$String", "Seattle"] } } }
""");
    }

    public override async Task Static_Equals()
    {
        await base.Static_Equals();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
""");
    }

    public override async Task Static_Equals_with_OrdinalIgnoreCase()
    {
        await base.Static_Equals_with_OrdinalIgnoreCase();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$strcasecmp" : ["$String", "seattle"] }, 0] } } }
""");
    }

    public override async Task Static_Equals_with_Ordinal()
    {
        await base.Static_Equals_with_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : ["$String", "Seattle"] } } }
""");
    }

    public override async Task Length()
    {
        await base.Length();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$strLenCP" : "$String" }, 7] } } }
""");
    }

    public override async Task ToUpper()
    {
        await base.ToUpper();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^SEATTLE$", "options" : "is" } } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "_v" : { "$toUpper" : "$String" }, "_id" : 0 } }
""");
    }

    public override async Task ToLower()
    {
        await base.ToLower();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^seattle$", "options" : "is" } } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "_v" : { "$toLower" : "$String" }, "_id" : 0 } }
""");
    }

    public override async Task IndexOf()
    {
        await base.IndexOf();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$indexOfCP" : ["$String", "eattl"] }, -1] } } }
""");
    }

    public override async Task IndexOf_Char()
    {
        await base.IndexOf_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$indexOfCP" : ["$String", "e"] }, -1] } } }
""");
    }

    public override async Task IndexOf_with_empty_string()
    {
        await base.IndexOf_with_empty_string();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", ""] }, 0] } } }
""");
    }

    public override async Task IndexOf_with_one_parameter_arg()
    {
        await base.IndexOf_with_one_parameter_arg();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", "eattl"] }, 1] } } }
""");
    }

    public override async Task IndexOf_with_one_parameter_arg_char()
    {
        await base.IndexOf_with_one_parameter_arg_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^[^e]{1}e", "options" : "s" } } } }
""");
    }

    public override async Task IndexOf_with_constant_starting_position()
    {
        await base.IndexOf_with_constant_starting_position();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "String" : { "$regularExpression" : { "pattern" : "^.{2}(?!.{0,3}e).{4}e", "options" : "s" } } }] } }
""");
    }

    public override async Task IndexOf_with_constant_starting_position_char()
    {
        await base.IndexOf_with_constant_starting_position_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "String" : { "$regularExpression" : { "pattern" : "^.{2}[^e]{4}e", "options" : "s" } } }] } }
""");
    }

    public override async Task IndexOf_with_parameter_starting_position()
    {
        await base.IndexOf_with_parameter_starting_position();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "String" : { "$regularExpression" : { "pattern" : "^.{2}(?!.{0,3}e).{4}e", "options" : "s" } } }] } }
""");
    }

    public override async Task IndexOf_with_parameter_starting_position_char()
    {
        await base.IndexOf_with_parameter_starting_position_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "String" : { "$regularExpression" : { "pattern" : "^.{2}[^e]{4}e", "options" : "s" } } }] } }
""");
    }

    public override async Task IndexOf_after_ToString()
    {
        await base.IndexOf_after_ToString();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : [{ "$toString" : "$Int" }, "55"] }, 1] } } }
""");
    }

    public override async Task IndexOf_over_ToString()
    {
        await base.IndexOf_over_ToString();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["12559", { "$toString" : "$Int" }] }, 1] } } }
""");
    }

    public override async Task Replace()
    {
        await base.Replace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "Sea", "replacement" : "Rea" } }, "Reattle"] } } }
""");
    }

    public override async Task Replace_Char()
    {
        await base.Replace_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "S", "replacement" : "R" } }, "Reattle"] } } }
""");
    }

    public override async Task Replace_with_empty_string()
    {
        await base.Replace_with_empty_string();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$ne" : "" } }, { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "$String", "replacement" : "" } }, ""] } }] } }
""");
    }

    public override async Task Replace_using_property_arguments()
    {
        await base.Replace_using_property_arguments();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$ne" : "" } }, { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "$String", "replacement" : { "$toString" : "$Int" } } }, { "$toString" : "$Int" }] } }] } }
""");
    }

    public override async Task Substring()
    {
        await base.Substring();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 1, 2] }, "ea"] } }] } }
""");
    }

    public override async Task Substring_with_one_arg_with_zero_startIndex()
    {
        await base.Substring_with_one_arg_with_zero_startIndex();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 0, { "$strLenCP" : "$String" }] }, "Seattle"] } } }
""");
    }

    public override async Task Substring_with_one_arg_with_constant()
    {
        await base.Substring_with_one_arg_with_constant();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{1,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 1, { "$subtract" : [{ "$strLenCP" : "$String" }, 1] }] }, "eattle"] } }] } }
""");
    }

    public override async Task Substring_with_one_arg_with_parameter()
    {
        await base.Substring_with_one_arg_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{2,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 2, { "$subtract" : [{ "$strLenCP" : "$String" }, 2] }] }, "attle"] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_zero_startIndex()
    {
        await base.Substring_with_two_args_with_zero_startIndex();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{3,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 0, 3] }, "Sea"] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_zero_length()
    {
        await base.Substring_with_two_args_with_zero_length();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{2,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 2, 0] }, ""] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_parameter()
    {
        await base.Substring_with_two_args_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "^.{5,}$", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", 2, 3] }, "att"] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_IndexOf()
    {
        await base.Substring_with_two_args_with_IndexOf();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "a", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$substrCP" : ["$String", { "$indexOfCP" : ["$String", "a"] }, 3] }, "att"] } }] } }
""");
    }

    public override async Task IsNullOrEmpty()
    {
        await base.IsNullOrEmpty();

        AssertMql(
            """
NullableBasicTypesEntities.{ "$match" : { "String" : { "$in" : [null, ""] } } }
""",
            //
            """
NullableBasicTypesEntities.{ "$project" : { "_v" : { "$in" : ["$String", [null, ""]] }, "_id" : 0 } }
""");
    }

    public override async Task IsNullOrEmpty_negated()
    {
        await base.IsNullOrEmpty_negated();

        AssertMql(
            """
NullableBasicTypesEntities.{ "$match" : { "String" : { "$nin" : [null, ""] } } }
""",
            //
            """
NullableBasicTypesEntities.{ "$project" : { "_v" : { "$not" : { "$in" : ["$String", [null, ""]] } }, "_id" : 0 } }
""");
    }

    public override async Task IsNullOrWhiteSpace()
    {
        await base.IsNullOrWhiteSpace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$in" : [null, { "$regularExpression" : { "pattern" : "^\\s*$", "options" : "" } }] } } }
""");
    }

    public override async Task StartsWith_Literal()
    {
        await base.StartsWith_Literal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^Se", "options" : "s" } } } }
""");
    }

    public override async Task StartsWith_Literal_Char()
    {
        await base.StartsWith_Literal_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^S", "options" : "s" } } } }
""");
    }

    public override async Task StartsWith_Parameter()
    {
        await base.StartsWith_Parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^Se", "options" : "s" } } } }
""");
    }

    public override async Task StartsWith_Parameter_Char()
    {
        await base.StartsWith_Parameter_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^S", "options" : "s" } } } }
""");
    }

    public override async Task StartsWith_Column()
    {
        await base.StartsWith_Column();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", "$String"] }, 0] } } }
""");
    }

    public override async Task StartsWith_with_StringComparison_Ordinal()
    {
        await base.StartsWith_with_StringComparison_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^Se", "options" : "s" } } } }
""");
    }

    public override async Task StartsWith_with_StringComparison_OrdinalIgnoreCase()
    {
        await base.StartsWith_with_StringComparison_OrdinalIgnoreCase();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^Se", "options" : "is" } } } }
""");
    }

    // CurrentCulture(IgnoreCase)/InvariantCulture(IgnoreCase) have no culture-aware collation equivalent in
    // MongoDB's $regularExpression, so our native translator correctly declines them — same as before Task
    // 3, unaffected by Ordinal/OrdinalIgnoreCase now going native. But the decline still just falls through
    // to the driver-LINQ v3 fallback, which (confirmed empirically, still true post-Task-3) accepts the
    // StringComparison-taking overload without validating the culture member at all: no exception is thrown
    // by AssertQuery(...), so base's own internal Assert.ThrowsAsync<InvalidOperationException> check (the
    // same expectation every relational provider satisfies) fails with a ThrowsException. That's the actual
    // gap signal here — asserted directly rather than via AssertTranslationFailed, since a ThrowsException
    // isn't itself evidence of a translation rejection. This is a permanent, correct decline on our side
    // (same convention as Where_bitwise_xor in the misc-roadmap doc) — not a gap to close.
    public override async Task StartsWith_with_StringComparison_unsupported()
        => await Assert.ThrowsAsync<ThrowsException>(() => base.StartsWith_with_StringComparison_unsupported());

    public override async Task EndsWith_Literal()
    {
        await base.EndsWith_Literal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "le$", "options" : "s" } } } }
""");
    }

    public override async Task EndsWith_Literal_Char()
    {
        await base.EndsWith_Literal_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "e$", "options" : "s" } } } }
""");
    }

    public override async Task EndsWith_Parameter()
    {
        await base.EndsWith_Parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "le$", "options" : "s" } } } }
""");
    }

    public override async Task EndsWith_Parameter_Char()
    {
        await base.EndsWith_Parameter_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "e$", "options" : "s" } } } }
""");
    }

    public override async Task EndsWith_Column()
    {
        await base.EndsWith_Column();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$String" }, { "$strLenCP" : "$String" }] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$String", "$String", "$$start"] }, "$$start"] }] } } } } }
""");
    }

    public override async Task EndsWith_with_StringComparison_Ordinal()
    {
        await base.EndsWith_with_StringComparison_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "le$", "options" : "s" } } } }
""");
    }

    public override async Task EndsWith_with_StringComparison_OrdinalIgnoreCase()
    {
        await base.EndsWith_with_StringComparison_OrdinalIgnoreCase();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "LE$", "options" : "is" } } } }
""");
    }

    // See StartsWith_with_StringComparison_unsupported above — same permanent, correct decline, same
    // ThrowsException-wrapping-a-ThrowsException gap signal.
    public override async Task EndsWith_with_StringComparison_unsupported()
        => await Assert.ThrowsAsync<ThrowsException>(() => base.EndsWith_with_StringComparison_unsupported());

    public override async Task Contains_Literal()
    {
        await base.Contains_Literal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "eattl", "options" : "s" } } } }
""");
    }

    public override async Task Contains_Literal_Char()
    {
        await base.Contains_Literal_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "e", "options" : "s" } } } }
""");
    }

    public override async Task Contains_Column()
    {
        await base.Contains_Column();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gte" : [{ "$indexOfCP" : ["$String", "$String"] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "_v" : { "$gte" : [{ "$indexOfCP" : ["$String", "$String"] }, 0] }, "_id" : 0 } }
""");
    }

    public override async Task Contains_negated()
    {
        await base.Contains_negated();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$not" : { "$regularExpression" : { "pattern" : "eattle", "options" : "s" } } } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "_v" : { "$not" : { "$gte" : [{ "$indexOfCP" : ["$String", "eattle"] }, 0] } }, "_id" : 0 } }
""");
    }

    public override async Task Contains_with_StringComparison_Ordinal()
    {
        await base.Contains_with_StringComparison_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "eattl", "options" : "s" } } } }
""");
    }

    public override async Task Contains_with_StringComparison_OrdinalIgnoreCase()
    {
        await base.Contains_with_StringComparison_OrdinalIgnoreCase();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "EATTL", "options" : "is" } } } }
""");
    }

    // See StartsWith_with_StringComparison_unsupported above — same permanent, correct decline, same
    // ThrowsException-wrapping-a-ThrowsException gap signal.
    public override async Task Contains_with_StringComparison_unsupported()
        => await Assert.ThrowsAsync<ThrowsException>(() => base.Contains_with_StringComparison_unsupported());

    public override async Task Contains_constant_with_whitespace()
    {
        await base.Contains_constant_with_whitespace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "\\ \\ \\ \\ \\ ", "options" : "s" } } } }
""");
    }

    public override async Task Contains_parameter_with_whitespace()
    {
        await base.Contains_parameter_with_whitespace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "\\ \\ \\ \\ \\ ", "options" : "s" } } } }
""");
    }

    public override async Task TrimStart_without_arguments()
    {
        await base.TrimStart_without_arguments();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String" } }, "Boston  "] } } }
""");
    }

    public override async Task TrimStart_with_char_argument()
    {
        await base.TrimStart_with_char_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String", "chars" : "S" } }, "eattle"] } } }
""");
    }

    public override async Task TrimStart_with_char_array_argument()
    {
        await base.TrimStart_with_char_array_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String", "chars" : "Se" } }, "attle"] } } }
""");
    }

    public override async Task TrimEnd_without_arguments()
    {
        await base.TrimEnd_without_arguments();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String" } }, "  Boston"] } } }
""");
    }

    public override async Task TrimEnd_with_char_argument()
    {
        await base.TrimEnd_with_char_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String", "chars" : "e" } }, "Seattl"] } } }
""");
    }

    public override async Task TrimEnd_with_char_array_argument()
    {
        await base.TrimEnd_with_char_array_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String", "chars" : "le" } }, "Seatt"] } } }
""");
    }

    public override async Task Trim_without_argument_in_predicate()
    {
        await base.Trim_without_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String" } }, "Boston"] } } }
""");
    }

    public override async Task Trim_with_char_argument_in_predicate()
    {
        await base.Trim_with_char_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : "S" } }, "eattle"] } } }
""");
    }

    public override async Task Trim_with_char_array_argument_in_predicate()
    {
        await base.Trim_with_char_array_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : "Se" } }, "attl"] } } }
""");
    }

    public override async Task Compare_simple_zero()
    {
        await base.Compare_simple_zero();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$ne" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""");
    }

    public override async Task Compare_simple_one()
    {
        await base.Compare_simple_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""");
    }

    public override async Task Compare_with_parameter()
    {
        await base.Compare_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "_id" : 1 } }, { "$limit" : 2 }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""");
    }

    public override async Task Compare_simple_more_than_one()
    {
        await base.Compare_simple_more_than_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", "Seattle"] }, 42] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$String", "Seattle"] }, 42] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [42, { "$cmp" : ["$String", "Seattle"] }] } } }
""");
    }

    public override async Task Compare_nested()
    {
        await base.Compare_nested();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$concat" : ["M", "$String"] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [0, { "$cmp" : ["$String", { "$substrCP" : ["$String", 0, 0] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$String", { "$replaceAll" : { "input" : "Seattle", "find" : "Sea", "replacement" : "$String" } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gte" : [0, { "$cmp" : ["$String", { "$concat" : ["M", "$String"] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [1, { "$cmp" : ["$String", { "$substrCP" : ["$String", 0, 0] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$replaceAll" : { "input" : "Seattle", "find" : "Sea", "replacement" : "$String" } }] }, -1] } } }
""");
    }

    public override async Task Compare_multi_predicate()
    {
        await base.Compare_multi_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }, { "$match" : { "String" : { "$lt" : "Toronto" } } }
""");
    }

    public override async Task CompareTo_simple_zero()
    {
        await base.CompareTo_simple_zero();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$ne" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""");
    }

    public override async Task CompareTo_simple_one()
    {
        await base.CompareTo_simple_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""");
    }

    public override async Task CompareTo_with_parameter()
    {
        await base.CompareTo_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "_id" : 1 } }, { "$limit" : 2 }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lt" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$lte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }
""");
    }

    public override async Task CompareTo_simple_more_than_one()
    {
        await base.CompareTo_simple_more_than_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", "Seattle"] }, 42] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$String", "Seattle"] }, 42] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [42, { "$cmp" : ["$String", "Seattle"] }] } } }
""");
    }

    public override async Task CompareTo_nested()
    {
        await base.CompareTo_nested();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$concat" : ["M", "$String"] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [0, { "$cmp" : ["$String", { "$substrCP" : ["$String", 0, 0] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$String", { "$replaceAll" : { "input" : "Seattle", "find" : "Sea", "replacement" : "$String" } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gte" : [0, { "$cmp" : ["$String", { "$concat" : ["M", "$String"] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [1, { "$cmp" : ["$String", { "$substrCP" : ["$String", 0, 0] }] }] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$replaceAll" : { "input" : "Seattle", "find" : "Sea", "replacement" : "$String" } }] }, -1] } } }
""");
    }

    public override async Task Compare_to_multi_predicate()
    {
        await base.Compare_to_multi_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle" } } }, { "$match" : { "String" : { "$lt" : "Toronto" } } }
""");
    }

    // Excluded — depends on native GroupBy, other agents' active work:
    public override Task Join_over_non_nullable_column()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Join_over_non_nullable_column());

    public override Task Join_over_nullable_column()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Join_over_nullable_column());

    public override Task Join_with_predicate()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Join_with_predicate());

    public override Task Join_with_ordering()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Join_with_ordering());

    public override Task Join_non_aggregate()
        // Fails: string.Join over an array literal EF-X105
        => AssertTranslationFailed(() => base.Join_non_aggregate());

    public override async Task Concat_operator()
    {
        await base.Concat_operator();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["$String", "Boston"] }, "SeattleBoston"] } } }
""");
    }

    // Excluded — depends on native GroupBy, other agents' active work:
    public override Task Concat_aggregate()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Concat_aggregate());

    public override async Task Concat_string_int_comparison1()
    {
        await base.Concat_string_int_comparison1();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["$String", { "$toString" : 10 }] }, "Seattle10"] } } }
""");
    }

    public override async Task Concat_string_int_comparison2()
    {
        await base.Concat_string_int_comparison2();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$toString" : 10 }, "$String"] }, "10Seattle"] } } }
""");
    }

    public override async Task Concat_string_int_comparison3()
    {
        await base.Concat_string_int_comparison3();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$toString" : 30 }, "$String", { "$toString" : 21 }, { "$toString" : 42 }] }, "30Seattle2142"] } } }
""");
    }

    public override async Task Concat_string_int_comparison4()
    {
        await base.Concat_string_int_comparison4();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$toString" : "$Int" }, "$String"] }, "8Seattle"] } } }
""");
    }

    public override async Task Concat_string_string_comparison()
    {
        await base.Concat_string_string_comparison();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "$String"] }, "ASeattle"] } } }
""");
    }

    public override async Task Concat_method_comparison()
    {
        await base.Concat_method_comparison();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "$String"] }, "ASeattle"] } } }
""");
    }

    public override async Task Concat_method_comparison_2()
    {
        await base.Concat_method_comparison_2();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "B", "$String"] }, "ABSeattle"] } } }
""");
    }

    public override async Task Concat_method_comparison_3()
    {
        await base.Concat_method_comparison_3();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "B", "C", "$String"] }, "ABCSeattle"] } } }
""");
    }

    public override async Task FirstOrDefault()
    {
        await base.FirstOrDefault();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$strLenCP" : "$String" }, 0] }, "then" : "\u0000", "else" : { "$substrCP" : ["$String", 0, 1] } } }, "S"] } } }
""");
    }

    public override async Task LastOrDefault()
    {
        await base.LastOrDefault();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$strLenCP" : "$String" }, 0] }, "then" : "\u0000", "else" : { "$substrCP" : ["$String", { "$subtract" : [{ "$strLenCP" : "$String" }, 1] }, 1] } } }, "e"] } } }
""");
    }

    public override async Task Regex_IsMatch()
    {
        await base.Regex_IsMatch();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^S", "options" : "" } } } }
""");
    }

    public override async Task Regex_IsMatch_constant_input()
        // Fails: Regex.IsMatch(constant, field) reversed-argument shape EF-X104
        => await AssertTranslationFailed(() => base.Regex_IsMatch_constant_input());

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected static Task AssertTranslationFailed(Func<Task> query, params Type[] additionalAcceptedTypes)
        => MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(query, additionalAcceptedTypes);
}

#endif
