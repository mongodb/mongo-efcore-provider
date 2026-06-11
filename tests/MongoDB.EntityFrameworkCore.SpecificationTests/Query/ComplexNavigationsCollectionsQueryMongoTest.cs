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

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class ComplexNavigationsCollectionsQueryMongoTest : ComplexNavigationsCollectionsQueryTestBase<
    ComplexNavigationsQueryMongoFixture>
{
    public ComplexNavigationsCollectionsQueryMongoTest(ComplexNavigationsQueryMongoFixture fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Collection_projection_over_GroupBy_over_parameter(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Collection_projection_over_GroupBy_over_parameter(async));
        AssertMql();
    }
    public override async Task Complex_multi_include_with_order_by_and_paging(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Complex_multi_include_with_order_by_and_paging(async));
        AssertMql();
#else
        await base.Complex_multi_include_with_order_by_and_paging(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "Name" : 1 } }, { "$skip" : 0 }, { "$limit" : 10 }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Required_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Required_Inverse3Id", "as" : "_inner._lookup_OneToMany_Required2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Complex_multi_include_with_order_by_and_paging_joins_on_correct_key(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Complex_multi_include_with_order_by_and_paging_joins_on_correct_key(async));
        AssertMql();
#else
        // Fails: Cross-collection reference Include returns wrong data / missing required element EF-X024
        await AssertTranslationFailed(() => base.Complex_multi_include_with_order_by_and_paging_joins_on_correct_key(async));
        AssertMql(
            """
LevelOne.{ "$sort" : { "Name" : 1 } }, { "$skip" : 0 }, { "$limit" : 10 }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._outer._id", "foreignField" : "Level1_Required_Id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Required_Inverse3Id", "as" : "_inner._lookup_OneToMany_Required2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Complex_multi_include_with_order_by_and_paging_joins_on_correct_key2(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Complex_multi_include_with_order_by_and_paging_joins_on_correct_key2(async));
        AssertMql();
#else
        // Fails: Cross-document navigation/multi-include returns wrong data EF-216
        await AssertTranslationFailed(() => base.Complex_multi_include_with_order_by_and_paging_joins_on_correct_key2(async));
        AssertMql(
            """
LevelOne.{ "$sort" : { "Name" : 1 } }, { "$skip" : 0 }, { "$limit" : 10 }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse2Id", "as" : "_lookup_OneToMany_Optional1" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional1", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_lookup_OneToMany_Optional1._id", "foreignField" : "_id", "as" : "_lookup_OneToOne_Required_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_PK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_lookup_OneToOne_Required_PK2._id", "foreignField" : "OneToMany_Optional_Inverse4Id", "as" : "_lookup_OneToOne_Required_PK2._lookup_OneToMany_Optional3" } }
""");
#endif
    }
    public override async Task Complex_query_issue_21665(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Complex_query_issue_21665(async));
        AssertMql();
    }
    public override async Task Complex_query_with_let_collection_projection_FirstOrDefault(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Complex_query_with_let_collection_projection_FirstOrDefault(async));
        AssertMql();
    }
    public override async Task Complex_query_with_let_collection_projection_FirstOrDefault_with_ToList_on_inner_and_outer(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Complex_query_with_let_collection_projection_FirstOrDefault_with_ToList_on_inner_and_outer(async));
        AssertMql();
    }
    public override async Task Filtered_ThenInclude_OrderBy(bool async)
    {
        await base.Filtered_ThenInclude_OrderBy(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }], "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_OrderBy(bool async)
    {
        await base.Filtered_include_OrderBy(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_OrderBy_EF_Property(bool async)
    {
        await base.Filtered_include_OrderBy_EF_Property(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_Skip_Take_with_another_Skip_Take_on_top_level(bool async)
    {
        await base.Filtered_include_Skip_Take_with_another_Skip_Take_on_top_level(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "_id" : -1 } }, { "$skip" : 1 }, { "$limit" : 5 }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : -1 } }, { "$skip" : 1 }, { "$limit" : 4 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_Skip_without_OrderBy(bool async)
    {
        await base.Filtered_include_Skip_without_OrderBy(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$skip" : 1 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_Take_with_another_Take_on_top_level(bool async)
    {
        await base.Filtered_include_Take_with_another_Take_on_top_level(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : -1 } }, { "$limit" : 4 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_Take_without_OrderBy(bool async)
    {
        await base.Filtered_include_Take_without_OrderBy(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$limit" : 1 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_ThenInclude_OrderBy(bool async)
    {
        await base.Filtered_include_ThenInclude_OrderBy(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$sort" : { "Name" : -1 } }], "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_after_different_filtered_include_different_level(bool async)
    {
        await base.Filtered_include_after_different_filtered_include_different_level(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "Name" : 1 } }, { "$limit" : 3 }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Required_Inverse3Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Bar" } } }, { "$sort" : { "Name" : -1 } }, { "$skip" : 1 }], "as" : "_lookup_OneToMany_Required2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_after_different_filtered_include_same_level(bool async)
    {
        await base.Filtered_include_after_different_filtered_include_same_level(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Required_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Bar" } } }, { "$sort" : { "Name" : -1 } }, { "$skip" : 1 }], "as" : "_lookup_OneToMany_Required1" } }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "Name" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_after_reference_navigation(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/ThenInclude not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Filtered_include_after_reference_navigation(async));
        AssertMql();
#else
        await base.Filtered_include_after_reference_navigation(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }, { "$limit" : 3 }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation(bool async)
    {
        await base.Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToOne_Optional_PK_Inverse3Id", "as" : "_lookup_OneToOne_Optional_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_PK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_and_non_filtered_include_on_same_navigation1(bool async)
    {
        await base.Filtered_include_and_non_filtered_include_on_same_navigation1(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_and_non_filtered_include_on_same_navigation2(bool async)
    {
        await base.Filtered_include_and_non_filtered_include_on_same_navigation2(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_OrderBy_Skip(bool async)
    {
        await base.Filtered_include_basic_OrderBy_Skip(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_OrderBy_Skip_Take(bool async)
    {
        await base.Filtered_include_basic_OrderBy_Skip_Take(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_OrderBy_Skip_Take_EF_Property(bool async)
    {
        await base.Filtered_include_basic_OrderBy_Skip_Take_EF_Property(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_OrderBy_Take(bool async)
    {
        await base.Filtered_include_basic_OrderBy_Take(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "Name" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_Where(bool async)
    {
        await base.Filtered_include_basic_Where(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "_id" : { "$gt" : 5 } } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_basic_Where_EF_Property(bool async)
    {
        await base.Filtered_include_basic_Where_EF_Property(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "_id" : { "$gt" : 5 } } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_calling_methods_directly_on_parameter_throws(bool async)
    {
        await base.Filtered_include_calling_methods_directly_on_parameter_throws(async);
        AssertMql();
    }
    public override async Task Filtered_include_complex_three_level_with_middle_having_filter1(bool async)
    {
        await base.Filtered_include_complex_three_level_with_middle_having_filter1(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "OneToMany_Required_Inverse4Id", "as" : "_lookup_OneToMany_Required3" } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse4Id", "as" : "_lookup_OneToMany_Optional3" } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }], "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_complex_three_level_with_middle_having_filter2(bool async)
    {
        await base.Filtered_include_complex_three_level_with_middle_having_filter2(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "OneToMany_Required_Inverse4Id", "as" : "_lookup_OneToMany_Required3" } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse4Id", "as" : "_lookup_OneToMany_Optional3" } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }], "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_context_accessed_inside_filter(bool async)
    {
#if EF8 || EF9
        await base.Filtered_include_context_accessed_inside_filter(async);
        AssertMql(
            """
LevelOne.{ "$count" : "_v" }
""",
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
#else
        await base.Filtered_include_context_accessed_inside_filter(async);
        AssertMql(
            """
LevelOne.{ "$count" : "_v" }
""",
            //
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
#endif
    }
    public override async Task Filtered_include_context_accessed_inside_filter_correlated(bool async)
    {
        await base.Filtered_include_context_accessed_inside_filter_correlated(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_different_filter_set_on_same_navigation_twice(bool async)
    {
        await base.Filtered_include_different_filter_set_on_same_navigation_twice(async);
        AssertMql();
    }
    public override async Task Filtered_include_different_filter_set_on_same_navigation_twice_multi_level(bool async)
    {
        await base.Filtered_include_different_filter_set_on_same_navigation_twice_multi_level(async);
        AssertMql();
    }
    public override async Task Filtered_include_include_parameter_used_inside_filter_throws(bool async)
    {
        await base.Filtered_include_include_parameter_used_inside_filter_throws(async);
        AssertMql();
    }
    public override async Task Filtered_include_is_considered_loaded(bool async)
    {
        await base.Filtered_include_is_considered_loaded(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_on_ThenInclude(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/ThenInclude not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Filtered_include_on_ThenInclude(async));
        AssertMql();
#else
        await base.Filtered_include_on_ThenInclude(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }, { "$limit" : 3 }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Filtered_include_on_ThenInclude_EF_Property(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/ThenInclude not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Filtered_include_on_ThenInclude_EF_Property(async));
        AssertMql();
#else
        await base.Filtered_include_on_ThenInclude_EF_Property(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "Name" : 1 } }, { "$skip" : 1 }, { "$limit" : 3 }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Filtered_include_outer_parameter_used_inside_filter(bool async)
    {
        // Fails: Correlated cross-DbSet subquery in a projection (filter references the outer Select parameter) EF-X001
        await AssertTranslationFailed(() => base.Filtered_include_outer_parameter_used_inside_filter(async));
        AssertMql();
    }
    public override async Task Filtered_include_same_filter_set_on_same_navigation_twice(bool async)
    {
        await base.Filtered_include_same_filter_set_on_same_navigation_twice(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : -1 } }, { "$limit" : 2 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_same_filter_set_on_same_navigation_twice_followed_by_ThenIncludes(bool async)
    {
        await base.Filtered_include_same_filter_set_on_same_navigation_twice_followed_by_ThenIncludes(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Required_Id", "as" : "_lookup_OneToOne_Required_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_FK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Filtered_include_variable_used_inside_filter(bool async)
    {
#if EF8 || EF9
        await base.Filtered_include_variable_used_inside_filter(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
#else
        await base.Filtered_include_variable_used_inside_filter(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_lookup_OneToMany_Optional1" } }
""");
#endif
    }
    public override async Task Filtered_include_with_Distinct_throws(bool async)
    {
        await base.Filtered_include_with_Distinct_throws(async);
        AssertMql();
    }
    public override async Task Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_FirstOrDefault_on_top_level(bool async)
    {
        await base.Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_FirstOrDefault_on_top_level(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "_id" : 1 } }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$limit" : 40 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }, { "$limit" : 1 }
""");
    }
    public override async Task Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_unordered_Take_on_top_level(bool async)
    {
        await base.Filtered_include_with_Take_without_order_by_followed_by_ThenInclude_and_unordered_Take_on_top_level(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "_id" : 1 } }, { "$limit" : 30 }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$limit" : 40 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Final_GroupBy_property_entity_Include_collection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_collection_multiple(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection_multiple(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_collection_nested(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection_nested(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_collection_reference(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection_reference(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_collection_reference_same_level(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection_reference_same_level(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_reference(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_reference(async));
        AssertMql();
    }
    public override async Task Final_GroupBy_property_entity_Include_reference_multiple(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_reference_multiple(async));
        AssertMql();
    }
    public override async Task FirstOrDefault_with_predicate_on_correlated_collection_in_projection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.FirstOrDefault_with_predicate_on_correlated_collection_in_projection(async));
        AssertMql();
    }
    public override async Task Include_ThenInclude_ThenInclude_followed_by_two_nested_selects(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_ThenInclude_ThenInclude_followed_by_two_nested_selects(async));
        AssertMql();
    }
    public override async Task Include_after_Select(bool async)
    {
        await base.Include_after_Select(async);
        AssertMql();
    }
    public override async Task Include_after_SelectMany(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_after_SelectMany(async));
        AssertMql();
    }
    public override async Task Include_after_SelectMany_and_multiple_reference_navigations(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_after_SelectMany_and_multiple_reference_navigations(async));
        AssertMql();
    }
    public override async Task Include_after_SelectMany_and_reference_navigation(bool async)
    {
        await base.Include_after_SelectMany_and_reference_navigation(async);
        AssertMql();
    }
    public override async Task Include_after_multiple_SelectMany_and_reference_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_after_multiple_SelectMany_and_reference_navigation(async));
        AssertMql();
    }
    public override async Task Include_and_ThenInclude_collections_followed_by_projecting_the_first_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_and_ThenInclude_collections_followed_by_projecting_the_first_collection(async));
        AssertMql();
    }
    public override async Task Include_collection(bool async)
    {
        await base.Include_collection(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse2Id", "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_ThenInclude_reference_followed_by_projection_into_anonmous_type(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_collection_ThenInclude_reference_followed_by_projection_into_anonmous_type(async));
        AssertMql();
    }
    public override async Task Include_collection_ThenInclude_two_references(bool async)
    {
        await base.Include_collection_ThenInclude_two_references(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToOne_Optional_PK_Inverse3Id", "as" : "_lookup_OneToOne_Optional_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_PK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_and_another_navigation_chain_followed_by_projecting_the_first_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_collection_and_another_navigation_chain_followed_by_projecting_the_first_collection(async));
        AssertMql();
    }
    public override async Task Include_collection_followed_by_complex_includes_and_projecting_the_included_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_collection_followed_by_complex_includes_and_projecting_the_included_collection(async));
        AssertMql();
    }
    public override async Task Include_collection_followed_by_include_reference(bool async)
    {
        await base.Include_collection_followed_by_include_reference(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToOne_Optional_PK_Inverse3Id", "as" : "_lookup_OneToOne_Optional_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_PK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_followed_by_projecting_the_included_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_collection_followed_by_projecting_the_included_collection(async));
        AssertMql();
    }
    public override async Task Include_collection_multiple(bool async)
    {
        // Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023
        await AssertTranslationFailed(() => base.Include_collection_multiple(async));
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToOne_Optional_PK_Inverse3Id", "as" : "_lookup_OneToOne_Optional_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_PK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_multiple_with_filter(bool async)
    {
        // Fails: Cross-collection navigation in a correlated subquery (root Where(... .Count() > 0)) not translated EF-216
        await AssertTranslationFailed(() => base.Include_collection_multiple_with_filter(async));
        AssertMql();
    }
    public override async Task Include_collection_multiple_with_filter_EF_Property(bool async)
    {
        // Fails: Cross-collection navigation in a correlated subquery (root Where(... .Count() > 0)) not translated EF-216
        await AssertTranslationFailed(() => base.Include_collection_multiple_with_filter_EF_Property(async));
        AssertMql();
    }
    public override async Task Include_collection_then_reference(bool async)
    {
        await base.Include_collection_then_reference(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_with_conditional_order_by(bool async)
    {
        await base.Include_collection_with_conditional_order_by(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$cond" : { "if" : { "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$Name" }, 2] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$Name", "03", "$$start"] }, "$$start"] }] } } }, "then" : 1, "else" : 2 } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse2Id", "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Include_collection_with_groupby_in_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Include_collection_with_groupby_in_subquery(async));
        AssertMql();
    }
    public override async Task Include_collection_with_groupby_in_subquery_and_filter_after_groupby(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Include_collection_with_groupby_in_subquery_and_filter_after_groupby(async));
        AssertMql();
    }
    public override async Task Include_collection_with_groupby_in_subquery_and_filter_before_groupby(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Include_collection_with_groupby_in_subquery_and_filter_before_groupby(async));
        AssertMql();
    }
    public override async Task Include_collection_with_multiple_orderbys_complex(bool async)
    {
        await base.Include_collection_with_multiple_orderbys_complex(async);
        AssertMql(
            """
LevelTwo.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$add" : [{ "$abs" : "$Level1_Required_Id" }, 7] } } }, { "$sort" : { "_key1" : 1, "_document.Name" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }
""");
    }
    public override async Task Include_collection_with_multiple_orderbys_complex_repeated(bool async)
    {
        await base.Include_collection_with_multiple_orderbys_complex_repeated(async);
        AssertMql(
            """
LevelTwo.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$subtract" : [0, "$Level1_Required_Id"] }, "_key2" : { "$subtract" : [0, "$Level1_Required_Id"] } } }, { "$sort" : { "_key1" : 1, "_key2" : 1, "_document.Name" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }
""");
    }
    public override async Task Include_collection_with_multiple_orderbys_complex_repeated_checked(bool async)
    {
        // Fails: checked issue EF-249
        await AssertTranslationFailed(() => base.Include_collection_with_multiple_orderbys_complex_repeated_checked(async));
        AssertMql(
            """
LevelTwo.
""");
    }
    public override async Task Include_collection_with_multiple_orderbys_member(bool async)
    {
        await base.Include_collection_with_multiple_orderbys_member(async);
        AssertMql(
            """
LevelTwo.{ "$sort" : { "Name" : 1, "Level1_Required_Id" : 1 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }
""");
    }
    public override async Task Include_collection_with_multiple_orderbys_methodcall(bool async)
    {
        await base.Include_collection_with_multiple_orderbys_methodcall(async);
        AssertMql(
            """
LevelTwo.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$abs" : "$Level1_Required_Id" } } }, { "$sort" : { "_key1" : 1, "_document.Name" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }
""");
    }
    public override async Task Include_collection_with_multiple_orderbys_property(bool async)
    {
        await base.Include_collection_with_multiple_orderbys_property(async);
        AssertMql(
            """
LevelTwo.{ "$sort" : { "Level1_Required_Id" : 1, "Name" : 1 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }
""");
    }
    public override async Task Include_inside_subquery(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Include_inside_subquery(async));
        AssertMql();
    }
    public override async Task Include_nested_with_optional_navigation(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_nested_with_optional_navigation(async));
        AssertMql();
#else
        await base.Include_nested_with_optional_navigation(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.Name" : { "$ne" : "L2 09" } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Required_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "Level3_Required_Id", "as" : "_lookup_OneToOne_Required_FK3" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_FK3", "preserveNullAndEmptyArrays" : true } }], "as" : "_inner._lookup_OneToMany_Required2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_partially_added_before_Where_and_then_build_upon(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_partially_added_before_Where_and_then_build_upon(async));
        AssertMql();
#else
        // Fails: Cross-collection reference Include missing required element on materialization EF-X024
        await AssertTranslationFailed(() => base.Include_partially_added_before_Where_and_then_build_upon(async));
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "OneToOne_Optional_PK_Inverse2Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "$or" : [{ "_outer._inner._id" : { "$lt" : 3 } }, { "_inner._id" : { "$gt" : 8 } }] } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "Level3_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK3" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK3", "preserveNullAndEmptyArrays" : true } }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_partially_added_before_Where_and_then_build_upon_with_filtered_include(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_partially_added_before_Where_and_then_build_upon_with_filtered_include(async));
        AssertMql();
#else
        // Fails: Cross-collection reference Include returns wrong data / missing required element EF-X024
        await AssertTranslationFailed(() => base.Include_partially_added_before_Where_and_then_build_upon_with_filtered_include(async));
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "OneToOne_Optional_PK_Inverse2Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "$or" : [{ "_outer._inner._id" : { "$lt" : 3 } }, { "_inner._id" : { "$gt" : 8 } }] } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Required_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "Level3_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK3" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK3", "preserveNullAndEmptyArrays" : true } }], "as" : "_inner._lookup_OneToMany_Required2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 3 }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_reference_ThenInclude_collection_order_by(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_reference_ThenInclude_collection_order_by(async));
        AssertMql();
#else
        await base.Include_reference_ThenInclude_collection_order_by(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "Name" : 1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_reference_and_collection_order_by(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_reference_and_collection_order_by(async));
        AssertMql();
#else
        await base.Include_reference_and_collection_order_by(async);
        AssertMql(
            """
LevelOne.{ "$sort" : { "Name" : 1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_reference_collection_order_by_reference_navigation(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_reference_collection_order_by_reference_navigation(async));
        AssertMql();
#else
        await base.Include_reference_collection_order_by_reference_navigation(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$sort" : { "_inner._id" : 1 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Include_reference_followed_by_include_collection(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Include_reference_followed_by_include_collection(async));
        AssertMql();
#else
        await base.Include_reference_followed_by_include_collection(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Including_reference_navigation_and_projecting_collection_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Including_reference_navigation_and_projecting_collection_navigation(async));
        AssertMql();
    }
    public override async Task LeftJoin_with_Any_on_outer_source_and_projecting_collection_from_inner(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.LeftJoin_with_Any_on_outer_source_and_projecting_collection_from_inner(async));
        AssertMql();
    }
    public override async Task Lift_projection_mapping_when_pushing_down_subquery(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Lift_projection_mapping_when_pushing_down_subquery(async));
        AssertMql();
    }
    public override async Task Multi_level_include_one_to_many_optional_and_one_to_many_optional_produces_valid_sql(bool async)
    {
        await base.Multi_level_include_one_to_many_optional_and_one_to_many_optional_produces_valid_sql(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }
    public override async Task Multiple_SelectMany_navigation_property_followed_by_select_collection_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Multiple_SelectMany_navigation_property_followed_by_select_collection_navigation(async));
        AssertMql();
    }
    public override async Task Multiple_SelectMany_with_Include(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Multiple_SelectMany_with_Include(async));
        AssertMql();
    }
    public override async Task Multiple_complex_include_select(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_complex_include_select(async));
        AssertMql();
#else
        await base.Multiple_complex_include_select(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_outer._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_outer._lookup_OneToMany_Optional1" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Multiple_complex_includes(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_complex_includes(async));
        AssertMql();
#else
        await base.Multiple_complex_includes(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_outer._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_outer._lookup_OneToMany_Optional1" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Multiple_complex_includes_EF_Property(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_complex_includes_EF_Property(async));
        AssertMql();
#else
        await base.Multiple_complex_includes_EF_Property(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_outer._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK2", "preserveNullAndEmptyArrays" : true } }], "as" : "_outer._lookup_OneToMany_Optional1" } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Multiple_complex_includes_self_ref(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_complex_includes_self_ref(async));
        AssertMql();
#else
        // Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023
        await AssertTranslationFailed(() => base.Multiple_complex_includes_self_ref(async));
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelOne", "localField" : "_outer.OneToOne_Optional_Self1Id", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelOne", "let" : { "localField" : "$_outer._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Self_Inverse1Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelOne", "localField" : "OneToOne_Optional_Self1Id", "foreignField" : "_id", "as" : "_lookup_OneToOne_Optional_Self1" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_Self1", "preserveNullAndEmptyArrays" : true } }], "as" : "_outer._lookup_OneToMany_Optional_Self1" } }
""");
#endif
    }
    public override async Task Multiple_complex_includes_self_ref_EF_Property(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_complex_includes_self_ref_EF_Property(async));
        AssertMql();
#else
        // Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023
        await AssertTranslationFailed(() => base.Multiple_complex_includes_self_ref_EF_Property(async));
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelOne", "localField" : "_outer.OneToOne_Optional_Self1Id", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelOne", "let" : { "localField" : "$_outer._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Self_Inverse1Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelOne", "localField" : "OneToOne_Optional_Self1Id", "foreignField" : "_id", "as" : "_lookup_OneToOne_Optional_Self1" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_Self1", "preserveNullAndEmptyArrays" : true } }], "as" : "_outer._lookup_OneToMany_Optional_Self1" } }
""");
#endif
    }
    public override async Task Multiple_include_with_multiple_optional_navigations(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_include_with_multiple_optional_navigations(async));
        AssertMql();
#else
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Multiple_include_with_multiple_optional_navigations(async));
        AssertMql(
            """
LevelOne.
""");
#endif
    }
    public override async Task Multiple_optional_navigation_with_Include(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_optional_navigation_with_Include(async));
        AssertMql();
#else
        // Fails: Cross-collection reference Include missing required element on materialization EF-X024
        await AssertTranslationFailed(() => base.Multiple_optional_navigation_with_Include(async));
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse2Id", "as" : "_lookup_OneToMany_Optional1" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional1", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_lookup_OneToMany_Optional1._id", "foreignField" : "_id", "as" : "_lookup_OneToOne_Required_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_PK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_lookup_OneToOne_Required_PK2._id", "foreignField" : "OneToMany_Optional_Inverse4Id", "as" : "_lookup_OneToOne_Required_PK2._lookup_OneToMany_Optional3" } }
""");
#endif
    }
    public override async Task Multiple_optional_navigation_with_string_based_Include(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Multiple_optional_navigation_with_string_based_Include(async));
        AssertMql();
#else
        // Fails: Cross-collection reference Include missing required element on materialization EF-X024
        await AssertTranslationFailed(() => base.Multiple_optional_navigation_with_string_based_Include(async));
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse2Id", "as" : "_lookup_OneToMany_Optional1" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional1", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_lookup_OneToMany_Optional1._id", "foreignField" : "_id", "as" : "_lookup_OneToOne_Required_PK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_PK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_lookup_OneToOne_Required_PK2._id", "foreignField" : "OneToMany_Optional_Inverse4Id", "as" : "_lookup_OneToOne_Required_PK2._lookup_OneToMany_Optional3" } }
""");
#endif
    }
    public override async Task Null_check_in_Dto_projection_should_not_be_removed(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Null_check_in_Dto_projection_should_not_be_removed(async));
        AssertMql();
    }
    public override async Task Null_check_in_anonymous_type_projection_should_not_be_removed(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Null_check_in_anonymous_type_projection_should_not_be_removed(async));
        AssertMql();
    }
    public override async Task Optional_navigation_with_Include_ThenInclude(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Optional_navigation_with_Include_ThenInclude(async));
        AssertMql();
#else
        await base.Optional_navigation_with_Include_ThenInclude(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_inner._id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelFour", "localField" : "_id", "foreignField" : "Level3_Optional_Id", "as" : "_lookup_OneToOne_Optional_FK3" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Optional_FK3", "preserveNullAndEmptyArrays" : true } }], "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Optional_navigation_with_Include_and_order(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Optional_navigation_with_Include_and_order(async));
        AssertMql();
#else
        await base.Optional_navigation_with_Include_and_order(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$sort" : { "_inner.Name" : 1 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Optional_navigation_with_order_by_and_Include(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Optional_navigation_with_order_by_and_Include(async));
        AssertMql();
#else
        await base.Optional_navigation_with_order_by_and_Include(async);
        AssertMql(
            """
LevelOne.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_outer._id", "foreignField" : "Level1_Optional_Id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$sort" : { "_inner.Name" : 1 } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_inner._id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_inner._lookup_OneToMany_Optional2" } }, { "$set" : { "_inner" : { "$cond" : [{ "$eq" : [{ "$type" : "$_inner._id" }, "missing"] }, "$$REMOVE", "$_inner"] } } }
""");
#endif
    }
    public override async Task Orderby_SelectMany_with_Include1(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Orderby_SelectMany_with_Include1(async));
        AssertMql();
    }
    public override async Task Project_collection_and_include(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_and_include(async));
        AssertMql();
    }
#if !EF8
    public override async Task Project_collection_and_nested_conditional(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_and_nested_conditional(async));
        AssertMql();
    }
#endif
    public override async Task Project_collection_and_root_entity(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_and_root_entity(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation_composed(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation_composed(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation_nested(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation_nested(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation_nested_anonymous(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation_nested_anonymous(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation_nested_with_take(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation_nested_with_take(async));
        AssertMql();
    }
    public override async Task Project_collection_navigation_using_ef_property(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_collection_navigation_using_ef_property(async));
        AssertMql();
    }
    public override async Task Project_navigation_and_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Project_navigation_and_collection(async));
        AssertMql();
    }
    public override async Task Projecting_collection_after_optional_reference_correlated_with_parent(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Projecting_collection_after_optional_reference_correlated_with_parent(async));
        AssertMql();
    }
    public override async Task Projecting_collection_with_FirstOrDefault(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Projecting_collection_with_FirstOrDefault(async));
        AssertMql();
    }
    public override async Task Projecting_collection_with_group_by_after_optional_reference_correlated_with_parent(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Projecting_collection_with_group_by_after_optional_reference_correlated_with_parent(async));
        AssertMql();
    }
    public override async Task Queryable_in_subquery_works_when_final_projection_is_List(bool async)
    {
        await base.Queryable_in_subquery_works_when_final_projection_is_List(async);
        AssertMql();
    }
    public override async Task Required_navigation_with_Include(bool async)
    {
        // Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023
        await AssertTranslationFailed(() => base.Required_navigation_with_Include(async));
        AssertMql(
            """
LevelThree.{ "$lookup" : { "from" : "LevelTwo", "localField" : "OneToMany_Optional_Inverse3Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Optional_Inverse3" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional_Inverse3", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelOne", "localField" : "_lookup_OneToMany_Optional_Inverse3.OneToMany_Required_Inverse2Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Required_Inverse2" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Required_Inverse2", "preserveNullAndEmptyArrays" : true } }
""");
    }
    public override async Task Required_navigation_with_Include_ThenInclude(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Required_navigation_with_Include_ThenInclude(async));
        AssertMql();
#else
        // Fails: Cross-collection multiple/self-ref/required Include returns wrong data EF-X023
        await AssertTranslationFailed(() => base.Required_navigation_with_Include_ThenInclude(async));
        AssertMql(
            """
LevelFour.{ "$lookup" : { "from" : "LevelThree", "localField" : "OneToMany_Optional_Inverse4Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Optional_Inverse4" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional_Inverse4", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "_lookup_OneToMany_Optional_Inverse4.OneToMany_Required_Inverse3Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Required_Inverse3" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Required_Inverse3", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelOne", "localField" : "OneToMany_Optional_Inverse2Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Optional_Inverse2" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Optional_Inverse2", "preserveNullAndEmptyArrays" : true } }
""");
#endif
    }
    public override async Task SelectMany_DefaultIfEmpty_multiple_times_with_joins_projecting_a_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_DefaultIfEmpty_multiple_times_with_joins_projecting_a_collection(async));
        AssertMql();
    }
    public override async Task SelectMany_navigation_property_followed_by_select_collection_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_navigation_property_followed_by_select_collection_navigation(async));
        AssertMql();
    }
    public override async Task SelectMany_navigation_property_with_include_and_followed_by_select_collection_navigation(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_navigation_property_with_include_and_followed_by_select_collection_navigation(async));
        AssertMql();
    }
    public override async Task SelectMany_over_conditional_empty_source(bool async)
    {
        await base.SelectMany_over_conditional_empty_source(async);
        AssertMql();
    }
    public override async Task SelectMany_over_conditional_null_source(bool async)
    {
        await base.SelectMany_over_conditional_null_source(async);
        AssertMql();
    }
    public override async Task SelectMany_with_Include1(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_Include1(async));
        AssertMql();
    }
    public override async Task SelectMany_with_Include2(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_Include2(async));
        AssertMql();
    }
    public override async Task SelectMany_with_Include_ThenInclude(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_Include_ThenInclude(async));
        AssertMql();
    }
    public override async Task SelectMany_with_Include_and_order_by(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_Include_and_order_by(async));
        AssertMql();
    }
    public override async Task SelectMany_with_navigation_and_Distinct(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_navigation_and_Distinct(async));
        AssertMql();
    }
    public override async Task SelectMany_with_navigation_and_Distinct_projecting_columns_including_join_key(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_navigation_and_Distinct_projecting_columns_including_join_key(async));
        AssertMql();
    }
    public override async Task SelectMany_with_order_by_and_Include(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_order_by_and_Include(async));
        AssertMql();
    }
    public override async Task Select_nav_prop_collection_one_to_many_required(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Select_nav_prop_collection_one_to_many_required(async));
        AssertMql();
    }
    public override async Task Select_subquery_single_nested_subquery(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Select_subquery_single_nested_subquery(async));
        AssertMql();
    }
    public override async Task Select_subquery_single_nested_subquery2(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Select_subquery_single_nested_subquery2(async));
        AssertMql();
    }
    public override async Task Skip_Take_Distinct_on_grouping_element(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_Distinct_on_grouping_element(async));
        AssertMql();
    }
    public override async Task Skip_Take_Select_collection_Skip_Take(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Skip_Take_Select_collection_Skip_Take(async));
        AssertMql();
    }
    public override async Task Skip_Take_ToList_on_grouping_element(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_ToList_on_grouping_element(async));
        AssertMql();
    }
    public override async Task Skip_Take_on_grouping_element(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_on_grouping_element(async));
        AssertMql();
    }
    public override async Task Skip_Take_on_grouping_element_inside_collection_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_on_grouping_element_inside_collection_projection(async));
        AssertMql();
    }
    public override async Task Skip_Take_on_grouping_element_into_non_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_on_grouping_element_into_non_entity(async));
        AssertMql();
    }
    public override async Task Skip_Take_on_grouping_element_with_collection_include(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_on_grouping_element_with_collection_include(async));
        AssertMql();
    }
    public override async Task Skip_Take_on_grouping_element_with_reference_include(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_Take_on_grouping_element_with_reference_include(async));
        AssertMql();
    }
    public override async Task Skip_on_grouping_element(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Skip_on_grouping_element(async));
        AssertMql();
    }
    public override async Task Take_Select_collection_Take(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Take_Select_collection_Take(async));
        AssertMql();
    }
    public override async Task Take_on_correlated_collection_in_projection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Take_on_correlated_collection_in_projection(async));
        AssertMql();
    }
    public override async Task Take_on_grouping_element(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Take_on_grouping_element(async));
        AssertMql();
    }

    public override async Task Complex_SelectMany_with_nested_navigations_and_explicit_DefaultIfEmpty_with_other_query_operators_composed_on_top(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Complex_SelectMany_with_nested_navigations_and_explicit_DefaultIfEmpty_with_other_query_operators_composed_on_top(async));
        AssertMql();
    }

    public override async Task Filtered_include_multiple_multi_level_includes_with_first_level_using_filter_include_on_one_of_the_chains_only(bool async)
    {
        await base.Filtered_include_multiple_multi_level_includes_with_first_level_using_filter_include_on_one_of_the_chains_only(async);
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$match" : { "Name" : { "$ne" : "Foo" } } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "Level2_Required_Id", "as" : "_lookup_OneToOne_Required_FK2" } }, { "$unwind" : { "path" : "$_lookup_OneToOne_Required_FK2", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "LevelThree", "localField" : "_id", "foreignField" : "OneToMany_Optional_Inverse3Id", "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }

    public override async Task Multi_level_include_correct_PK_is_chosen_as_the_join_predicate_for_queries_that_join_same_table_multiple_times(bool async)
    {
        // Fails: Cross-document navigation/multi-include returns wrong data EF-216
        await AssertTranslationFailed(() => base.Multi_level_include_correct_PK_is_chosen_as_the_join_predicate_for_queries_that_join_same_table_multiple_times(async));
        AssertMql(
            """
LevelOne.{ "$lookup" : { "from" : "LevelTwo", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse2Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelThree", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$OneToMany_Optional_Inverse3Id", "$$localField"] } } }, { "$lookup" : { "from" : "LevelTwo", "localField" : "OneToMany_Required_Inverse3Id", "foreignField" : "_id", "as" : "_lookup_OneToMany_Required_Inverse3" } }, { "$unwind" : { "path" : "$_lookup_OneToMany_Required_Inverse3", "preserveNullAndEmptyArrays" : true } }], "as" : "_lookup_OneToMany_Optional2" } }], "as" : "_lookup_OneToMany_Optional1" } }
""");
    }

    public override async Task SelectMany_with_predicate_and_DefaultIfEmpty_projecting_root_collection_element_and_another_collection(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.SelectMany_with_predicate_and_DefaultIfEmpty_projecting_root_collection_element_and_another_collection(async));
        AssertMql();
    }

    protected new static async Task AssertTranslationFailed(Func<Task> query)
    {
        try
        {
            await query();
        }
        catch
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected query to fail but it succeeded.");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
