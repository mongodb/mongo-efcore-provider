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

// Same metadata pattern as NativeJoinScopeTranslatorTests: real IEntityType metadata and a real
// TransparentIdentifierFactory type (its Outer/Inner are fields, so use Expression.PropertyOrField).
public class MongoGroupElementTranslatorTests
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

    private const string InnerPrefix = "_lookup_Inner";

    private static (IEntityType Outer, IEntityType Inner) GetEntityTypes()
    {
        using var db = SingleEntityDbContext.Create<OuterEntity>(mb => mb.Entity<InnerEntity>());
        return (db.Model.FindEntityType(typeof(OuterEntity))!, db.Model.FindEntityType(typeof(InnerEntity))!);
    }

    private static MongoGroupElementTranslator NewJoinScoped()
    {
        var (outer, inner) = GetEntityTypes();
        var scope = new MongoJoinScope(outer, [new MongoJoinScopeLevel(inner, InnerPrefix, isLeftOuter: false)]);
        return new MongoGroupElementTranslator(outer, scope, priorGrouping: null);
    }

    private static ParameterExpression NewElementParam()
        => Expression.Parameter(TransparentIdentifierFactory.Create(typeof(OuterEntity), typeof(InnerEntity)), "ti");

    [Fact]
    public void Root_mode_translates_a_root_entity_member()
    {
        var (outer, _) = GetEntityTypes();
        var translator = new MongoGroupElementTranslator(outer, joinScope: null, priorGrouping: null);
        var e = Expression.Parameter(typeof(OuterEntity), "e");

        Assert.False(translator.IsJoinScoped);
        Assert.True(translator.TryTranslateValue(Expression.Property(e, nameof(OuterEntity.Name)), out var result));
        Assert.Equal("Name", Assert.IsAssignableFrom<MongoFieldExpression>(result).ElementName);
    }

    [Fact]
    public void Join_mode_translates_an_outer_hop_unprefixed()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Name");

        Assert.True(translator.IsJoinScoped);
        Assert.True(translator.TryTranslateValue(body, out var result));
        Assert.Equal("Name", Assert.IsAssignableFrom<MongoFieldExpression>(result).ElementName);
    }

    [Fact]
    public void Join_mode_translates_an_inner_hop_under_the_lookup_prefix()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Inner"), "Total");

        Assert.True(translator.TryTranslateValue(body, out var result));
        Assert.Contains(InnerPrefix, Assert.IsAssignableFrom<MongoFieldExpression>(result).ElementName);
    }

    [Fact]
    public void Join_mode_declines_a_body_spanning_both_scopes()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.Add(
            Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Id"),
            Expression.PropertyOrField(Expression.PropertyOrField(ti, "Inner"), "Total"));

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Join_mode_declines_an_entity_typed_parameter()
    {
        // Review Focus #4: a join whose result selector projected one side hands the binder an entity-typed
        // parameter. Name-based resolution against the root entity would silently read the wrong field.
        var translator = NewJoinScoped();
        var inner = Expression.Parameter(typeof(InnerEntity), "x");

        Assert.False(translator.TryTranslateValue(Expression.Property(inner, nameof(InnerEntity.Name)), out _));
        Assert.False(translator.TryTranslate(
            Expression.Equal(Expression.Property(inner, nameof(InnerEntity.Name)), Expression.Constant("a")), out _));
    }

    [Fact]
    public void Join_mode_translates_a_parameter_free_body_with_the_root_translator()
    {
        var translator = NewJoinScoped();

        Assert.True(translator.TryTranslateValue(Expression.Constant(5), out var result));
        Assert.IsType<MongoConstantExpression>(result);
    }

    [Fact]
    public void Join_mode_declines_TryTranslateField_over_the_element()
    {
        var translator = NewJoinScoped();
        var ti = NewElementParam();
        Expression body = Expression.PropertyOrField(Expression.PropertyOrField(ti, "Outer"), "Name");

        Assert.False(translator.TryTranslateField(body, out _));
    }
}
