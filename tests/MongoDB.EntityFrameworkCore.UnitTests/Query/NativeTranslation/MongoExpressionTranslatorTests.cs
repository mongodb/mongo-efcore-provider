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

    private class Customer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
        public double DoubleScore { get; set; }
        public string Name { get; set; } = "";
        public bool Active { get; set; }
        public int? NullableAge { get; set; }
        public bool? NullableFlag { get; set; }
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

    [Fact]
    public void Composite_PK_property_access_reports_not_translatable()
    {
        // Build a model where (OrderId, ProductId) form the composite primary key.
        using var db = SingleEntityDbContext.Create<OrderLine>(mb =>
            mb.Entity<OrderLine>().HasKey(e => new { e.OrderId, e.ProductId }));
        var entityType = db.Model.FindEntityType(typeof(OrderLine))!;

        // A predicate over one of the composite-PK components should be rejected.
        var body = PredicateBody<OrderLine>(ol => ol.OrderId == 10248);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
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

    // Empirically, the driver's own LINQ translator emits raw $divide / $mod for int operands with no
    // truncation emulation (confirmed via MongoQueryMode.DriverLinq — see task-7-report.md), so the
    // renderer's existing $expr rendering already matches driver output exactly: no special-casing needed.

    [Fact]
    public void Translates_divide_operand()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Age / c.Score > 1;

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var cmp = Assert.IsType<MongoBinaryExpression>(result);
        var div = Assert.IsType<MongoBinaryExpression>(cmp.Left);
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
    // excludes it, so the whole predicate falls back to driver-LINQ rather than emitting an incorrect
    // $add over strings.

    [Fact]
    public void String_concatenation_operand_is_not_translated()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => c.Name + "y" == "Zed";

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // A numeric cast inside a field-to-field/arithmetic-operand comparison is rejected (falls back):
    // empirically the driver's own LINQ translator renders the SAME cast inconsistently depending on
    // shape (explicit $toDouble on a bare field-to-field comparison, silently dropped inside arithmetic)
    // — reproducing that exactly would mean re-deriving driver-internal numeric-promotion rules, so this
    // shape falls back to driver-LINQ rather than risk diverging from it. See task-7-report.md.

    [Fact]
    public void Cast_in_field_to_field_comparison_reports_not_translatable()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age > c.Score;

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void Cast_in_arithmetic_operand_reports_not_translatable()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age + c.Score > 5;

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // The existing query-native member-vs-constant cast guard (HasNumericConvert) is unchanged by
    // EF-329: it still causes the whole predicate to fall back rather than route through $expr.

    [Fact]
    public void Cast_on_member_vs_constant_still_reports_not_translatable()
    {
        var translator = NewTranslator(GetEntityType<Customer>());
        Expression<Func<Customer, bool>> predicate = c => (double)c.Age > 5.0;

        var translated = translator.TryTranslate(predicate.Body, out var result);

        Assert.False(translated);
        Assert.Null(result);
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
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
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
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
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
        Assert.Equal("Threshold", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
    }
}
