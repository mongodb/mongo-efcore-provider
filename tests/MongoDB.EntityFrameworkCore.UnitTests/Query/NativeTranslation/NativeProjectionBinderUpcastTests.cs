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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// A reference-type upcast wrapped around a constructed DTO (<c>x =&gt; (BaseDto)new DerivedDto { … }</c>), which EF's
/// nav-expansion produces when a covariant <c>IQueryable&lt;Derived&gt;</c> is consumed as <c>IQueryable&lt;Base&gt;</c>
/// (EF's <c>Return_type_of_singular_operator_is_preserved</c>), binds the construction itself.
/// </summary>
public class NativeProjectionBinderUpcastTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    private class IdDto
    {
        public string Name { get; set; } = "";
    }

    private class IdAndCityDto : IdDto
    {
        public string City { get; set; } = "";
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Customer>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Customer))!);
    }

    [Fact]
    public void Upcast_member_init_projection_binds_the_construction()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, IdDto>> selector = c => (IdDto)new IdAndCityDto { Name = c.Name, City = c.City };
        Assert.Equal(ExpressionType.Convert, selector.Body.NodeType); // guard: the compiler kept the Convert

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "Name");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "City");
    }

    [Fact]
    public void Boxing_conversion_of_a_value_type_construction_is_not_unwrapped()
    {
        var mongoQ = TestQuery();
        Expression<Func<Customer, object>> selector = c => (object)new ValueTuple<string>(c.Name);

        // Boxing a struct is not a reference upcast: the new arm must not fire for it.
        NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector);
        Assert.DoesNotContain(mongoQ.Select.Projection, p => p.Alias == "Item1");
    }
}
