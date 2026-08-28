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

// Follow NativeSelectManyBinderTests.cs's ModelBuilder/OnModelCreating pattern (same directory) to get real
// IEntityType instances for "Outer"/"Inner"-shaped test entities — do not hand-mock IEntityType, since
// MongoExpressionTranslator reads real IProperty/IEntityType metadata.
//
// The parameter TYPE for every test in this file is built via the REAL
// Microsoft.EntityFrameworkCore.Query.TransparentIdentifierFactory.Create(...) — the exact type EF Core's
// nav-expansion generates for a join, NOT a hand-written class. This matters: that real type exposes
// "Outer"/"Inner" as public FIELDS, not properties (verified empirically against EF8 and EF10) — an earlier
// version of this file used a hand-written class with auto-PROPERTIES named Outer/Inner, which is why a
// field-vs-property bug in NativeJoinScopeTranslator's type-shape guard (it called Type.GetProperty, which
// always returns null for a real join parameter) passed every test here while declining 100% of real
// queries. `Expression.PropertyOrField` is used throughout so the same test code works regardless of which
// kind the member turns out to be.
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
        return new MongoJoinScope(outer, inner, InnerPrefix, isLeftOuter);
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
        var field = Assert.IsType<MongoFieldExpression>(result);
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
        // Two DIFFERENT members sharing a name across scopes — proves resolution is by parameter identity
        // via the rewrite, never by member name.
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.Equal(
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name"),
            Expression.PropertyOrField(Expression.PropertyOrField(x, "Inner"), "Name"));

        var translated = NativeJoinScopeTranslator.TryTranslatePredicate(scope, x, body, out var result);

        Assert.True(translated);
        var binary = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
        var left = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("Name", left.ElementName);
        var right = Assert.IsType<MongoFieldExpression>(binary.Right);
        Assert.Equal(InnerPrefix + ".Name", right.ElementName);
    }

    [Fact]
    public void Declines_a_shape_the_underlying_translator_cannot_handle()
    {
        // x.Outer.Name.ToUpper() — ToUpper has no query-dialect equivalent (mirrors
        // MongoExpressionTranslatorTests.Unsupported_method_call_reports_not_translatable).
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
    // Pins the exact defect the review round found: a hand-written property-based Ti fixture masked a
    // Type.GetProperty-only guard that always returns null (declines) for a real, field-based
    // TransparentIdentifier<TOuter,TInner> parameter. This test constructs the parameter type via the SAME
    // TransparentIdentifierFactory.Create EF Core itself uses, so it fails if the guard regresses back to a
    // property-only lookup.

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
        // Would have passed even with the field-vs-property bug: TryTranslateValue doesn't touch the guard.
        // Guarded belt-and-braces by Real_EF_generated_TransparentIdentifier_type_exposes_Outer_and_Inner_as_fields
        // above, which pins that Outer/Inner really are fields on this type.
        var scope = NewScope();
        var x = NewRootParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(x, "Outer"), "Name");

        Assert.True(NativeJoinScopeTranslator.TryTranslateValue(scope, x, body, out _));
    }

    // ── Guard 1: unscoped root access alongside a genuine Outer/Inner rewrite declines ──────────────────

    [Fact]
    public void Declines_when_body_also_accesses_root_param_outside_Outer_or_Inner()
    {
        // Not a realistic EF-generated shape (a flat TransparentIdentifier only ever exposes Outer/Inner),
        // but exercises the SawUnscopedRootAccess guard directly: some OTHER access to rootParam (here, a
        // bare-parameter ToString() call, rather than a member access rooted on it) alongside a genuine
        // x.Outer.Name access must still decline the WHOLE body, not partially translate it.
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
        // The actual regression shape: a SECOND join chained onto the same select recycles the FIRST join's
        // JoinScope (Outer=OuterEntity, Inner=InnerEntity), but the parameter it's evaluated against is the
        // doubly-nested TransparentIdentifier<TransparentIdentifier<OuterEntity,InnerEntity>, OtherEntity>
        // the chained join actually produces. The recorded scope's "Inner" (InnerEntity) does NOT match this
        // parameter's own top-level "Inner" (OtherEntity), so the guard must decline structurally rather than
        // let ScopeSplittingVisitor rewrite the wrong level.
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
        // Sharper version of the case above: the chained join's top-level Inner is InnerEntity too (e.g. two
        // joins onto the same target entity type — SameTargetTypeJoinTests' actual regression shape), so the
        // CLR type comparison alone would pass. The guard must still decline: rootParam.Type itself is the
        // NESTED shape, and its "Outer" member (the whole first-join TransparentIdentifier) does not equal
        // scope.OuterEntityType.ClrType (OuterEntity) — that's what actually catches this.
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
}
