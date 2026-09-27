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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="NativeGroupByBinder"/>, which parses a LINQ <c>GroupBy(key).Select(aggregate)</c>
/// into the <see cref="MongoGrouping"/> IR on <see cref="MongoSelectDefinition"/>.
/// </summary>
public class NativeGroupByBinderTests
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Region { get; set; } = "";
        public int Amount { get; set; }
        public int Quantity { get; set; }
        public DateTime OrderDate { get; set; }
        public DateTime? ShippedDate { get; set; }
        public Guid ExternalId { get; set; }
    }

    private class OrderGroup
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
        public int Total { get; set; }
    }

    private class OrderKeyDto
    {
        public string Country { get; }
        public string Region { get; }
        public OrderKeyDto(string country, string region) { Country = country; Region = region; }
    }

    private class KeyOnlyDto
    {
        public string CustomerId { get; }
        public KeyOnlyDto(string customerId) { CustomerId = customerId; }
    }

    private class OrderWithRepresentedKey
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
    }

    private class ParamAndRegionKey
    {
        public int A { get; }
        public string Region { get; }
        public ParamAndRegionKey(int a, string region) { A = a; Region = region; }
    }

    // Note: deliberately does NOT use a member literally named "Id" — the driver's own
    // NamedIdMemberConvention (DefaultConventionPack) treats ANY class's "Id"/"id"/"_id"-named member as the
    // document's id member and forces its stored element name to "_id" regardless of the CLR member name,
    // which would make this fixture collide with Finding C1's own renamed-element guard for reasons unrelated
    // to what these tests are pinning.
    private class MemberInitKeyDto
    {
        public int Year { get; set; }
        public string Country { get; set; } = "";
    }

    // EF-322 SP7 fix-wave (Finding C1): a MemberInitExpression key DTO whose bound member's stored BSON
    // element name (via [BsonElement]) differs from its own CLR member name — the exact shape that crashes
    // g.Key's readback (FormatException: element "C" does not match any field or property) if TryBindGroupKey
    // ever admitted it, since $group._id would be written under the CLR name ("C") while the DTO's own driver
    // class-map deserializer expects the element name ("cc").
    private class RenamedElementKeyDto
    {
        [BsonElement("cc")]
        public string Country { get; set; } = "";
    }

    // EF-322 SP7 fix-wave round-2 (re-review coverage gap): same C1 shape as RenamedElementKeyDto, but the
    // renamed member is the SECOND bound member, not the first — proves the guard's class-map lookup checks
    // every binding, not just binding[0].
    private class TwoMemberSecondRenamedKeyDto
    {
        public string Country { get; set; } = "";

        [BsonElement("rr")]
        public string Region { get; set; } = "";
    }

    // A DTO combining a parameterized ctor with an object initializer — the shape
    // MemberInit_dto_key_with_parameterized_ctor_and_initializer_declines needs a SETTABLE second member,
    // since ParamAndRegionKey above has only get-only properties set by its ctor and is unusable with
    // Expression.Bind.
    private class ParamCtorWithSettableInitializerDto
    {
        public string Country { get; }
        public string Region { get; set; } = "";
        public ParamCtorWithSettableInitializerDto(string country) { Country = country; }
    }

    // EF-322 SP3 final-review fix regression: an entity whose own member happens to be literally named
    // "Key" — proving TryTranslateAccumulatorCondition's fallback declines a mixed element/g.Key predicate
    // rather than letting the ordinary (name-based, parameter-blind) translator silently resolve "Key"
    // against THIS entity instead of the grouping's g.Key.
    private class KeyNamedFieldEntity
    {
        public ObjectId Id { get; set; }
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
        public int Value { get; set; }
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Order>();
        var entityType = db.Model.FindEntityType(typeof(Order))!;
        return new MongoQueryExpression(entityType);
    }

    private static MongoQueryExpression TestQueryFor<T>() where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        var entityType = db.Model.FindEntityType(typeof(T))!;
        return new MongoQueryExpression(entityType);
    }

    // ── TryBindGroupKey ──────────────────────────────────────────────────────────

    [Fact]
    public void Scalar_key_binds_single_part_with_null_name()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => x.Country;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Single(parts);
        Assert.Null(parts[0].Name);
        var field = Assert.IsType<MongoFieldExpression>(parts[0].FieldRef);
        Assert.Equal("Country", field.ElementName);
        // Not finalized into Grouping until the projection is bound.
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Composite_key_binds_two_named_parts()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Country", parts[0].Name);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(parts[0].FieldRef).ElementName);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Computed_key_binds_via_TryTranslateValue()
    {
        // EF-322 SP1: a computed key part (a DateTime.Year extraction here) is no longer a hard decline —
        // TryBindGroupKey now falls through to the same TryTranslateValue every accumulator operand already
        // uses, which resolves this to a MongoDatePartExpression. There is no backing IProperty for a computed
        // key part, so HasDefaultKeySerialization's converter/BsonRepresentation check does not apply here —
        // same reasoning as the pre-existing literal-constant key case.
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> key = x => x.OrderDate.Year;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        Assert.IsType<MongoDatePartExpression>(part.FieldRef);
    }

    [Fact]
    public void Composite_key_with_computed_part_binds_via_TryTranslateValue()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Country", parts[0].Name);
        Assert.IsType<MongoFieldExpression>(parts[0].FieldRef);
        Assert.Equal("Yr", parts[1].Name);
        Assert.IsType<MongoDatePartExpression>(parts[1].FieldRef);
    }

    [Fact]
    public void Scalar_key_binds_even_with_pre_existing_source_side_ordering_and_paging()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(
            new MongoFieldExpression(property: null!, elementName: "Amount"), Ascending: true));
        mongoQ.Select.AppendSkip(new MongoConstantExpression(1, forSerialization: null));
        mongoQ.Select.AppendLimit(new MongoConstantExpression(10, forSerialization: null));

        Expression<Func<Order, string>> key = x => x.Country;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.NotNull(mongoQ.Select.PendingGroupKey);
        // The pre-existing sort/skip/limit ops are untouched — they stay in PipelineOps ahead of the eventual $group.
        Assert.Equal(3, mongoQ.Select.PipelineOps.Count);
    }

    [Fact]
    public void Empty_key_binds_zero_parts()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Assert.NotNull(mongoQ.Select.PendingGroupKey);
        Assert.Empty(mongoQ.Select.PendingGroupKey!);
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Constructor_call_key_with_two_arguments_does_not_bind_as_empty_key()
    {
        // NewExpression.Members is null for EVERY non-anonymous-type constructor call, not just new{} — this
        // must NOT be mistaken for a zero-part key, or a genuine 2-part key silently collapses to one group.
        var mongoQ = TestQuery();
        Expression<Func<Order, OrderKeyDto>> key = x => new OrderKeyDto(x.Country, x.Region);

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void EF_Property_scalar_key_binds()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => EF.Property<string>(x, nameof(Order.Country));

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        var field = Assert.IsType<MongoFieldExpression>(part.FieldRef);
        Assert.Equal("Country", field.ElementName);
    }

    [Fact]
    public void Captured_parameter_key_binds_as_parameter()
    {
        // A plain `x => a` over a compiler-captured local does NOT reach TryBindGroupKey in this shape: EF
        // Core's own ParameterExtractingExpressionVisitor rewrites a captured variable into an EF query
        // parameter node (a prefixed ParameterExpression on EF8/EF9, a QueryParameterExpression on EF10)
        // BEFORE the provider's translator ever sees it — a hand-built lambda skips that rewrite entirely.
        // Build the POST-extraction shape directly, mirroring MongoExpressionTranslatorTests'
        // Query_parameter_becomes_MongoParameterExpression_not_constant, which hand-builds the identical node
        // for the same reason.
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "a_0";
        Expression efParam = Expression.Parameter(typeof(int), paramName);
#else
        const string paramName = "__a_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int));
#endif
        var key = Expression.Lambda<Func<Order, int>>(efParam, xParam);

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        var part = Assert.Single(parts);
        Assert.Null(part.Name);
        var mongoParam = Assert.IsType<MongoParameterExpression>(part.FieldRef);
        Assert.Equal(paramName, mongoParam.Name);
    }

    [Fact]
    public void Composite_key_with_mixed_member_and_EF_Property_parts_binds()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { A = EF.Property<string>(x, nameof(Order.Country)), x.Region };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("A", parts[0].Name);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(parts[0].FieldRef).ElementName);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Composite_key_with_parameter_and_member_parts_binds()
    {
        // Same hand-built-shape reasoning as Captured_parameter_key_binds_as_parameter above: a genuine
        // anonymous-type composite key compiles to Expression.New(ctor, args, members) with EF's
        // post-extraction parameter node as one of the args — reproduced here with a named DTO instead of a
        // compiler-generated anonymous type (Expression.New doesn't care which; NewExpression.Members is
        // populated identically either way).
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
        var regionMember = Expression.Property(xParam, nameof(Order.Region));
#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "a_0";
        Expression efParam = Expression.Parameter(typeof(int), paramName);
#else
        const string paramName = "__a_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int));
#endif
        var ctor = typeof(ParamAndRegionKey).GetConstructor([typeof(int), typeof(string)])!;
        var members = new[]
        {
            typeof(ParamAndRegionKey).GetProperty(nameof(ParamAndRegionKey.A))!,
            typeof(ParamAndRegionKey).GetProperty(nameof(ParamAndRegionKey.Region))!
        };
        var newExpr = Expression.New(ctor, [efParam, regionMember], members);
        var key = Expression.Lambda<Func<Order, ParamAndRegionKey>>(newExpr, xParam);

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("A", parts[0].Name);
        Assert.IsType<MongoParameterExpression>(parts[0].FieldRef);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Non_default_serialized_property_key_still_declines()
    {
        // A GroupBy key over a property with a non-default BsonRepresentation has no safe generic _id
        // readback (same reasoning HasDefaultKeySerialization's own doc comment gives for the ordinary member
        // case) — TryTranslateValue's AllFieldsDefaultSerialized check must still catch this once the
        // dispatch broadens, not just the narrower TryTranslateField path it replaces.
        using var db = SingleEntityDbContext.Create<OrderWithRepresentedKey>(builder =>
            builder.Entity<OrderWithRepresentedKey>().Property(o => o.Country).HasBsonRepresentation(BsonType.String));
        var entityType = db.Model.FindEntityType(typeof(OrderWithRepresentedKey))!;
        var mongoQ = new MongoQueryExpression(entityType);
        Expression<Func<OrderWithRepresentedKey, string>> key = x => x.Country;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_binds_named_parts()
    {
        // EF-322 SP7: a MemberInitExpression DTO key (new NominalType { A = ..., B = ... }) is no longer a
        // hard decline — TryBindGroupKey now has its own MemberInitExpression case, binding each
        // MemberAssignment through TryBindKeyPartValue exactly like the anonymous-type NewExpression
        // composite-key case, so each part carries its own bound member's Name and readback happens the SAME
        // way a genuine named composite key already reads back (never as a single unnamed
        // MongoDocumentConstructionExpression scalar the way a MemberInitExpression VALUE operand would).
        var mongoQ = TestQuery();
        Expression<Func<Order, MemberInitKeyDto>> key = x => new MemberInitKeyDto { Year = x.OrderDate.Year, Country = x.Country };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Year", parts[0].Name);
        Assert.IsType<MongoDatePartExpression>(parts[0].FieldRef);
        Assert.Equal("Country", parts[1].Name);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void MemberInit_dto_key_with_parameterized_ctor_and_initializer_declines()
    {
        // Review Focus edge case: a MemberInitExpression whose base NewExpression takes a non-zero number of
        // constructor arguments (a DTO combining a parameterized ctor WITH an object initializer) has no
        // established key-part-naming convention in this codebase — TryBindGroupKey's own case guard requires
        // NewExpression.Arguments.Count == 0, so this must decline the WHOLE key rather than silently
        // dropping the ctor argument.
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
        var countryArg = Expression.Property(xParam, nameof(Order.Country));
        var ctor = typeof(ParamCtorWithSettableInitializerDto).GetConstructor([typeof(string)])!;
        var newExpr = Expression.New(ctor, countryArg);
        var regionMember = typeof(ParamCtorWithSettableInitializerDto).GetProperty(nameof(ParamCtorWithSettableInitializerDto.Region))!;
        var memberInit = Expression.MemberInit(newExpr, Expression.Bind(regionMember, Expression.Property(xParam, nameof(Order.Region))));
        var key = Expression.Lambda<Func<Order, ParamCtorWithSettableInitializerDto>>(memberInit, xParam);

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_with_nested_member_binding_declines()
    {
        // Review Focus edge case: a binding that is a MemberMemberBinding (a nested initializer, e.g.
        // `new Foo { Bar = { Baz = 1 } }`) rather than a plain MemberAssignment has no single translatable
        // VALUE — TryBindGroupKey's own loop must decline the WHOLE key, not just that one part.
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
        var ctor = typeof(MemberInitKeyDto).GetConstructor(Type.EmptyTypes)!;
        var newExpr = Expression.New(ctor);
        var yearMember = typeof(MemberInitKeyDto).GetProperty(nameof(MemberInitKeyDto.Year))!;
        // MemberBind only needs the outer member's own MemberInfo to construct a MemberMemberBinding — it
        // doesn't require the member's TYPE to itself expose any settable sub-members, since TryBindGroupKey
        // never inspects a binding's contents; it only checks whether the binding IS a MemberAssignment.
        var nestedBinding = Expression.MemberBind(yearMember, Array.Empty<MemberBinding>());
        var memberInit = Expression.MemberInit(newExpr, nestedBinding);
        var key = Expression.Lambda<Func<Order, MemberInitKeyDto>>(memberInit, xParam);

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_with_renamed_element_declines()
    {
        // EF-322 SP7 fix-wave (Finding C1): key parts are named after the bound CLR member, but g.Key is read
        // back through the DTO's OWN driver class-map serializer, which honors [BsonElement]/naming
        // conventions rather than the CLR member name — admitting this shape would write $group._id as
        // {Country: ...} while the DTO's class map expects the stored element "cc", crashing readback with a
        // FormatException. Must decline the whole key (fall back to driver-LINQ) instead.
        var mongoQ = TestQuery();
        Expression<Func<Order, RenamedElementKeyDto>> key = x => new RenamedElementKeyDto { Country = x.Country };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_with_second_member_renamed_declines()
    {
        // EF-322 SP7 fix-wave round-2 (re-review coverage gap): the renamed member is the SECOND binding, not
        // the first — pins that the class-map lookup loop checks every MemberAssignment, not just the first
        // one it encounters.
        var mongoQ = TestQuery();
        Expression<Func<Order, TwoMemberSecondRenamedKeyDto>> key =
            x => new TwoMemberSecondRenamedKeyDto { Country = x.Country, Region = x.Region };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Parameter_key_of_an_unmappable_CLR_type_declines_instead_of_throwing()
    {
        // Final-review fix: a captured-parameter key whose declared CLR type BsonValue.Create cannot map
        // (e.g. System.Guid) used to bind successfully (TryTranslateValue only checks translatability, not
        // renderability) and then throw ArgumentException at pipeline-BUILD time instead of declining
        // cleanly — the same "probe before admitting" gate NativeSlotPopulator.TryProbeBareValueRenders
        // already applies to a bare value/parameter SORT key must apply here too.
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "g_0";
        Expression efParam = Expression.Parameter(typeof(Guid), paramName);
#else
        const string paramName = "__g_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(Guid));
#endif
        var key = Expression.Lambda<Func<Order, Guid>>(efParam, xParam);

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Computed_key_part_flows_through_PriorGrouping_for_nested_GroupBy()
    {
        // Mirrors GroupBy_aggregate_followed_another_GroupBy_aggregate: the FIRST GroupBy's key has a computed
        // part; a second GroupBy nests on the first's flattened projection via PriorGrouping. This only
        // proves the binder-level plumbing accepts a computed first-level key alongside PriorGrouping — the
        // full nested-GroupBy shape is proven end-to-end by the spec suite (Task 2 of the SP1 plan).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> proj = g => new { g.Key };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        mongoQ.Select.SnapshotPriorGroupingForNestedGroupBy();
        Assert.NotNull(mongoQ.Select.PriorGrouping);

        Expression<Func<Order, string>> secondKey = x => x.Country;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, secondKey));
        Assert.NotNull(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void HAVING_on_first_GroupBy_survives_nesting_a_second_GroupBy()
    {
        // Final-review fix: SnapshotPriorGroupingForNestedGroupBy moved Grouping/Projection aside into
        // PriorGrouping/PriorGroupingProjection but NOT GroupHavingPredicate — the outer GroupBy's own
        // TryBindGroupProjection then unconditionally overwrote GroupHavingPredicate with its own (here,
        // absent) HAVING, silently dropping the FIRST GroupBy's filter entirely (every group returned,
        // not just the ones that passed HAVING). Mirrors the reviewer's repro:
        // GroupBy(Country).Where(g => g.Count() > 1).Select(g => new{g.Key, C=g.Count()})
        //   .GroupBy(x => x.C).Select(g => new{g.Key, N=g.Count()})
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> firstHaving = g => g.Count() > 1;
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, firstHaving));

        Expression<Func<IGrouping<string, Order>, object>> firstProj =
            g => new { g.Key, C = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, firstProj, out _));
        Assert.NotNull(mongoQ.Select.GroupHavingPredicate);

        mongoQ.Select.SnapshotPriorGroupingForNestedGroupBy();

        // The snapshot must have carried the first HAVING into PriorGroupHavingPredicate...
        Assert.NotNull(mongoQ.Select.PriorGroupHavingPredicate);
        // ...and cleared the live slot, so the OUTER GroupBy (which has no HAVING of its own here) doesn't
        // inherit a stale value.
        Assert.Null(mongoQ.Select.GroupHavingPredicate);

        Expression<Func<object, int>> secondKey = x => 0; // outer key shape is irrelevant to this regression
        // (bind directly against PendingGroupKey to avoid needing a real "C" member on an anonymous type)
        mongoQ.Select.PendingGroupKey = [new MongoGroupingKeyPart(null, new MongoConstantExpression(0, forSerialization: null))];

        Expression<Func<IGrouping<int, object>, object>> secondProj = g => new { N = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, secondProj, out _));

        // The outer GroupBy has no HAVING of its own — GroupHavingPredicate stays null...
        Assert.Null(mongoQ.Select.GroupHavingPredicate);
        // ...but the FIRST GroupBy's HAVING must still be there, ready for MongoSelectLowerer to emit
        // alongside PriorGrouping's own $group.
        Assert.NotNull(mongoQ.Select.PriorGroupHavingPredicate);
    }

    [Fact]
    public void Empty_key_with_bare_aggregate_binds_group()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> proj =
            g => g.Sum(o => o.Amount);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareLeafAlias));

        Assert.Empty(mongoQ.Select.Grouping!.Key);
        var acc = Assert.Single(mongoQ.Select.Grouping.Accumulators);
        Assert.Equal("$sum", acc.Operator);
        Assert.NotNull(bareLeafAlias);
    }

    [Fact]
    public void Empty_key_with_bare_g_Key_readback_goes_native()
    {
        // EF-322 SP7 (Task 2): g.Key over a zero-part key now flattens to "_id" — the group's own empty
        // document is itself the correct readback for an empty anonymous-type key, so this no longer needs to
        // decline. Superseded name/premise of this test's previous form (Empty_key_with_bare_g_Key_readback_declines).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> proj =
            g => new { g.Key, Sum = g.Sum(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.NotNull(mongoQ.Select.Grouping);
        Assert.Empty(mongoQ.Select.Grouping!.Key);
        var keyProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Key");
        var keyRef = Assert.IsType<MongoElementRefExpression>(keyProjection.Expression);
        Assert.Equal("_id", keyRef.Path);
    }

    // ── TryBindGroupProjection ─────────────────────────────────────────────────────

    private static MongoQueryExpression BoundScalarKeyQuery()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => x.Country;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        return mongoQ;
    }

    [Fact]
    public void Scalar_key_and_count_binds_group()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var grouping = mongoQ.Select.Grouping!;
        Assert.Single(grouping.Key);
        Assert.Null(grouping.Key[0].Name);
        Assert.Collection(grouping.Accumulators,
            a =>
            {
                Assert.Equal("Count", a.OutputField);
                Assert.Equal("$sum", a.Operator);
                Assert.Null(a.Operand);
            });
    }

    [Fact]
    public void HAVING_where_then_select_binds_grouping_with_having_predicate()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Count() > 4;
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // TWO accumulators: the HAVING's own ("__agg0") and the Select's own ("Count") — a harmless
        // redundant $group field, same precedent as this file's existing orderAccumulators handling.
        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Equal("__agg0", mongoQ.Select.Grouping.Accumulators[0].OutputField);
        Assert.Equal("Count", mongoQ.Select.Grouping.Accumulators[1].OutputField);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.GroupHavingPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("__agg0", left.Path);
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }

    [Fact]
    public void HAVING_key_comparison_then_select_binds_grouping_with_no_extra_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Key == "ALFKI";
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, c = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Only the Select's OWN accumulator ("c") — the HAVING key comparison needed none of its own.
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("c", acc.OutputField);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.GroupHavingPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("_id", left.Path);
    }

    [Fact]
    public void Compound_HAVING_where_predicate_still_declines()
    {
        // Final-review coverage gap: TryBindGroupPredicateComparison only ever recognizes a SINGLE binary
        // comparison (its own pattern match requires Unwrap(body) to BE a BinaryExpression with one of the
        // six comparison NodeTypes) — an AndAlso/OrElse compound HAVING predicate doesn't match that pattern
        // at all and must still decline (fall back), not silently apply only half the filter. Mirrors
        // TryBindGroupTerminalAggregate's own existing Compound_predicate_returns_false, but for the
        // Where-then-Select HAVING path specifically (a materially different code path — TryBindGroupWherePredicate
        // / TryBindGroupProjection's own PendingGroupPredicate consumption — not previously covered here).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Count() > 4 && g.Count() < 10;

        Assert.False(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }

    [Fact]
    public void Sum_with_member_selector_binds_sum_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.Equal("Amount", Assert.IsType<MongoFieldExpression>(acc.Operand).ElementName);
    }

    [Fact]
    public void Distinct_then_Select_Distinct_Max_treats_leading_Distinct_hop_as_a_no_op()
    {
        // GroupBy_group_Distinct_Select_Distinct_aggregate's exact shape: g.Distinct() immediately before
        // .Select(selector).Distinct() is a provable no-op for ANY subsequent reduction (deduping whole rows
        // first can only ever match or exceed the final distinct-mapped set's size, never change it) — must
        // bind with NO condition at all, not a trivially-true one.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Max = g.Distinct().Select(e => e.OrderDate).Distinct().Max() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$addToSet", acc.Operator);
        // No $cond wrapper — the operand is the bare field reference, unconditional.
        Assert.IsType<MongoFieldExpression>(acc.Operand);
    }

    [Fact]
    public void Where_then_Select_Distinct_Max_binds_conditional_addToSet_with_REMOVE_else()
    {
        // GroupBy_group_Where_Select_Distinct_aggregate's exact shape: a genuine per-element filter before
        // Select(...).Distinct().Max() — this one DOES need a $cond, unlike the Distinct-hop case above.
        // Selects ShippedDate (nullable — matching the real Northwind model's own OrderDate: DateTime?, this
        // test fixture's own OrderDate is non-nullable for simplicity elsewhere in this file) so Max()'s
        // result type is nullable: a non-nullable result here would trip the final-review empty-filtered-
        // group guard (a filter CAN legitimately empty out a group's contribution, and a non-nullable
        // Min/Max/Avg has no way to represent that safely) for a shape this specific unit test isn't trying
        // to exercise.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Max = g.Where(e => e.Amount > 0).Select(e => e.ShippedDate).Distinct().Max() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$addToSet", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.Equal("ShippedDate", Assert.IsType<MongoFieldExpression>(cond.IfTrue).ElementName);
        var elseRef = Assert.IsType<MongoElementRefExpression>(cond.IfFalse);
        Assert.Equal(MongoElementRefExpression.RemoveSentinelPath, elseRef.Path);
    }

    [Fact]
    public void Count_with_predicate_binds_conditional_sum()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { TenK = g.Count(e => e.Amount < 100) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("TenK", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);
    }

    [Fact]
    public void Two_counts_with_different_predicates_bind_two_independent_accumulators()
    {
        // GroupBy_multiple_Count_with_predicate's exact shape: TWO differently-thresholded Count(pred) calls
        // in the SAME Select must bind to DIFFERENT output fields with DIFFERENT conditions, not collapse
        // into one.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { TenK = g.Count(e => e.Amount < 100), EleventK = g.Count(e => e.Amount < 200) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        var first = mongoQ.Select.Grouping.Accumulators[0];
        var second = mongoQ.Select.Grouping.Accumulators[1];
        Assert.Equal("TenK", first.OutputField);
        Assert.Equal("EleventK", second.OutputField);
        var firstBound = Assert.IsType<MongoBinaryExpression>(Assert.IsType<MongoConditionalExpression>(first.Operand).Test);
        var secondBound = Assert.IsType<MongoBinaryExpression>(Assert.IsType<MongoConditionalExpression>(second.Operand).Test);
        Assert.Equal(100, Assert.IsType<MongoConstantExpression>(firstBound.Right).Value);
        Assert.Equal(200, Assert.IsType<MongoConstantExpression>(secondBound.Right).Value);
    }

    [Fact]
    public void Where_then_Sum_binds_conditional_operand_with_REMOVE_else()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Where(e => e.Amount > 0).Sum(e => e.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        Assert.Equal("Amount", Assert.IsType<MongoFieldExpression>(cond.IfTrue).ElementName);
        var elseRef = Assert.IsType<MongoElementRefExpression>(cond.IfFalse);
        Assert.Equal(MongoElementRefExpression.RemoveSentinelPath, elseRef.Path);
    }

    [Fact]
    public void Where_on_group_key_then_Min_resolves_key_via_its_own_raw_expression_not_id()
    {
        // GroupBy_constant_with_where_on_grouping_with_aggregate_operators's exact shape: the predicate
        // references g.Key, not the per-element parameter at all. Must resolve to the key's OWN stored
        // FieldRef (here a literal MongoConstantExpression, since the key is GroupBy(o => 1)) — NEVER
        // MongoElementRefExpression("_id"), which does not exist yet inside this accumulator's own $group
        // stage (referencing "_id" here would be a circular reference to that stage's own output).
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> constantKey = x => 1;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, constantKey));

        Expression<Func<IGrouping<int, Order>, object>> proj =
            g => new { Min = g.Where(i => 1 == g.Key).Min(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$min", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        // The key side resolves to the key's own raw expression (a literal 1), not "_id".
        Assert.IsType<MongoConstantExpression>(test.Left);
        Assert.IsNotType<MongoElementRefExpression>(test.Left);
    }

    [Fact]
    public void Where_predicate_mixing_element_and_key_reference_declines()
    {
        // Deliberately out of scope: TryTranslateAccumulatorCondition only recognizes a predicate that is
        // EITHER purely a g.Key comparison OR purely an ordinary per-element expression, never both combined
        // in one condition. Must decline cleanly (fall back), not crash or silently drop half the condition.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Where(e => e.Amount > 5 && g.Key == "ALFKI").Sum(e => e.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Where_then_Count_binds_conditional_sum_accumulator()
    {
        // LongCount_after_GroupBy_aggregate's inner Select shape (as a NAMED member here; the bare-body
        // variant, matching the real test exactly, is proven by the next test).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount < 250).Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Filtered", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        // Unlike the Sum/Min/Max filtered arm's $$REMOVE else-branch, a filtered COUNT sums 0/1 either way —
        // an unmatched element must contribute 0, not $$REMOVE (which would be wrong for $sum: it silently
        // drops the summand, but 0 is exactly what an unmatched element should contribute here anyway; this
        // assertion pins that the constant 0 is actually emitted, not the $$REMOVE sentinel some OTHER
        // accumulator kind uses).
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);
    }

    [Fact]
    public void Bare_body_Where_then_Count_binds_under_synthetic_alias()
    {
        // LongCount_after_GroupBy_aggregate's EXACT shape: a bare (non-`new{}`) Select body.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> proj =
            g => g.Where(e => e.Amount < 250).Count();

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        Assert.Equal(NativeProjectionBinder.SyntheticBareProjectionAlias, bareAlias);
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal(bareAlias, acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
    }

    [Fact]
    public void Where_then_LongCount_binds_conditional_sum_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount < 250).LongCount() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$sum", acc.Operator);
        Assert.IsType<MongoConditionalExpression>(acc.Operand);
    }

    [Fact]
    public void Where_then_Count_mixing_element_and_key_reference_declines()
    {
        // Review Focus: the SAME mixed-reference guard the Sum/Min/Max filtered arm already relies on must
        // also gate the new Count/LongCount arm — proven separately, since it is easy to wire the new arm
        // past TryTranslateAccumulatorCondition's guard by accident (e.g. by not routing through it at all).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount > 5 && g.Key == "ALFKI").Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Mixed_element_and_key_predicate_declines_even_when_entity_has_a_Key_named_member()
    {
        // Final-review fix: TryTranslateAccumulatorCondition's fallback used to delegate a predicate that
        // mixes a per-element condition with a g.Key reference straight to the ordinary translator, which
        // resolves members by NAME against the entity type regardless of which parameter they hang off. When
        // the entity itself happens to have a member literally named "Key", `e.Value > 0 && g.Key == "A"`
        // would silently resolve g.Key as if it were e.Key — wrong data, not the intended decline. Proven
        // here with an entity that actually has a "Key" member (Order does not, so this shape used to
        // decline "successfully" for the wrong reason).
        var mongoQ = TestQueryFor<KeyNamedFieldEntity>();
        Expression<Func<KeyNamedFieldEntity, string>> keySelector = x => x.Category;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, keySelector));

        Expression<Func<IGrouping<string, KeyNamedFieldEntity>, object>> proj =
            g => new { Total = g.Count(e => e.Value > 0 && g.Key == "A") };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Count_with_predicate_key_comparison_constant_serializes_through_the_keys_own_property()
    {
        // Final-review fix: the accumulator-condition key-access arm used to translate its comparison
        // constant with forSerialization: null (mirrors the exact bug SP2 already fixed once for HAVING —
        // see HAVING_key_comparison_constant_serializes_through_the_keys_own_property) — fine for a
        // string/int key, but throws ArgumentException at render time for a type like Guid that
        // BsonValue.Create cannot map directly.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built rather than `g => g.Key == guidValue` over a captured local: the C# compiler lowers a
        // captured Guid to a closure-field MemberExpression, not a ConstantExpression — same lesson as SP1's
        // parameter-key tests and SP2's own Guid HAVING test.
        var groupParam = Expression.Parameter(typeof(IGrouping<Guid, Order>), "g");
        var elementParam = Expression.Parameter(typeof(Order), "o");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<Guid, Order>.Key));
        var guidConstant = Expression.Constant(Guid.NewGuid(), typeof(Guid));
        var countPred = Expression.Lambda<Func<Order, bool>>(Expression.Equal(keyAccess, guidConstant), elementParam);
        var countMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Order));
        var countCall = Expression.Call(countMethod, groupParam, countPred);
        var proj = Expression.Lambda<Func<IGrouping<Guid, Order>, int>>(countCall, groupParam);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var constant = Assert.IsType<MongoConstantExpression>(test.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("ExternalId", constant.ForSerialization!.Name);

        // Rendering must not throw (ArgumentException: ".NET type System.Guid cannot be mapped to a
        // BsonValue") — the actual observable bug when ForSerialization was null.
        var rendered = MongoAggregationExpressionRenderer.Render(cond, new PlaceholderTable());
        Assert.NotNull(rendered);
    }

    [Fact]
    public void Ternary_over_Guid_key_comparison_constant_serializes_through_the_keys_own_property()
    {
        // SP4 final-review fix: TryTranslateGroupProjectionConditionOrValue's key-vs-constant comparison arms
        // used to translate the constant with forSerialization: null (the exact bug class SP3's final review
        // already fixed once for TryTranslateAccumulatorCondition — see
        // Count_with_predicate_key_comparison_constant_serializes_through_the_keys_own_property above) — fine
        // for a string/int key, but throws ArgumentException at render time for a type like Guid that
        // BsonValue.Create cannot map directly.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built rather than `g => g.Key == guidValue ? "A" : "other"` over a captured local: the C#
        // compiler lowers a captured Guid to a closure-field MemberExpression, not a ConstantExpression — same
        // lesson as the Count-predicate Guid test above.
        var groupParam = Expression.Parameter(typeof(IGrouping<Guid, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<Guid, Order>.Key));
        var guidConstant = Expression.Constant(Guid.NewGuid(), typeof(Guid));
        var ternary = Expression.Condition(
            Expression.Equal(keyAccess, guidConstant), Expression.Constant("A"), Expression.Constant("other"));
        var proj = Expression.Lambda<Func<IGrouping<Guid, Order>, object>>(ternary, groupParam);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        var projection = mongoQ.Select.Projection.Single(p => p.Alias == bareAlias);
        var cond = Assert.IsType<MongoConditionalExpression>(projection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var constant = Assert.IsType<MongoConstantExpression>(test.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("ExternalId", constant.ForSerialization!.Name);

        // Rendering must not throw (ArgumentException: ".NET type System.Guid cannot be mapped to a
        // BsonValue") — the actual observable bug when ForSerialization was null.
        var rendered2 = MongoAggregationExpressionRenderer.Render(cond, new PlaceholderTable());
        Assert.NotNull(rendered2);
    }

    [Fact]
    public void Where_then_Min_over_non_nullable_result_declines_to_avoid_silent_default_on_empty_group()
    {
        // Final-review fix: if every element in a group fails the filter, $$REMOVE leaves the $min/$max/$avg
        // accumulator null, and the native shaper would read that as default(T) for a non-nullable result —
        // silently wrong (both LINQ-to-objects and the driver-LINQ fallback throw InvalidOperationException
        // for an empty sequence instead). Must decline so the query falls back rather than risk this.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Min = g.Where(e => e.Amount > 999999).Min(e => e.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Where_then_Select_Distinct_Max_over_non_nullable_result_declines_to_avoid_silent_default_on_empty_group()
    {
        // Same risk as the direct-accumulator case above, but via the $addToSet-then-external-reduce path
        // TryBindDistinctAccumulator's Where-hop uses.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Max = g.Where(e => e.Amount > 999999).Select(e => e.Amount).Distinct().Max() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Sum_with_computed_selector_binds_computed_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Amount * 2) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.IsType<MongoBinaryExpression>(acc.Operand);
    }

    [Fact]
    public void Ternary_over_nullable_key_comparison_binds_conditional_projection()
    {
        // GroupBy_aggregate_projecting_conditional_expression_based_on_group_key's exact shape:
        // .GroupBy(o => o.OrderDate).Select(g => new { Key = g.Key == null ? "is null" : "is not null", ... }).
        // Uses ShippedDate here (nullable, like the real Northwind OrderDate: DateTime?) so the comparison is
        // meaningful.
        var mongoQ = TestQuery();
        Expression<Func<Order, DateTime?>> key = x => x.ShippedDate;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<DateTime?, Order>, object>> proj =
            g => new { Label = g.Key == null ? "is null" : "is not null", Sum = g.Sum(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Two flatten projections: the computed Label and the ordinary Sum accumulator. Flatten output
        // aliases live on MongoSelectDefinition's own Projection list (set via AddProjection), not on
        // MongoGrouping itself — see this file's other tests' own `mongoQ.Select.Projection` usage.
        Assert.Equal(2, mongoQ.Select.Projection.Count);
        var labelProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Label");
        var cond = Assert.IsType<MongoConditionalExpression>(labelProjection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Null(Assert.IsType<MongoConstantExpression>(test.Right).Value);
        Assert.Equal("is null", Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal("is not null", Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);

        // The Sum accumulator itself still binds normally — this plan doesn't touch that path.
        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Ternary_leaf_referencing_an_accumulator_declines()
    {
        // Review Focus: neither target test needs an accumulator inside a computed projection leaf — must
        // decline cleanly, not silently bind wrong (ReferencesParameter's existing guard catches g.Count()
        // the same way it already catches a mixed g.Key reference).
        var mongoQ = TestQuery();
        Expression<Func<Order, DateTime?>> key = x => x.ShippedDate;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<DateTime?, Order>, object>> proj =
            g => new { Label = g.Key == null ? g.Count() : 0 };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Ternary_test_mixing_element_and_key_reference_declines()
    {
        // Review Focus: a per-element reference in the ternary's TEST (not the key) must decline via the
        // SAME ReferencesParameter guard SP3 established, not attempt a mixed resolution.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Label = g.Where(o => o.Amount > 0).Any() ? g.Key : "none" };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Ternary_over_composite_key_null_comparison_admits_whole_key_read()
    {
        // Review Focus: TryGetKeyMemberPath's allowWholeKeyRead:true (already-tested, pre-existing behavior)
        // resolves even a composite key's bare g.Key to "_id" wholesale. Comparing a composite sub-document to
        // a scalar null has no realistic query meaning, but this plan doesn't special-case a decline nobody
        // asked for — it stays structurally admitted, matching the flatten loop's own existing default. Built
        // by hand (not a typed `g => g.Key == null ? ... : ...` lambda) because the composite key's CLR type
        // is a compiler-generated anonymous type with no literal spelling available here; uses the bare-body
        // path (no `new {}` wrapper) the SAME way `TryBindGroupProjection`'s own `isBareBody`/
        // `SyntheticBareProjectionAlias` handling already supports elsewhere in this file.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var groupParam = Expression.Parameter(typeof(IGrouping<object, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<object, Order>.Key));
        var nullConstant = Expression.Constant(null, typeof(object));
        var ternary = Expression.Condition(
            Expression.Equal(keyAccess, nullConstant), Expression.Constant("no key"), Expression.Constant("has key"));
        var proj = Expression.Lambda<Func<IGrouping<object, Order>, object>>(ternary, groupParam);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        var projection = mongoQ.Select.Projection.Single(p => p.Alias == bareAlias);
        var cond = Assert.IsType<MongoConditionalExpression>(projection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
    }

    [Fact]
    public void Ternary_over_empty_key_null_comparison_binds_without_crashing()
    {
        // Fix-round addition (review gap): pins ResolveKeyMemberSerializationProperty's own
        // `keyParts.Count == 0` guard, which had no test reaching it with an empty keyParts list.
        // TryTranslateGroupProjectionConditionOrValue (the Select-projection ternary/coalesce CONDITION
        // path, reached from a computed leaf in the flatten $project) is a DIFFERENT call site than
        // TryBindGroupSideOperand's Where-clause path already covered by
        // Where_key_comparison_over_empty_key_binds_direct_comparison_with_no_accumulator. For a zero-part
        // key, TryGetKeyMemberPath resolves g.Key == null's left side to a non-null "_id" path (isComposite
        // is false for a zero-part key, and allowWholeKeyRead defaults to true), so
        // ResolveKeyMemberSerializationProperty("_id", keyParts: [], isComposite: false) is invoked to
        // serialize the null constant. Before this task's fix that method read
        // `isComposite ? null : keyParts[0]...` — with isComposite false here, it would index keyParts[0] on
        // an empty list and throw IndexOutOfRangeException. Built by hand (not a typed lambda) for the same
        // reason as Ternary_over_composite_key_null_comparison_admits_whole_key_read: the empty anonymous-
        // type key has no literal C# spelling here.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var groupParam = Expression.Parameter(typeof(IGrouping<object, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<object, Order>.Key));
        var nullConstant = Expression.Constant(null, typeof(object));
        var ternary = Expression.Condition(
            Expression.Equal(keyAccess, nullConstant), Expression.Constant("no key"), Expression.Constant("has key"));
        var proj = Expression.Lambda<Func<IGrouping<object, Order>, object>>(ternary, groupParam);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        var projection = mongoQ.Select.Projection.Single(p => p.Alias == bareAlias);
        var cond = Assert.IsType<MongoConditionalExpression>(projection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Null(Assert.IsType<MongoConstantExpression>(test.Right).Value);
    }

    [Fact]
    public void Ternary_over_empty_key_non_null_comparison_declines_whole_projection()
    {
        // EF-322 SP7 fix-wave (Finding I1): the OTHER comparison call site (this one runs in the
        // Select-projection ternary CONDITION path, TryTranslateGroupProjectionConditionOrValue, vs. the
        // Where/HAVING path covered by Where_key_comparison_over_empty_key_against_non_null_value_declines).
        // A non-null comparison in a g.Key == x ? a : b shape over a zero-part key must decline the WHOLE
        // projection (TryBindGroupProjection returns false) — not admit the comparison and defer the crash to
        // render time, and not silently mis-fall-through to treating `g.Key == x` as an ordinary per-element
        // value (which ReferencesParameter's own guard would ALSO reject, but for the wrong reason).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var groupParam = Expression.Parameter(typeof(IGrouping<object, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<object, Order>.Key));
        var nonNullConstant = Expression.Constant(new object(), typeof(object));
        var ternary = Expression.Condition(
            Expression.Equal(keyAccess, nonNullConstant), Expression.Constant("has key"), Expression.Constant("no key"));
        var proj = Expression.Lambda<Func<IGrouping<object, Order>, object>>(ternary, groupParam);

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Coalesce_over_key_binds_coalesce_projection()
    {
        // GroupBy_orderby_projection_with_coalesce_operation's exact shape:
        // .GroupBy(c => c.City).Select(x => new { Locality = x.Key ?? "Unknown", Count = x.Count() }).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Locality = g.Key ?? "Unknown", Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var localityProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Locality");
        var coalesce = Assert.IsType<MongoCoalesceExpression>(localityProjection.Expression);
        var keyRef = Assert.IsType<MongoElementRefExpression>(coalesce.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Equal("Unknown", Assert.IsType<MongoConstantExpression>(coalesce.Right).Value);

        // The Count accumulator itself still binds normally.
        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Chained_coalesce_over_key_binds_right_nested_coalesce()
    {
        // Review Focus: a ?? b ?? c is right-associative (a ?? (b ?? c)) — the recursive function must walk
        // into the RIGHT operand when it is itself a Coalesce, not just handle two flat operands.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Locality = g.Key ?? "Fallback1" ?? "Fallback2" };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var localityProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Locality");
        var outer = Assert.IsType<MongoCoalesceExpression>(localityProjection.Expression);
        Assert.IsType<MongoElementRefExpression>(outer.Left);
        var inner = Assert.IsType<MongoCoalesceExpression>(outer.Right);
        Assert.Equal("Fallback1", Assert.IsType<MongoConstantExpression>(inner.Left).Value);
        Assert.Equal("Fallback2", Assert.IsType<MongoConstantExpression>(inner.Right).Value);
    }

    [Fact]
    public void Sum_with_constant_selector_binds_constant_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => 1) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(acc.Operand).Value);
    }

    [Fact]
    public void Sum_with_cast_selector_binds_cast_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => (long)x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.IsType<MongoFieldExpression>(acc.Operand);
    }

    [Fact]
    public void Sum_with_string_length_selector_binds_length_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Region.Length) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var length = Assert.IsType<MongoStringLengthExpression>(acc.Operand);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(length.Operand).ElementName);
    }

    [Fact]
    public void Min_max_average_map_to_operators()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Lo = g.Min(x => x.Amount), Hi = g.Max(x => x.Amount), Av = g.Average(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var accs = mongoQ.Select.Grouping!.Accumulators;
        Assert.Equal("$min", accs.Single(a => a.OutputField == "Lo").Operator);
        Assert.Equal("$max", accs.Single(a => a.OutputField == "Hi").Operator);
        Assert.Equal("$avg", accs.Single(a => a.OutputField == "Av").Operator);
    }

    [Fact]
    public void Key_access_is_not_an_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Only the Count accumulator; the key member is not an accumulator.
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Count", acc.OutputField);
    }

    [Fact]
    public void MemberInit_dto_projection_binds()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, OrderGroup>> proj =
            g => new OrderGroup { Key = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var accs = mongoQ.Select.Grouping!.Accumulators;
        Assert.Equal(2, accs.Count);
        Assert.Contains(accs, a => a.OutputField == "Count" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(accs, a => a.OutputField == "Total" && a.Operator == "$sum" && a.Operand != null);
    }

    [Fact]
    public void Computed_operand_with_two_fields_binds_computed_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Amount * x.Quantity) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.IsType<MongoBinaryExpression>(acc.Operand);
    }

    [Fact]
    public void Bare_projection_body_with_no_accumulator_returns_false()
    {
        // A bare (unwrapped) g.Key over a COMPOSITE key declines at a different site than the scalar-key case
        // (Bare_g_Key_with_no_aggregate_still_declines, which covers a SCALAR key) — TryGetKeyMemberPath's
        // "keyPath == null" branch for a composite key that can't flatten to one field, not the guard this plan
        // touches.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> proj = g => g.Key;

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Ctor_dto_composite_key_sub_member_with_no_aggregate_binds_group()
    {
        // A composite-key sub-member projected back through a ctor-wrapped DTO (g.Key.Country, not the whole
        // g.Key) with no aggregate — resolves via TryGetKeyMemberPath's composite-sub-member branch, has zero
        // accumulators, and is not bare (ctor-wrapped), so it now reaches the admitted branch. One row per
        // distinct (Country, Region) pair, matching LINQ's own GroupBy semantics.
        var mongoQ = TestQuery();

        Assert.True(BindCompositeKeyAndProjection(mongoQ,
            x => new { x.Country, x.Region },
            g => new KeyOnlyDto(g.Key.Country),
            out _));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_id.Country", Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    // Binds a composite GroupBy key and its result projection with the SAME compiler-synthesized anonymous
    // TKey shared across both lambdas via generic type inference — the only way to write `g.Key.<Sub>`
    // against a composite key from a separately-declared projection lambda (IGrouping<TKey, TElement>'s TKey
    // can't otherwise be named from outside the key-selector expression that created it).
    private static bool BindCompositeKeyAndProjection<TKey>(
        MongoQueryExpression mongoQ,
        Expression<Func<Order, TKey>> keySelector,
        Expression<Func<IGrouping<TKey, Order>, object>> resultSelector,
        out string? bareLeafAlias)
    {
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, keySelector));
        return NativeGroupByBinder.TryBindGroupProjection(mongoQ, resultSelector, out bareLeafAlias);
    }

    [Fact]
    public void Ctor_dto_key_only_with_no_aggregate_binds_group()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new KeyOnlyDto(g.Key);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_id", Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    [Fact]
    public void Anonymous_type_key_only_with_no_aggregate_binds_group()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Bare_g_Key_with_no_aggregate_still_declines()
    {
        // A bare (unwrapped) g.Key projection is a plain-Distinct-equivalent shape this plan does not attempt —
        // must keep declining, unchanged.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj = g => g.Key;

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Wrapped_key_only_combined_with_pending_ordering_aggregate_still_declines()
    {
        // A zero-Select-accumulator projection combined with a pending-ordering aggregate is an untested,
        // explicitly out-of-scope combination — must keep declining, unchanged.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj = g => new KeyOnlyDto(g.Key);

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Aggregate_over_non_grouping_source_returns_false()
    {
        // The accumulator's SOURCE is a DIFFERENT sequence (an in-scope array), not the grouping parameter g.
        // Binding it to a $group accumulator would silently drop the real computation and return the group's
        // row count instead, diverging from driver-LINQ. It must NOT bind → the projection falls back.
        var mongoQ = BoundScalarKeyQuery();
        var others = new[] { 1, 2, 3, 4, 5, 6 };
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = others.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Sum_over_non_grouping_source_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        var others = new[] { new Order { Amount = 1 } };
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = others.Sum(x => x.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Accumulator_output_field_named_id_returns_false()
    {
        // An accumulator whose result member is literally "_id" would emit a second "_id" element into the
        // $group document (which already carries the grouping key under "_id"), throwing a BsonDocument
        // duplicate-key exception at pipeline build rather than falling back cleanly. Reject it here so the
        // shape falls back to driver-LINQ (and throws only under NativeOnly).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { _id = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Key_member_projected_to_id_alias_still_binds()
    {
        // A KEY member projected to an "_id" alias reads the group's own "_id" back and does NOT collide with
        // the reserved field, so it must remain natively representable — the guard is scoped to accumulators.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { _id = g.Key, Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Count", acc.OutputField);
    }

    [Fact]
    public void Projection_without_bound_key_returns_false()
    {
        var mongoQ = TestQuery(); // TryBindGroupKey never called
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    // ── TryBindGroupProjection: pending OrderBy/ThenBy (composed before the Select) ─────────────

    [Fact]
    public void Pending_order_by_key_resolves_to_id_reference()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, string>> orderKey = g => g.Key;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var order = Assert.Single(mongoQ.Select.GroupOrderOp!.Orderings);
        Assert.True(order.Ascending);
        Assert.Equal("_id", Assert.IsType<MongoElementRefExpression>(order.KeySelector).Path);
        Assert.Null(mongoQ.Select.PendingGroupOrderings);
    }

    [Fact]
    public void Pending_order_by_aggregate_adds_its_own_accumulator_alongside_the_projected_one()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Two SEPARATE $sum:1 accumulators — one for the ordering, one for the projection. Deliberately not
        // de-duplicated (see this task's Architecture note).
        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "Count" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "_orderAgg0" && a.Operator == "$sum" && a.Operand == null);

        var order = Assert.Single(mongoQ.Select.GroupOrderOp!.Orderings);
        Assert.Equal("_orderAgg0", Assert.IsType<MongoElementRefExpression>(order.KeySelector).Path);
    }

    [Fact]
    public void Pending_order_by_aggregate_not_projected_still_adds_accumulator_but_not_flattened()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        // Projects Sum, not Count — but the ordering still needs Count computed in the SAME $group.
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Sum(x => x.Amount) };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "_orderAgg0" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "Total" && a.Operator == "$sum");

        // The flatten projection (what the final $project keeps) only has the user-requested members.
        Assert.DoesNotContain(mongoQ.Select.Projection, p => p.Alias == "_orderAgg0");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "Total");
    }

    [Fact]
    public void Pending_order_by_key_then_by_aggregate_resolves_both_in_order()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, string>> byKey = g => g.Key;
        Expression<Func<IGrouping<string, Order>, int>> byCount = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, byKey), (false, byCount)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Collection(mongoQ.Select.GroupOrderOp!.Orderings,
            o =>
            {
                Assert.True(o.Ascending);
                Assert.Equal("_id", Assert.IsType<MongoElementRefExpression>(o.KeySelector).Path);
            },
            o =>
            {
                Assert.False(o.Ascending);
                Assert.Equal("_orderAgg0", Assert.IsType<MongoElementRefExpression>(o.KeySelector).Path);
            });
    }

    [Fact]
    public void Pending_order_by_computed_expression_declines()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count() * 2;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Pending_order_by_bare_key_over_composite_key_declines()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> orderKey = g => g.Key;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<object, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Pending_order_by_g_Key_over_empty_key_goes_native()
    {
        // EF-322 SP7 (Task 2): OrderBy(g => g.Key) composed BEFORE an empty-key GroupBy reaches
        // TryGetKeyMemberPath through the PENDING-ORDERING call site (allowWholeKeyRead: false) — a zero-part
        // key is never "isComposite", so this call site's own composite-only decline no longer applies to it
        // either; it now resolves to "_id" and sorts on the group's own empty document, exactly like the
        // terminal-Select path. Superseded name/premise of this test's previous form
        // (Pending_order_by_g_Key_over_empty_key_declines).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> orderKey = g => g.Key;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<object, Order>, object>> proj = g => g.Sum(o => o.Amount);

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.NotNull(mongoQ.Select.Grouping);
        var sortOp = Assert.IsType<MongoSortOp>(mongoQ.Select.GroupOrderOp);
        var ordering = Assert.Single(sortOp.Orderings);
        var orderRef = Assert.IsType<MongoElementRefExpression>(ordering.KeySelector);
        Assert.Equal("_id", orderRef.Path);
    }

    // ── TryBindGroupProjection: pending Skip/Take (composed before the Select) ──────────────────

    [Fact]
    public void Pending_paging_resolves_to_GroupPagingOps_in_arrival_order()
    {
        var mongoQ = BoundScalarKeyQuery();
        mongoQ.Select.PendingGroupPaging =
        [
            new MongoSkipOp(new MongoConstantExpression(0, forSerialization: null)),
            new MongoLimitOp(new MongoConstantExpression(0, forSerialization: null))
        ];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Collection(mongoQ.Select.GroupPagingOps,
            op => Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoSkipOp>(op).Count).Value),
            op => Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoLimitOp>(op).Count).Value));
        Assert.Null(mongoQ.Select.PendingGroupPaging);
    }

    [Fact]
    public void No_pending_paging_leaves_GroupPagingOps_empty()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Empty(mongoQ.Select.GroupPagingOps);
    }

    // ── TryBindGroupTerminalAggregate ──────────────────────────────────────────────
    // GroupBy(key).{Count()|LongCount()|Any()|Any(pred)|All(pred)|Count(pred)|LongCount(pred)} with NO
    // intervening Select — the "GroupBy_without_aggregate" family (EF-449).

    [Fact]
    public void Pending_paging_declines_terminal_aggregate_instead_of_dropping_it()
    {
        // EF-322 fix round: a Skip/Take already recorded into PendingGroupPaging (e.g.
        // GroupBy(key).Skip(1).Count()) has no mechanism here to apply the paging before the aggregate — this
        // bare-terminal-aggregate path never runs a Select/flatten stage for GroupPagingOps to attach to — so
        // it must decline rather than silently aggregate over every group and drop the paging entirely.
        var mongoQ = BoundScalarKeyQuery();
        mongoQ.Select.PendingGroupPaging = [new MongoSkipOp(new MongoConstantExpression(1, forSerialization: null))];

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Bare_Count_with_no_predicate_binds_zero_accumulator_grouping()
    {
        var mongoQ = BoundScalarKeyQuery();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));

        Assert.NotNull(mongoQ.Select.Grouping);
        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        Assert.Null(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoAggregateOperator.Count, mongoQ.Select.Cardinality!.Aggregate);
    }

    [Fact]
    public void Bare_Any_with_no_predicate_binds_zero_accumulator_grouping()
    {
        var mongoQ = BoundScalarKeyQuery();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, null, typeof(bool)));

        Assert.NotNull(mongoQ.Select.Grouping);
        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        Assert.Null(mongoQ.Select.PostGroupPredicate);
        Assert.True(mongoQ.Select.Cardinality!.PresenceOnly);
    }

    [Fact]
    public void Count_with_count_predicate_binds_accumulator_and_match()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, pred, typeof(int)));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$sum", acc.Operator);
        Assert.Null(acc.Operand);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal(acc.OutputField, left.Path);
        var right = Assert.IsType<MongoConstantExpression>(match.Right);
        Assert.Equal(1, right.Value);
    }

    [Fact]
    public void Any_with_count_predicate_binds_direct_comparison()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
    }

    [Fact]
    public void All_with_count_predicate_negates_comparison()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.All, pred, typeof(bool)));

        // All(pred) matches the COMPLEMENT: presence of a group failing pred means All is false. Relational
        // operators are $not-wrapped, never inverted (mirrors MongoExpressionNegator's own rule).
        var wrapped = Assert.IsType<MongoUnaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoUnaryOperator.Not, wrapped.Operator);
        var inner = Assert.IsType<MongoBinaryExpression>(wrapped.Operand);
        Assert.Equal(MongoBinaryOperator.GreaterThan, inner.Operator);
        Assert.False(mongoQ.Select.Cardinality!.PresentValue as bool?);
    }

    [Fact]
    public void Reversed_operand_order_binds_flipped_operator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => 1 < g.Count();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator); // flipped from < to >
        Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.IsType<MongoConstantExpression>(match.Right);
    }

    [Fact]
    public void Compound_predicate_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1 && g.Count() < 10;

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Non_bare_group_declines()
    {
        var mongoQ = TestQuery(); // no PendingGroupKey at all

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));
    }

    [Fact]
    public void Already_finalized_grouping_declines()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));
    }

    [Fact]
    public void Any_with_key_comparison_predicate_binds_direct_comparison_with_no_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Key == "ALFKI";

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("_id", left.Path);
        var right = Assert.IsType<MongoConstantExpression>(match.Right);
        Assert.Equal("ALFKI", right.Value);
    }

    [Fact]
    public void Where_key_comparison_then_bare_Any_applies_filter_with_no_accumulator()
    {
        // Regression for the accumulator-nullability trap: TryBindGroupWherePredicate's key-access arm
        // produces a PendingGroupPredicate with a NULL accumulator (a key comparison needs no $group
        // accumulator of its own); TryBindGroupTerminalAggregate must still emit it as the $match predicate,
        // not silently discard it because accumulator is null — that would return every group instead of
        // the filtered subset, a correctness regression, not just a missed capability.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Key == "ALFKI";

        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.NotNull(mongoQ.Select.PendingGroupPredicate);
        Assert.Null(mongoQ.Select.PendingGroupPredicate!.Value.Accumulator);

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, null, typeof(bool)));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
    }

    [Fact]
    public void Where_key_comparison_over_composite_key_still_declines()
    {
        // A bare g.Key over a COMPOSITE key has no single field to compare against a scalar constant — same
        // "no single field" reasoning every other key-flatten call site in this file applies. Must still
        // decline (fall back), not crash or silently compare the wrong thing.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, bool>> wherePred = g => g.Key == new { Country = "US", Region = "West" };

        Assert.False(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }

    [Fact]
    public void Where_key_comparison_over_empty_key_binds_direct_comparison_with_no_accumulator()
    {
        // EF-322 SP7 (Task 2): a zero-part key's g.Key reaching the HAVING/terminal-predicate path
        // (TryBindGroupSideOperand) with an EMPTY keyParts list must not crash (IndexOutOfRangeException) —
        // it resolves to "_id" like any other key, with no single backing property to serialize against
        // (keySerializationProperty stays null, mirroring the composite-key case).
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, bool>> wherePred = g => g.Key == null;

        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.NotNull(mongoQ.Select.PendingGroupPredicate);
        Assert.Null(mongoQ.Select.PendingGroupPredicate!.Value.Accumulator);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PendingGroupPredicate!.Value.Comparison);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("_id", left.Path);
    }

    [Fact]
    public void Where_key_comparison_over_empty_key_against_non_null_value_declines()
    {
        // EF-322 SP7 fix-wave (Finding I1): a zero-part key's g.Key resolves to "_id" (Task 2) with no single
        // backing property. Admitting a comparison against anything other than a literal null here would
        // hand the OTHER (non-null) operand to TryTranslateComparisonConstant with forSerialization: null,
        // which succeeds at BIND time regardless of the constant's CLR type and only crashes later, at
        // render time, when MongoValueRenderer.RenderValue falls back to the generic BsonValue.Create for a
        // non-BSON-mappable type — a decline that should happen HERE, not a downstream crash. Must decline
        // (fall back to driver-LINQ) instead of admitting the comparison.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var groupParam = Expression.Parameter(typeof(IGrouping<object, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<object, Order>.Key));
        var nonNullConstant = Expression.Constant(new object(), typeof(object));
        var wherePred = Expression.Lambda<Func<IGrouping<object, Order>, bool>>(
            Expression.Equal(keyAccess, nonNullConstant), groupParam);

        Assert.False(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }

    [Fact]
    public void HAVING_key_comparison_constant_serializes_through_the_keys_own_property()
    {
        // Final-review fix: the key-access arm's comparison constant used to translate with
        // forSerialization: null (TryTranslateComparisonConstant's old, fixed signature) — fine for a
        // string/int key, but MongoValueRenderer.RenderValue falls back to the generic BsonValue.Create for a
        // null ForSerialization, which throws ArgumentException for a type like Guid that BsonValue.Create
        // cannot map directly. The key's own IProperty (already known-safe — a value-converted/non-default-
        // represented key already declined when the key itself was bound) must flow through to the
        // comparison's constant/parameter side so it renders via the SAME property serializer the key's
        // stored value itself uses.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built rather than `g => g.Key == guidValue` over a captured local: the C# compiler lowers a
        // captured Guid to a closure-field MemberExpression, not a ConstantExpression — same lesson as SP1's
        // parameter-key tests. This constructs a genuine ConstantExpression directly.
        var groupParam = Expression.Parameter(typeof(IGrouping<Guid, Order>), "g");
        var keyAccess = Expression.Property(groupParam, nameof(IGrouping<Guid, Order>.Key));
        var guidConstant = Expression.Constant(Guid.NewGuid(), typeof(Guid));
        var wherePred = Expression.Lambda<Func<IGrouping<Guid, Order>, bool>>(
            Expression.Equal(keyAccess, guidConstant), groupParam);
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));

        var (_, comparison) = mongoQ.Select.PendingGroupPredicate!.Value;
        var binary = Assert.IsType<MongoBinaryExpression>(comparison);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("ExternalId", constant.ForSerialization!.Name);

        // Rendering must not throw — this is the actual observable bug (ArgumentException: ".NET type
        // System.Guid cannot be mapped to a BsonValue") when ForSerialization was null.
        var rendered = MongoAggregationExpressionRenderer.Render(comparison, new PlaceholderTable());
        Assert.NotNull(rendered);
    }

    // ── NativeCardinalityBinder.TryBindAggregate composed after GroupBy(key).Select(aggregate) ─────────────
    // All_after_GroupBy_aggregate2: Orders.GroupBy(o => o.CustomerID).Select(g => g.Sum(...)).All(v => v >= 0).
    // The predicate's bare parameter (v) is the Select's OWN flattened scalar output — the accumulator — NOT
    // the GroupBy key, even though the key is ALSO a single field-backed part (the same shape
    // MongoExpressionTranslator's EF-322 gap-3 carve-out uses for a genuine bare-scalar Distinct). Regression
    // for a bug where TryResolveMember matched the carve-out on the key regardless of Accumulators, binding the
    // predicate's constant against the KEY property's serializer (a string) instead of the accumulator's.

    [Fact]
    public void All_predicate_after_GroupBy_Select_scalar_aggregate_binds_against_accumulator_not_key()
    {
        var mongoQ = BoundScalarKeyQuery(); // key: x => x.Country (single field-backed key part)
        mongoQ.Select.IsGroupBy = true; // set by the visitor's GroupBy handling in production, not this binder
        Expression<Func<IGrouping<string, Order>, object>> proj = g => g.Sum(o => o.Amount);
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareLeafAlias));
        Assert.NotNull(bareLeafAlias);

        Expression<Func<int, bool>> pred = v => v >= 0;

        Assert.True(NativeCardinalityBinder.TryBindAggregate(
            mongoQ, MongoAggregateOperator.All, selector: null, predicate: pred, resultType: typeof(bool)));

        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PostGroupOps));
        var negated = Assert.IsType<MongoUnaryExpression>(matchOp.Predicate);
        Assert.Equal(MongoUnaryOperator.Not, negated.Operator);
        var comparison = Assert.IsType<MongoBinaryExpression>(negated.Operand);

        // The comparison must read the accumulator's OWN flattened alias, never the GroupBy key's field.
        var left = Assert.IsType<MongoElementRefExpression>(comparison.Left);
        Assert.Equal(bareLeafAlias, left.Path);
    }
}
