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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoExpressionTranslator"/>, which translates EF predicate/key-selector
/// lambda bodies into dialect-agnostic <see cref="MongoExpression"/> trees.
/// </summary>
public class MongoExpressionTranslatorTests
{
    // --- Entity model used across tests ---

    // EF-403 (slice A1, Task 5) identity-like-convert fixture. Tier is a plain (unconverted) enum — the
    // constant-serializer discriminator for an enum-as-string CONSTANT is covered functionally
    // (NativeCastTests.Enum_as_string_comparison_goes_native_and_returns_the_right_values); these unit tests
    // pin the CLASSIFICATION only (does the comparison get admitted, and via which of the two tolerate arms).
    private enum Tier { Bronze, Silver, Gold }

    // Fix round 1 (EF-403 Task 5): a SUB-int-backed enum. C#'s own binary numeric promotion promotes a
    // short/byte/ushort/sbyte-backed enum's equality/relational comparison to Int32 — a WIDENING of the
    // enum's own underlying type, not an exact match — so the member-side Convert here targets Int32, not
    // ShortTier's own Int16. This is the shape that cost the whole BuiltInDataTypesMongoTest family on first
    // delivery, because Tier (above) is Int32-backed and so never exposed a promotion the exact-match rule
    // already covered by coincidence.
    private enum ShortTier : short { Bronze, Silver, Gold }

    // Fix round 1 regression pin: a LONG-backed enum NARROWED to int is a genuine narrowing of the underlying
    // type and must stay declined — the widening check is directional (narrower-underlying -> wider-target
    // only), never the reverse.
    private enum LongTier : long { Bronze, Silver, Gold }

    private class Customer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
        public double DoubleScore { get; set; }
        public string Name { get; set; } = "";
        public bool Active { get; set; }
        public Tier Level { get; set; }
        public ShortTier ShortLevel { get; set; }
        public LongTier LongLevel { get; set; }
        public char Grade { get; set; }
        public int? NullableAge { get; set; }
        public bool? NullableFlag { get; set; }
    }

    // Fixture for the EF-400 `.Value`-peel conjunct. CustomerCode declares its own `Value` member and is
    // mapped as a VALUE-CONVERTED scalar, so the receiver (`Code`) is a real mapped property — which is what
    // makes an unconditional (name-only) peel resolve the WRONG field rather than merely decline. `Amount` is
    // the genuine Nullable<T> control on the same entity.
    private readonly struct CustomerCode
    {
        public CustomerCode(string value) => Value = value;

        public string Value { get; }
    }

    private class CodedEntity
    {
        public ObjectId Id { get; set; }
        public CustomerCode Code { get; set; }
        public int? Amount { get; set; }
    }

    // Two-scope (correlated reference SelectMany) fixtures — InnerRef and OuterRef deliberately share a
    // "Name" member to prove identity-based routing never conflates the two scopes by name.
    private class InnerRef
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    private class OuterRef
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Threshold { get; set; }
    }

    // Fixture for TryTranslateValue (computed numeric leaf) tests — a value-converted EncStatus property
    // covers guard B (a property lacking default serialization).
    private class Order
    {
        public ObjectId Id { get; set; }
        public int Price { get; set; }
        public int Qty { get; set; }
        public int Gross { get; set; }
        public int Tax { get; set; }
        public int Count { get; set; }
        public double Weight { get; set; }
        public string Tag { get; set; } = "";
        public int EncStatus { get; set; }
        // A CONVERTED, NON-int property, needed (and EncStatus above is NOT enough) to pin
        // AllFieldsDefaultSerialized's MongoConvertExpression case: an int->long/double cast of EncStatus is
        // WIDENING and unwraps in TranslateOperand's Convert branch before ever reaching a MongoConvertExpression
        // — allowNumericWidening is true for TryTranslateValue and int->double/int->long are both admitted
        // widenings, so the guard would never be exercised. A NARROWING cast of a converted double (double->int)
        // is genuinely type-changing regardless of allowNumericWidening, so it always builds a
        // MongoConvertExpression wrapping the converted field — the shape the guard must inspect.
        public double EncWeight { get; set; }

        // Fixtures for the bool/DateTime string-concat decline pins (MEASURED $toString divergence — see
        // MongoExpressionTranslator.TranslateConcatOperand's remarks).
        public bool Flag { get; set; }
        public DateTime When { get; set; }
    }

    // Fixtures for owned single-reference dotted-path resolution (EF-322 Task 2).

    private class OwnedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        // Deliberately shares its name (and CLR type) with OwnedPost.IsActive — see OwnedPost.
        public bool IsActive { get; set; }
        public OwnedAddress Address { get; set; } = null!;
        public List<OwnedPost> Posts { get; set; } = [];
        // Deliberately shares its NAVIGATION NAME with OwnedPost.Comments, and its element deliberately shares
        // the "Text" property name with OwnedComment — the pair needed to give
        // Correlated_owned_collection_Any_nested_quantifier_source_is_declined teeth (a differently-named nav,
        // or an element without a matching scalar name, would decline for an unrelated reason).
        public List<OwnedTag> Comments { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    private class OwnedTag
    {
        public string Text { get; set; } = "";
    }

    private class OwnedAddress
    {
        public string City { get; set; } = "";
        public bool IsPrimary { get; set; }
        public OwnedGeo Geo { get; set; } = null!;
        public List<OwnedNote> Notes { get; set; } = [];
    }

    private class OwnedGeo
    {
        public string Country { get; set; } = "";
    }

    private class OwnedPost
    {
        public string Heading { get; set; } = "";
        // Title/IsActive DELIBERATELY COLLIDE with OwnedBlog.Title/OwnedBlog.IsActive (same name, same CLR
        // type). Without a colliding name the correlated-element-predicate guard cannot be exercised at all:
        // the element-scoped translator resolves members by NAME, so an enclosing-scoped `b.Title` only
        // mis-resolves — instead of declining for the unrelated reason "no such property on the element" —
        // when the element declares the same name too. See the Correlated_* tests below.
        public string Title { get; set; } = "";
        public bool IsActive { get; set; }
        public int Rank { get; set; }
        public int Other { get; set; }
        public OwnedGeo Geo { get; set; } = null!;
        public List<OwnedComment> Comments { get; set; } = [];
    }

    private class OwnedComment
    {
        public string Text { get; set; } = "";
    }

    private class OwnedNote
    {
        public string Body { get; set; } = "";
    }

    private static IEntityType GetOwnedBlogEntityType()
    {
        using var db = SingleEntityDbContext.Create<OwnedBlog>(mb =>
        {
            mb.Entity<OwnedBlog>().OwnsOne(b => b.Address, a =>
            {
                a.OwnsOne(x => x.Geo);
                a.OwnsMany(x => x.Notes);
            });
            mb.Entity<OwnedBlog>().OwnsMany(b => b.Posts, p =>
            {
                p.OwnsOne(x => x.Geo);
                p.OwnsMany(x => x.Comments);
            });
            mb.Entity<OwnedBlog>().OwnsMany(b => b.Comments);
        });
        return db.Model.FindEntityType(typeof(OwnedBlog))!;
    }

    // b => b.Address.City style selectors (value-type members get a Convert-to-object wrapper; strip it).
    private static Expression FieldBody<T>(Expression<Func<T, object?>> selector)
        => selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : selector.Body;

    /// <summary>
    /// Returns the entity type for <typeparamref name="T"/> from a minimal in-memory model.
    /// </summary>
    private static IEntityType GetEntityType<T>() where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        // We need the model to stay alive for the test — grab the entity type from the model directly.
        return db.Model.FindEntityType(typeof(T))!;
    }

    /// <summary>
    /// Creates a <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    private static MongoExpressionTranslator NewTranslator(IEntityType entityType)
        => new(entityType);

    /// <summary>
    /// Extracts the body of a predicate lambda as a raw <see cref="Expression"/>.
    /// </summary>
    private static Expression PredicateBody<T>(Expression<Func<T, bool>> predicate)
        => predicate.Body;

    /// <summary>
    /// Builds a <see cref="MongoExpressionTranslator"/> over a fresh <see cref="Order"/> model (with
    /// <see cref="Order.EncStatus"/> and <see cref="Order.EncWeight"/> each configured with a value converter,
    /// for guard-B coverage) and extracts the body of a numeric value-selector lambda, for
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> tests.
    /// </summary>
    private static (MongoExpressionTranslator Translator, Expression Body) BuildValueBody<T>(
        Expression<Func<T, object>> valueSelector) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>(mb =>
        {
            if (typeof(T) == typeof(Order))
            {
                mb.Entity<Order>().Property(o => o.EncStatus).HasConversion(v => v * 2, v => v / 2);
                mb.Entity<Order>().Property(o => o.EncWeight).HasConversion(v => v * 2, v => v / 2);
            }
        });
        var entityType = db.Model.FindEntityType(typeof(T))!;
        // Value-selector lambdas returning a numeric type get an implicit Convert-to-object wrapper —
        // unwrap it so the body matches what EF's own translation pipeline would hand the translator.
        var body = valueSelector.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : valueSelector.Body;
        return (new MongoExpressionTranslator(entityType), body);
    }

    // ------------------------------------------------------------------
    // Test 1: simple comparison → MongoBinaryExpression(GreaterThan, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_simple_comparison_to_field_op()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age > 21);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        var field = Assert.IsType<MongoFieldExpression>(bin.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(bin.Right);
        Assert.Equal(21, constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 2: conjunction → top-level MongoBinaryExpression(AndAlso, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Conjunction_maps_to_AndAlso()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age > 21 && c.Age < 65);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.AndAlso, bin.Operator);
        Assert.IsType<MongoBinaryExpression>(bin.Left);
        Assert.IsType<MongoBinaryExpression>(bin.Right);
    }

    // ------------------------------------------------------------------
    // Test 3: method call → returns false, result null
    // ------------------------------------------------------------------

    [Fact]
    public void Unsupported_method_call_reports_not_translatable()
    {
        // string.StartsWith/EndsWith/Contains became natively representable in EF-329 — use a genuinely
        // unsupported method call (ToUpper has no query-dialect equivalent) to keep this test meaningful.
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Name.ToUpper() == "A");
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 4: query parameter → MongoParameterExpression (B2 invariant)
    // Constructs a parameterized body by hand, mimicking what EF emits for
    // a captured local in `var minAge = 21; ctx.Set<Customer>().Where(c => c.Age > minAge)`.
    // ------------------------------------------------------------------

    [Fact]
    public void Query_parameter_becomes_MongoParameterExpression_not_constant()
    {
        var entityType = GetEntityType<Customer>();

        // Build: c.Age > <query-parameter>  — shape differs by EF version.
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        // EF8/EF9: query parameters are plain ParameterExpressions whose names start with the EF prefix.
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "minAge_0";
        Expression efParam = Expression.Parameter(typeof(int), paramName);
#else
        // EF10: query parameters are QueryParameterExpression nodes.
        const string paramName = "__minAge_0";
        Expression efParam = new Microsoft.EntityFrameworkCore.Query.QueryParameterExpression(paramName, typeof(int));
#endif
        var body = Expression.GreaterThan(ageMember, efParam);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        // The body should translate successfully …
        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        Assert.IsType<MongoFieldExpression>(bin.Left);
        // … and the right side must be a MongoParameterExpression, not a MongoConstantExpression.
        var mongoParam = Assert.IsType<MongoParameterExpression>(bin.Right);
        Assert.Equal(paramName, mongoParam.Name);
    }

    // ------------------------------------------------------------------
    // Test 5: bare boolean field → MongoFieldExpression (bool)
    // ------------------------------------------------------------------

    [Fact]
    public void Bare_boolean_field_translates_to_field_expression()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Active);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Active", field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 6: negated boolean field → MongoUnaryExpression(Not, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Negated_boolean_field_translates_to_Not_unary()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => !c.Active);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var unary = Assert.IsType<MongoUnaryExpression>(result);
        Assert.Equal(MongoUnaryOperator.Not, unary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(unary.Operand);
        Assert.Equal("Active", field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 7: OrElse → MongoBinaryExpression(OrElse, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void OrElse_maps_to_OrElse()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age < 18 || c.Age > 65);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.OrElse, bin.Operator);
    }

    // ------------------------------------------------------------------
    // Test 8: composite-PK property → returns false, result null
    // A property that is part of a composite primary key is stored under
    // "_id.<element>", which the native translator cannot address. It must
    // fall back to driver-LINQ rather than emit a $match against the wrong
    // top-level field.
    // ------------------------------------------------------------------

    private class OrderLine
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    // Composite-PK fixture: its key components are stored under "_id" and are not addressable by their own
    // top-level element names, which is what TryResolveMember's composite-PK guard declines.
    private class CompositeKeyed
    {
        public int KeyA { get; set; }
        public int KeyB { get; set; }
        public string Label { get; set; } = "";
    }

    [Fact]
    public void Composite_PK_property_access_resolves_natively()
    {
        // Build a model where (OrderId, ProductId) form the composite primary key.
        using var db = SingleEntityDbContext.Create<OrderLine>(mb =>
            mb.Entity<OrderLine>().HasKey(e => new { e.OrderId, e.ProductId }));
        var entityType = db.Model.FindEntityType(typeof(OrderLine))!;

        // A predicate over one of the composite-PK components should now resolve natively via _id.<name>.
        var body = PredicateBody<OrderLine>(ol => ol.OrderId == 10248);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        Assert.NotNull(result);
        Assert.IsType<MongoBinaryExpression>(result);
    }

    // ------------------------------------------------------------------
    // Test 9: nullable-property equality → MongoBinaryExpression(Equal, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_nullable_equality()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.NullableAge == 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
    }

    // ------------------------------------------------------------------
    // Test 10: `== null` → MongoBinaryExpression(Equal, field, null constant)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_is_null()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name == null;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Null(constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 11: `!= null` → MongoBinaryExpression(NotEqual, field, null constant)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_is_not_null()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.NullableAge != null;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.NotEqual, binary.Operator);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Null(constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 12: bare nullable-bool member access reports not translatable
    // (three-valued semantics the query dialect does not match — stays
    // out of scope for this task; must keep falling back to driver-LINQ).
    // ------------------------------------------------------------------

    [Fact]
    public void Bare_nullable_bool_field_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // Bare member access (c.NullableFlag) can't be expressed directly as a Func<Customer, bool>
        // lambda body (bool? has no implicit conversion to bool), so build the tree by hand — the
        // same shape EF would produce for a bare nullable-bool predicate member.
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var member = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.NullableFlag))!);

        var translated = translator.TryTranslate(member, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 13: `!c.NullableFlag` (Not over a bare nullable-bool member
    // access) reports not translatable — same three-valued-logic guard
    // as the bare member-access case, reached via the `!` fallback path
    // instead of a direct predicate. Must keep falling back to driver-LINQ.
    // ------------------------------------------------------------------

    [Fact]
    public void Not_over_nullable_bool_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // `!c.NullableFlag` can't be expressed directly as a Func<Customer, bool> lambda (bool? has no
        // implicit conversion to bool), so build the tree by hand as EF would produce it.
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var member = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.NullableFlag))!);
        var not = Expression.Not(member);

        var translated = translator.TryTranslate(not, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 14: static Enumerable.Contains over an inline array → MongoInExpression
    // ------------------------------------------------------------------

    [Fact]
    public void Static_enumerable_contains_over_inline_array_translates_to_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // Build ages.Contains(c.Age) by hand with an explicit ConstantExpression — a captured local
        // (e.g. a lambda-closure field) or an inline `new[] { .. }` literal both compile to expression
        // shapes (MemberExpression / NewArrayInit) other than ConstantExpression; this test targets the
        // ConstantExpression collection shape specifically.
        var ages = new[] { 1, 2, 3 };
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        var body = Expression.Call(containsMethod, Expression.Constant(ages), ageMember);

        Assert.True(translator.TryTranslate(body, out var result));
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("Age", inExpr.Field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(inExpr.Values);
        Assert.Same(ages, constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 15: instance List<T>.Contains → MongoInExpression
    // ------------------------------------------------------------------

    [Fact]
    public void Instance_list_contains_translates_to_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        var ages = new List<int> { 1, 2, 3 };
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);
        var containsMethod = typeof(List<int>).GetMethod(nameof(List<int>.Contains), [typeof(int)])!;
        var body = Expression.Call(Expression.Constant(ages), containsMethod, ageMember);

        Assert.True(translator.TryTranslate(body, out var result));
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("Age", inExpr.Field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 16: Contains over a query-parameter collection → MongoParameterExpression values
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_query_parameter_collection_translates_to_parameter_values()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "ages_0";
        Expression efParam = Expression.Parameter(typeof(int[]), paramName);
#else
        const string paramName = "__ages_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int[]));
#endif
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        var body = Expression.Call(containsMethod, efParam, ageMember);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        var parameter = Assert.IsType<MongoParameterExpression>(inExpr.Values);
        Assert.Equal(paramName, parameter.Name);
    }

    // ------------------------------------------------------------------
    // Test 17: negated Contains → MongoInExpression with Negated == true ($nin)
    // ------------------------------------------------------------------

    [Fact]
    public void Negated_contains_translates_to_negated_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        var ages = new[] { 1, 2, 3 };
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        var body = Expression.Not(Expression.Call(containsMethod, Expression.Constant(ages), ageMember));

        Assert.True(translator.TryTranslate(body, out var result));
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.True(inExpr.Negated);
        Assert.Equal("Age", inExpr.Field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 18: Contains with a non-field item argument → falls back (null)
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_with_non_field_item_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        var ages = new[] { 1, 2, 3 };
        Expression<Func<Customer, bool>> predicate = c => ages.Contains(c.Age + 1);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 18b (EF-329): inline array-literal Contains (NewArrayInit shape, as seen on EF8
    // where the compiler does not pre-fold `new[] { .. }.Contains(..)` into a ConstantExpression)
    // → MongoInExpression
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_new_array_init_translates_to_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // Build new[] { 10, 30 }.Contains(c.Age) by hand using NewArrayInit rather than a
        // ConstantExpression — this is the shape EF8 hands the translator for an inline array
        // literal (the compiler folds it into a ConstantExpression only from EF9/net9+ onward).
        var arrayExpr = Expression.NewArrayInit(typeof(int), Expression.Constant(10), Expression.Constant(30));
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        var body = Expression.Call(containsMethod, arrayExpr, ageMember);

        Assert.True(translator.TryTranslate(body, out var result));
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("Age", inExpr.Field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(inExpr.Values);
        var values = Assert.IsType<int[]>(constant.Value);
        Assert.Equal([10, 30], values);
    }

    // ------------------------------------------------------------------
    // Test 18c-18f (EF-382): arrayField.Contains(constant) — the MIRROR shape of the $in arm above (there
    // the ITEM resolves to a field and the collection is a client-side set of values; here the RECEIVER is a
    // genuine stored array FIELD and the item is a single constant value). MongoDB's implicit array-element
    // match ({ field: value }) expresses this natively → MongoArrayContainsExpression, never $in.
    // ------------------------------------------------------------------

    [Fact]
    public void Array_field_contains_constant_translates_to_array_contains_expression()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var body = PredicateBody<OwnedBlog>(b => b.Tags.Contains("keep"));

        Assert.True(translator.TryTranslate(body, out var result));
        var containsExpr = Assert.IsType<MongoArrayContainsExpression>(result);
        Assert.False(containsExpr.Negated);
        Assert.Equal("Tags", containsExpr.Field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(containsExpr.Value);
        Assert.Null(constant.ForSerialization); // eagerly rendered to a BsonValue at translate time
        Assert.Equal(new BsonString("keep"), constant.Value);
    }

    [Fact]
    public void Negated_array_field_contains_constant_translates_to_negated_array_contains()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var body = PredicateBody<OwnedBlog>(b => !b.Tags.Contains("keep"));

        Assert.True(translator.TryTranslate(body, out var result));
        var containsExpr = Assert.IsType<MongoArrayContainsExpression>(result);
        Assert.True(containsExpr.Negated);
        Assert.Equal("Tags", containsExpr.Field.ElementName);
    }

    // Item is a FIELD, not a constant — this is the AMBIGUOUS double-field shape (neither the new arm, which
    // requires a constant item, nor the $in arm's own TranslateInValues, which requires a constant/parameter/
    // array-literal collection, can represent it): declines exactly as it did before EF-382, not a regression.
    [Fact]
    public void Array_field_contains_field_item_still_declines()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var body = PredicateBody<OwnedBlog>(b => b.Tags.Contains(b.Title));

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // The correctness guard: a WHOLE-COLLECTION value converter has no per-element serializer
    // (IBsonArraySerializer.TryGetItemSerializationInfo is unavailable on the resulting
    // ValueConverterSerializer<,>), so this declines rather than risk comparing the constant against the
    // wrong (whole-list) shape — see TranslateArrayContainsItem's remarks.
    [Fact]
    public void Array_field_contains_over_whole_collection_value_converted_property_declines()
    {
        using var db = SingleEntityDbContext.Create<ConvertedListEntity>(mb =>
            mb.Entity<ConvertedListEntity>().Property(e => e.Ratings)
                .HasConversion(
                    v => v.Select(x => x.ToString()).ToList(),
                    v => v.Select(x => (Tier)Enum.Parse(typeof(Tier), x)).ToList()));
        var entityType = db.Model.FindEntityType(typeof(ConvertedListEntity))!;
        var translator = NewTranslator(entityType);
        var body = PredicateBody<ConvertedListEntity>(e => e.Ratings.Contains(Tier.Gold));

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    private class ConvertedListEntity
    {
        public ObjectId Id { get; set; }
        public List<Tier> Ratings { get; set; } = [];
    }

    // ------------------------------------------------------------------
    // Test 19-23: string.StartsWith/EndsWith/Contains (EF-329) → MongoRegexExpression
    // ------------------------------------------------------------------

    [Fact]
    public void StartsWith_translates_to_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith("A");

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal("Name", regex.Field.ElementName);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
        Assert.False(regex.Negated);
        var constant = Assert.IsType<MongoConstantExpression>(regex.Term);
        Assert.Equal("A", constant.Value);
    }

    [Fact]
    public void EndsWith_translates_to_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.EndsWith("Z");

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.EndsWith, regex.Kind);
    }

    [Fact]
    public void Contains_on_string_translates_to_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.Contains("x");

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Contains, regex.Kind);
    }

    [Fact]
    public void Negated_starts_with_translates_to_negated_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => !c.Name.StartsWith("A");

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.True(regex.Negated);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
    }

    [Fact]
    public void Parameterized_starts_with_term_translates_to_parameter_expression()
    {
        // Simulate an EF-processed query parameter (as EF Core would substitute for a captured local),
        // mirroring Contains_over_query_parameter_collection_translates_to_parameter_values above — a
        // plain hand-built Expression<Func<>> captures locals as closure-field access, not a real
        // EF query-parameter node, so it must be built explicitly here.
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var nameMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Name))!);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "term_0";
        Expression efParam = Expression.Parameter(typeof(string), paramName);
#else
        const string paramName = "__term_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(string));
#endif
        var startsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        var body = Expression.Call(nameMember, startsWithMethod, efParam);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var regex = Assert.IsType<MongoRegexExpression>(result);
        var parameter = Assert.IsType<MongoParameterExpression>(regex.Term);
        Assert.Equal(paramName, parameter.Name);
    }

    [Fact]
    public void StartsWith_with_string_comparison_overload_reports_not_translatable()
    {
        // The driver-LINQ v3 provider does not support the StringComparison-taking overloads (confirmed
        // empirically — see Task 6 report); matching only the plain single-arg overload keeps native and
        // fallback behavior identical, so this shape must fall back rather than be mistranslated.
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith("A", StringComparison.Ordinal);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 24-31: field-to-field and arithmetic-operand comparisons (EF-329) → $expr-shaped trees
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_field_to_field_comparison()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age == c.Score;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var left = Assert.IsType<MongoFieldExpression>(binary.Left);
        var right = Assert.IsType<MongoFieldExpression>(binary.Right);
        Assert.Equal("Age", left.ElementName);
        Assert.Equal("Score", right.ElementName);
    }

    [Fact]
    public void Translates_arithmetic_operand()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age + c.Score > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);
        var add = Assert.IsType<MongoBinaryExpression>(cmp.Left); // the $add subtree
        Assert.Equal(MongoBinaryOperator.Add, add.Operator);
        Assert.IsType<MongoFieldExpression>(add.Left);
        Assert.IsType<MongoFieldExpression>(add.Right);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Equal(5, constant.Value);
    }

    [Fact]
    public void Translates_subtract_operand()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age - c.Score > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var sub = Assert.IsType<MongoBinaryExpression>(cmp.Left);
        Assert.Equal(MongoBinaryOperator.Subtract, sub.Operator);
    }

    [Fact]
    public void Translates_multiply_operand()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age * c.Score > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var mul = Assert.IsType<MongoBinaryExpression>(cmp.Left);
        Assert.Equal(MongoBinaryOperator.Multiply, mul.Operator);
    }

    // Modulo still maps straight through: the driver's own LINQ translator emits a raw $mod for int operands
    // with no dividend-sign emulation, and so does this translator. DIVISION no longer matches driver-LINQ —
    // EF-434 gave it C#'s truncating semantics (MongoBinaryOperator.IntegerDivide → $trunc of $divide) on the
    // grounds that agreeing with C# beats agreeing with a driver-LINQ shape that is simply wrong for an
    // integral result; the emitted MQL of a supported query is not contract (see the top-level AGENTS.md
    // versioning rubric).

    [Fact]
    public void Translates_integral_divide_operand_as_IntegerDivide()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age / c.Score > 1;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var div = Assert.IsType<MongoBinaryExpression>(cmp.Left);
        Assert.Equal(MongoBinaryOperator.IntegerDivide, div.Operator);
    }

    [Fact]
    public void Translates_non_integral_divide_operand_as_plain_Divide()
    {
        // THE DISCRIMINATOR for where the integral-ness decision is made. Both operands are still int-typed
        // MongoFieldExpressions after TranslateOperand unwraps the widening Convert, so a renderer-side
        // "are the operands integral?" rule would wrongly truncate this. Only the BinaryExpression's own
        // result type (double) tells the two apart, which is why MapArithmeticOperator takes the node.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age / c.Score > 1;

        Assert.True(translator.TryTranslateValue(
            ((BinaryExpression)predicate.Body).Left, out var value));
        var div = Assert.IsType<MongoBinaryExpression>(value);
        Assert.Equal(MongoBinaryOperator.Divide, div.Operator);
    }

    [Fact]
    public void Translates_modulo_operand()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age % c.Score == 1;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var mod = Assert.IsType<MongoBinaryExpression>(cmp.Left);
        Assert.Equal(MongoBinaryOperator.Modulo, mod.Operator);
    }

    // Compiler-generated string.Concat (ExpressionType.Add on string operands) must NOT be treated as
    // arithmetic $add — the IsNumericType guard on the arithmetic operand branch of TranslateOperand
    // excludes it. It now translates via a dedicated MongoConcatExpression ($concat) branch instead of
    // declining, matching the arithmetic-operand comparison shapes elsewhere in this file (e.g.
    // Translates_modulo_operand).

    [Fact]
    public void String_concatenation_operand_translates_to_concat_in_comparison_position()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Name + "y" == "Zed";

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);
        var concat = Assert.IsType<MongoConcatExpression>(cmp.Left);
        Assert.Equal(2, concat.Operands.Count);
    }

    // EF-322 slice A1, Task 3: a numeric cast inside a field-to-field/arithmetic-operand comparison NOW
    // TRANSLATES — TranslateOperand's Convert branch renders a type-changing cast to a renderable target as
    // an explicit MongoConvertExpression ($toX) rather than declining, matching the shape the driver's own
    // LINQ translator renders in this exact position (spike §3.1, P05/P14). Values agree regardless — $add
    // et al. operate on the raw BSON numeric value, so an explicit $toDouble changes nothing about the
    // arithmetic result. These two used to assert "reports_not_translatable"; they now assert the converted
    // shape. See NativeCastTests (functional) for the end-to-end Native == DriverLinq == CLR proof.

    [Fact]
    public void Cast_in_field_to_field_comparison_translates_to_a_convert_node()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age > c.Score;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);
        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        Assert.Equal(typeof(double), convert.Type);
        Assert.IsType<MongoFieldExpression>(convert.Operand);
        // c.Score (int) also implicitly widens to double to compare against the explicitly-cast left side.
        var rightConvert = Assert.IsType<MongoConvertExpression>(cmp.Right);
        Assert.Equal(typeof(double), rightConvert.Type);
        Assert.IsType<MongoFieldExpression>(rightConvert.Operand);
    }

    [Fact]
    public void Cast_in_arithmetic_operand_translates_to_a_convert_node()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age + c.Score > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);
        var add = Assert.IsType<MongoBinaryExpression>(cmp.Left);
        Assert.Equal(MongoBinaryOperator.Add, add.Operator);
        // Both operands widen to double for the +, so both sides are explicit converts — same depth of
        // assertion (Type plus operand kind) as the field-to-field sibling above.
        var leftConvert = Assert.IsType<MongoConvertExpression>(add.Left);
        Assert.Equal(typeof(double), leftConvert.Type);
        Assert.IsType<MongoFieldExpression>(leftConvert.Operand);
        var rightConvert = Assert.IsType<MongoConvertExpression>(add.Right);
        Assert.Equal(typeof(double), rightConvert.Type);
        Assert.IsType<MongoFieldExpression>(rightConvert.Operand);
        Assert.IsType<MongoConstantExpression>(cmp.Right);
    }

    // EF-403 (slice A1, Task 4) re-baselines the tripwire that used to sit here,
    // `Cast_on_member_vs_constant_still_reports_not_translatable`, into the four cases below. The
    // query-native member-vs-constant cast guard (HasNumericConvert) no longer VETOES every cast — it
    // CLASSIFIES, tolerating a widening numeric layer whose target MQL can express, and still declining
    // everything else. The tripwire was written for EF-329, which deliberately did not move this guard;
    // this task is the one that does.

    [Fact]
    public void Widening_cast_on_member_vs_constant_now_translates_with_the_constant_in_the_comparison_type()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age > 5.0;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);

        // The widening layer is ABSORBED, not rendered: the field ref is the plain stored field, exactly as
        // for a bare `c.Age > 5.0`, and NOT a MongoConvertExpression. That is what keeps the comparison in the
        // indexable query dialect (a $toDouble would force $expr) and what makes the emitted MQL identical to
        // the driver's, which drops a widening cast on this path too.
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);

        // The load-bearing half: the constant carries NO serialization context, so it renders in the
        // COMPARISON's type (double) rather than being coerced back to the stored int. With the property
        // attached, a fractional constant would be TRUNCATED and the query would return wrong rows.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.0, constant.Value);
    }

    [Fact]
    public void Widening_cast_with_the_member_on_the_RIGHT_also_serializes_the_constant_in_the_comparison_type()
    {
        // TranslateComparison has TWO query-native branches — member-on-left and the MIRRORED member-on-right —
        // each with its own HasNumericConvert call and its own TranslateValue call site. Every other case for
        // this task puts the member on the left, so without this the mirrored branch's constant rule would be
        // revertible with nothing red. The shape is reachable: the provider carries `Mirror` precisely because
        // EF does not normalise operand order.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => 5.0 < (double)c.Age;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        // Mirrored: `5.0 < (double)c.Age` becomes `c.Age > 5.0` with the field on Left.
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.0, constant.Value);
    }

    [Fact]
    public void Widening_cast_under_a_nullable_lift_on_member_vs_constant_now_translates()
    {
        // The shape EF actually produces for a nullable closure comparison:
        // Convert(Convert(c.Age, Int64), Nullable<Int64>) > <value>. The OUTER layer changes nothing but
        // nullability, so it must be SKIPPED rather than classified — `from == to` there, so a widening test
        // applied to it would answer false and decline the whole comparison. MEASURED: this is exactly what
        // separated 16 converted specification cases from the spike's predicted 18
        // (NorthwindWhereQueryMongoTest.Where_method_call_nullable_type_reverse_closure_via_query_cache).
        // The threshold is written as a LITERAL, not a captured local: a captured local compiles to a closure
        // MemberExpression, which IsSimpleValue rejects (only a ConstantExpression or an EF query parameter is
        // a "simple value"), so the comparison would decline for a reason that has nothing to do with the cast.
        // In the specification suite the same slot is an EF query parameter, which IS simple.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (long?)c.Age > (long?)5L;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
    }

    // EF-403 (slice A1, Task 7) — RENAMED from `Narrowing_cast_on_member_vs_constant_still_reports_not_
    // translatable`, and the assertions inverted, because this shape's disposition is exactly what Task 7
    // changes. HasNumericConvert still DECLINES the narrowing cast (its three-outcome classification is
    // untouched); what changed is what that decline LANDS ON — the whole comparison used to be vetoed
    // (`return null`), and now only the query-native BRANCH declines and control falls through to the general
    // $expr path.
    //
    // The node shape is the discriminating part, not merely "it translated". Absorption (the bug this must
    // never become) would produce a bare MongoFieldExpression on the left with the constant carrying the
    // property's own serializer — i.e. the raw stored value compared untruncated, silently answering a
    // different question. The fall-through produces a MongoConvertExpression WRAPPING that field, and a
    // constant with NO serialization context (the $expr path serializes via BsonValue.Create).
    [Fact]
    public void Narrowing_cast_on_member_vs_constant_falls_through_to_the_expr_path()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.DoubleScore > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);

        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        Assert.Equal(typeof(int), convert.Type);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("DoubleScore", field.ElementName);

        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
    }

    // The MIRRORED operand order reaches the SAME fall-through, through the second of TranslateComparison's
    // two classification sites. The $expr path deliberately does NOT mirror the operator, so unlike the
    // query-native branch the constant stays on the LEFT — that asymmetry is the thing worth pinning here.
    [Fact]
    public void Mirrored_narrowing_cast_on_constant_vs_member_falls_through_to_the_expr_path()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => 5 < (int)c.DoubleScore;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.LessThan, cmp.Operator); // NOT mirrored to GreaterThan

        var constant = Assert.IsType<MongoConstantExpression>(cmp.Left);
        Assert.Null(constant.ForSerialization);

        var convert = Assert.IsType<MongoConvertExpression>(cmp.Right);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("DoubleScore", field.ElementName);
    }

    [Fact]
    public void Widening_cast_to_an_unrenderable_target_on_member_vs_constant_still_reports_not_translatable()
    {
        // int -> float IS a widening conversion, but MQL has no $toFloat, so
        // MongoConvertExpression.ToOperatorFor declines it — the guard consults that single definition of the
        // admissible set rather than a second hand-rolled list. Without that conjunct this shape would be
        // tolerated on the strength of the widening test alone.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (float)c.Age > 5.0f;

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void Widening_cast_over_a_value_converted_property_keeps_the_property_serializer()
    {
        // The conjunct the spike left UNVERIFIED (§6.2, §11): HasDefaultKeySerialization, not "the property's
        // CLR type is not an enum". EncStatus is a plain int (so "not an enum" holds) carried through a value
        // converter (so HasDefaultKeySerialization does NOT), and the two rules disagree — the shipped one
        // keeps the property serializer, which is the only thing that renders the constant in the STORED form.
        var (translator, body) = BuildOrderPredicateBody(o => (long)o.EncStatus > 5L);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("EncStatus", constant.ForSerialization!.Name);
    }

    /// <summary>
    /// Builds a translator over the same value-converted <see cref="Order"/> model
    /// <see cref="BuildValueBody{T}"/> uses, but for a PREDICATE lambda.
    /// </summary>
    private static (MongoExpressionTranslator Translator, Expression Body) BuildOrderPredicateBody(
        Expression<Func<Order, bool>> predicate)
    {
        using var db = SingleEntityDbContext.Create<Order>(mb =>
        {
            mb.Entity<Order>().Property(o => o.EncStatus).HasConversion(v => v * 2, v => v / 2);
            mb.Entity<Order>().Property(o => o.EncWeight).HasConversion(v => v * 2, v => v / 2);
        });
        var entityType = db.Model.FindEntityType(typeof(Order))!;
        return (new MongoExpressionTranslator(entityType), predicate.Body);
    }

    // EF-403 (slice A1, Task 7) — GUARD B on the site-B fall-through. A cast the query-native branch cannot
    // absorb now falls through to the $expr path, but ONLY when the member's property is default-serialized:
    // the $expr path renders `{$toInt: "$EncWeight"}` over the RAW STORED value, which for a value-converted
    // property is not the value the comparison is about. MEASURED end to end (see
    // NativeCastTests.Narrowing_cast_comparison_over_a_value_converted_property_still_declines): without the
    // guard the shape returned one row where zero is correct, silently, under the DEFAULT Native mode.
    //
    // Asserting `!translated` here is the whole point — this shape must keep the PRE-Task-7 disposition (veto
    // the comparison, fall back to driver-LINQ), not merely avoid absorption.
    [Fact]
    public void Narrowing_cast_over_a_value_converted_property_does_not_fall_through_to_expr()
    {
        var (translator, body) = BuildOrderPredicateBody(o => (int)o.EncWeight > 3);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // The control that stops the guard above from being read as "any cast over any Order property declines":
    // Weight is the SAME CLR type (double) on the SAME entity, with NO converter, and it DOES fall through.
    [Fact]
    public void Narrowing_cast_over_a_default_serialized_property_on_the_same_entity_still_falls_through()
    {
        var (translator, body) = BuildOrderPredicateBody(o => (int)o.Weight > 3);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("Weight", field.ElementName);
    }

    // EF-403 (slice A1, Task 5) — the IDENTITY-LIKE arm. HasNumericConvert now tolerates a SECOND family of
    // member-side converts (enum ↔ underlying, char -> int, boxing to object), reported separately from the
    // widening-numeric arm above because the two demand OPPOSITE constant treatment (ConstantSerializationContext).

    [Fact]
    public void Enum_to_underlying_convert_on_member_vs_constant_translates_and_keeps_the_field_unconverted()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.Level == (int)Tier.Gold;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);

        // The identity-like layer is ABSORBED, not rendered: the field ref is the plain stored field, exactly
        // as for a bare `c.Level == Tier.Gold` — never a MongoConvertExpression.
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Level", field.ElementName);

        // The load-bearing half of THIS arm (the opposite of the widening arm's rule): the constant KEEPS the
        // property's own serializer, because this is the SAME stored value under a different declared CLR
        // type — not a comparison moved into a different type.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Level", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Underlying_to_enum_convert_on_member_vs_constant_translates()
    {
        // The symmetric direction — IsIdentityLikeConvert admits BOTH `enum -> underlying` and
        // `underlying -> enum`, not just the one the enum-as-string spec fixture happens to exercise.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (Tier)c.Age == Tier.Silver;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Age", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Char_to_int_convert_on_member_vs_constant_translates()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.Grade == 65;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Grade", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Grade", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Boxing_convert_to_object_on_member_vs_constant_translates()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (object)c.Age == (object)5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Age", constant.ForSerialization!.Name);
        Assert.Equal(5, constant.Value);
    }

    [Fact]
    public void Boxing_convert_with_the_member_on_the_RIGHT_also_keeps_the_property_serializer()
    {
        // The mirrored branch has its own separate HasNumericConvert call and its own separate
        // ConstantSerializationContext call site — without this, reverting only that branch to the widening
        // treatment would be revertible with nothing red.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (object)5 == (object)c.Age;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Age", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Identity_like_convert_over_a_value_converted_property_also_keeps_the_property_serializer()
    {
        // The identity-like arm's rule is UNCONDITIONAL (unlike the widening arm, which only keeps the
        // property serializer when HasDefaultKeySerialization is false) — it always keeps the property
        // serializer, so a value-converted property behind an identity-like convert must still render through
        // its own converter/serializer, not the raw boxed value.
        var (translator, body) = BuildOrderPredicateBody(o => (object)o.EncStatus == (object)5);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("EncStatus", constant.ForSerialization!.Name);
    }

    // EF-403 (slice A1, Task 5) FIX ROUND 1 — the enum-promotion gap the spec-suite measurement found.
    // C# promotes a SUB-int-backed (short/byte/ushort/sbyte) enum's equality comparison to Int32, which is a
    // WIDENING of the enum's own underlying type, not an exact match to it. Every enum fixture added before
    // this fix was Int32-backed and so could never expose the gap.

    [Fact]
    public void Short_backed_enum_comparison_promoted_to_int_by_the_compiler_still_translates()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.ShortLevel == ShortTier.Gold;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);

        // Absorbed, not rendered: the field ref is the plain stored field, exactly as for a bare
        // `c.ShortLevel == ShortTier.Gold` — never a MongoConvertExpression.
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("ShortLevel", field.ElementName);

        // Identity-like, not widening-numeric: the constant keeps the property's own serializer.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("ShortLevel", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Long_backed_enum_narrowed_to_int_is_not_absorbed_as_identity_like()
    {
        // Regression pin for the boundary the identity-like arm must NOT cross: a LONG-backed enum's
        // underlying type (Int64) narrowed down to Int32 is a genuine narrowing, not a promotion —
        // IsWideningNumericConvert is directional (narrower -> wider only) and must never admit this shape.
        //
        // EF-403 (slice A1, Task 7) — RENAMED from `Long_backed_enum_narrowed_to_int_still_declines`, and the
        // assertion changed from "does not translate" to "does not ABSORB", because Task 7 changed what the
        // decline lands on: the comparison is no longer vetoed, it falls through to the $expr path and renders
        // the cast explicitly as $toInt. THE DISCRIMINATION IS UNCHANGED IN STRENGTH — it just moved from the
        // translated/not-translated axis to the node-shape axis. If IsIdentityLikeConvert wrongly admitted
        // Int64 -> Int32, HasNumericConvert would return FALSE, the query-native branch would be taken, and
        // cmp.Left would be a bare MongoFieldExpression (the raw stored long compared untruncated, with the
        // constant carrying the enum property's own serializer) — which is what this test now rejects.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.LongLevel == (int)LongTier.Gold;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        Assert.Equal(typeof(int), convert.Type);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("LongLevel", field.ElementName);

        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
    }

    // EF-403 (slice A1, Task 5) FIX ROUND 1 — the flag-precedence fix. A single Convert CHAIN can set both
    // toleratedWideningTarget (from an inner widening layer) AND identity-like (from an outer boxing layer);
    // the widening arm must win, or an identity-like layer merely wrapping a widening one would mask the
    // truncation protection case 9 exists to pin.

    [Fact]
    public void Boxing_over_a_widening_cast_lets_the_widening_arm_win_precedence()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (object)(double)c.Age == (object)5.5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);

        // The DISCRIMINATING assertion is ForSerialization, not Value. Truncation happens later, at render
        // time, in MongoValueRenderer.ToBsonValue -> BsonValueSerializer.Coerce(int, 5.5) -> 6 — constant.Value
        // itself stays 5.5 either way at THIS (translation) layer, so asserting it does not by itself
        // discriminate the two arms (fix round 2 correction: an earlier version of this test called it "the
        // load-bearing assertion", which is wrong). Had the identity-like (boxing) layer wrongly won,
        // ForSerialization would be non-null (Age's own serializer), which is what drives that truncation at
        // render time — case 9's silent-wrong-rows shape end to end. See
        // NativeCastTests.Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row for the
        // ROWS-level functional pin of the render-time consequence this unit test cannot reach.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.5, constant.Value);
    }

    [Fact]
    public void Boxing_over_a_widening_cast_with_the_member_on_the_RIGHT_also_lets_widening_win()
    {
        // The mirrored branch has its own separate HasNumericConvert call and its own separate
        // ConstantSerializationContext call site — pinned in both directions, per the fix-round instruction.
        // As with the sibling test above, ForSerialization is the discriminating assertion; Value is not.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (object)5.5 == (object)(double)c.Age;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.5, constant.Value);
    }

    // A plain field-vs-constant comparison (no arithmetic, no field-to-field) must still translate via
    // the SP1 query-native path — field on Left, mirrored if necessary — so the renderer keeps routing it
    // to $match, not $expr. This guards against regressing SP1 shapes while broadening acceptance.

    [Fact]
    public void Field_vs_constant_still_translates_to_query_native_shape_with_field_on_left()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => 21 < c.Age; // constant on the left in source

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        // Mirrored: `21 < c.Age` becomes `c.Age > 21` with the field on Left.
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        var field = Assert.IsType<MongoFieldExpression>(bin.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(bin.Right);
        Assert.Equal(21, constant.Value);
    }

    // ------------------------------------------------------------------
    // Two-scope (correlated) resolution
    // ------------------------------------------------------------------

    [Fact]
    public void Two_scope_correlated_comparison_routes_inner_prefixed_and_outer_root()
    {
        var innerType = GetEntityType<InnerRef>();
        var outerType = GetEntityType<OuterRef>();
        var outerParam = Expression.Parameter(typeof(OuterRef), "o");
        var innerParam = Expression.Parameter(typeof(InnerRef), "r");
        // r.Tag == o.Name
        var body = Expression.Equal(
            Expression.Property(innerParam, nameof(InnerRef.Tag)),
            Expression.Property(outerParam, nameof(OuterRef.Name)));
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.Equal("_lookup_Refs.Tag", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    [Fact]
    public void Two_scope_shadowed_member_name_resolves_by_parameter_identity_not_name()
    {
        var innerType = GetEntityType<InnerRef>();
        var outerType = GetEntityType<OuterRef>();
        var outerParam = Expression.Parameter(typeof(OuterRef), "o");
        var innerParam = Expression.Parameter(typeof(InnerRef), "r");
        // r.Name == o.Name — same member name on both scopes; must resolve to DISTINCT field refs.
        var body = Expression.Equal(
            Expression.Property(innerParam, nameof(InnerRef.Name)),
            Expression.Property(outerParam, nameof(OuterRef.Name)));
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("_lookup_Refs.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    [Fact]
    public void Two_scope_inner_only_conjunct_still_gets_the_inner_prefix()
    {
        var innerType = GetEntityType<InnerRef>();
        var outerType = GetEntityType<OuterRef>();
        var outerParam = Expression.Parameter(typeof(OuterRef), "o");
        var innerParam = Expression.Parameter(typeof(InnerRef), "r");
        // r.Tag == "x" — no outer reference; in two-scope mode the inner field is still prefixed.
        var body = Expression.Equal(
            Expression.Property(innerParam, nameof(InnerRef.Tag)),
            Expression.Constant("x"));
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("_lookup_Refs.Tag", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.IsType<MongoConstantExpression>(bin.Right);
    }

    [Fact]
    public void Two_scope_numeric_correlated_comparison_translates()
    {
        var innerType = GetEntityType<InnerRef>();
        var outerType = GetEntityType<OuterRef>();
        var outerParam = Expression.Parameter(typeof(OuterRef), "o");
        var innerParam = Expression.Parameter(typeof(InnerRef), "r");
        // r.Score >= o.Threshold — proves the full comparison breadth flows through field-to-field.
        var body = Expression.GreaterThanOrEqual(
            Expression.Property(innerParam, nameof(InnerRef.Score)),
            Expression.Property(outerParam, nameof(OuterRef.Threshold)));
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThanOrEqual, bin.Operator);
        Assert.Equal("_lookup_Refs.Score", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Threshold", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    // ------------------------------------------------------------------
    // TryTranslateValue: numeric computed-leaf VALUE expressions (EF-347 Task 2)
    // ------------------------------------------------------------------

    [Fact]
    public void TryTranslateValue_multiply_of_two_int_fields_translates_to_binary_multiply()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Price * o.Qty); // int * int
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var binary = Assert.IsType<MongoBinaryExpression>(expr);
        Assert.Equal(MongoBinaryOperator.Multiply, binary.Operator);
    }

    [Fact]
    public void TryTranslateValue_subtract_translates()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Gross - o.Tax);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        Assert.Equal(MongoBinaryOperator.Subtract, Assert.IsType<MongoBinaryExpression>(expr).Operator);
    }

    [Fact]
    public void TryTranslateValue_integer_division_translates_to_integer_divide() // EF-434: guard A removed
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Price / o.Qty); // int / int
        Assert.True(translator.TryTranslateValue(body, out var expr));
        Assert.Equal(MongoBinaryOperator.IntegerDivide, Assert.IsType<MongoBinaryExpression>(expr).Operator);
    }

    [Fact]
    public void TryTranslateValue_floating_division_is_accepted()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Weight / o.Count); // double / int -> double
        Assert.True(translator.TryTranslateValue(body, out var expr));
        Assert.Equal(MongoBinaryOperator.Divide, Assert.IsType<MongoBinaryExpression>(expr).Operator);
    }

    [Fact]
    public void TryTranslateValue_string_concat_translates_to_MongoConcatExpression()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + "!");
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        Assert.Equal(2, concat.Operands.Count);
        Assert.IsType<MongoFieldExpression>(concat.Operands[0]);
        Assert.IsType<MongoConstantExpression>(concat.Operands[1]);
    }

    [Fact]
    public void TryTranslateValue_int_string_concat_wraps_the_int_operand_in_ToString()
    {
        // o.Tag + o.Price compiles to string.Concat(o.Tag, (object)o.Price) — the int operand arrives
        // boxed (Convert to object), matching C#'s string operator +(string, object) overload.
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + o.Price);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        Assert.Equal(2, concat.Operands.Count);
        Assert.IsType<MongoFieldExpression>(concat.Operands[0]);
        var converted = Assert.IsType<MongoConvertExpression>(concat.Operands[1]);
        Assert.Equal(typeof(string), converted.Type);
        Assert.IsType<MongoFieldExpression>(converted.Operand);
    }

    [Fact]
    public void TryTranslateValue_string_int_concat_reversed_order_wraps_the_int_operand_in_ToString()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Price + o.Tag);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        Assert.Equal(2, concat.Operands.Count);
        var converted = Assert.IsType<MongoConvertExpression>(concat.Operands[0]);
        Assert.Equal(typeof(string), converted.Type);
        Assert.IsType<MongoFieldExpression>(concat.Operands[1]);
    }

    [Fact]
    public void TryTranslateValue_three_way_string_concat_flattens_into_one_MongoConcatExpression()
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + o.Price + o.Qty);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        Assert.Equal(3, concat.Operands.Count);
    }

    [Fact]
    public void TryTranslateValue_string_concat_with_value_converted_operand_is_rejected() // guard B
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + o.EncStatus);
        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void TryTranslateValue_string_concat_with_bool_operand_translates() // accepted divergence, matches driver-LINQ — see NativeStringConcatTests
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + o.Flag);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        var converted = Assert.IsType<MongoConvertExpression>(concat.Operands[1]);
        Assert.Equal(typeof(string), converted.Type);
    }

    [Fact]
    public void TryTranslateValue_string_concat_with_DateTime_operand_translates() // accepted divergence, matches driver-LINQ — see NativeStringConcatTests
    {
        var (translator, body) = BuildValueBody<Order>(o => o.Tag + o.When);
        Assert.True(translator.TryTranslateValue(body, out var expr));
        var concat = Assert.IsType<MongoConcatExpression>(expr);
        var converted = Assert.IsType<MongoConvertExpression>(concat.Operands[1]);
        Assert.Equal(typeof(string), converted.Type);
    }

    [Fact]
    public void TryTranslateValue_value_converted_operand_is_rejected() // guard B
    {
        // Order.EncStatus is configured with a value converter in BuildValueBody's model builder.
        var (translator, body) = BuildValueBody<Order>(o => o.EncStatus + o.Qty);
        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void TryTranslateValue_top_level_narrowing_cast_renders_as_an_explicit_convert()
    {
        // (int)o.Weight is a double->int TRUNCATING cast at the very top of the value body. MongoDB has no
        // truncating-cast equivalent, so silently STRIPPING it would return the raw double — a wrong-data bug.
        // TryTranslateValue must NOT Unwrap the top-level node, so the narrowing-aware Convert branch used to
        // reject it outright. EF-322 slice A1, Task 3: since int IS a renderable $toX target, this now
        // translates to an explicit MongoConvertExpression instead of declining — the cast is preserved by
        // being RENDERED ($toInt), not by being dropped.
        var (translator, body) = BuildValueBody<Order>(o => (int)o.Weight);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var convert = Assert.IsType<MongoConvertExpression>(result);
        Assert.Equal(typeof(int), convert.Type);
        Assert.IsType<MongoFieldExpression>(convert.Operand);
    }

    [Fact]
    public void TryTranslateValue_narrowing_cast_around_arithmetic_is_rejected()
    {
        // (short)(o.Price + o.Qty) is an int->short narrowing cast wrapping a whole $add subtree — same class
        // of silent-truncation bug as the bare narrowing cast above; must be rejected, not silently dropped.
        var (translator, body) = BuildValueBody<Order>(o => (short)(o.Price + o.Qty));
        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void TryTranslateValue_narrowing_cast_over_a_converted_operand_is_rejected() // guard B, via MongoConvertExpression
    {
        // (int)o.EncWeight is a double->int NARROWING cast of a VALUE-CONVERTED property, so it builds a
        // MongoConvertExpression wrapping the converted field (EncWeight is not "int", so it can't take the
        // widening-unwrap branch the way an (int)o.EncStatus cast could — see EncWeight's own doc comment).
        // AllFieldsDefaultSerialized MUST recurse into the operand of a MongoConvertExpression rather than
        // falling into its own catch-all (which answers `true` unconditionally): without that recursion, this
        // would pass Guard B and build a computed sort key over EncWeight's RAW STORED value — silent wrong
        // ORDER under default Native, since the value converter (v => v*2 / v => v/2) is never applied.
        var (translator, body) = BuildValueBody<Order>(o => (int)o.EncWeight);

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    // ------------------------------------------------------------------
    // Owned single-reference dotted-path resolution (EF-322 Task 2)
    // ------------------------------------------------------------------

    [Fact]
    public void Owned_single_ref_subproperty_resolves_to_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var ok = translator.TryTranslateField(FieldBody<OwnedBlog>(b => b.Address.City), out var field);

        Assert.True(ok);
        Assert.Equal("Address.City", field!.ElementName);
    }

    [Fact]
    public void Nested_owned_single_ref_subproperty_resolves_to_deep_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var ok = translator.TryTranslateField(FieldBody<OwnedBlog>(b => b.Address.Geo.Country), out var field);

        Assert.True(ok);
        Assert.Equal("Address.Geo.Country", field!.ElementName);
    }

    [Fact]
    public void Owned_subproperty_comparison_translates_to_dotted_field_op()
    {
        var entityType = GetOwnedBlogEntityType();
        var body = PredicateBody<OwnedBlog>(b => b.Address.City == "NYC");
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        var field = Assert.IsType<MongoFieldExpression>(bin.Left);
        Assert.Equal("Address.City", field.ElementName);
    }

    [Fact]
    public void Owned_bare_bool_subproperty_translates_to_dotted_field()
    {
        var entityType = GetOwnedBlogEntityType();
        var body = PredicateBody<OwnedBlog>(b => b.Address.IsPrimary);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Address.IsPrimary", field.ElementName);
    }

    [Fact]
    public void Two_scope_owned_subproperty_is_declined()
    {
        // A two-scope (SelectMany-unwind) translator must NOT engage the owned dotted-path walk. innerType is
        // deliberately GetOwnedBlogEntityType() (a type that genuinely owns an "Address" embedded-reference
        // navigation), not an unrelated type: an unrelated innerType (e.g. Customer, which has no "Address")
        // would decline anyway via FindNavigation returning null, passing vacuously even if the two-scope
        // guard were deleted. Probed directly (fix-report record, pre-EF-424): with ONLY the two-scope guard
        // commented out, this test failed — TryResolveOwnedFieldPath had NO other guard that also caught this
        // input, because innerType here is the ROOT OwnedBlog type. (Its old IsDocumentRoot guard, since
        // removed by EF-424 in favor of scope-relative path construction, would not have caught it either, for
        // the same reason — it never overlapped with the two-scope guard for this shape.) The two-scope guard
        // below is therefore the ONLY thing standing between this input and a wrong dotted path.
        var outerType = GetOwnedBlogEntityType();
        var innerType = GetOwnedBlogEntityType();
        var outerParam = Expression.Parameter(typeof(OwnedBlog), "o");
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_X");
        var body = Expression.Property(Expression.Property(outerParam, nameof(OwnedBlog.Address)), nameof(OwnedAddress.City));

        Assert.False(translator.TryTranslateField(body, out _));
    }

    [Fact]
    public void Owned_subproperty_via_EFProperty_shape_resolves_to_dotted_field()
    {
        // Real EF-translated queries rewrite owned-nav hops to EF.Property(root, "Nav") calls (NOT plain
        // member access). Build that shape by hand to lock the EF.Property branch of the walk:
        //   EF.Property<OwnedAddress>(b, "Address").City   -> "Address.City"
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(OwnedBlog), "b");
        var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(OwnedAddress));
        var addressCall = Expression.Call(efProperty, param, Expression.Constant("Address"));
        var body = Expression.Property(addressCall, nameof(OwnedAddress.City));

        Assert.True(translator.TryTranslateField(body, out var field));
        Assert.Equal("Address.City", field!.ElementName);
    }

    // ------------------------------------------------------------------
    // Owned-collection quantifiers → $elemMatch (EF-322)
    // ------------------------------------------------------------------

    [Fact]
    public void Owned_collection_Any_with_predicate_translates_to_elem_match()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Heading == "x"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        Assert.False(elemMatch.Negated);
        var comparison = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        // ELEMENT-RELATIVE: "Heading", NOT "Posts.Heading".
        Assert.Equal("Heading", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_bare_Any_translates_to_a_count_comparison()
    {
        // Bare Any() IS "Count >= 1" and is no longer represented by MongoElemMatchExpression at all — see
        // MongoElemMatchExpression's remarks (EF-322 Task 5, unifying the two representations).
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any());

        Assert.True(translator.TryTranslate(body, out var result));
        var comparison = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThanOrEqual, comparison.Operator);
        var size = Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal("Posts", size.FieldName);
        Assert.True(size.NullSafe);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(comparison.Right).Value);
    }

    [Fact]
    public void Negated_owned_collection_Any_flips_the_negated_flag()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => !b.Posts.Any(p => p.Heading == "x"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.True(elemMatch.Negated);
        Assert.NotNull(elemMatch.ElementPredicate);
    }

    [Fact]
    public void Nested_owned_collection_Any_translates_to_nested_elem_match_with_relative_paths()
    {
        // The inner array path must be ELEMENT-relative ("Comments"), not root-relative
        // ("Posts.Comments"). This is the proof that scope-relative path building works.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t")));

        Assert.True(translator.TryTranslate(body, out var result));
        var outer = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", outer.ArrayPath);
        var inner = Assert.IsType<MongoElemMatchExpression>(outer.ElementPredicate);
        Assert.Equal("Comments", inner.ArrayPath);
        var comparison = Assert.IsType<MongoBinaryExpression>(inner.ElementPredicate);
        Assert.Equal("Text", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_Any_through_an_owned_reference_hop_builds_a_dotted_array_path()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Address.Notes.Any(n => n.Body == "b"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Address.Notes", elemMatch.ArrayPath);
    }

    [Fact]
    public void Owned_collection_Any_in_the_exact_shape_EF_produces_resolves()
    {
        // EF hands the translator the Queryable overload, the source wrapped in ONE AsQueryable() call, the
        // lambda Quote-wrapped, and owned-nav hops rewritten to EF.Property calls:
        //   Queryable.Any(Call(AsQueryable, [EF.Property(b, "Posts")]), Quote(p => p.Heading == "x"))
        // A C# lambda compiles to the Enumerable overload instead, so this hand-built tree is the ONLY unit
        // coverage of the shape production queries actually take.
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(OwnedBlog), "b");
        var efProperty = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(List<OwnedPost>));
        var postsCall = Expression.Call(efProperty, param, Expression.Constant("Posts"));
        var asQueryable = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.AsQueryable) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(OwnedPost));
        var source = Expression.Call(asQueryable, postsCall);
        Expression<Func<OwnedPost, bool>> elementPredicate = p => p.Heading == "x";
        var anyMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(OwnedPost));
        var body = Expression.Call(anyMethod, source, Expression.Quote(elementPredicate));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        var comparison = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Heading", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_Any_with_field_to_field_element_predicate_is_declined()
    {
        // Field-to-field has no query-dialect form and $expr is not usable inside $elemMatch, so the
        // whole quantifier declines (query falls back to driver-LINQ).
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Rank > p.Other));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Owned_collection_Any_with_nested_owned_scalar_leaf_resolves_scope_relatively()
    {
        // FLIPPED BY EF-424 (this test USED TO ASSERT A DECLINE, as "..._is_declined" — the comment that
        // stood here explained why: "the element-scoped child translator is not a document root, so
        // TryResolveOwnedFieldPath's IsDocumentRoot guard declines it"). EF-424 replaced that guard with a
        // SCOPE-RELATIVE path construction (joining each hop's own containing element name, mirroring the
        // sibling TryResolveOwnedCollectionPath), so p.Geo.Country — a scalar leaf reached through an owned
        // single-reference hop INSIDE the element — now resolves to "Geo.Country", relative to the element
        // scope, and the whole quantifier goes native via $elemMatch.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Geo.Country == "US"));

        Assert.True(translator.TryTranslate(body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        var comparison = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Geo.Country", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Primitive_collection_Any_is_declined_by_the_quantifier_matcher()
    {
        // Tags is a primitive collection PROPERTY, not a navigation — FindNavigation returns null, so the
        // path resolver declines. Defensive lock only: in a real query EF's own
        // AllAnyToContainsRewritingExpressionVisitor rewrites `Any(t => t == "x")` into `Contains("x")`
        // BEFORE the native translator sees it, so no Any node reaches this matcher for this shape.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Tags.Any(t => t == "x"));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Whole_element_equality_Any_is_declined()
    {
        // The element parameter itself has no member access to resolve.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var target = new OwnedComment { Text = "t" };
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c == target)));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Two_scope_owned_collection_Any_is_declined()
    {
        // A two-scope (SelectMany-unwind) translator must not engage the owned-collection walk. innerType is
        // deliberately GetOwnedBlogEntityType() (a type that genuinely owns a "Posts" embedded-collection
        // navigation), not an unrelated type: absent the _outerParam/_innerPrefix guard, the hop walk below
        // would find that navigation on innerType and build a (wrongly-scoped) path instead of declining, so
        // this test only passes because the guard fires — an unrelated innerType would pass vacuously even
        // with the guard deleted, which is why it is not used here.
        var outerType = GetOwnedBlogEntityType();
        var innerType = GetOwnedBlogEntityType();
        var outerParam = Expression.Parameter(typeof(OwnedBlog), "o");
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_X");
        Expression<Func<OwnedPost, bool>> elementPredicate = p => p.Heading == "x";
        var anyMethod = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(OwnedPost));
        var body = Expression.Call(
            anyMethod, Expression.Property(outerParam, nameof(OwnedBlog.Posts)), elementPredicate);

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Negated_owned_collection_bare_Any_inverts_the_count_comparison_via_the_negator()
    {
        // Bare Any() is no longer a MongoElemMatchExpression (see the sibling
        // Owned_collection_bare_Any_translates_to_a_count_comparison test), so the Not arm no longer has a
        // MongoElemMatchExpression operand to flip Negated on for this shape. FIX ROUND 1 (EF-322 Task 5,
        // pulled forward from Task 6): the Not arm now recognizes a MongoBinaryExpression over a
        // MongoSizeExpression and routes it through MongoExpressionNegator.TryNegate rather than wrapping it
        // in a generic MongoUnaryExpression(Not, ...) — that generic wrap does NOT render (RenderUnary
        // requires a MongoFieldExpression on the comparison's left, which MongoSizeExpression is not), so
        // without this routing !Posts.Any() would decline to render at all. The negator inverts >= to <,
        // giving Count < 1, which Task 3's renderer renders as the same array-index existence form
        // (negated). This test pins the FIXED translate-time shape.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => !b.Posts.Any());

        Assert.True(translator.TryTranslate(body, out var result));
        var comparison = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.LessThan, comparison.Operator);
        var size = Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal("Posts", size.FieldName);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(comparison.Right).Value);
    }

    // ------------------------------------------------------------------
    // Correlated element predicates are DECLINED (EF-322 review fix C1)
    // ------------------------------------------------------------------
    //
    // The Any arm translates its element predicate with a SINGLE-SCOPE, element-scoped translator, and
    // single-scope TryResolveMember resolves a member by NAME with no parameter-identity check. So a member
    // rooted on the ENCLOSING query parameter silently resolves against the ELEMENT type whenever both types
    // declare the same name — retargeting the condition and returning WRONG ROWS. OwnedPost.Title/IsActive
    // exist purely to make that collision reachable here (see the class); with the guard removed, each of the
    // three tests below translates successfully to an $elemMatch on the ELEMENT's own Title/IsActive.

    [Fact]
    public void Correlated_owned_collection_Any_element_predicate_is_declined()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        // b.Title is the OWNER's Title; OwnedPost declares a Title too, so a name-based resolution would
        // happily (and wrongly) build { Posts: { $elemMatch: { Title: "x" } } }.
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.Title == "x"));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Correlated_owned_collection_Any_mixed_conjunct_element_predicate_is_declined()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        // The element-only conjunct is perfectly translatable; the correlated one poisons the whole predicate,
        // so the WHOLE quantifier must decline rather than translate the half it understands.
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.Title == "x" && p.Rank > 1));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Correlated_owned_collection_Any_bare_bool_element_predicate_is_declined()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        // The bare-boolean arm of TranslateNode has the same name-only resolution hazard.
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.IsActive));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Correlated_owned_collection_Any_nested_quantifier_source_is_declined()
    {
        // TryResolveOwnedCollectionPath accepts ANY ParameterExpression root, so an inner quantifier whose
        // SOURCE is rooted on the enclosing parameter resolves against the ELEMENT scope when the element
        // declares a same-named collection navigation — b.Comments (the OWNER's OwnedTag collection) would
        // silently become the element's own Comments. The correlation guard closes that too, because the
        // enclosing parameter is free in the OUTER element-predicate body. OwnedBlog.Comments/OwnedTag.Text are
        // named to collide precisely so this test fails when the guard is removed (verified).
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.Comments.Any(c => c.Text == "t")));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Nested_owned_collection_Any_is_not_declined_by_the_correlation_guard()
    {
        // GUARD-DOES-NOT-OVER-DECLINE. `c` is a ParameterExpression appearing inside the outer element
        // predicate's body, but it is BOUND by the inner lambda, so it is not FREE and must not trigger the
        // correlation decline. A naive "any parameter other than mine" check would kill nested Any, which is a
        // supported shape.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t")));

        Assert.True(translator.TryTranslate(body, out var result));
        var outer = Assert.IsType<MongoElemMatchExpression>(result);
        var inner = Assert.IsType<MongoElemMatchExpression>(outer.ElementPredicate);
        Assert.Equal("Comments", inner.ArrayPath);
    }

    // ------------------------------------------------------------------
    // Owned-collection All → negated $elemMatch (EF-322 Task 2)
    // ------------------------------------------------------------------
    //
    // These helpers render the FULL MQL (rather than just inspecting the MongoExpression tree) because the
    // point of this section is the negated-complement SHAPE ($not/$elemMatch over the De Morgan'd predicate),
    // which is easiest to verify end-to-end as BSON. They reuse OwnedBlog/OwnedPost — that fixture already has
    // Posts/Heading/Rank/Other/Title with Post.Title deliberately colliding with Blog.Title (see the class
    // comments above), which is exactly what the correlation-decline test below needs.

    private static MongoExpression? TryTranslateBlogPredicate(Expression<Func<OwnedBlog, bool>> predicate)
    {
        var entityType = GetOwnedBlogEntityType();
        return new MongoExpressionTranslator(entityType).TryTranslate(predicate.Body, out var result)
            ? result
            : null;
    }

    private static MongoExpression TranslateBlogPredicate(
        Expression<Func<OwnedBlog, bool>> predicate, out BsonDocument rendered)
    {
        var translated = TryTranslateBlogPredicate(predicate);
        Assert.NotNull(translated);
        rendered = new MongoQueryLanguageRenderer().Render(translated, new PlaceholderTable()).AsBsonDocument;
        return translated;
    }

    [Fact]
    public void Owned_collection_All_translates_to_a_negated_elem_match()
    {
        var translated = TranslateBlogPredicate(b => b.Posts.All(p => p.Rank > 5), out var renderer);

        var elemMatch = Assert.IsType<MongoElemMatchExpression>(translated);
        Assert.Equal("Posts", elemMatch.ArrayPath);
        Assert.True(elemMatch.Negated);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Rank: { $not: { $gt: 5 } } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_with_equality_renders_the_ne_complement()
    {
        TranslateBlogPredicate(b => b.Posts.All(p => p.Heading == "x"), out var renderer);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Heading: { $ne: 'x' } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_with_a_conjunction_renders_a_de_morgan_or()
    {
        TranslateBlogPredicate(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "x"), out var renderer);
        Assert.Equal(
            BsonDocument.Parse(
                "{ Posts: { $not: { $elemMatch: { $or: [ { Rank: { $not: { $gt: 5 } } }, { Heading: { $ne: 'x' } } ] } } } }"),
            renderer);
    }

    [Fact]
    public void Negated_owned_collection_All_drops_the_not_wrapper()
    {
        var translated = TranslateBlogPredicate(b => !b.Posts.All(p => p.Rank > 5), out var renderer);

        Assert.False(Assert.IsType<MongoElemMatchExpression>(translated).Negated);
        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { Rank: { $not: { $gt: 5 } } } } }"),
            renderer);
    }

    [Fact]
    public void Owned_collection_All_over_a_field_to_field_element_predicate_declines()
    {
        // The negator has no exact complement for a field-to-field comparison, so the whole quantifier
        // declines and the query falls back — it must NOT emit an $expr inside $elemMatch, which is a hard
        // server error.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => p.Rank > p.Other)));
    }

    [Fact]
    public void Owned_collection_All_with_a_correlated_element_predicate_declines()
    {
        // Same ReferencesEnclosingScope guard the Any arm relies on: the element-scoped translator resolves
        // members by NAME, and OwnedBlog.Title / OwnedPost.Title deliberately collide, so without the guard
        // the owner-rooted condition would be silently retargeted at the element.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => b.Title == "x")));
    }

    // ------------------------------------------------------------------
    // Owned-collection Count in a predicate goes native (EF-322 Task 6)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_owned_collection_Count_greater_than_constant()
    {
        var translated = TryTranslateBlogPredicate(b => b.Posts.Count > 2);

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal(MongoBinaryOperator.GreaterThan, comparison.Operator);
        var size = Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal("Posts", size.FieldName);
        Assert.True(size.NullSafe);
    }

    [Fact]
    public void Translates_owned_collection_Count_call_form()
        => Assert.NotNull(TryTranslateBlogPredicate(b => b.Posts.Count() > 2));

    [Fact]
    public void Translates_owned_collection_LongCount_call_form()
        => Assert.NotNull(TryTranslateBlogPredicate(b => b.Posts.LongCount() > 2L));

    [Fact]
    public void Normalizes_a_reversed_count_comparison_so_the_size_node_is_on_the_left()
    {
        // The query renderer's array-index form recognizes only size-on-the-left, so the translator mirrors.
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => 2 < b.Posts.Count));

        Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal(MongoBinaryOperator.GreaterThan, comparison.Operator);
    }

    [Fact]
    public void Translates_a_count_reached_through_an_owned_reference_hop()
    {
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => b.Address.Notes.Count > 1));

        Assert.Equal("Address.Notes", Assert.IsType<MongoSizeExpression>(comparison.Left).FieldName);
    }

    [Fact]
    public void Negates_a_count_comparison_by_inverting_rather_than_wrapping()
    {
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => !(b.Posts.Count > 2)));

        Assert.Equal(MongoBinaryOperator.LessThanOrEqual, comparison.Operator);
    }

    [Fact]
    public void A_count_inside_a_quantifier_resolves_element_relatively()
    {
        // The element-scoped child translator resolves the inner array relative to the ELEMENT ("Comments"),
        // not the root ("Posts.Comments") — which is what the enclosing $elemMatch expects.
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(
            TryTranslateBlogPredicate(b => b.Posts.Any(p => p.Comments.Count > 1)));

        var inner = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Comments", Assert.IsType<MongoSizeExpression>(inner.Left).FieldName);
    }

    [Fact]
    public void A_mapped_scalar_property_named_Count_is_not_mistaken_for_a_cardinality_expression()
    {
        // `o.Count > 2` is resolved by TranslateComparison's first branch (bare member vs. simple value) and
        // never reaches TranslateOperand at all, so this pins the end-to-end result — a mapped `Count` field
        // resolves as that FIELD, not a cardinality expression — without exercising TranslateOperand's own
        // ordering. See the sibling test below for the TranslateOperand-routed case, and TryMatchCountExpression's
        // remarks for why the real protection is structural (TryResolveOwnedCollectionPath), not call-site order.
        var translator = NewTranslator(GetEntityType<Order>());

        Assert.True(translator.TryTranslate(
            PredicateBody<Order>(o => o.Count > 2), out var translated));

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal("Count", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void A_mapped_scalar_property_named_Count_still_resolves_as_a_field_inside_TranslateOperand()
    {
        // A field-to-field comparison has no simple-value side, so BOTH operands route through TranslateOperand —
        // unlike the sibling test above, whose `o.Count > 2` is resolved by TranslateComparison's first branch and
        // never reaches TranslateOperand at all. This therefore covers the TranslateOperand path specifically.
        // It does NOT pin the call-site ORDERING: moving count recognition ahead of TryResolveMember was measured
        // to turn no test red, because TryResolveOwnedCollectionPath declines a bare-parameter receiver on its
        // zero-hop check regardless of order. See that method's remarks for the structural argument.
        var translator = NewTranslator(GetEntityType<Order>());

        Assert.True(translator.TryTranslate(PredicateBody<Order>(o => o.Count > o.Qty), out var translated));

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal("Count", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
        Assert.Equal("Qty", Assert.IsType<MongoFieldExpression>(comparison.Right).ElementName);
    }

    [Fact]
    public void A_predicated_Count_now_translates_to_a_filtered_size_comparison()
    {
        // USED TO PIN a decline: "Count(pred) has no array-index form and needs $expr over $filter — a separate
        // slice." EF-359 Task 2 is that separate slice: TryMatchCountExpression now recognizes the predicated
        // overload and TranslateOperand builds a MongoFilteredSizeExpression instead of returning null.
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => b.Posts.Count(p => p.Rank > 1) > 2));

        Assert.Equal(MongoBinaryOperator.GreaterThan, comparison.Operator);
        var filtered = Assert.IsType<MongoFilteredSizeExpression>(comparison.Left);
        Assert.Equal("Posts", filtered.ArrayPath);
        var elementPredicate = Assert.IsType<MongoBinaryExpression>(filtered.ElementPredicate);
        Assert.Equal("Rank", Assert.IsType<MongoFieldExpression>(elementPredicate.Left).ElementName);
    }

    [Fact]
    public void Element_predicate_outside_the_renderable_set_is_now_admitted_at_translate_time()
    {
        // EF-365 REBASELINE. This test used to pin MongoAggregationExpressionRenderer.CanRender's decline in
        // TranslateOperand's filtered-count branch (EF-359 fix round 1): a regex predicate has no
        // aggregation-dialect rendering, so CanRender declined it here, at the bare translator layer, before the
        // leaf was ever admitted. EF-365 deleted that translate-time CanRender call site so a non-renderable
        // element predicate gets a graceful driver-LINQ fallback instead of a crash (see
        // NativeOwnedCollectionFilteredCountTests' EF-365 tests for the end-to-end behavior). At THIS layer the
        // leaf is therefore now ADMITTED — a MongoBinaryExpression whose Left is a MongoFilteredSizeExpression
        // wrapping the unrenderable MongoRegexExpression — and the decline moves downstream, to
        // MongoAggregationExpressionRenderer.Render's own catch-all (still gated by CanRender internally) at
        // pipeline-build time.
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => b.Posts.Count(p => p.Heading.StartsWith("h")) > 0));

        var filtered = Assert.IsType<MongoFilteredSizeExpression>(comparison.Left);
        Assert.IsType<MongoRegexExpression>(filtered.ElementPredicate);
    }

    [Fact]
    public void A_primitive_collection_Count_declines()
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION; Tags is a property.
        => Assert.Null(TryTranslateBlogPredicate(b => b.Tags.Count > 2));

    [Fact]
    public void Correlated_Count_element_predicate_with_two_distinct_free_parameters_declines()
    {
        // Task-3 review fix (EF-421, Finding 1): the Count(pred) two-scope arm accepts a correlation only
        // when ReferencesEnclosingScope reports EXACTLY ONE free parameter and it ReferenceEquals SelfParam.
        // FreeParameterVisitor used to report only the FIRST free parameter found — so a predicate body with
        // TWO distinct free parameters, where the first one happened to match SelfParam, would pass the
        // identity check and build a two-scope child translator; that child would then resolve the SECOND
        // free parameter's members by NAME against the element entity type (neither the outer param nor
        // bound), silently retargeting the condition — the exact wrong-rows hazard this file's own constraint
        // forbids. FreeParameterVisitor.FoundParameter now reports null whenever two-or-more DISTINCT free
        // parameters are found, so the call site's ordinary ReferenceEquals check already declines this shape
        // with no extra condition.
        //
        // No known EF LINQ query-authoring path produces a Count(pred) body with two distinct FREE
        // ParameterExpressions today — a captured local surfaces as a closure-field MemberExpression, not an
        // extra free parameter — so this shape is built by hand (rather than compiled from a C# lambda) as a
        // defense-in-depth regression pin, mirroring how Query_parameter_becomes_MongoParameterExpression_
        // not_constant above hand-builds a shape no ordinary lambda produces either.
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var bParam = Expression.Parameter(typeof(OwnedBlog), "b");
        var pParam = Expression.Parameter(typeof(OwnedPost), "p");
        // An unrelated free parameter: neither the element parameter (p), nor a query parameter (plain name,
        // no EF query-parameter prefix), nor the enclosing translator's own SelfParam (set to bParam below).
        var xParam = Expression.Parameter(typeof(int), "x");

        var postsMember = Expression.Property(bParam, nameof(OwnedBlog.Posts));

        // Post.Title deliberately collides with Blog.Title (see the OwnedPost class comment) — if the guard
        // were weakened, b.Title would be silently resolved as the ELEMENT's own Title instead of declining.
        var titleEqualsOuter = Expression.Equal(
            Expression.Property(pParam, nameof(OwnedPost.Title)),
            Expression.Property(bParam, nameof(OwnedBlog.Title)));
        var rankEqualsX = Expression.Equal(Expression.Property(pParam, nameof(OwnedPost.Rank)), xParam);
        var predicateBody = Expression.AndAlso(titleEqualsOuter, rankEqualsX);
        var predicateLambda = Expression.Lambda(predicateBody, pParam);

        var countCall = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Count), [typeof(OwnedPost)], postsMember, predicateLambda);
        var comparisonBody = Expression.GreaterThan(countCall, Expression.Constant(0));

        // Mirrors NativeSlotPopulator.PopulateNativeSlots: SelfParam is set to the enclosing predicate's own
        // root parameter, which here is bParam — the FIRST free parameter FreeParameterVisitor would
        // encounter under the pre-fix behavior, making this shape a false accept without the fix.
        translator.SelfParam = bParam;

        Assert.False(translator.TryTranslate(comparisonBody, out var result));
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // EF-322 stream 1, slice A2: a top-level EF.Property leaf resolves in
    // all three positions (predicate / sort key / projection value).
    // ------------------------------------------------------------------

    [Fact]
    public void EF_Property_top_level_leaf_resolves_in_predicate_position()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => EF.Property<int>(c, "Age") > 21);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        Assert.Equal("Age", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    }

    [Fact]
    public void EF_Property_top_level_leaf_resolves_in_sort_position()
    {
        var entityType = GetEntityType<Customer>();
        var body = FieldBody<Customer>(c => EF.Property<int>(c, "Age"));
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslateField(body, out var field));
        Assert.Equal("Age", field!.ElementName);
    }

    [Fact]
    public void EF_Property_top_level_leaf_resolves_in_value_position()
    {
        var entityType = GetEntityType<Customer>();
        var body = FieldBody<Customer>(c => EF.Property<int>(c, "Age") + 1);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslateValue(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("Age", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    }

    [Fact]
    public void EF_Property_naming_an_unmapped_member_declines()
    {
        var entityType = GetEntityType<Customer>();
        var body = FieldBody<Customer>(c => EF.Property<int>(c, "NotAProperty"));
        var translator = NewTranslator(entityType);

        Assert.False(translator.TryTranslateField(body, out _));
    }

    // A hand-built EF.Property node, so the test controls the receiver shape exactly. EF's own nav-expansion
    // emits a BARE receiver, but the C# compiler may wrap a reference argument in a Convert-to-object for the
    // `object entity` parameter — the implementation unwraps it (Step 3), and these two tests cover both shapes:
    // this helper builds the bare form, and the C#-lambda tests above build whatever Roslyn emits.
    private static MethodCallExpression EfProperty<TProperty>(Expression root, string name)
        => Expression.Call(
            typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(TProperty)),
            root,
            Expression.Constant(name));

    [Fact]
    public void EF_Property_on_the_outer_param_resolves_against_the_OUTER_scope_by_identity()
    {
        // InnerRef and OuterRef both declare "Name", so a by-NAME resolution would silently answer with the
        // inner scope's field. Two-scope mode routes by ReferenceEquals on the parameter — and must do so for
        // the EF.Property spelling exactly as it already does for the member-access spelling
        // (cf. Two_scope_shadowed_member_name_resolves_by_parameter_identity_not_name, above).
        var innerType = GetEntityType<InnerRef>();
        var outerType = GetEntityType<OuterRef>();
        var outerParam = Expression.Parameter(typeof(OuterRef), "o");
        var innerParam = Expression.Parameter(typeof(InnerRef), "r");
        // EF.Property<string>(r, "Name") == EF.Property<string>(o, "Name")
        var body = Expression.Equal(
            EfProperty<string>(innerParam, nameof(InnerRef.Name)),
            EfProperty<string>(outerParam, nameof(OuterRef.Name)));
        var translator = new MongoExpressionTranslator(innerType, outerParam, outerType, "_lookup_Refs");

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("_lookup_Refs.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    [Fact]
    public void EF_Property_naming_a_composite_primary_key_component_resolves_natively()
    {
        // A composite-PK component is stored nested under "_id" and is now addressable via the "_id.<element>"
        // dotted path. Both member-access and EF.Property spellings resolve natively.
        using var db = SingleEntityDbContext.Create<CompositeKeyed>(
            mb => mb.Entity<CompositeKeyed>().HasKey(x => new { x.KeyA, x.KeyB }));
        var entityType = db.Model.FindEntityType(typeof(CompositeKeyed))!;
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(CompositeKeyed), "c");

        Assert.True(translator.TryTranslateField(EfProperty<int>(param, nameof(CompositeKeyed.KeyA)), out var compositeKeyField));
        Assert.Equal("_id.KeyA", compositeKeyField!.ElementName);

        // Control: a NON-key scalar on the same entity also resolves.
        Assert.True(translator.TryTranslateField(EfProperty<string>(param, nameof(CompositeKeyed.Label)), out var ok));
        Assert.Equal("Label", ok!.ElementName);
    }

    // ------------------------------------------------------------------
    // EF-322 stream 1, slice A5 (EF-400): Nullable<T>.Value peels to the underlying
    // field; Nullable<T>.HasValue becomes the existing "!= null" node.
    // ------------------------------------------------------------------

    [Fact]
    public void Nullable_Value_peels_to_the_underlying_field_in_predicate_position()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.NullableAge!.Value > 21);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("NullableAge", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    }

    [Fact]
    public void Nullable_Value_peels_to_the_underlying_field_in_value_position()
    {
        var entityType = GetEntityType<Customer>();
        var body = FieldBody<Customer>(c => c.NullableAge!.Value + 1);
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslateValue(body, out var result));
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal("NullableAge", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
    }

    [Fact]
    public void Nullable_HasValue_becomes_the_same_node_as_an_explicit_null_comparison()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => c.NullableAge.HasValue), out var viaHasValue));
        Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => c.NullableAge != null), out var viaNullCheck));

        // Same operator, same field, same right-hand constant — the two spellings must be indistinguishable at
        // the IR level, which is what makes the renderer and MongoExpressionNegator correct for HasValue for free.
        var a = Assert.IsType<MongoBinaryExpression>(viaHasValue);
        var b = Assert.IsType<MongoBinaryExpression>(viaNullCheck);
        Assert.Equal(b.Operator, a.Operator);
        Assert.Equal(MongoBinaryOperator.NotEqual, a.Operator);
        Assert.Equal(
            Assert.IsType<MongoFieldExpression>(b.Left).ElementName,
            Assert.IsType<MongoFieldExpression>(a.Left).ElementName);
        Assert.Null(Assert.IsType<MongoConstantExpression>(a.Right).Value);
    }

    [Fact]
    public void Negated_HasValue_renders_to_a_form_that_selects_null_AND_missing()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        Assert.True(translator.TryTranslate(PredicateBody<Customer>(c => !c.NullableAge.HasValue), out var result));

        // Pin the RENDERED form, not the node kind: what matters is that the emitted query selects both a stored
        // null and a MISSING element, which is what LINQ's !HasValue means. `$not` over `$ne: null` does, because
        // $eq/$ne partition every BSON value INCLUDING missing (the rule MongoExpressionNegator's own remarks
        // state, and the reason equality may be inverted where the four relational operators may not).
        var rendered = new MongoQueryLanguageRenderer().Render(result!, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ 'NullableAge' : { '$not' : { '$ne' : null } } }"),
            rendered.AsBsonDocument);
    }

    [Fact]
    public void A_user_type_member_named_Value_is_NOT_peeled()
    {
        // The `Nullable.GetUnderlyingType(...) is not null` conjunct on the .Value peel is load-bearing, not a
        // redundant sibling of the name test. `Code` is a MAPPED scalar property (a value-converted struct), so
        // WITHOUT the conjunct the peel strips `.Value`, resolves the RECEIVER, and returns the element that
        // backs `x.Code` — silently answering a question about `Code` when the query asked about `Code.Value`,
        // and bypassing the value converter while doing so. WITH the conjunct the shape declines and falls back
        // to driver-LINQ. (Same shape of reasoning as ClassifyJoinHop's IsTransparentIdentifierType conjunct.)
        using var db = SingleEntityDbContext.Create<CodedEntity>(
            mb => mb.Entity<CodedEntity>().Property(e => e.Code)
                .HasConversion(c => "X" + c.Value, s => new CustomerCode(s.Substring(1))));
        var entityType = db.Model.FindEntityType(typeof(CodedEntity))!;
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(CodedEntity), "e");

        // e.Code.Value — "Value" on a USER struct, not on Nullable<T>.
        var userValue = Expression.Property(
            Expression.Property(param, nameof(CodedEntity.Code)), nameof(CustomerCode.Value));
        Assert.False(translator.TryTranslateField(userValue, out _));

        // Control 1: the receiver itself DOES resolve, so the decline above is the conjunct and not a broken
        // fixture — this is exactly the field the peel would wrongly return.
        Assert.True(translator.TryTranslateField(Expression.Property(param, nameof(CodedEntity.Code)), out var code));
        Assert.Equal("Code", code!.ElementName);

        // Control 2: a genuine Nullable<T>.Value on the SAME entity still peels, so the conjunct narrows the peel
        // rather than disabling it.
        var realNullable = Expression.Property(
            Expression.Property(param, nameof(CodedEntity.Amount)), "Value");
        Assert.True(translator.TryTranslateField(realNullable, out var amount));
        Assert.Equal("Amount", amount!.ElementName);
    }

    // ------------------------------------------------------------------
    // EF-403: a cast-bearing sort key must not be stripped to the raw field (Task 2)
    // ------------------------------------------------------------------

    [Fact]
    public void Sort_key_keeps_a_narrowing_cast_so_it_declines_rather_than_sorting_by_the_raw_value()
    {
        var translator = NewTranslator(GetEntityType<Customer>());

        // (int)c.DoubleScore — order-CHANGING, so TryTranslateField must NOT resolve it to the raw field.
        Assert.False(translator.TryTranslateField(FieldBody<Customer>(c => (int)c.DoubleScore), out _));
    }

    [Fact]
    public void Sort_key_still_strips_an_order_preserving_cast()
    {
        var translator = NewTranslator(GetEntityType<Customer>());

        // Widening, boxing and nullable converts are all value-preserving and must still resolve to the field.
        Assert.True(translator.TryTranslateField(FieldBody<Customer>(c => (double)c.Age), out var widened));
        Assert.Equal("Age", widened!.ElementName);

        Assert.True(translator.TryTranslateField(FieldBody<Customer>(c => (object)c.Age), out var boxed));
        Assert.Equal("Age", boxed!.ElementName);
    }
}
