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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

// Uses real IEntityType metadata (ModelBuilder pattern from NativeSelectManyBinderTests) and a real
// TransparentIdentifierFactory.Create(...) parameter type. The real type exposes Outer/Inner as fields, not
// properties; a hand-written property-based fixture would mask a GetProperty-only guard that declines every real
// query. `Expression.PropertyOrField` is used so tests don't depend on which kind the member is.
public class NativeJoinScopeTranslatorTests
{
    private class OuterEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class InnerEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Total { get; set; }
    }

    private class OtherEntity
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    private const string InnerPrefix = "_lookup_Inner";

    private static (IEntityType Outer, IEntityType Inner) GetEntityTypes()
    {
        using var db = SingleEntityDbContext.Create<OuterEntity>(mb => mb.Entity<InnerEntity>());
        var outer = db.Model.FindEntityType(typeof(OuterEntity))!;
        var inner = db.Model.FindEntityType(typeof(InnerEntity))!;
        return (outer, inner);
    }

    private static MongoJoinScope NewScope(bool isLeftOuter = false)
    {
        var (outer, inner) = GetEntityTypes();
        return new MongoJoinScope(outer, [new MongoJoinScopeLevel(inner, InnerPrefix, isLeftOuter)]);
    }

    /// <summary>The real EF-generated flat <c>TransparentIdentifier&lt;OuterEntity, InnerEntity&gt;</c> type.</summary>
    private static ParameterExpression NewRootParam()
        => Expression.Parameter(TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity)), "x");

    [Fact]
    public void Translates_outer_side_member_access_unprefixed()
    {
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name");

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result);

        Assert.True(translated);
        // MongoOuterFieldExpression: TranslateOperand resolves Outer-rooted members via TryResolveMember's isOuter
        // branch. Renders identically to MongoFieldExpression outside a $filter/$map element scope.
        var field = Assert.IsType<MongoOuterFieldExpression>(result);
        Assert.Equal("Name", field.ElementName);
    }

    [Fact]
    public void Translates_inner_side_member_access_prefixed()
    {
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Total");

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal(InnerPrefix + ".Total", field.ElementName);
    }

    [Fact]
    public void Translates_mixed_scope_equality_predicate()
    {
        // Same member name on both scopes: resolution is by parameter identity, not name.
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name"),
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Name"));

        var translated = NativeJoinScopeTranslator.TryTranslatePredicate(scope, x, body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        // Left (Outer) is MongoOuterFieldExpression — same reason as Translates_outer_side_member_access_unprefixed.
        var left = Assert.IsType<MongoOuterFieldExpression>(binary.Left);
        Assert.Equal("Name", left.ElementName);
        var right = Assert.IsType<MongoFieldExpression>(binary.Right);
        Assert.Equal(InnerPrefix + ".Name", right.ElementName);
    }

    [Fact]
    public void Declines_a_shape_the_underlying_translator_cannot_handle()
    {
        // x.Outer.Name.ToUpper(): no query-dialect equivalent.
        var scope = NewScope();
        var x = NewRootParam();
        var name = Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name");
        var toUpper = typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!;
        Expression body = Expression.Call(name, toUpper);

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ── Guard: real (field-based) TransparentIdentifier shape actually reaches the translator ──────────
    // Builds the parameter type via EF's own TransparentIdentifierFactory.Create, so it fails if the type-shape
    // guard regresses to a property-only lookup (the real type uses fields).

    [Fact]
    public void Real_EF_generated_TransparentIdentifier_type_exposes_Outer_and_Inner_as_fields()
    {
        var type = TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity));

        Assert.Null(type.GetProperty("Outer"));
        Assert.Null(type.GetProperty("Inner"));
        Assert.NotNull(type.GetField("Outer"));
        Assert.NotNull(type.GetField("Inner"));
    }

    [Fact]
    public void Translates_against_the_real_field_based_TransparentIdentifier_shape()
    {
        // TryTranslateValue doesn't touch the guard; the fields test above pins the shape.
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name");

        Assert.True(NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out _));
    }

    // ── Guard 1: unscoped root access alongside a genuine Outer/Inner rewrite declines ──────────────────

    [Fact]
    public void Declines_when_body_also_accesses_root_param_outside_Outer_or_Inner()
    {
        // Not a realistic EF shape, but exercises SawUnscopedRootAccess directly: any other use of rootParam (here
        // a bare ToString()) alongside x.Outer.Name must decline the whole body, not partially translate it.
        var scope = NewScope();
        var x = NewRootParam();
        var outerName = Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name");
        var toString = typeof(object).GetMethod(nameof(ToString))!;
        Expression body = Expression.AndAlso(
            Expression.Equal(outerName, Expression.Constant("Alice")),
            Expression.NotEqual(Expression.Call(x, toString), Expression.Constant(null, typeof(string))));

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result)
            || NativeJoinScopeTranslator.TryTranslatePredicate(scope, x, body, out result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ── Guard 2: nested/chained TransparentIdentifier<TransparentIdentifier<O,I>,I> declines ────────────

    [Fact]
    public void Declines_a_nested_chained_TransparentIdentifier_shape()
    {
        // A second chained join recycles the first join's JoinScope, but the parameter is the nested
        // TransparentIdentifier<TransparentIdentifier<OuterEntity,InnerEntity>, OtherEntity>. The recorded Inner
        // doesn't match the parameter's Inner, so the guard must decline rather than rewrite the wrong level.
        var scope = NewScope();
        var firstJoinType = TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity));
        var nestedType = TransparentIdentifierFactory.Create(firstJoinType, typeof(OtherEntity));
        var x = Expression.Parameter(nestedType, "x");
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label");

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void Declines_a_nested_chained_TransparentIdentifier_shape_even_when_types_coincidentally_match()
    {
        // Both joins target InnerEntity (SameTargetTypeJoinTests' shape), so the Inner type check passes; the
        // guard still declines because the parameter's Outer (the first-join TransparentIdentifier) isn't
        // scope.OuterEntityType.ClrType.
        var scope = NewScope();
        var firstJoinType = TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity));
        var nestedType = TransparentIdentifierFactory.Create(firstJoinType, typeof(InnerEntity));
        var x = Expression.Parameter(nestedType, "x");
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Name");

        var translated = NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ── Guard 3: ReferencesInnerScope ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReferencesInnerScope_is_false_for_an_outer_only_predicate()
    {
        var x = NewRootParam();
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name"),
            Expression.Constant("Alice"));

        Assert.False(NativeJoinScopeTranslator.ReferencesInnerScope(x, body));
    }

    [Fact]
    public void ReferencesInnerScope_is_true_for_a_bare_inner_leaf()
    {
        var x = NewRootParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Total");

        Assert.True(NativeJoinScopeTranslator.ReferencesInnerScope(x, body));
    }

    [Fact]
    public void ReferencesInnerScope_is_true_for_a_mixed_outer_and_inner_predicate()
    {
        var x = NewRootParam();
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name"),
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Name"));

        Assert.True(NativeJoinScopeTranslator.ReferencesInnerScope(x, body));
    }

    // ── TryTranslateRootScopeOnly: chained (2-level) join scope ──────────────────────────────────────────
    // TransparentIdentifier<TransparentIdentifier<OuterEntity, InnerEntity>, OtherEntity>: Levels[0] is the first
    // join (InnerEntity), Levels[1] the second (OtherEntity).

    private static MongoJoinScope NewTwoLevelScope()
    {
        var (outer, inner) = GetEntityTypes();
        using var db = SingleEntityDbContext.Create<OuterEntity>(
            mb =>
            {
                mb.Entity<InnerEntity>();
                mb.Entity<OtherEntity>();
            });
        var other = db.Model.FindEntityType(typeof(OtherEntity))!;
        return new MongoJoinScope(outer, [new MongoJoinScopeLevel(inner, InnerPrefix, false), new MongoJoinScopeLevel(other, "_lookup_Other", false)]);
    }

    /// <summary>
    /// The real EF-generated nested <c>TransparentIdentifier&lt;TransparentIdentifier&lt;OuterEntity,
    /// InnerEntity&gt;, OtherEntity&gt;</c> type a chained (two-join) query produces.
    /// </summary>
    private static ParameterExpression NewChainedRootParam()
    {
        var firstJoinType = TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity));
        var nestedType = TransparentIdentifierFactory.Create(firstJoinType, typeof(OtherEntity));
        return Expression.Parameter(nestedType, "x");
    }

    [Fact]
    public void TryTranslateRootScopeOnly_translates_a_root_scoped_predicate_over_a_two_level_chain()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Outer.Name == "Alice" — root-scoped: OuterEntity.Name.
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name"),
            Expression.Constant("Alice"));

        var translated = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(scope, x, body, valueMode: false, out var result);

        Assert.True(translated);
        Assert.NotNull(result);
    }

    [Fact]
    public void TryTranslateRootScopeOnly_declines_a_predicate_touching_the_first_joins_inner_level()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Inner.Total == 5 — touches the FIRST join's Inner side; must decline.
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Inner"), "Total"),
            Expression.Constant(5));

        var translated = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(scope, x, body, valueMode: false, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void TryTranslateRootScopeOnly_declines_a_predicate_touching_the_second_joins_inner_level()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Inner.Label == "foo" — the second (outermost) join's Inner side; must also decline.
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label"),
            Expression.Constant("foo"));

        var translated = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(scope, x, body, valueMode: false, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void TryTranslateRootScopeOnly_declines_a_predicate_mixing_root_and_inner_scopes()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Outer.Name == x.Inner.Label — spans two distinct scope indices (0 and 2); CrossScope.
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name"),
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label"));

        var translated = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(scope, x, body, valueMode: false, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void TryTranslateRootScopeOnly_translates_a_root_scoped_value_over_a_two_level_chain()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Outer.Name (value/sort-key mode, not a predicate).
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name");

        var translated = NativeJoinScopeTranslator.TryTranslateRootScopeOnly(scope, x, body, valueMode: true, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Name", field.ElementName);
    }

    // ── TryTranslateSingleScope: any single scope in a chain, not just the root ─────────────────────────────

    [Fact]
    public void TryTranslateSingleScope_translates_a_root_scoped_value_over_a_two_level_chain()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Outer.Name — root (scope 0): must resolve unprefixed, same as TryTranslateRootScopeOnly.
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name");

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Name", field.ElementName);
    }

    [Fact]
    public void TryTranslateSingleScope_translates_the_first_joins_inner_field_prefixed()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Inner.Total — the FIRST join's Inner side (scope 1).
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Inner"), "Total");

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal(InnerPrefix + ".Total", field.ElementName);
    }

    [Fact]
    public void TryTranslateSingleScope_translates_the_second_joins_inner_field_prefixed()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Inner.Label — the SECOND (outermost) join's Inner side (scope 2).
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label");

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("_lookup_Other.Label", field.ElementName);
    }

    [Fact]
    public void TryTranslateSingleScope_translates_a_computed_expression_within_one_level()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Inner.Total + 1 — computed, but still rooted at exactly ONE scope (level 1).
        Expression body = Expression.Add(
            Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Inner"), "Total"),
            Expression.Constant(1));

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        var left = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal(InnerPrefix + ".Total", left.ElementName);
    }

    [Fact]
    public void TryTranslateSingleScope_declines_a_leaf_mixing_root_and_a_joins_inner_scope()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Outer.Outer.Name.Length + x.Inner.Label.Length — spans scopes 0 and 2. Length (a member, not a
        // method) keeps this purely a cross-scope decline.
        Expression body = Expression.Add(
            Expression.PropertyOrField(
                Expression.PropertyOrField(Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Outer"), "Name"),
                "Length"),
            Expression.PropertyOrField(
                Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label"), "Length"));

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    [Fact]
    public void TryTranslateSingleScope_declines_a_shape_the_underlying_translator_cannot_handle()
    {
        var scope = NewTwoLevelScope();
        var x = NewChainedRootParam();

        // x => x.Inner.Label.ToUpper() — single scope (2), but ToUpper() has no query-dialect equivalent.
        var label = Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Label");
        var toUpper = typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!;
        Expression body = Expression.Call(label, toUpper);

        var translated = NativeJoinScopeTranslator.TryTranslateSingleScope(scope, x, body, valueMode: true, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ── TryMatchScopeNullCheck: depth-agnostic null-check matcher ──────────────────────────────────────

    [Fact]
    public void Matches_bare_Inner_not_equal_null_at_depth_one()
    {
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.NotEqual(
            Expression.PropertyOrField(x, "Inner"), Expression.Constant(null, typeof(InnerEntity)));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out var scopeIndex, out var isNotNull);

        Assert.True(matched);
        Assert.Equal(1, scopeIndex);
        Assert.True(isNotNull);
    }

    [Fact]
    public void Matches_null_equal_bare_Inner_operand_order_reversed()
    {
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.Equal(
            Expression.Constant(null, typeof(InnerEntity)), Expression.PropertyOrField(x, "Inner"));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out var scopeIndex, out var isNotNull);

        Assert.True(matched);
        Assert.Equal(1, scopeIndex);
        Assert.False(isNotNull);
    }

    [Fact]
    public void Declines_null_check_against_the_root_scope()
    {
        // Only an Inner side (index > 0) can be absent after a left-outer $lookup+$unwind.
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.Equal(
            Expression.PropertyOrField(x, "Outer"), Expression.Constant(null, typeof(OuterEntity)));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out _, out _);

        Assert.False(matched);
    }

    [Fact]
    public void Declines_a_member_access_beyond_the_bare_scope_leaf()
    {
        // A null check on a field of the joined entity, not the join itself: the operand must be the bare scope
        // parameter.
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.NotEqual(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Name"),
            Expression.Constant(null, typeof(string)));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out _, out _);

        Assert.False(matched);
    }

    [Fact]
    public void Declines_a_self_compare_with_no_null_operand()
    {
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.Equal(
            Expression.PropertyOrField(x, "Inner"), Expression.PropertyOrField(x, "Inner"));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out _, out _);

        Assert.False(matched);
    }

    [Fact]
    public void Declines_a_non_equality_test()
    {
        var scope = NewScope(isLeftOuter: true);
        var x = NewRootParam();
        Expression test = Expression.AndAlso(Expression.Constant(true), Expression.Constant(true));

        var matched = NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, x, test, out _, out _);

        Assert.False(matched);
    }
}
