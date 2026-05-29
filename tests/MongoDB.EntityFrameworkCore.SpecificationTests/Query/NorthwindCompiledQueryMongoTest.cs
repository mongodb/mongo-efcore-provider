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
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindCompiledQueryMongoTest : NorthwindCompiledQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindCompiledQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        fixture.TestMqlLoggerFactory.Clear();
        //fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override void DbSet_query()
    {
        base.DbSet_query();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void DbSet_query_first()
    {
        base.DbSet_query_first();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Query_ending_with_include()
    {
        base.Query_ending_with_include();

        AssertMql(
            """
Customers.
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANATR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANTON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "AROUT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BERGS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLAUS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLONP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOLID" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BONAP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOTTM" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BSBEV" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CACTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CENTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CHOPS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "COMMI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CONSH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "DRACD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "DUMON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "EASTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ERNSH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FAMIA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FISSA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FOLIG" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FOLKO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FURIB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GALED" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GODOS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GOURL" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GREAL" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GROSR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HANAR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HILAA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HUNGC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HUNGO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ISLAT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "KOENE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LACOR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAMAI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAUGB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAZYK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LEHMS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LETSS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LILAS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LINOD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LONEP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MAGAA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MAISD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MEREP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MORGK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "NORTS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OCEAN" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OLDWO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OTTIK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PARIS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PERIC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PICCO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PRINI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUEDE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUEEN" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUICK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RANCH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RATTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "REGGC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RICAR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RICSU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ROMEY" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SANTG" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SAVEA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SEVES" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SIMOB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SPECD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SPLIR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SUPRD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "THEBI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "THECR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TOMSP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TORTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TRADH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TRAIH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VAFFE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VICTE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VINET" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WANDK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WARTH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WELLI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WHITC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WILMK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WOLZA" } }
""",
            //
            """
Customers.
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANATR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANTON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "AROUT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BERGS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLAUS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLONP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOLID" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BONAP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOTTM" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BSBEV" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CACTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CENTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CHOPS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "COMMI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CONSH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "DRACD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "DUMON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "EASTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ERNSH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FAMIA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FISSA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FOLIG" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FOLKO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FRANS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "FURIB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GALED" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GODOS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GOURL" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GREAL" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "GROSR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HANAR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HILAA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HUNGC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "HUNGO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ISLAT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "KOENE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LACOR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAMAI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAUGB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LAZYK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LEHMS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LETSS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LILAS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LINOD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "LONEP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MAGAA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MAISD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MEREP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "MORGK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "NORTS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OCEAN" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OLDWO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "OTTIK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PARIS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PERIC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PICCO" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "PRINI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUEDE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUEEN" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "QUICK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RANCH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RATTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "REGGC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RICAR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "RICSU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ROMEY" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SANTG" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SAVEA" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SEVES" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SIMOB" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SPECD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SPLIR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "SUPRD" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "THEBI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "THECR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TOMSP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TORTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TRADH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "TRAIH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VAFFE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VICTE" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "VINET" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WANDK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WARTH" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WELLI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WHITC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WILMK" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "WOLZA" } }
""");
    }

    public override void Untyped_context()
    {
        base.Untyped_context();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Query_with_single_parameter()
    {
        base.Query_with_single_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void First_query_with_single_parameter()
    {
        base.First_query_with_single_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override void Query_with_two_parameters()
    {
        base.Query_with_two_parameters();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Query_with_three_parameters()
    {
        base.Query_with_three_parameters();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Query_with_contains()
    {
        base.Query_with_contains();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$in" : ["ANATR"] } } }
""");
    }

    public override void Query_with_closure()
    {
        base.Query_with_closure();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override void Compiled_query_when_does_not_end_in_query_operator()
    {
         // Fails: Compiled query with non-query operator issue EF-X011
         Assert.Contains(
             "No ultimate source found",
             Assert.Throws<ArgumentException>(() => base.Compiled_query_when_does_not_end_in_query_operator()).Message);

         AssertMql(
             """
Customers.
""");
    }

    public override async Task Compiled_query_with_max_parameters()
    {
        await base.Compiled_query_with_max_parameters();

        AssertMql(
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANATR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANTON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "AROUT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BERGS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLAUS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLONP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOLID" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BONAP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BSBEV" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CACTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CENTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CHOPS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CONSH" } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$count" : "_v" }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANATR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANTON" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "AROUT" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BERGS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLAUS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BLONP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BOLID" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BONAP" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "BSBEV" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CACTU" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CENTC" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CHOPS" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "CONSH" } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$count" : "_v" }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }] } }, { "$count" : "_v" }
""");
    }

    public override void Query_with_array_parameter()
    {
        base.Query_with_array_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Query_with_array_parameter_async()
    {
        await base.Query_with_array_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Multiple_queries()
    {
        AssertNoMultiCollectionQuerySupport(() => base.Multiple_queries());
    }

    public override void Compiled_query_when_using_member_on_context()
    {
        #if EF9 // XUnit assembly loading issue

        base.Compiled_query_when_using_member_on_context();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }
""");

        #endif
    }

    public override async Task First_query_with_cancellation_async()
    {
        await base.First_query_with_cancellation_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override async Task DbSet_query_first_async()
    {
        await base.DbSet_query_first_async();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override async Task First_query_with_single_parameter_async()
    {
        await base.First_query_with_single_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override async Task Keyless_query_first_async()
    {
        await base.Keyless_query_first_async();

        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : 1 } }, { "$limit" : 1 }
""");
    }

    public override async Task Query_with_closure_async_null()
    {
        await base.Query_with_closure_async_null();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : null } }
""");
    }

    public override async Task Query_with_three_parameters_async()
    {
        await base.Query_with_three_parameters_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Query_with_two_parameters_async()
    {
        await base.Query_with_two_parameters_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Keyless_query_async()
    {
        await base.Keyless_query_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override async Task Query_with_single_parameter_async()
    {
        await base.Query_with_single_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Keyless_query_first()
    {
        base.Keyless_query_first();

        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Query_with_closure_null()
    {
        base.Query_with_closure_null();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : null } }
""");
    }

    public override async Task Query_with_closure_async()
    {
        await base.Query_with_closure_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task Untyped_context_async()
    {
        await base.Untyped_context_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override async Task DbSet_query_async()
    {
        await base.DbSet_query_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Keyless_query()
    {
        base.Keyless_query();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Query_with_single_parameter_with_include()
    {
        base.Query_with_single_parameter_with_include();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ANATR" } }
""");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    // Fails: Cross-document navigation access issue EF-216
    private static void AssertNoMultiCollectionQuerySupport(Action query)
        => Assert.Contains("Unsupported cross-DbSet query between",
            Assert.Throws<InvalidOperationException>(query).Message);
}
