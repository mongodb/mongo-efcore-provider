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

    // No member named "Id": the driver's NamedIdMemberConvention would force its element name to "_id" and trip
    // the renamed-element guard for unrelated reasons.
    private class MemberInitKeyDto
    {
        public int Year { get; set; }
        public string Country { get; set; } = "";
    }

    // A MemberInit key DTO whose [BsonElement] name differs from its CLR name. Admitting it would write
    // $group._id under "Country" while the DTO's class map reads "cc", so g.Key readback throws FormatException.
    private class RenamedElementKeyDto
    {
        [BsonElement("cc")]
        public string Country { get; set; } = "";
    }

    // Like RenamedElementKeyDto, but the renamed member is the second binding — the guard must check every one.
    private class TwoMemberSecondRenamedKeyDto
    {
        public string Country { get; set; } = "";

        [BsonElement("rr")]
        public string Region { get; set; } = "";
    }

    // A parameterized ctor plus an object initializer; needs a settable second member for Expression.Bind.
    private class ParamCtorWithSettableInitializerDto
    {
        public string Country { get; }
        public string Region { get; set; } = "";
        public ParamCtorWithSettableInitializerDto(string country) { Country = country; }
    }

    // An entity member literally named "Key": a mixed element/g.Key accumulator condition must decline rather
    // than let the name-based translator resolve "Key" against the entity instead of g.Key.
    private class KeyNamedFieldEntity
    {
        public ObjectId Id { get; set; }
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
        public int Value { get; set; }
    }

    // A nested-construction GroupBy projection member: `Container = new NestedContainer { Name = "x",
    // Value = g.Sum(...) }` (Odata_groupby_empty_key).
    private class NestedContainer
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private class NestedWrapper
    {
        public NestedContainer Container { get; set; } = null!;
    }

    private class TwoNestedWrapper
    {
        public NestedContainer First { get; set; } = null!;
        public NestedContainer Second { get; set; } = null!;
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
        // A computed key part resolves through TryTranslateValue. It has no backing IProperty, so
        // HasDefaultKeySerialization's converter/BsonRepresentation check does not apply.
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
        // NewExpression.Members is null for every non-anonymous constructor call, not just new{}; treating this
        // as a zero-part key would silently collapse a 2-part key to one group.
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
        // A captured local reaches the provider only after EF's ParameterExtractingExpressionVisitor rewrites it
        // (a prefixed ParameterExpression on EF8/EF9, QueryParameterExpression on EF10), so hand-build that
        // post-extraction node, as MongoExpressionTranslatorTests does.
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
        // Hand-built for the same reason as Captured_parameter_key_binds_as_parameter; a named DTO stands in for
        // the anonymous type (NewExpression.Members is populated identically).
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
        // A key over a property with a non-default BsonRepresentation has no safe generic _id readback;
        // TryTranslateValue's AllFieldsDefaultSerialized check must catch it.
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
        // Each MemberAssignment binds through TryBindKeyPartValue like an anonymous composite key, so parts
        // carry their member names and read back as a named composite key.
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
        // A parameterized ctor with an object initializer has no key-part naming convention; the whole key must
        // decline rather than silently drop the ctor argument.
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
        // A MemberMemberBinding (`new Foo { Bar = { Baz = 1 } }`) has no single translatable value; the whole
        // key must decline.
        var mongoQ = TestQuery();
        var xParam = Expression.Parameter(typeof(Order), "x");
        var ctor = typeof(MemberInitKeyDto).GetConstructor(Type.EmptyTypes)!;
        var newExpr = Expression.New(ctor);
        var yearMember = typeof(MemberInitKeyDto).GetProperty(nameof(MemberInitKeyDto.Year))!;
        // TryBindGroupKey only checks whether a binding is a MemberAssignment, so an empty MemberBind suffices.
        var nestedBinding = Expression.MemberBind(yearMember, Array.Empty<MemberBinding>());
        var memberInit = Expression.MemberInit(newExpr, nestedBinding);
        var key = Expression.Lambda<Func<Order, MemberInitKeyDto>>(memberInit, xParam);

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_with_renamed_element_declines()
    {
        // Key parts are named after CLR members, but g.Key reads back through the DTO's class map, which honors
        // [BsonElement]. Admitting this writes {Country: ...} where "cc" is expected (FormatException on
        // readback), so the whole key must decline.
        var mongoQ = TestQuery();
        Expression<Func<Order, RenamedElementKeyDto>> key = x => new RenamedElementKeyDto { Country = x.Country };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void MemberInit_dto_key_with_second_member_renamed_declines()
    {
        // The renamed member is the second binding: the class-map check must cover every MemberAssignment.
        var mongoQ = TestQuery();
        Expression<Func<Order, TwoMemberSecondRenamedKeyDto>> key =
            x => new TwoMemberSecondRenamedKeyDto { Country = x.Country, Region = x.Region };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Parameter_key_of_an_unmappable_CLR_type_declines_instead_of_throwing()
    {
        // TryTranslateValue checks translatability, not renderability: a parameter of a type BsonValue.Create
        // can't map (Guid) would throw ArgumentException at pipeline build. Same probe-before-admitting rule as
        // NativeSlotPopulator.TryProbeBareValueRenders for sort keys.
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
        // Binder-level plumbing only: a computed first-level key alongside PriorGrouping. The full nested
        // GroupBy is covered by GroupBy_aggregate_followed_another_GroupBy_aggregate in the spec suite.
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
        // SnapshotPriorGroupingForNestedGroupBy must carry GroupHavingPredicate aside too; otherwise the outer
        // GroupBy's TryBindGroupProjection overwrites it and the first GroupBy's HAVING is silently dropped.
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
        // ...and cleared the live slot so the outer GroupBy doesn't inherit it.
        Assert.Null(mongoQ.Select.GroupHavingPredicate);

        Expression<Func<object, int>> secondKey = x => 0; // outer key shape is irrelevant to this regression
        // (bind directly against PendingGroupKey to avoid needing a real "C" member on an anonymous type)
        mongoQ.Select.PendingGroupKey = [new MongoGroupingKeyPart(null, new MongoConstantExpression(0, forSerialization: null))];

        Expression<Func<IGrouping<int, object>, object>> secondProj = g => new { N = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, secondProj, out _));

        // The outer GroupBy has no HAVING of its own — GroupHavingPredicate stays null...
        Assert.Null(mongoQ.Select.GroupHavingPredicate);
        // ...but the first GroupBy's HAVING is still there for MongoSelectLowerer to emit with PriorGrouping.
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
        // g.Key over a zero-part key flattens to "_id": the group's empty document is the correct readback.
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

        // Two accumulators: the HAVING's ("__agg0") and the Select's ("Count") — a harmless redundant field.
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

        // Only the Select's own accumulator ("c"); the key comparison needs none.
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
        // TryBindGroupPredicateComparison recognizes only a single binary comparison; an AndAlso/OrElse HAVING
        // must decline rather than apply half the filter. Where-then-Select counterpart of
        // Compound_predicate_returns_false.
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
        // g.Distinct() before .Select(selector).Distinct() cannot change the final distinct set, so it must bind
        // with no condition at all, not a trivially-true one.
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
        // A genuine per-element filter before Select(...).Distinct().Max() needs a $cond. Uses nullable
        // ShippedDate so Max() is nullable; a non-nullable result would trip the empty-filtered-group guard.
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
        // Two differently-thresholded Count(pred) calls must bind to separate fields with separate conditions.
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
        // The predicate references g.Key only. It must resolve to the key's own FieldRef (here a constant, from
        // GroupBy(o => 1)), never "_id", which doesn't exist yet inside this accumulator's own $group stage.
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
        // TryTranslateAccumulatorCondition handles a pure g.Key comparison or a pure per-element predicate, never
        // both combined; must decline rather than crash or drop half the condition.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Where(e => e.Amount > 5 && g.Key == "ALFKI").Sum(e => e.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Where_then_Count_binds_conditional_sum_accumulator()
    {
        // LongCount_after_GroupBy_aggregate's inner shape as a named member; the next test covers the bare body.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount < 250).Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Filtered", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        // A filtered count emits constant 0 for unmatched elements, not the $$REMOVE sentinel other accumulators
        // use.
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);
    }

    [Fact]
    public void Bare_body_Where_then_Count_binds_under_synthetic_alias()
    {
        // LongCount_after_GroupBy_aggregate's exact shape: a bare (non-`new{}`) Select body.
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
        // The Count/LongCount arm must route through TryTranslateAccumulatorCondition's mixed-reference guard
        // too.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount > 5 && g.Key == "ALFKI").Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Mixed_element_and_key_predicate_declines_even_when_entity_has_a_Key_named_member()
    {
        // The ordinary translator resolves members by name, so with an entity member named "Key",
        // `e.Value > 0 && g.Key == "A"` would silently resolve g.Key as e.Key. Must decline. (Order has no
        // "Key" member, so it can't exercise this.)
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
        // The key comparison constant must serialize through the key's property (ForSerialization); with null,
        // a Guid constant throws ArgumentException at render time. See also
        // HAVING_key_comparison_constant_serializes_through_the_keys_own_property.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built: the compiler lowers a captured Guid to a closure-field MemberExpression, not a constant.
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

        // Must not throw ArgumentException (".NET type System.Guid cannot be mapped to a BsonValue").
        var rendered = MongoAggregationExpressionRenderer.Render(cond, new PlaceholderTable());
        Assert.NotNull(rendered);
    }

    [Fact]
    public void Ternary_over_Guid_key_comparison_constant_serializes_through_the_keys_own_property()
    {
        // Same ForSerialization requirement as
        // Count_with_predicate_key_comparison_constant_serializes_through_the_keys_own_property, for
        // TryTranslateGroupProjectionConditionOrValue's key-vs-constant comparison.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built: a captured Guid would be a closure-field MemberExpression, not a constant.
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

        // Must not throw ArgumentException (".NET type System.Guid cannot be mapped to a BsonValue").
        var rendered2 = MongoAggregationExpressionRenderer.Render(cond, new PlaceholderTable());
        Assert.NotNull(rendered2);
    }

    [Fact]
    public void Where_then_Min_over_non_nullable_result_declines_to_avoid_silent_default_on_empty_group()
    {
        // If every element fails the filter, $$REMOVE leaves $min/$max/$avg null and the shaper would read
        // default(T) for a non-nullable result — silently wrong (LINQ and driver-LINQ throw on an empty
        // sequence). Must decline.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Min = g.Where(e => e.Amount > 999999).Min(e => e.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Where_then_Select_Distinct_Max_over_non_nullable_result_declines_to_avoid_silent_default_on_empty_group()
    {
        // Same risk via TryBindDistinctAccumulator's $addToSet-then-reduce path.
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
        // GroupBy_aggregate_projecting_conditional_expression_based_on_group_key's shape, using nullable
        // ShippedDate so the null comparison is meaningful.
        var mongoQ = TestQuery();
        Expression<Func<Order, DateTime?>> key = x => x.ShippedDate;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<DateTime?, Order>, object>> proj =
            g => new { Label = g.Key == null ? "is null" : "is not null", Sum = g.Sum(o => o.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Two flatten projections (Label and Sum); flatten aliases live on MongoSelectDefinition.Projection.
        Assert.Equal(2, mongoQ.Select.Projection.Count);
        var labelProjection = mongoQ.Select.Projection.Single(p => p.Alias == "Label");
        var cond = Assert.IsType<MongoConditionalExpression>(labelProjection.Expression);
        var test = Assert.IsType<MongoBinaryExpression>(cond.Test);
        var keyRef = Assert.IsType<MongoElementRefExpression>(test.Left);
        Assert.Equal("_id", keyRef.Path);
        Assert.Null(Assert.IsType<MongoConstantExpression>(test.Right).Value);
        Assert.Equal("is null", Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal("is not null", Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);

        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Ternary_leaf_referencing_an_accumulator_declines()
    {
        // An accumulator inside a computed projection leaf must decline (ReferencesParameter's guard catches
        // g.Count() like a mixed g.Key reference).
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
        // A per-element reference in the ternary's test must decline via ReferencesParameter.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Label = g.Where(o => o.Amount > 0).Any() ? g.Key : "none" };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Ternary_over_composite_key_null_comparison_admits_whole_key_read()
    {
        // allowWholeKeyRead resolves a composite key's bare g.Key to "_id"; comparing that to null is
        // meaningless but stays structurally admitted, like the flatten loop. Hand-built because the composite
        // key's anonymous type has no literal spelling; uses the bare-body path.
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
        // Pins ResolveKeyMemberSerializationProperty's `keyParts.Count == 0` guard on the Select-projection
        // ternary path (TryTranslateGroupProjectionConditionOrValue); the Where path is covered by
        // Where_key_comparison_over_empty_key_binds_direct_comparison_with_no_accumulator. Without the guard,
        // g.Key == null over a zero-part key indexes keyParts[0] and throws IndexOutOfRangeException.
        // Hand-built: the empty anonymous key has no literal spelling.
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
        // Select-projection ternary counterpart of Where_key_comparison_over_empty_key_against_non_null_value_declines:
        // a non-null comparison over a zero-part key must decline the whole projection rather than defer a
        // crash to render time.
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
        // GroupBy_orderby_projection_with_coalesce_operation's shape:
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

        Assert.Single(mongoQ.Select.Grouping!.Accumulators);
    }

    [Fact]
    public void Chained_coalesce_over_key_binds_right_nested_coalesce()
    {
        // a ?? b ?? c is right-associative (a ?? (b ?? c)): the right operand is itself a Coalesce.
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
        // A bare g.Key over a composite key declines in TryGetKeyMemberPath (no single field to flatten to); the
        // scalar-key case is Bare_g_Key_with_no_aggregate_still_declines.
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
        // A composite-key sub-member (g.Key.Country) through a ctor-wrapped DTO with no aggregate: zero
        // accumulators, one row per distinct (Country, Region) pair.
        var mongoQ = TestQuery();

        Assert.True(BindCompositeKeyAndProjection(mongoQ,
            x => new { x.Country, x.Region },
            g => new KeyOnlyDto(g.Key.Country),
            out _));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_id.Country", Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    // Binds a composite key and projection sharing the compiler-synthesized anonymous TKey via type inference —
    // the only way to write `g.Key.<Sub>` in a separately-declared projection lambda.
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
        // A bare g.Key projection is a plain-Distinct-equivalent shape that isn't attempted.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj = g => g.Key;

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Wrapped_key_only_combined_with_pending_ordering_aggregate_still_declines()
    {
        // A zero-accumulator projection combined with a pending-ordering aggregate is out of scope.
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
        // The accumulator's source is an in-scope array, not g. Binding it would silently return the group's
        // row count instead; must fall back.
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
        // An accumulator named "_id" would duplicate the $group key element and throw at pipeline build; reject
        // it so the shape falls back.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { _id = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Key_member_projected_to_id_alias_still_binds()
    {
        // A key member aliased "_id" reads the group's own "_id" and doesn't collide; the guard is
        // accumulator-only.
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

        // Two separate $sum:1 accumulators (ordering and projection), deliberately not de-duplicated.
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
        // OrderBy(g => g.Key) before an empty-key GroupBy resolves through the pending-ordering call site
        // (allowWholeKeyRead: false); a zero-part key is never composite, so it sorts on "_id".
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
    // GroupBy(key).{Count()|LongCount()|Any()|Any(pred)|All(pred)|Count(pred)|LongCount(pred)} with no
    // intervening Select — the "GroupBy_without_aggregate" family.

    [Fact]
    public void Pending_paging_declines_terminal_aggregate_instead_of_dropping_it()
    {
        // Pending Skip/Take (GroupBy(key).Skip(1).Count()) has no flatten stage to attach to on this path, so it
        // must decline rather than aggregate every group and drop the paging.
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
        // A key comparison yields a PendingGroupPredicate with a null accumulator; it must still be emitted as
        // the $match, not discarded (which would return every group).
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
        // A composite g.Key has no single field to compare to a constant; must decline.
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
        // A zero-part key's g.Key on the HAVING/terminal path (TryBindGroupSideOperand) resolves to "_id" with
        // no backing property, rather than indexing an empty keyParts (IndexOutOfRangeException).
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
        // With no backing property, a non-null comparison operand would get forSerialization: null, bind fine,
        // and crash at render time in BsonValue.Create for a non-BSON-mappable type. Must decline at bind time.
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
        // The comparison constant must serialize through the key's own IProperty (already known-safe, since a
        // converted/non-default-represented key declines at key binding). With null ForSerialization,
        // BsonValue.Create throws ArgumentException for Guid.
        var mongoQ = TestQuery();
        Expression<Func<Order, Guid>> key = x => x.ExternalId;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // Hand-built: a captured Guid would be a closure-field MemberExpression, not a ConstantExpression.
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

        // Must not throw ArgumentException (".NET type System.Guid cannot be mapped to a BsonValue").
        var rendered = MongoAggregationExpressionRenderer.Render(comparison, new PlaceholderTable());
        Assert.NotNull(rendered);
    }

    // ── NativeCardinalityBinder.TryBindAggregate composed after GroupBy(key).Select(aggregate) ─────────────
    // All_after_GroupBy_aggregate2: Orders.GroupBy(o => o.CustomerID).Select(g => g.Sum(...)).All(v => v >= 0).
    // The predicate's parameter is the Select's flattened accumulator, not the key — even though the key is a
    // single field-backed part (the shape MongoExpressionTranslator's bare-scalar Distinct carve-out matches).
    // Binding against the key would serialize the constant with the key's (string) serializer.

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

        // Must read the accumulator's flattened alias, never the key's field.
        var left = Assert.IsType<MongoElementRefExpression>(comparison.Left);
        Assert.Equal(bareLeafAlias, left.Path);
    }

    // ── TryBindNestedGroupProjectionConstruction ──────────────────────────────────────────────

    [Fact]
    public void Nested_construction_projection_member_binds_group()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new NestedWrapper
            {
                Container = new NestedContainer { Name = "x", Value = g.Sum(o => o.Amount) }
            };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var projection = mongoQ.Select.Projection.Single(p => p.Alias == "Container");
        var construction = Assert.IsType<MongoDocumentConstructionExpression>(projection.Expression);
        Assert.Equal(2, construction.Members.Count);

        Assert.Equal("Name", construction.Members[0].MemberName);
        Assert.IsType<MongoConstantExpression>(construction.Members[0].Value);

        Assert.Equal("Value", construction.Members[1].MemberName);
        var elementRef = Assert.IsType<MongoElementRefExpression>(construction.Members[1].Value);

        // "_nestedAgg1": a synthetic name is allocated before TryBindAccumulator for every non-key member,
        // including the constant "Name", so indices aren't contiguous. Only uniqueness matters (see the
        // two-sibling test).
        Assert.Equal("_nestedAgg1", elementRef.Path);

        Assert.Contains(mongoQ.Select.Grouping!.Accumulators, a => a.OutputField == "_nestedAgg1");
    }

    [Fact]
    public void Nested_construction_member_referencing_per_element_value_declines_whole_projection()
    {
        // A per-element reference (g.First()) mixed into a nested construction with an accumulator must decline
        // the whole projection, never produce a partial read.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new NestedWrapper
            {
                Container = new NestedContainer { Name = g.First().Country, Value = g.Sum(o => o.Amount) }
            };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Two_sibling_nested_constructions_use_distinct_synthetic_accumulator_field_names()
    {
        // Two top-level members, each with a nested accumulator: synthetic field names must not collide.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new TwoNestedWrapper
            {
                First = new NestedContainer { Name = "a", Value = g.Sum(o => o.Amount) },
                Second = new NestedContainer { Name = "b", Value = g.Count() }
            };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var firstConstruction = Assert.IsType<MongoDocumentConstructionExpression>(
            mongoQ.Select.Projection.Single(p => p.Alias == "First").Expression);
        var secondConstruction = Assert.IsType<MongoDocumentConstructionExpression>(
            mongoQ.Select.Projection.Single(p => p.Alias == "Second").Expression);

        var firstRef = Assert.IsType<MongoElementRefExpression>(firstConstruction.Members[1].Value);
        var secondRef = Assert.IsType<MongoElementRefExpression>(secondConstruction.Members[1].Value);

        // The counter is shared across siblings and also advances for each "Name" constant, so the paths are
        // "_nestedAgg1"/"_nestedAgg3"; only non-collision is pinned.
        Assert.NotEqual(firstRef.Path, secondRef.Path);
        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
    }
}
