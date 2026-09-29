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

    // The base test is named "Equals", which xUnit1024 flags as colliding with object.Equals; the override
    // can't be renamed.
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
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^seattle\\z", "options" : "is" } } } }
""");
    }

    public override async Task Equals_with_Ordinal()
    {
        await base.Equals_with_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
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
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^seattle\\z", "options" : "is" } } } }
""");
    }

    public override async Task Static_Equals_with_Ordinal()
    {
        await base.Static_Equals_with_Ordinal();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : "Seattle" } }
""");
    }

    public override async Task Length()
    {
        await base.Length();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 7] } } }
""");
    }

    public override async Task ToUpper()
    {
        await base.ToUpper();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^SEATTLE\\z", "options" : "is" } } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "String" : "$String", "_id" : 0 } }
""");
    }

    public override async Task ToLower()
    {
        await base.ToLower();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$regularExpression" : { "pattern" : "^seattle\\z", "options" : "is" } } } }
""",
            //
            """
BasicTypesEntities.{ "$project" : { "String" : "$String", "_id" : 0 } }
""");
    }

    public override async Task IndexOf()
    {
        await base.IndexOf();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$indexOfCP" : ["$String", { "$literal" : "eattl" }] }, -1] } } }
""");
    }

    public override async Task IndexOf_Char()
    {
        await base.IndexOf_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$indexOfCP" : ["$String", { "$literal" : "e" }] }, -1] } } }
""");
    }

    public override async Task IndexOf_with_empty_string()
    {
        await base.IndexOf_with_empty_string();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", { "$literal" : "" }] }, 0] } } }
""");
    }

    public override async Task IndexOf_with_one_parameter_arg()
    {
        await base.IndexOf_with_one_parameter_arg();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : [{ "$literal" : "eattl" }, null] }, null] }, "then" : null, "else" : { "$indexOfCP" : ["$String", { "$literal" : "eattl" }] } } }, 1] } } }
""");
    }

    public override async Task IndexOf_with_one_parameter_arg_char()
    {
        await base.IndexOf_with_one_parameter_arg_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : [{ "$literal" : "e" }, null] }, null] }, "then" : null, "else" : { "$indexOfCP" : ["$String", { "$literal" : "e" }] } } }, 1] } } }
""");
    }

    public override async Task IndexOf_with_constant_starting_position()
    {
        await base.IndexOf_with_constant_starting_position();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gt" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", { "$literal" : "e" }, 2] }, 6] } }] } }
""");
    }

    public override async Task IndexOf_with_constant_starting_position_char()
    {
        await base.IndexOf_with_constant_starting_position_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gt" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", { "$literal" : "e" }, 2] }, 6] } }] } }
""");
    }

    public override async Task IndexOf_with_parameter_starting_position()
    {
        await base.IndexOf_with_parameter_starting_position();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gt" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", { "$literal" : "e" }, 2] }, 6] } }] } }
""");
    }

    public override async Task IndexOf_with_parameter_starting_position_char()
    {
        await base.IndexOf_with_parameter_starting_position_char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gt" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$String", { "$literal" : "e" }, 2] }, 6] } }] } }
""");
    }

    public override async Task IndexOf_after_ToString()
    {
        await base.IndexOf_after_ToString();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : [{ "$toString" : "$Int" }, { "$literal" : "55" }] }, 1] } } }
""");
    }

    public override async Task IndexOf_over_ToString()
    {
        await base.IndexOf_over_ToString();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : [{ "$toString" : "$Int" }, null] }, null] }, "then" : null, "else" : { "$indexOfCP" : [{ "$literal" : "12559" }, { "$toString" : "$Int" }] } } }, 1] } } }
""");
    }

    public override async Task Replace()
    {
        await base.Replace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : { "$literal" : "Sea" }, "replacement" : { "$ifNull" : [{ "$literal" : "Rea" }, ""] } } }, { "$literal" : "Reattle" }] } } }
""");
    }

    public override async Task Replace_Char()
    {
        await base.Replace_Char();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : { "$literal" : "S" }, "replacement" : { "$ifNull" : [{ "$literal" : "R" }, ""] } } }, { "$literal" : "Reattle" }] } } }
""");
    }

    public override async Task Replace_with_empty_string()
    {
        await base.Replace_with_empty_string();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$ne" : "" } }, { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "$String", "replacement" : { "$ifNull" : [{ "$literal" : "" }, ""] } } }, { "$literal" : "" }] } }] } }
""");
    }

    public override async Task Replace_using_property_arguments()
    {
        await base.Replace_using_property_arguments();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$ne" : "" } }, { "$expr" : { "$eq" : [{ "$replaceAll" : { "input" : "$String", "find" : "$String", "replacement" : { "$ifNull" : [{ "$toString" : "$Int" }, ""] } } }, { "$toString" : "$Int" }] } }] } }
""");
    }

    public override async Task Substring()
    {
        await base.Substring();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 3] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 1, 2] } } }, { "$literal" : "ea" }] } }] } }
""");
    }

    public override async Task Substring_with_one_arg_with_zero_startIndex()
    {
        await base.Substring_with_one_arg_with_zero_startIndex();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, { "$subtract" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 0] }] } } }, { "$literal" : "Seattle" }] } } }
""");
    }

    public override async Task Substring_with_one_arg_with_constant()
    {
        await base.Substring_with_one_arg_with_constant();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 1] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 1, { "$subtract" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 1] }] } } }, { "$literal" : "eattle" }] } }] } }
""");
    }

    public override async Task Substring_with_one_arg_with_parameter()
    {
        await base.Substring_with_one_arg_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 2, { "$subtract" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 2] }] } } }, { "$literal" : "attle" }] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_zero_startIndex()
    {
        await base.Substring_with_two_args_with_zero_startIndex();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 3] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, 3] } } }, { "$literal" : "Sea" }] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_zero_length()
    {
        await base.Substring_with_two_args_with_zero_length();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 2] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 2, 0] } } }, { "$literal" : "" }] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_parameter()
    {
        await base.Substring_with_two_args_with_parameter();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "$expr" : { "$gte" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$strLenCP" : "$String" } } }, 5] } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 2, 3] } } }, { "$literal" : "att" }] } }] } }
""");
    }

    public override async Task Substring_with_two_args_with_IndexOf()
    {
        await base.Substring_with_two_args_with_IndexOf();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$regularExpression" : { "pattern" : "a", "options" : "s" } } }, { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", { "$indexOfCP" : ["$String", { "$literal" : "a" }] }, 3] } } }, { "$literal" : "att" }] } }] } }
""");
    }

    public override async Task IsNullOrEmpty()
    {
        await base.IsNullOrEmpty();

        AssertMql(
            """
NullableBasicTypesEntities.{ "$match" : { "$or" : [{ "String" : null }, { "String" : "" }] } }
""",
            //
            """
NullableBasicTypesEntities.{ "$project" : { "_v" : { "$or" : [{ "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, { "$eq" : ["$String", { "$literal" : "" }] }] }, "_id" : 0 } }
""");
    }

    public override async Task IsNullOrEmpty_negated()
    {
        await base.IsNullOrEmpty_negated();

        AssertMql(
            """
NullableBasicTypesEntities.{ "$match" : { "$and" : [{ "String" : { "$ne" : null } }, { "String" : { "$ne" : "" } }] } }
""",
            //
            """
NullableBasicTypesEntities.{ "$project" : { "_v" : { "$and" : [{ "$ne" : [{ "$ifNull" : ["$String", null] }, null] }, { "$ne" : ["$String", { "$literal" : "" }] }] }, "_id" : 0 } }
""");
    }

    public override async Task IsNullOrWhiteSpace()
    {
        await base.IsNullOrWhiteSpace();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$or" : [{ "String" : null }, { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : "\t\n\u000b\f\r \u0085             \u2028\u2029  　" } }, { "$literal" : "" }] } }] } }
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

    // Culture-sensitive comparisons have no $regularExpression equivalent, so the native translator declines
    // them (permanently). The driver-LINQ fallback then accepts the overload without checking the culture, so
    // nothing throws and the base's Assert.ThrowsAsync<InvalidOperationException> fails with ThrowsException.
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

    // Same decline as StartsWith_with_StringComparison_unsupported.
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
BasicTypesEntities.{ "$project" : { "_v" : { "$not" : [{ "$regexMatch" : { "input" : "$String", "regex" : { "$regularExpression" : { "pattern" : "eattle", "options" : "s" } } } }] }, "_id" : 0 } }
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

    // Same decline as StartsWith_with_StringComparison_unsupported.
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
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String", "chars" : "\t\n\u000b\f\r \u0085             \u2028\u2029  　" } }, { "$literal" : "Boston  " }] } } }
""");
    }

    public override async Task TrimStart_with_char_argument()
    {
        await base.TrimStart_with_char_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String", "chars" : { "$literal" : "S" } } }, { "$literal" : "eattle" }] } } }
""");
    }

    public override async Task TrimStart_with_char_array_argument()
    {
        await base.TrimStart_with_char_array_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$ltrim" : { "input" : "$String", "chars" : { "$literal" : "Se" } } }, { "$literal" : "attle" }] } } }
""");
    }

    public override async Task TrimEnd_without_arguments()
    {
        await base.TrimEnd_without_arguments();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String", "chars" : "\t\n\u000b\f\r \u0085             \u2028\u2029  　" } }, { "$literal" : "  Boston" }] } } }
""");
    }

    public override async Task TrimEnd_with_char_argument()
    {
        await base.TrimEnd_with_char_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String", "chars" : { "$literal" : "e" } } }, { "$literal" : "Seattl" }] } } }
""");
    }

    public override async Task TrimEnd_with_char_array_argument()
    {
        await base.TrimEnd_with_char_array_argument();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$rtrim" : { "input" : "$String", "chars" : { "$literal" : "le" } } }, { "$literal" : "Seatt" }] } } }
""");
    }

    public override async Task Trim_without_argument_in_predicate()
    {
        await base.Trim_without_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : "\t\n\u000b\f\r \u0085             \u2028\u2029  　" } }, { "$literal" : "Boston" }] } } }
""");
    }

    public override async Task Trim_with_char_argument_in_predicate()
    {
        await base.Trim_with_char_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : { "$literal" : "S" } } }, { "$literal" : "eattle" }] } } }
""");
    }

    public override async Task Trim_with_char_array_argument_in_predicate()
    {
        await base.Trim_with_char_array_argument_in_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$trim" : { "input" : "$String", "chars" : { "$literal" : "Se" } } }, { "$literal" : "attl" }] } } }
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
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""");
    }

    public override async Task Compare_simple_more_than_one()
    {
        await base.Compare_simple_more_than_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "_id" : { "$type" : -1 } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "_id" : { "$type" : -1 } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { } }
""");
    }

    public override async Task Compare_nested()
    {
        await base.Compare_nested();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$concat" : [{ "$literal" : "M" }, { "$ifNull" : ["$String", ""] }] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, 0] } } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$replaceAll" : { "input" : { "$literal" : "Seattle" }, "find" : { "$literal" : "Sea" }, "replacement" : { "$ifNull" : ["$String", ""] } } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lte" : [{ "$cmp" : ["$String", { "$concat" : [{ "$literal" : "M" }, { "$ifNull" : ["$String", ""] }] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, 0] } } }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$replaceAll" : { "input" : { "$literal" : "Seattle" }, "find" : { "$literal" : "Sea" }, "replacement" : { "$ifNull" : ["$String", ""] } } }] }, -1] } } }
""");
    }

    public override async Task Compare_multi_predicate()
    {
        await base.Compare_multi_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle", "$lt" : "Toronto" } } }
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
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$literal" : "Seattle" }] }, -1] } } }
""");
    }

    public override async Task CompareTo_simple_more_than_one()
    {
        await base.CompareTo_simple_more_than_one();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "_id" : { "$type" : -1 } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "_id" : { "$type" : -1 } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { } }
""");
    }

    public override async Task CompareTo_nested()
    {
        await base.CompareTo_nested();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$String", { "$concat" : [{ "$literal" : "M" }, { "$ifNull" : ["$String", ""] }] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$ne" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, 0] } } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$replaceAll" : { "input" : { "$literal" : "Seattle" }, "find" : { "$literal" : "Sea" }, "replacement" : { "$ifNull" : ["$String", ""] } } }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$lte" : [{ "$cmp" : ["$String", { "$concat" : [{ "$literal" : "M" }, { "$ifNull" : ["$String", ""] }] }] }, 0] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$substrCP" : ["$String", 0, 0] } } }] }, 1] } } }
""",
            //
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : [{ "$ifNull" : ["$String", null] }, { "$replaceAll" : { "input" : { "$literal" : "Seattle" }, "find" : { "$literal" : "Sea" }, "replacement" : { "$ifNull" : ["$String", ""] } } }] }, -1] } } }
""");
    }

    public override async Task Compare_to_multi_predicate()
    {
        await base.Compare_to_multi_predicate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "String" : { "$gte" : "Seattle", "$lt" : "Toronto" } } }
""");
    }

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

    public override async Task Join_non_aggregate()
    {
        await base.Join_non_aggregate();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : ["$String", { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : "|" }, { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : "foo" }, { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : "|" }, { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : null }, { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : "|" }, { "$literal" : "" }] }, { "$ifNull" : [{ "$literal" : "bar" }, { "$literal" : "" }] }] }, { "$literal" : "Seattle|foo||bar" }] } } }
""");
    }

    public override async Task Concat_operator()
    {
        await base.Concat_operator();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : ["$String", ""] }, { "$literal" : "Boston" }] }, { "$literal" : "SeattleBoston" }] } } }
""");
    }

    public override Task Concat_aggregate()
        // Fails: depends on native GroupBy (other agent's work) EF-149
        => AssertTranslationFailed(() => base.Concat_aggregate());

    public override async Task Concat_string_int_comparison1()
    {
        await base.Concat_string_int_comparison1();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : ["$String", ""] }, { "$ifNull" : [{ "$toString" : 10 }, ""] }] }, { "$literal" : "Seattle10" }] } } }
""");
    }

    public override async Task Concat_string_int_comparison2()
    {
        await base.Concat_string_int_comparison2();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$toString" : 10 }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "10Seattle" }] } } }
""");
    }

    public override async Task Concat_string_int_comparison3()
    {
        await base.Concat_string_int_comparison3();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$toString" : 30 }, ""] }, { "$ifNull" : ["$String", ""] }, { "$ifNull" : [{ "$toString" : 21 }, ""] }, { "$toString" : 42 }] }, { "$literal" : "30Seattle2142" }] } } }
""");
    }

    public override async Task Concat_string_int_comparison4()
    {
        await base.Concat_string_int_comparison4();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$toString" : "$Int" }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "8Seattle" }] } } }
""");
    }

    public override async Task Concat_string_string_comparison()
    {
        await base.Concat_string_string_comparison();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$literal" : "A" }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "ASeattle" }] } } }
""");
    }

    public override async Task Concat_method_comparison()
    {
        await base.Concat_method_comparison();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$literal" : "A" }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "ASeattle" }] } } }
""");
    }

    public override async Task Concat_method_comparison_2()
    {
        await base.Concat_method_comparison_2();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$literal" : "A" }, ""] }, { "$ifNull" : [{ "$literal" : "B" }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "ABSeattle" }] } } }
""");
    }

    public override async Task Concat_method_comparison_3()
    {
        await base.Concat_method_comparison_3();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$ifNull" : [{ "$literal" : "A" }, ""] }, { "$ifNull" : [{ "$literal" : "B" }, ""] }, { "$ifNull" : [{ "$literal" : "C" }, ""] }, { "$ifNull" : ["$String", ""] }] }, { "$literal" : "ABCSeattle" }] } } }
""");
    }

    public override async Task FirstOrDefault()
    {
        await base.FirstOrDefault();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$cond" : { "if" : { "$eq" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 0] }, "then" : "\u0000", "else" : { "$substrCP" : [{ "$ifNull" : ["$String", ""] }, 0, 1] } } } } }, { "$literal" : "S" }] } } }
""");
    }

    public override async Task LastOrDefault()
    {
        await base.LastOrDefault();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$String", null] }, null] }, "then" : null, "else" : { "$cond" : { "if" : { "$eq" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 0] }, "then" : "\u0000", "else" : { "$substrCP" : [{ "$ifNull" : ["$String", ""] }, { "$subtract" : [{ "$strLenCP" : { "$ifNull" : ["$String", ""] } }, 1] }, 1] } } } } }, { "$literal" : "e" }] } } }
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
    {
        await base.Regex_IsMatch_constant_input();

        AssertMql(
            """
BasicTypesEntities.{ "$match" : { "$expr" : { "$regexMatch" : { "input" : { "$literal" : "Seattle" }, "regex" : "$String", "options" : "" } } } }
""");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected static Task AssertTranslationFailed(Func<Task> query, params Type[] additionalAcceptedTypes)
        => MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(query, additionalAcceptedTypes);
}

#endif
