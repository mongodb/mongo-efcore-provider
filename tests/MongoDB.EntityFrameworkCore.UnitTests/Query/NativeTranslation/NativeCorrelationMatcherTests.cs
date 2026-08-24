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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="NativeCorrelationMatcher"/>, which recognizes a correlated
/// <c>Where</c>-over-<see cref="Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression"/> shape and
/// resolves it to the single matching collection navigation off the outer entity. Extracted (EF-347 slice 5,
/// Task 1) from <see cref="NativeProjectionBinder"/>'s projected-<c>Count</c> recognition.
/// </summary>
public class NativeCorrelationMatcherTests
{
    private class Customer
    {
        public int Id { get; set; }
        public List<Order> Orders { get; set; } = [];
        public List<Note> Notes { get; set; } = [];
    }

    private class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public decimal Amount { get; set; }
    }

    private class Note
    {
        public string Text { get; set; } = "";
    }

    private static (IEntityType CustomerType, IEntityType OrderType, IEntityType NoteType, INavigation OrdersNav, INavigation NotesNav) TestModel()
    {
        using var db = SingleEntityDbContext.Create<Customer>(mb =>
        {
            mb.Entity<Order>();
            mb.Entity<Customer>().HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            mb.Entity<Customer>().OwnsMany(c => c.Notes);
        });
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var orderType = db.Model.FindEntityType(typeof(Order))!;
        var ordersNav = customerType.FindNavigation(nameof(Customer.Orders))!;
        var notesNav = customerType.FindNavigation(nameof(Customer.Notes))!;
        var noteType = notesNav.TargetEntityType;
        return (customerType, orderType, noteType, ordersNav, notesNav);
    }

    private static (ParameterExpression Outer, ParameterExpression Dependent) Params()
        => (Expression.Parameter(typeof(Customer), "c"), Expression.Parameter(typeof(Order), "o"));

    private static readonly MethodInfo EfPropertyOfInt = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(int));

    // Note has no real CLR "CustomerId" member (it's a shadow FK property, backing the owned relationship) —
    // an EF.Property(n, "CustomerId") call is the structurally-correct way to reference it, mirroring what
    // GetRootParameter/TryGetSimplePropertyName recognize for shadow-property FK access.
    private static Expression ShadowProperty(ParameterExpression note, string name)
        => Expression.Call(EfPropertyOfInt, note, Expression.Constant(name));

    private static Expression BareEquality(ParameterExpression outer, ParameterExpression dependent)
        => Expression.Equal(
            Expression.Property(outer, nameof(Customer.Id)),
            Expression.Property(dependent, nameof(Order.CustomerId)));

    private static Expression NullGuardedEquality(ParameterExpression outer, ParameterExpression dependent)
    {
        var nullGuard = Expression.NotEqual(
            Expression.Convert(Expression.Property(outer, nameof(Customer.Id)), typeof(object)),
            Expression.Constant(null, typeof(object)));
        return Expression.AndAlso(nullGuard, BareEquality(outer, dependent));
    }

    // ── Success cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Bare_equality_matches_reference_navigation()
    {
        var (customerType, orderType, _, ordersNav, _) = TestModel();
        var (outer, dependent) = Params();

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            BareEquality(outer, dependent), customerType, outer, orderType, requireEmbedded: false, out var navigation);

        Assert.True(result);
        Assert.Same(ordersNav, navigation);
    }

    [Fact]
    public void Null_guarded_equality_matches_reference_navigation()
    {
        var (customerType, orderType, _, ordersNav, _) = TestModel();
        var (outer, dependent) = Params();

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            NullGuardedEquality(outer, dependent), customerType, outer, orderType, requireEmbedded: false, out var navigation);

        Assert.True(result);
        Assert.Same(ordersNav, navigation);
    }

    [Fact]
    public void Null_guard_on_either_side_of_AndAlso_matches()
    {
        var (customerType, orderType, _, ordersNav, _) = TestModel();
        var (outer, dependent) = Params();

        var nullGuard = Expression.NotEqual(
            Expression.Convert(Expression.Property(outer, nameof(Customer.Id)), typeof(object)),
            Expression.Constant(null, typeof(object)));
        // Equality AndAlso null-guard (guard on the RIGHT, rather than the left).
        var body = Expression.AndAlso(BareEquality(outer, dependent), nullGuard);

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            body, customerType, outer, orderType, requireEmbedded: false, out var navigation);

        Assert.True(result);
        Assert.Same(ordersNav, navigation);
    }

    // ── requireEmbedded filtering ────────────────────────────────────────────────

    [Fact]
    public void RequireEmbedded_true_over_reference_navigation_returns_false()
    {
        var (customerType, orderType, _, _, _) = TestModel();
        var (outer, dependent) = Params();

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            BareEquality(outer, dependent), customerType, outer, orderType, requireEmbedded: true, out _);

        Assert.False(result);
    }

    [Fact]
    public void RequireEmbedded_false_over_owned_navigation_returns_false()
    {
        var (customerType, _, noteType, _, _) = TestModel();
        var outer = Expression.Parameter(typeof(Customer), "c");
        var noteParam = Expression.Parameter(typeof(Note), "n");
        // Note has no real CLR FK property; the owned nav's shadow FK is conventionally named
        // "CustomerId" too (same as the reference nav's), so referencing that name off a Note-typed
        // dependent parameter mirrors the shape TryMatchCorrelatedCollection expects to recognize.
        var body = Expression.Equal(
            Expression.Property(outer, nameof(Customer.Id)),
            ShadowProperty(noteParam, "CustomerId"));

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            body, customerType, outer, noteType, requireEmbedded: false, out _);

        Assert.False(result);
    }

    [Fact]
    public void RequireEmbedded_true_over_owned_navigation_matches()
    {
        var (customerType, _, noteType, _, notesNav) = TestModel();
        var outer = Expression.Parameter(typeof(Customer), "c");
        var noteParam = Expression.Parameter(typeof(Note), "n");
        var body = Expression.Equal(
            Expression.Property(outer, nameof(Customer.Id)),
            ShadowProperty(noteParam, "CustomerId"));

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            body, customerType, outer, noteType, requireEmbedded: true, out var navigation);

        Assert.True(result);
        Assert.Same(notesNav, navigation);
    }

    // ── Rejection cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Extra_conjunct_returns_false()
    {
        var (customerType, orderType, _, _, _) = TestModel();
        var (outer, dependent) = Params();

        var extra = Expression.GreaterThan(Expression.Property(dependent, nameof(Order.Amount)), Expression.Constant(5m));
        var body = Expression.AndAlso(BareEquality(outer, dependent), extra);

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            body, customerType, outer, orderType, requireEmbedded: false, out _);

        Assert.False(result);
    }

    [Fact]
    public void Non_equality_body_returns_false()
    {
        var (customerType, orderType, _, _, _) = TestModel();
        var (outer, dependent) = Params();

        var body = Expression.GreaterThan(Expression.Property(dependent, nameof(Order.Amount)), Expression.Constant(5m));

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            body, customerType, outer, orderType, requireEmbedded: false, out _);

        Assert.False(result);
    }

    // ── Ambiguous candidates ─────────────────────────────────────────────────────
    // EF Core's own model-building invariants make it impossible to construct two GENUINE navigations on one
    // outer entity type whose ForeignKey both resolve to a property literally named the same on the same
    // target entity type (only one property can carry a given name, and only one foreign key may be declared
    // over a given property set). To exercise the "more than one candidate" branch, a second, independent
    // model supplies a second real navigation, and a minimal DispatchProxy-based facade re-targets its
    // TargetEntityType (the ONLY member overridden) so it collides with the first model's Order entity type —
    // every other member (IsCollection/IsEmbedded/ForeignKey) is answered by the real, untouched navigation.

    private class OverrideProxy : DispatchProxy
    {
        private object _inner = null!;
        private Dictionary<string, Func<object?[]?, object?>> _overrides = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod != null && _overrides.TryGetValue(targetMethod.Name, out var fn)
                ? fn(args)
                : targetMethod!.Invoke(_inner, args);

        public static TInterface Create<TInterface>(object inner, Dictionary<string, Func<object?[]?, object?>> overrides)
        {
            var proxy = (OverrideProxy)(object)Create<TInterface, OverrideProxy>()!;
            proxy._inner = inner;
            proxy._overrides = overrides;
            return (TInterface)(object)proxy;
        }
    }

    private class SecondCustomer
    {
        public int Id { get; set; }
        public List<SecondOrder> Orders2 { get; set; } = [];
    }

    private class SecondOrder
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
    }

    [Fact]
    public void Ambiguous_two_navigations_same_target_and_fk_name_returns_false()
    {
        var (customerType, orderType, _, ordersNav, _) = TestModel();

        using var db2 = SingleEntityDbContext.Create<SecondCustomer>(mb =>
        {
            mb.Entity<SecondOrder>();
            mb.Entity<SecondCustomer>().HasMany(c => c.Orders2).WithOne().HasForeignKey(o => o.CustomerId);
        });
        var secondCustomerType = db2.Model.FindEntityType(typeof(SecondCustomer))!;
        var realSecondNav = secondCustomerType.FindNavigation(nameof(SecondCustomer.Orders2))!;

        // A second real navigation (own model, own FK named "CustomerId" like ordersNav's) with its
        // TargetEntityType re-pointed at orderType — the only way to legitimately collide two navigations
        // on target+FK-name given EF Core's own uniqueness invariants (see comment above).
        var secondNav = OverrideProxy.Create<INavigation>(realSecondNav, new()
        {
            ["get_TargetEntityType"] = _ => orderType
        });

        var fakeOuterEntityType = OverrideProxy.Create<IEntityType>(customerType, new()
        {
            [nameof(IEntityType.GetNavigations)] = _ => new[] { ordersNav, secondNav }
        });

        var (outer, dependent) = Params();

        var result = NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            BareEquality(outer, dependent), fakeOuterEntityType, outer, orderType, requireEmbedded: false, out _);

        Assert.False(result);
    }
}
