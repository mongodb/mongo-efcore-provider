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
using System.Collections.Immutable;
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

    // Identity-like-convert fixture (plain enum). These tests pin classification only; enum-as-string
    // constants are covered by NativeCastTests.Enum_as_string_comparison_goes_native_and_returns_the_right_values.
    private enum Tier { Bronze, Silver, Gold }

    // Sub-int-backed enum: C# promotes its comparisons to Int32, so the member-side Convert widens the underlying
    // Int16 rather than matching it exactly (an Int32-backed enum never exposes this).
    private enum ShortTier : short { Bronze, Silver, Gold }

    // Long-backed enum narrowed to int must decline: the widening check is directional.
    private enum LongTier : long { Bronze, Silver, Gold }

    private class Customer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
        public double DoubleScore { get; set; }
        public string Name { get; set; } = "";
        public string Nickname { get; set; } = "";
        public bool Active { get; set; }
        public Tier Level { get; set; }
        public ShortTier ShortLevel { get; set; }
        public LongTier LongLevel { get; set; }
        public char Grade { get; set; }
        public int? NullableAge { get; set; }
        public bool? NullableFlag { get; set; }
    }

    // `.Value`-peel fixture: CustomerCode has its own `Value` member and is a value-converted scalar, so a
    // name-only peel would resolve the wrong field rather than decline. `Amount` is the Nullable<T> control.
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

    // Two-scope (correlated SelectMany) fixtures; both have "Name" to prove routing is by identity, not name.
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

    // TryTranslateValue fixture; value-converted EncStatus covers the non-default-serialization guard.
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
        // Converted double for AllFieldsDefaultSerialized's MongoConvertExpression case: widening casts of
        // EncStatus unwrap before reaching a MongoConvertExpression, but a narrowing double->int cast always
        // builds one around the converted field.
        public double EncWeight { get; set; }

        // For the bool/DateTime string-concat declines ($toString diverges; see TranslateConcatOperand).
        public bool Flag { get; set; }
        public DateTime When { get; set; }
    }

    // Fixtures for owned single-reference dotted-path resolution.

    private class OwnedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        // Deliberately shares its name (and CLR type) with OwnedPost.IsActive — see OwnedPost.
        public bool IsActive { get; set; }
        public OwnedAddress Address { get; set; } = null!;
        public List<OwnedPost> Posts { get; set; } = [];
        // Same nav name as OwnedPost.Comments and same "Text" element member as OwnedComment, so
        // Correlated_owned_collection_Any_nested_quantifier_source_is_declined can't decline for an unrelated reason.
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
        // Title/IsActive collide with OwnedBlog's on purpose: the element-scoped translator resolves by name, so
        // an enclosing `b.Title` only mis-resolves (rather than declining as missing) when the names match. See
        // the Correlated_* tests.
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
    /// Builds a translator over an <see cref="Order"/> model (with value-converted <see cref="Order.EncStatus"/> and
    /// <see cref="Order.EncWeight"/>) and extracts a numeric value-selector body for
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
        // Strip the implicit Convert-to-object so the body matches what EF hands the translator.
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
        // PadLeft has no native translation.
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Name.PadLeft(3) == "A");
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
    // Test 4b: bare boolean query parameter predicate root → MongoParameterExpression
    //
    // EF hoists a predicate subtree that doesn't reference the source (e.g. `data.Contains(x + "c")`) into one
    // bool parameter; it must translate like a literal true/false root, not decline.
    // ------------------------------------------------------------------

    [Fact]
    public void Bare_boolean_query_parameter_predicate_root_translates_to_parameter_expression()
    {
        var entityType = GetEntityType<Customer>();

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "Contains_0";
        Expression body = Expression.Parameter(typeof(bool), paramName);
#else
        const string paramName = "__Contains_0";
        Expression body = new Microsoft.EntityFrameworkCore.Query.QueryParameterExpression(paramName, typeof(bool));
#endif

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var mongoParam = Assert.IsType<MongoParameterExpression>(result);
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
    // Test 8: composite-PK property → resolves under "_id.<element>"
    // ------------------------------------------------------------------

    private class OrderLine
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    // Composite-PK fixture: key components are stored under "_id", not at their own top-level names.
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

        var body = PredicateBody<OrderLine>(ol => ol.OrderId == 10248);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        Assert.NotNull(result);
        Assert.IsType<MongoBinaryExpression>(result);
    }

    // A negated conjunction with a field-to-field (aggregation-only) leaf has no exact query-dialect De Morgan
    // complement, and a $not wrap would change rows for missing/null fields, so the whole predicate declines.
    [Fact]
    public void Negated_mixed_dialect_conjunction_declines()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => !(c.Age == c.Score && c.NullableAge == 1);

        Assert.False(translator.TryTranslate(predicate.Body, out _));
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
    // Test 11b-11d: instance Equals(...) translates like `==`, but only for the IEquatable<T>.Equals(T)
    // overload, never Equals(object).
    // ------------------------------------------------------------------

    [Fact]
    public void Equals_method_call_translates_to_equal_binary()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age.Equals(21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal(21, constant.Value);
    }

    // As in the spec test Where_equals_using_int_overload_on_mismatched_types: a ushort argument binds to
    // Equals(int) with a widening Convert — equivalent to `c.Age == (int)shortPrm`.
    [Fact]
    public void Equals_method_call_with_widening_convert_translates_to_equal_binary()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age.Equals((ushort)21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal(21, constant.Value);
    }

    // As in Where_equals_using_object_overload_on_mismatched_types: a ulong binds to Equals(object), which is
    // always false for a non-int argument. Emitting $eq would be wrong (MongoDB compares numbers by value), so
    // it folds to `false`, as the driver-LINQ bridge does.
    [Fact]
    public void Equals_method_call_using_object_overload_on_mismatched_types_folds_to_false_constant()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age.Equals((ulong)21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var constant = Assert.IsType<MongoConstantExpression>(result);
        Assert.Equal(false, constant.Value);
    }

    // Nullable<T> only has Equals(object), so `c.NullableAge.Equals(21)` uses it even though the underlying
    // types match; this must translate, not fold or decline.
    [Fact]
    public void Equals_method_call_on_nullable_receiver_with_matching_underlying_type_translates_to_equal_binary()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.NullableAge.Equals(21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("NullableAge", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal(21, constant.Value);
    }

    // Nullable receiver with a mismatched underlying type (int vs long): always false in C#, so folds to `false`.
    [Fact]
    public void Equals_method_call_on_nullable_receiver_with_mismatched_underlying_type_folds_to_false_constant()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.NullableAge.Equals(21L);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var constant = Assert.IsType<MongoConstantExpression>(result);
        Assert.Equal(false, constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 11e-11f: static object.Equals(a, b). Both arguments are always boxed, so (as in the driver-LINQ
    // bridge) one boxing layer is peeled from each and the unboxed types must match.
    // ------------------------------------------------------------------

    [Fact]
    public void Static_object_equals_translates_to_equal_binary()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => object.Equals(c.Age, 21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
        Assert.Equal(21, constant.Value);
    }

    // Int32 vs Int64 unboxed types: folds to `false`, as for the instance Equals(object) case.
    [Fact]
    public void Static_object_equals_with_mismatched_types_folds_to_false_constant()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => object.Equals(c.Age, (long)21);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var constant = Assert.IsType<MongoConstantExpression>(result);
        Assert.Equal(false, constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 12: bare nullable-bool member access reports not translatable
    // (three-valued semantics the query dialect doesn't match).
    // ------------------------------------------------------------------

    [Fact]
    public void Bare_nullable_bool_field_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // bool? can't be a Func<Customer, bool> body, so build the tree by hand.
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var member = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.NullableFlag))!);

        var translated = translator.TryTranslate(member, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 13: `!c.NullableFlag` reports not translatable (same guard, via
    // the `!` path).
    // ------------------------------------------------------------------

    [Fact]
    public void Not_over_nullable_bool_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // bool? can't be a Func<Customer, bool> body, so build the tree by hand.
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

        // Built by hand to get a ConstantExpression collection (a captured local or inline literal compiles to
        // a MemberExpression / NewArrayInit instead).
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

    [Theory]
    [InlineData(typeof(IReadOnlySet<int>))]
    [InlineData(typeof(IImmutableSet<int>))]
    // EF8 hands these concrete-type instance calls over un-normalized (EF9+ rewrites them to Enumerable.Contains).
    [InlineData(typeof(System.Collections.ObjectModel.ReadOnlyCollection<int>))]
    [InlineData(typeof(ImmutableHashSet<int>))]
    public void Instance_set_interface_contains_is_matched(Type setInterface)
    {
        var set = Expression.Parameter(setInterface, "set");
        var item = Expression.Parameter(typeof(int), "item");
        var call = Expression.Call(set, setInterface.GetMethod("Contains", [typeof(int)])!, item);

        Assert.True(MongoExpressionTranslator.TryMatchContainsMethod(call, out var collection, out var matchedItem));
        Assert.Same(set, collection);
        Assert.Same(item, matchedItem);
    }

    [Fact]
    public void Negated_contains_over_empty_inline_list_translates_to_negated_in_with_empty_values()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        var body = PredicateBody<Customer>(c => !new List<int>().Contains(c.Age));

        Assert.True(translator.TryTranslate(body, out var result));
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.True(inExpr.Negated);
        var values = Assert.IsType<MongoConstantExpression>(inExpr.Values);
        Assert.Empty((System.Collections.IEnumerable)values.Value!);
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
    // Test 16b: `args[i]` into a query-parameter array → MongoParameterExpression with ArrayElementIndex.
    // EF10 rewrites the indexer to Enumerable.ElementAt(source, index); both that and ArrayIndex are covered.
    // ------------------------------------------------------------------

    [Fact]
    public void ElementAt_over_query_parameter_array_translates_to_array_element_parameter()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "args_0";
        Expression efParam = Expression.Parameter(typeof(int[]), paramName);
#else
        const string paramName = "__args_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int[]));
#endif
        var elementAtMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.ElementAt) && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType == typeof(int))
            .MakeGenericMethod(typeof(int));
        var elementAt = Expression.Call(elementAtMethod, efParam, Expression.Constant(0));
        var body = Expression.Equal(ageMember, elementAt);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.IsType<MongoFieldExpression>(bin.Left);
        var parameter = Assert.IsType<MongoParameterExpression>(bin.Right);
        Assert.Equal(paramName, parameter.Name);
        Assert.Equal(0, parameter.ArrayElementIndex);
    }

    [Fact]
    public void ArrayIndex_over_query_parameter_array_translates_to_array_element_parameter()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "args_0";
        Expression efParam = Expression.Parameter(typeof(int[]), paramName);
#else
        const string paramName = "__args_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int[]));
#endif
        var arrayIndex = Expression.ArrayIndex(efParam, Expression.Constant(1));
        var body = Expression.Equal(ageMember, arrayIndex);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        var parameter = Assert.IsType<MongoParameterExpression>(bin.Right);
        Assert.Equal(paramName, parameter.Name);
        Assert.Equal(1, parameter.ArrayElementIndex);
    }

    [Fact]
    public void ElementAt_over_query_parameter_array_on_the_left_side_still_translates()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "args_0";
        Expression efParam = Expression.Parameter(typeof(int[]), paramName);
#else
        const string paramName = "__args_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(int[]));
#endif
        var elementAtMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.ElementAt) && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType == typeof(int))
            .MakeGenericMethod(typeof(int));
        var elementAt = Expression.Call(elementAtMethod, efParam, Expression.Constant(0));
        // The mirrored shape: value on the LEFT, member on the RIGHT.
        var body = Expression.Equal(elementAt, ageMember);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.IsType<MongoFieldExpression>(bin.Left);
        var parameter = Assert.IsType<MongoParameterExpression>(bin.Right);
        Assert.Equal(paramName, parameter.Name);
        Assert.Equal(0, parameter.ArrayElementIndex);
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
    // Test 17b: `customers.Contains(c)` over a parameter entity list → MongoInExpression over the primary key,
    // with keys extracted per element at runtime (ExtractEntityKeyFromArrayElements).
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_entity_list_query_parameter_translates_to_key_in()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "customers_0";
        Expression efParam = Expression.Parameter(typeof(List<Customer>), paramName);
#else
        const string paramName = "__customers_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(List<Customer>));
#endif
        var containsMethod = typeof(List<Customer>).GetMethod(nameof(List<Customer>.Contains), [typeof(Customer)])!;
        var body = Expression.Call(efParam, containsMethod, cParam);

        var translator = NewTranslator(entityType);
        translator.SelfParam = cParam;
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("_id", inExpr.Field.ElementName);
        var parameter = Assert.IsType<MongoParameterExpression>(inExpr.Values);
        Assert.Equal(paramName, parameter.Name);
        Assert.True(parameter.ExtractEntityKeyFromArrayElements);
    }

    [Fact]
    public void Contains_over_entity_list_constant_translates_to_key_in_with_null_elements_preserved()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");

        var other = new Customer { Id = ObjectId.GenerateNewId() };
        var customers = new List<Customer?> { null, other };
        var containsMethod = typeof(List<Customer?>).GetMethod(nameof(List<Customer?>.Contains), [typeof(Customer)])!;
        var body = Expression.Call(Expression.Constant(customers), containsMethod, cParam);

        var translator = NewTranslator(entityType);
        translator.SelfParam = cParam;
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("_id", inExpr.Field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(inExpr.Values);
        var keys = Assert.IsType<List<object?>>(constant.Value);
        Assert.Equal([null, other.Id], keys);
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
    // Test 18b: inline array-literal Contains (NewArrayInit, as on EF8) → MongoInExpression
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_new_array_init_translates_to_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // NewArrayInit is what EF8 hands the translator for an inline array literal.
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
    // Test 18f: `new[] { prm1, prm2 }.Contains(c.CustomerID)` with separately captured locals: each element is
    // its own parameter inside a NewArrayInit → MongoInExpression over a MongoValueListExpression of parameters.
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_new_array_init_of_separate_query_parameters_translates_to_value_list()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        const string paramName0 = QueryCompilationContext.QueryParameterPrefix + "prm1_0";
        const string paramName1 = QueryCompilationContext.QueryParameterPrefix + "prm2_0";
        Expression efParam0 = Expression.Parameter(typeof(int), paramName0);
        Expression efParam1 = Expression.Parameter(typeof(int), paramName1);
#else
        const string paramName0 = "__prm1_0";
        const string paramName1 = "__prm2_0";
        Expression efParam0 = new QueryParameterExpression(paramName0, typeof(int));
        Expression efParam1 = new QueryParameterExpression(paramName1, typeof(int));
#endif
        var arrayExpr = Expression.NewArrayInit(typeof(int), efParam0, efParam1);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        var body = Expression.Call(containsMethod, arrayExpr, ageMember);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var inExpr = Assert.IsType<MongoInExpression>(result);
        Assert.False(inExpr.Negated);
        Assert.Equal("Age", inExpr.Field.ElementName);
        var list = Assert.IsType<MongoValueListExpression>(inExpr.Values);
        Assert.Equal(2, list.Elements.Count);
        var p0 = Assert.IsType<MongoParameterExpression>(list.Elements[0]);
        var p1 = Assert.IsType<MongoParameterExpression>(list.Elements[1]);
        Assert.Equal(paramName0, p0.Name);
        Assert.Equal(paramName1, p1.Name);
    }

    // ------------------------------------------------------------------
    // Test 18g: Contains over a computed item → MongoComputedInExpression ($expr array-form $in), since there
    // is no bare field for a query-dialect $in.
    // ------------------------------------------------------------------

    [Fact]
    public void Contains_over_string_concat_item_translates_to_computed_in()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        // Built by hand for a ConstantExpression collection (see Test 14).
        var data = new[] { "AliceSuffix", "BobSuffix" };
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var nameMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Name))!);
        var concatItem = Expression.Add(
            nameMember, Expression.Constant("Suffix"),
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(string));
        var body = Expression.Call(containsMethod, Expression.Constant(data), concatItem);

        Assert.True(translator.TryTranslate(body, out var result));
        var computedIn = Assert.IsType<MongoComputedInExpression>(result);
        Assert.False(computedIn.Negated);
        var concat = Assert.IsType<MongoConcatExpression>(computedIn.Needle);
        Assert.Equal(2, concat.Operands.Count);
        Assert.IsType<MongoFieldExpression>(concat.Operands[0]);
        var constant = Assert.IsType<MongoConstantExpression>(computedIn.Values);
        var values = Assert.IsType<string[]>(constant.Value);
        Assert.Equal(["AliceSuffix", "BobSuffix"], values);
    }

    [Fact]
    public void Not_over_string_concat_contains_flips_negated()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);

        var data = new[] { "AliceSuffix", "BobSuffix" };
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var nameMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Name))!);
        var concatItem = Expression.Add(
            nameMember, Expression.Constant("Suffix"),
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(string));
        var containsCall = Expression.Call(containsMethod, Expression.Constant(data), concatItem);
        var body = Expression.Not(containsCall);

        Assert.True(translator.TryTranslate(body, out var result));
        var computedIn = Assert.IsType<MongoComputedInExpression>(result);
        Assert.True(computedIn.Negated);
    }

    // ------------------------------------------------------------------
    // Test 18c-18f: arrayField.Contains(constant), the mirror of $in: a stored array field and a constant item
    // → MongoArrayContainsExpression (implicit element match { field: value }).
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

    // A field item with a field collection: neither the array-contains arm nor the $in arm represents it.
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

    // A whole-collection value converter has no per-element serializer, so this declines rather than compare
    // the constant against the wrong shape (see TranslateArrayContainsItem).
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
    // Test 19-23: string.StartsWith/EndsWith/Contains → MongoRegexExpression
    // ------------------------------------------------------------------

    [Fact]
    public void StartsWith_translates_to_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith("A");

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(regex.Field);
        Assert.Equal("Name", field.ElementName);
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
        // Build a real EF query-parameter node; a lambda would capture a closure-field access instead.
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
    public void StartsWith_with_column_term_translates_to_field_to_field_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith(c.Nickname);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(regex.Field);
        Assert.Equal("Name", field.ElementName);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
        Assert.False(regex.Negated);
        var term = Assert.IsType<MongoFieldExpression>(regex.Term);
        Assert.Equal("Nickname", term.ElementName);
    }

    [Fact]
    public void StartsWith_with_same_column_on_both_sides_translates_to_field_to_field_regex_expression()
    {
        // The shape of the spec test All_top_level_column.
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith(c.Name);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        var term = Assert.IsType<MongoFieldExpression>(regex.Term);
        Assert.Equal("Name", term.ElementName);
    }

    [Fact]
    public void Contains_with_column_term_translates_to_field_to_field_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.Contains(c.Nickname);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Contains, regex.Kind);
        Assert.IsType<MongoFieldExpression>(regex.Term);
    }

    [Fact]
    public void EndsWith_with_column_term_translates_to_field_to_field_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.EndsWith(c.Nickname);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.EndsWith, regex.Kind);
        Assert.IsType<MongoFieldExpression>(regex.Term);
    }

    [Fact]
    public void Negated_starts_with_column_term_translates_to_negated_field_to_field_regex_expression()
    {
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => !c.Name.StartsWith(c.Nickname);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.True(regex.Negated);
        Assert.IsType<MongoFieldExpression>(regex.Term);
    }

    [Fact]
    public void StartsWith_with_string_comparison_ordinal_overload_translates_to_regex_expression()
    {
        // Ordinal is culture-independent, so regex reproduces it (case-sensitive).
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith("A", StringComparison.Ordinal);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.StartsWith, regex.Kind);
        Assert.False(regex.CaseInsensitive);
    }

    [Fact]
    public void StartsWith_with_string_comparison_current_culture_overload_reports_not_translatable()
    {
        // Culture-sensitive comparisons have no $regularExpression equivalent, so this declines. (The driver-LINQ
        // fallback runs them with ordinal semantics — a driver-side risk — rather than this translator
        // mistranslating them.)
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith("A", StringComparison.CurrentCulture);

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("StartsWith")]
    [InlineData("Contains")]
    [InlineData("EndsWith")]
    public void Field_to_field_term_with_OrdinalIgnoreCase_reports_not_translatable(string kind)
    {
        // Field-to-field OrdinalIgnoreCase declines: the only $expr folding tool is $toLower, which is ASCII-only
        // and would silently mismatch non-ASCII rows. Driver-LINQ then throws cleanly (see
        // NativeStringCaseInsensitiveMatchTests.DriverLinq_field_to_field_OrdinalIgnoreCase_throws).
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = kind switch
        {
            "StartsWith" => c => c.Name.StartsWith(c.Nickname, StringComparison.OrdinalIgnoreCase),
            "Contains" => c => c.Name.Contains(c.Nickname, StringComparison.OrdinalIgnoreCase),
            "EndsWith" => c => c.Name.EndsWith(c.Nickname, StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void Field_to_field_term_with_Ordinal_still_translates()
    {
        // The case-sensitive field-to-field shape still translates.
        var entityType = GetEntityType<Customer>();
        var translator = NewTranslator(entityType);
        Expression<Func<Customer, bool>> predicate = c => c.Name.StartsWith(c.Nickname, StringComparison.Ordinal);

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.False(regex.CaseInsensitive);
        Assert.IsType<MongoFieldExpression>(regex.Term);
    }

    // ------------------------------------------------------------------
    // Test 24-31: field-to-field and arithmetic-operand comparisons → $expr-shaped trees
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

    // Modulo maps straight to $mod (as driver-LINQ does). Integer division uses C#'s truncating semantics
    // (IntegerDivide → $trunc of $divide), deliberately differing from driver-LINQ.

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
        // Both operands are int fields after the widening Convert unwraps, so only the BinaryExpression's
        // result type (double) says not to truncate; hence MapArithmeticOperator takes the node.
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

    // String Add must not become arithmetic $add (IsNumericType guard); it translates to MongoConcatExpression.

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

    // A numeric cast in a field-to-field/arithmetic comparison renders as MongoConvertExpression ($toX), as
    // driver-LINQ does; the arithmetic result is unchanged. End-to-end coverage is in NativeCastTests.

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
        // c.Score implicitly widens to double too.
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
        // Both operands widen to double for the +.
        var leftConvert = Assert.IsType<MongoConvertExpression>(add.Left);
        Assert.Equal(typeof(double), leftConvert.Type);
        Assert.IsType<MongoFieldExpression>(leftConvert.Operand);
        var rightConvert = Assert.IsType<MongoConvertExpression>(add.Right);
        Assert.Equal(typeof(double), rightConvert.Type);
        Assert.IsType<MongoFieldExpression>(rightConvert.Operand);
        Assert.IsType<MongoConstantExpression>(cmp.Right);
    }

    // The member-vs-constant cast guard (HasNumericConvert) tolerates a widening numeric layer MQL can express
    // and declines everything else.

    [Fact]
    public void Widening_cast_on_member_vs_constant_now_translates_with_the_constant_in_the_comparison_type()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age > 5.0;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);

        // The widening layer is absorbed, not rendered as a MongoConvertExpression, keeping the comparison in the
        // indexable query dialect (a $toDouble would force $expr), as driver-LINQ does.
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);

        // The constant has no serialization context, so it renders as double; coercing it to the stored int
        // would truncate a fractional constant and return wrong rows.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.0, constant.Value);
    }

    [Fact]
    public void Widening_cast_with_the_member_on_the_RIGHT_also_serializes_the_constant_in_the_comparison_type()
    {
        // Covers TranslateComparison's mirrored (member-on-right) branch, which has its own HasNumericConvert
        // and TranslateValue calls; EF doesn't normalize operand order.
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
        // EF's shape for a nullable closure comparison: Convert(Convert(c.Age, Int64), Nullable<Int64>) > value.
        // The outer layer only adds nullability, so it must be skipped, not classified (a widening test would
        // answer false). A literal threshold is used because a captured local isn't a "simple value" here.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (long?)c.Age > (long?)5L;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
    }

    // A narrowing cast makes only the query-native branch decline; the comparison falls through to $expr. The
    // node shape matters: absorbing the cast (bare field, constant with the property serializer) would compare
    // the untruncated stored value. The fall-through wraps the field in MongoConvertExpression and the constant
    // has no serialization context. A relational fall-through is conjoined with a numeric type bracket over the same
    // field, even for a non-nullable property (a missing element would otherwise compare as null in $expr).
    [Fact]
    public void Narrowing_cast_on_member_vs_constant_falls_through_to_the_expr_path()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.DoubleScore > 5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = UnwrapNumericTypeBracket(result, "DoubleScore");
        Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);

        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        Assert.Equal(typeof(int), convert.Type);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("DoubleScore", field.ElementName);

        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
    }

    // Mirrored operand order reaches the same fall-through via the second classification site. The $expr path
    // doesn't mirror, so the constant stays on the left.
    [Fact]
    public void Mirrored_narrowing_cast_on_constant_vs_member_falls_through_to_the_expr_path()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => 5 < (int)c.DoubleScore;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = UnwrapNumericTypeBracket(result, "DoubleScore");
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
        // int -> float widens, but MQL has no $toFloat; the guard uses MongoConvertExpression.ToOperatorFor as
        // the single definition of admissible targets.
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (float)c.Age > 5.0f;

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void Widening_cast_over_a_value_converted_property_keeps_the_property_serializer()
    {
        // The rule is HasDefaultKeySerialization, not "not an enum": EncStatus is a value-converted int, so the
        // constant keeps the property serializer and renders in the stored form.
        var (translator, body) = BuildOrderPredicateBody(o => (long)o.EncStatus > 5L);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("EncStatus", constant.ForSerialization!.Name);
    }

    /// <summary>
    /// Like <see cref="BuildValueBody{T}"/>, but for a predicate lambda.
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

    // The $expr fall-through requires a default-serialized property: `{$toInt: "$EncWeight"}` would read the raw
    // stored value, silently returning wrong rows (see
    // NativeCastTests.Narrowing_cast_comparison_over_a_value_converted_property_still_declines). Must decline.
    [Fact]
    public void Narrowing_cast_over_a_value_converted_property_does_not_fall_through_to_expr()
    {
        var (translator, body) = BuildOrderPredicateBody(o => (int)o.EncWeight > 3);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // Control: unconverted Weight (same type and entity) does fall through.
    [Fact]
    public void Narrowing_cast_over_a_default_serialized_property_on_the_same_entity_still_falls_through()
    {
        var (translator, body) = BuildOrderPredicateBody(o => (int)o.Weight > 3);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = UnwrapNumericTypeBracket(result, "Weight");
        var convert = Assert.IsType<MongoConvertExpression>(cmp.Left);
        var field = Assert.IsType<MongoFieldExpression>(convert.Operand);
        Assert.Equal("Weight", field.ElementName);
    }

    // Asserts AndAlso(MongoNumericTypeBracketExpression(field), comparison) and returns the comparison.
    private static MongoBinaryExpression UnwrapNumericTypeBracket(MongoExpression? result, string elementName)
    {
        var and = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        var bracket = Assert.IsType<MongoNumericTypeBracketExpression>(and.Left);
        Assert.Equal(elementName, bracket.Field.ElementName);
        return Assert.IsType<MongoBinaryExpression>(and.Right);
    }

    // The identity-like arm: HasNumericConvert also tolerates enum ↔ underlying, char -> int and boxing, reported
    // separately from widening because the constant is treated oppositely (ConstantSerializationContext).

    [Fact]
    public void Enum_to_underlying_convert_on_member_vs_constant_translates_and_keeps_the_field_unconverted()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (int)c.Level == (int)Tier.Gold;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);

        // Absorbed, not rendered: the plain stored field.
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Level", field.ElementName);

        // Unlike the widening arm, the constant keeps the property serializer: same stored value, different
        // declared CLR type.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("Level", constant.ForSerialization!.Name);
    }

    [Fact]
    public void Underlying_to_enum_convert_on_member_vs_constant_translates()
    {
        // IsIdentityLikeConvert admits both directions (underlying -> enum here).
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
        // Covers the mirrored branch's own HasNumericConvert / ConstantSerializationContext calls.
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
        // The identity-like arm always keeps the property serializer, so a value-converted property renders
        // through its converter.
        var (translator, body) = BuildOrderPredicateBody(o => (object)o.EncStatus == (object)5);

        Assert.True(translator.TryTranslate(body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.NotNull(constant.ForSerialization);
        Assert.Equal("EncStatus", constant.ForSerialization!.Name);
    }

    // Enum promotion: C# promotes a sub-int-backed enum's comparison to Int32, widening the underlying type.

    [Fact]
    public void Short_backed_enum_comparison_promoted_to_int_by_the_compiler_still_translates()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.ShortLevel == ShortTier.Gold;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, cmp.Operator);

        // Absorbed, not rendered: the plain stored field.
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
        // Int64 -> Int32 is a narrowing, not a promotion, so it must not be absorbed: it falls through to $expr
        // with an explicit $toInt. Wrongly absorbing it would leave a bare field comparing the untruncated long.
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

    // Flag precedence: a Convert chain can be both widening (inner) and identity-like (outer boxing); widening
    // must win, or the boxing layer masks the truncation protection.

    [Fact]
    public void Boxing_over_a_widening_cast_lets_the_widening_arm_win_precedence()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (object)(double)c.Age == (object)5.5;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));

        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var field = Assert.IsType<MongoFieldExpression>(cmp.Left);
        Assert.Equal("Age", field.ElementName);

        // ForSerialization is the discriminating assertion: Value stays 5.5 either way, and truncation happens at
        // render time through Age's serializer if the boxing arm wrongly won. Row-level pin:
        // NativeCastTests.Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row.
        var constant = Assert.IsType<MongoConstantExpression>(cmp.Right);
        Assert.Null(constant.ForSerialization);
        Assert.Equal(5.5, constant.Value);
    }

    [Fact]
    public void Boxing_over_a_widening_cast_with_the_member_on_the_RIGHT_also_lets_widening_win()
    {
        // Mirrored branch; ForSerialization is the discriminating assertion.
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

    // A plain field-vs-constant comparison stays on the query-native path (field on Left, mirrored if needed),
    // so it renders to $match, not $expr.

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
    // TryTranslateValue: numeric computed-leaf value expressions
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
    public void TryTranslateValue_integer_division_translates_to_integer_divide()
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
        // Compiles to string.Concat(o.Tag, (object)o.Price): the int operand arrives boxed.
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
        // A top-level double->int cast must not be stripped (that would return the raw double); it renders as an
        // explicit MongoConvertExpression ($toInt).
        var (translator, body) = BuildValueBody<Order>(o => (int)o.Weight);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var convert = Assert.IsType<MongoConvertExpression>(result);
        Assert.Equal(typeof(int), convert.Type);
        Assert.IsType<MongoFieldExpression>(convert.Operand);
    }

    [Fact]
    public void TryTranslateValue_narrowing_cast_around_arithmetic_is_rejected()
    {
        // int->short narrowing around an $add has no $toX target; must be rejected, not dropped.
        var (translator, body) = BuildValueBody<Order>(o => (short)(o.Price + o.Qty));
        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void TryTranslateValue_narrowing_cast_over_a_converted_operand_is_rejected() // guard B, via MongoConvertExpression
    {
        // A narrowing cast of a value-converted property builds a MongoConvertExpression over the field;
        // AllFieldsDefaultSerialized must recurse into it, or a computed sort key would use the raw stored value
        // (silently wrong order).
        var (translator, body) = BuildValueBody<Order>(o => (int)o.EncWeight);

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    // ------------------------------------------------------------------
    // Owned single-reference dotted-path resolution
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
        // A two-scope (SelectMany-unwind) translator must not engage the owned dotted-path walk. innerType owns
        // "Address" so the test isn't vacuous; the two-scope guard is the only thing preventing a wrong path.
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
        // EF rewrites owned-nav hops to EF.Property(root, "Nav"); build that shape by hand:
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
    // Owned-collection quantifiers → $elemMatch
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
        // Element-relative: "Heading", not "Posts.Heading".
        Assert.Equal("Heading", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void Owned_collection_bare_Any_translates_to_a_count_comparison()
    {
        // Bare Any() is "Count >= 1", not a MongoElemMatchExpression (see its remarks).
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
        // The inner array path must be element-relative ("Comments"), not "Posts.Comments".
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
        // A C# lambda compiles to the Enumerable overload, so this is the only unit coverage of the real shape.
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
        // Field-to-field has no query-dialect form and $expr can't be used inside $elemMatch, so this declines.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Rank > p.Other));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Owned_collection_Any_with_nested_owned_scalar_leaf_resolves_scope_relatively()
    {
        // An owned reference hop inside the element resolves scope-relative ("Geo.Country"), so the quantifier
        // goes native via $elemMatch.
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
        // Tags is a primitive collection property, not a navigation, so the path resolver declines. Defensive:
        // EF rewrites this Any into Contains before the native translator sees it.
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
        // A two-scope translator must not engage the owned-collection walk. innerType owns "Posts" so that,
        // without the _outerParam/_innerPrefix guard, a wrongly-scoped path would be built (not a vacuous pass).
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
        // Bare Any() is a size comparison, so the Not arm negates it via MongoExpressionNegator.TryNegate (Count
        // < 1) rather than a generic MongoUnaryExpression(Not), which RenderUnary can't render over a
        // MongoSizeExpression.
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
    // Correlated element predicates are declined
    // ------------------------------------------------------------------
    //
    // The element-scoped translator resolves members by name, so an enclosing-scope member with a same-named
    // element member would silently retarget the condition (wrong rows). OwnedPost.Title/IsActive make that
    // collision reachable.

    [Fact]
    public void Correlated_owned_collection_Any_element_predicate_is_declined()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        // Name-based resolution would wrongly build { Posts: { $elemMatch: { Title: "x" } } }.
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.Title == "x"));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Correlated_owned_collection_Any_mixed_conjunct_element_predicate_is_declined()
    {
        var translator = NewTranslator(GetOwnedBlogEntityType());
        // The correlated conjunct makes the whole quantifier decline, not just that half.
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
        // TryResolveOwnedCollectionPath accepts any parameter root, so the owner's b.Comments would silently
        // become the element's Comments. The correlation guard catches it (b is free in the element predicate).
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => b.Comments.Any(c => c.Text == "t")));

        Assert.False(translator.TryTranslate(body, out _));
    }

    [Fact]
    public void Nested_owned_collection_Any_is_not_declined_by_the_correlation_guard()
    {
        // The guard must not over-decline: `c` is bound by the inner lambda, not free, so nested Any translates.
        var translator = NewTranslator(GetOwnedBlogEntityType());
        var body = PredicateBody<OwnedBlog>(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t")));

        Assert.True(translator.TryTranslate(body, out var result));
        var outer = Assert.IsType<MongoElemMatchExpression>(result);
        var inner = Assert.IsType<MongoElemMatchExpression>(outer.ElementPredicate);
        Assert.Equal("Comments", inner.ArrayPath);
    }

    // ------------------------------------------------------------------
    // Owned-collection All → negated $elemMatch
    // ------------------------------------------------------------------
    //
    // These render full MQL because the point is the negated-complement shape ($not/$elemMatch over the
    // De Morgan'd predicate).

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
        // No exact complement for field-to-field, and $expr inside $elemMatch is a server error, so decline.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => p.Rank > p.Other)));
    }

    [Fact]
    public void Owned_collection_All_with_a_correlated_element_predicate_declines()
    {
        // Same ReferencesEnclosingScope guard as Any.
        Assert.Null(TryTranslateBlogPredicate(b => b.Posts.All(p => b.Title == "x")));
    }

    // ------------------------------------------------------------------
    // Owned-collection Count in a predicate goes native
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
        // The inner array is element-relative ("Comments"), as $elemMatch expects.
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(
            TryTranslateBlogPredicate(b => b.Posts.Any(p => p.Comments.Count > 1)));

        var inner = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Comments", Assert.IsType<MongoSizeExpression>(inner.Left).FieldName);
    }

    [Fact]
    public void A_mapped_scalar_property_named_Count_is_not_mistaken_for_a_cardinality_expression()
    {
        // A mapped `Count` property resolves as a field, not a cardinality. This goes through TranslateComparison's
        // first branch; the sibling test covers TranslateOperand (see TryMatchCountExpression's remarks).
        var translator = NewTranslator(GetEntityType<Order>());

        Assert.True(translator.TryTranslate(
            PredicateBody<Order>(o => o.Count > 2), out var translated));

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal("Count", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void A_mapped_scalar_property_named_Count_still_resolves_as_a_field_inside_TranslateOperand()
    {
        // Field-to-field, so both operands go through TranslateOperand. Protection is structural
        // (TryResolveOwnedCollectionPath's zero-hop check), not call-site order.
        var translator = NewTranslator(GetEntityType<Order>());

        Assert.True(translator.TryTranslate(PredicateBody<Order>(o => o.Count > o.Qty), out var translated));

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal("Count", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
        Assert.Equal("Qty", Assert.IsType<MongoFieldExpression>(comparison.Right).ElementName);
    }

    [Fact]
    public void A_predicated_Count_now_translates_to_a_filtered_size_comparison()
    {
        // Count(pred) builds a MongoFilteredSizeExpression ($size over $filter).
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
        // A regex element predicate has no aggregation rendering, but the translator admits it; the decline
        // happens at pipeline build (MongoAggregationExpressionRenderer.Render), giving a graceful fallback
        // (see NativeOwnedCollectionFilteredCountTests).
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
    public void An_outer_scoped_owned_navigation_null_equality_declines_in_a_two_scope_element_translator()
    {
        // `b.Posts.Any(p => b.Address == null)`: an outer owned-nav path as a MongoElementRefExpression would
        // render element-relative ("$$this.Address", always missing), answering true for every row.
        // MongoOuterFieldExpression needs an IProperty a navigation lacks, so this must decline.
        var blog = GetOwnedBlogEntityType();
        var postType = blog.FindNavigation(nameof(OwnedBlog.Posts))!.TargetEntityType;
        var addressType = blog.FindNavigation(nameof(OwnedBlog.Address))!.TargetEntityType;

        var bParam = Expression.Parameter(blog.ClrType, "b");
        var body = Expression.Equal(
            Expression.Property(bParam, nameof(OwnedBlog.Address)),
            Expression.Constant(null, addressType.ClrType));

        // The element-scoped two-scope translator the correlated-quantifier / Count(pred) arms build:
        // outerParam set, innerPrefix null.
        var translator = new MongoExpressionTranslator(postType, bParam, blog, innerPrefix: null);

        Assert.False(translator.TryTranslate(body, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Correlated_Count_element_predicate_with_two_distinct_free_parameters_declines()
    {
        // The Count(pred) two-scope arm requires exactly one free parameter equal to SelfParam.
        // FreeParameterVisitor.FoundParameter reports null for two or more distinct free parameters; otherwise
        // the second would resolve by name against the element type (wrong rows). Hand-built defense in depth:
        // no known EF query produces two free parameters here.
        var entityType = GetOwnedBlogEntityType();
        var translator = NewTranslator(entityType);

        var bParam = Expression.Parameter(typeof(OwnedBlog), "b");
        var pParam = Expression.Parameter(typeof(OwnedPost), "p");
        // An unrelated free parameter: not p, not a query parameter, not SelfParam.
        var xParam = Expression.Parameter(typeof(int), "x");

        var postsMember = Expression.Property(bParam, nameof(OwnedBlog.Posts));

        // Post.Title collides with Blog.Title, so a weakened guard would resolve b.Title as the element's.
        var titleEqualsOuter = Expression.Equal(
            Expression.Property(pParam, nameof(OwnedPost.Title)),
            Expression.Property(bParam, nameof(OwnedBlog.Title)));
        var rankEqualsX = Expression.Equal(Expression.Property(pParam, nameof(OwnedPost.Rank)), xParam);
        var predicateBody = Expression.AndAlso(titleEqualsOuter, rankEqualsX);
        var predicateLambda = Expression.Lambda(predicateBody, pParam);

        var countCall = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Count), [typeof(OwnedPost)], postsMember, predicateLambda);
        var comparisonBody = Expression.GreaterThan(countCall, Expression.Constant(0));

        // As in NativeSlotPopulator.PopulateNativeSlots; bParam is also the first free parameter encountered.
        translator.SelfParam = bParam;

        Assert.False(translator.TryTranslate(comparisonBody, out var result));
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // A top-level EF.Property leaf resolves in all three positions
    // (predicate / sort key / projection value).
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

    // Builds EF.Property with a bare receiver (as nav-expansion emits); the C#-lambda tests above cover the
    // Convert-to-object receiver Roslyn may emit.
    private static MethodCallExpression EfProperty<TProperty>(Expression root, string name)
        => Expression.Call(
            typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(TProperty)),
            root,
            Expression.Constant(name));

    [Fact]
    public void EF_Property_on_the_outer_param_resolves_against_the_OUTER_scope_by_identity()
    {
        // Both types declare "Name"; the EF.Property spelling must route by parameter identity too
        // (cf. Two_scope_shadowed_member_name_resolves_by_parameter_identity_not_name).
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
        // A composite-PK component resolves to "_id.<element>" in both spellings.
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
    // Nullable<T>.Value peels to the underlying field; Nullable<T>.HasValue
    // becomes the "!= null" node.
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

        // The two spellings must be identical in the IR, so the renderer and negator handle HasValue for free.
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

        // Pins the rendered form: !HasValue must select stored null and missing. `$not` over `$ne: null` does,
        // since $eq/$ne partition every BSON value including missing.
        var rendered = new MongoQueryLanguageRenderer().Render(result!, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ 'NullableAge' : { '$not' : { '$ne' : null } } }"),
            rendered.AsBsonDocument);
    }

    [Fact]
    public void A_user_type_member_named_Value_is_NOT_peeled()
    {
        // The peel must require Nullable<T>, not just the name "Value": `Code` is a mapped value-converted
        // struct, so a name-only peel would resolve `x.Code` for `x.Code.Value`, bypassing the converter.
        using var db = SingleEntityDbContext.Create<CodedEntity>(
            mb => mb.Entity<CodedEntity>().Property(e => e.Code)
                .HasConversion(c => "X" + c.Value, s => new CustomerCode(s.Substring(1))));
        var entityType = db.Model.FindEntityType(typeof(CodedEntity))!;
        var translator = NewTranslator(entityType);
        var param = Expression.Parameter(typeof(CodedEntity), "e");

        // "Value" on a user struct, not Nullable<T>.
        var userValue = Expression.Property(
            Expression.Property(param, nameof(CodedEntity.Code)), nameof(CustomerCode.Value));
        Assert.False(translator.TryTranslateField(userValue, out _));

        // Control: the receiver itself resolves, so the decline above is the conjunct, not a broken fixture.
        Assert.True(translator.TryTranslateField(Expression.Property(param, nameof(CodedEntity.Code)), out var code));
        Assert.Equal("Code", code!.ElementName);

        // Control: a real Nullable<T>.Value on the same entity still peels.
        var realNullable = Expression.Property(
            Expression.Property(param, nameof(CodedEntity.Amount)), "Value");
        Assert.True(translator.TryTranslateField(realNullable, out var amount));
        Assert.Equal("Amount", amount!.ElementName);
    }

    // ------------------------------------------------------------------
    // A cast-bearing sort key must not be stripped to the raw field
    // ------------------------------------------------------------------

    [Fact]
    public void Sort_key_keeps_a_narrowing_cast_so_it_declines_rather_than_sorting_by_the_raw_value()
    {
        var translator = NewTranslator(GetEntityType<Customer>());

        // (int)c.DoubleScore changes order, so it must not resolve to the raw field.
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

    // ------------------------------------------------------------------
    // Constructed-tuple comparison (`new Tuple<string>(c.Name) == new Tuple<string>("A")`, as in
    // Where_compare_tuple_constructed_equal): must be recognized before the general field-to-field/$expr path.
    // ------------------------------------------------------------------

    [Fact]
    public void Single_value_tuple_equality_translates_to_binary_of_tuples()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => new Tuple<string>(c.Name) == new Tuple<string>("A");

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);

        var left = Assert.IsType<MongoTupleExpression>(binary.Left);
        var leftField = Assert.IsType<MongoFieldExpression>(Assert.Single(left.Elements));
        Assert.Equal("Name", leftField.ElementName);

        var right = Assert.IsType<MongoTupleExpression>(binary.Right);
        var rightConstant = Assert.IsType<MongoConstantExpression>(Assert.Single(right.Elements));
        Assert.Equal("A", rightConstant.Value);
    }

    [Fact]
    public void Multi_value_tuple_inequality_translates_to_binary_of_tuples_per_element()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate =
            c => new Tuple<string, string>(c.Name, c.Nickname) != new Tuple<string, string>("A", "B");

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.NotEqual, binary.Operator);

        var left = Assert.IsType<MongoTupleExpression>(binary.Left);
        Assert.Equal(2, left.Elements.Count);
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(left.Elements[0]).ElementName);
        Assert.Equal("Nickname", Assert.IsType<MongoFieldExpression>(left.Elements[1]).ElementName);

        var right = Assert.IsType<MongoTupleExpression>(binary.Right);
        Assert.Equal(2, right.Elements.Count);
        Assert.Equal("A", Assert.IsType<MongoConstantExpression>(right.Elements[0]).Value);
        Assert.Equal("B", Assert.IsType<MongoConstantExpression>(right.Elements[1]).Value);
    }

    // ------------------------------------------------------------------
    // The `Tuple.Create(...)` spelling: a MethodCallExpression, not a NewExpression, so it needs its own arm.
    // ------------------------------------------------------------------

    [Fact]
    public void Tuple_create_multi_value_equality_translates_to_binary_of_tuples()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate =
            c => Tuple.Create(c.Name, c.Nickname) == Tuple.Create("A", "B");

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);

        var left = Assert.IsType<MongoTupleExpression>(binary.Left);
        Assert.Equal(2, left.Elements.Count);
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(left.Elements[0]).ElementName);
        Assert.Equal("Nickname", Assert.IsType<MongoFieldExpression>(left.Elements[1]).ElementName);

        var right = Assert.IsType<MongoTupleExpression>(binary.Right);
        Assert.Equal(2, right.Elements.Count);
        Assert.Equal("A", Assert.IsType<MongoConstantExpression>(right.Elements[0]).Value);
        Assert.Equal("B", Assert.IsType<MongoConstantExpression>(right.Elements[1]).Value);
    }

    // A field-free `Tuple.Create(...)` is funcletized into one parameter holding the whole Tuple; it must
    // decompose into one MongoParameterExpression per element (same Name, ArrayElementIndex 0..n-1).
    [Fact]
    public void Tuple_create_multi_value_equality_against_a_materialized_tuple_parameter_decomposes_per_element()
    {
        var entityType = GetEntityType<Customer>();
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var nameMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Name))!);
        var nicknameMember =
            Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Nickname))!);

        var tupleCreate = typeof(Tuple).GetMethods()
            .First(m => m.Name == nameof(Tuple.Create) && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(string), typeof(string));
        var leftTuple = Expression.Call(tupleCreate, nameMember, nicknameMember);

#if EF8 || EF9
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "tuple_0";
        Expression efParam = Expression.Parameter(typeof(Tuple<string, string>), paramName);
#else
        const string paramName = "__tuple_0";
        Expression efParam = new QueryParameterExpression(paramName, typeof(Tuple<string, string>));
#endif

        var body = Expression.Equal(leftTuple, efParam);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);

        var right = Assert.IsType<MongoTupleExpression>(binary.Right);
        Assert.Equal(2, right.Elements.Count);

        var firstElement = Assert.IsType<MongoParameterExpression>(right.Elements[0]);
        Assert.Equal(paramName, firstElement.Name);
        Assert.Equal(0, firstElement.ArrayElementIndex);

        var secondElement = Assert.IsType<MongoParameterExpression>(right.Elements[1]);
        Assert.Equal(paramName, secondElement.Name);
        Assert.Equal(1, secondElement.ArrayElementIndex);
    }
}
