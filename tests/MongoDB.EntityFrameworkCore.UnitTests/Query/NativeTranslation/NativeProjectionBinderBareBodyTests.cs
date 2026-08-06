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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-322 step 3a: <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/>'s BARE-selector-body arm.
/// Asserts the DERIVED ALIAS and its TIER, not just the admit/decline boolean — the alias IS the mechanism this
/// slice turns on, and a test that only checked the boolean would stay green if the alias were wrong.
/// </summary>
public class NativeProjectionBinderBareBodyTests
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Amount { get; set; }
        public List<string> Tags { get; set; } = null!;
        public Address Address { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> Model = mb => mb.Entity<Order>().OwnsOne(o => o.Address);

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Order>(Model);
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Order))!);
    }

    [Fact]
    public void Bare_top_level_scalar_is_admitted_with_the_element_name_as_the_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> selector = o => o.Country;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Country", projection.Alias);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(projection.Expression).ElementName);

        // The alias is registered on the carrier under the BARE sentinel key, which is what every alias-reading
        // site consults — a bare body's own ProjectionMember has no last member to derive a name from.
        Assert.True(mongoQ.Select.IsBareProjection);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Country", alias);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_single_property_primary_key_is_admitted_with_the_underscore_id_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, ObjectId>> selector = o => o.Id;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        // A document root's PK is stored at "_id" (PrimaryKeyDiscoveryConvention rewrites it), so tier 1's
        // alias-IS-the-path rule makes the alias "_id" rather than the CLR member name. That is what lets
        // RenderProject suppress its default `_id : 0` exclusion instead of emitting a malformed mix.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_id", projection.Alias);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("_id", alias);
    }

    [Fact]
    public void Bare_primitive_collection_property_is_admitted_with_the_element_name_as_the_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, List<string>>> selector = o => o.Tags;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        // A primitive collection is a mapped PROPERTY, so it arrives as a plain member access and resolves to a
        // MongoFieldExpression — it never reaches the owned-array branch at all.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Tags", projection.Alias);
        Assert.IsType<MongoFieldExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_owned_hop_scalar_is_declined_and_leaves_no_override()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> selector = o => o.Address.City;

        // The EF-362 tripwire. The leaf resolves fine — to the DOTTED path "Address.City" — but a dotted alias
        // is read back as a literal key while MongoDB renders `$project: {"Address.City": …}` as NESTED output,
        // so tier 1 requires a non-dotted path.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }

    [Fact]
    public void Bare_arithmetic_leaf_is_declined_because_it_has_no_document_path()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Amount * 2;

        // A computed leaf translates perfectly well, so this decline is the NODE-KIND gate doing its job and not
        // a translation failure: an arithmetic leaf is backed by no document element, so there is no path to use
        // as the alias and no whole-document read that could be correct on a fallback route.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_constant_leaf_is_declined()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = _ => 0;

        // Deliberate: a falsy constant renders as a BARE VALUE, which $project reads as an EXCLUSION flag and
        // then aborts the aggregate ("Cannot do exclusion on field X in inclusion projection").
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void Bare_whole_entity_parameter_is_declined_by_the_arm()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, Order>> selector = o => o;

        // The positive control at unit level. TranslateSelect returns a `x => x` Select unchanged before the
        // binder is ever called, so this shape does not reach here in practice — but the arm must not be able to
        // match a bare ParameterExpression even if it did, or whole-entity queries would start emitting a
        // $project keyed by nothing.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void A_bare_body_on_an_already_populated_projection_is_declined()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> first = o => o.Country;
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, first));

        Expression<Func<Order, int>> second = o => o.Amount;

        // This is what makes the alias carrier provably WRITE-ONCE, and therefore what makes
        // AddProjectionAliasOverride safe to use Dictionary.Add: the one writer cannot commit a second bare
        // override on the same select. Without the guard the second call would append a second projection AND
        // attempt a second override write for the one bare sentinel key.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, second));

        // And it declines cleanly — the first projection and the first override are untouched.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Country", projection.Alias);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Country", alias);
    }

    [Fact]
    public void A_wrapped_body_still_registers_no_alias_override()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> selector = o => new {o.Country, o.Amount};

        // The inertness control for the wrapped path: a wrapped projection's alias comes from its member name,
        // so it must register nothing — an override here would make IsBareProjection true and wrongly trip both
        // narrowings and the late-fallback strip.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(2, mongoQ.Select.Projection.Count);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }
}
