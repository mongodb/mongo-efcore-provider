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
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Which arm of <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/> a positional construction lands on:
/// the scalar positional path (`_ctorArg&lt;N&gt;` leaves read by index), the whole-entity client construction
/// (<see cref="NativeClientWholeEntityShape"/>), or the row-independent client-evaluated leaf. The selectors are not
/// funcletized here, so the row-independent container reaches the binder as a construction.
/// </summary>
public class NativeProjectionBinderContainerTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    private static string Describe(Customer customer) => customer.Name;

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Customer>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Customer))!);
    }

    private static List<string> Aliases(MongoQueryExpression mongoQ)
        => mongoQ.Select.Projection.Select(p => p.Alias).ToList();

    [Fact]
    public void Scalar_container_takes_the_positional_path_with_one_alias_per_element()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object[]>> selector = c => new object[] { c.Name, c.City };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.True(mongoQ.Select.HasPositionalCtorProjectionShaper);
        Assert.Equal(["_ctorArg0", "_ctorArg1"], Aliases(mongoQ));
    }

    // The latent crash: the multi-argument positional arm admitted the whole entity as a `$$ROOT` leaf.
    [Fact]
    public void Whole_entity_positional_ctor_argument_never_takes_the_scalar_positional_path()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, KeyValuePair<Customer, string>>> selector
            = c => new KeyValuePair<Customer, string>(c, c.City);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.False(mongoQ.Select.HasPositionalCtorProjectionShaper);
        Assert.True(mongoQ.Select.HasClientWrappedWholeEntityShaper);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void Whole_entity_container_element_never_takes_the_scalar_positional_path()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object[]>> selector = c => new object[] { c, c.City };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.False(mongoQ.Select.HasPositionalCtorProjectionShaper);
        Assert.True(mongoQ.Select.HasClientWrappedWholeEntityShaper);
        Assert.Empty(mongoQ.Select.Projection);
    }

    // No whole-entity element, but an opaque client call on the entity: the whole-entity client construction claims it,
    // so the container arm must step aside rather than decline on the untranslatable call.
    [Fact]
    public void Container_the_whole_entity_client_construction_claims_is_left_to_it()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object[]>> selector = c => new object[] { Describe(c), c.City };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.False(mongoQ.Select.HasPositionalCtorProjectionShaper);
        Assert.True(mongoQ.Select.HasClientWrappedWholeEntityShaper);
    }

    // The read side fails closed: a shaper-side ProjectionBindingExpression inside a client-only tree over the whole
    // entity would be resolved by its projection member off the raw whole document (nothing was $project-ed) and read
    // null, so it must not be an accepted leaf. The read-side classifier sends it to Walk, whose default arm declines.
    [Fact]
    public void Read_side_client_whole_entity_tree_declines_a_projection_binding_leaf()
    {
        var mongoQ = TestQuery();
        var entityShaper = new StructuralTypeShaperExpression(
            mongoQ.CollectionExpression.EntityType,
            new ProjectionBindingExpression(mongoQ, new ProjectionMember(), typeof(ValueBuffer)),
            nullable: false);
        var binding = new ProjectionBindingExpression(mongoQ, new ProjectionMember(), typeof(string));
        var classify = MongoShapedQueryCompilingExpressionVisitor.ClassifyClientWholeEntityShaperOperand;

        Assert.Equal(ClientWholeEntityOperand.Walk, classify(binding));

        // Control: the whole entity beside an entity-free constant is admitted.
        Assert.True(NativeClientWholeEntityShape.IsClientOnlyTree(
            Expression.NewArrayInit(typeof(object),
                Expression.Convert(entityShaper, typeof(object)), Expression.Constant("k", typeof(object))),
            classify));
        // The whole entity beside a projection binding is not.
        Assert.False(NativeClientWholeEntityShape.IsClientOnlyTree(
            Expression.NewArrayInit(typeof(object),
                Expression.Convert(entityShaper, typeof(object)), Expression.Convert(binding, typeof(object))),
            classify));
    }

    // All-row-independent: Task 4's client-evaluated leaf (only the constant sentinel is projected), not `_ctorArg<N>`.
    [Fact]
    public void Row_independent_container_is_the_client_evaluated_leaf_not_a_positional_container()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object[]>> selector = c => new object[] { 1, "a" };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.False(mongoQ.Select.HasPositionalCtorProjectionShaper);
        Assert.True(mongoQ.Select.HasClientEvaluatedProjectionLeaf);
        Assert.DoesNotContain(mongoQ.Select.Projection, p => p.Alias?.StartsWith("_ctorArg", StringComparison.Ordinal) == true);
    }
}
