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

using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class ColumnAttributeConventionTests
{
    [Fact]
    public static void ColumnAttribute_specified_names_are_used_as_element_names()
    {
        using var db = new BaseDbContext();
        Assert.Equal("attributeSpecifiedName", GetElementName(db, (Customer c) => c.Name));
    }

    [Fact]
    public static void ModelBuilder_specified_field_names_override_ColumnAttribute_names()
    {
        using var db = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("fluentSpecifiedName", GetElementName(db, (Customer c) => c.Name));
    }

    static string GetElementName<TEntity, TProperty>(DbContext context, Expression<Func<TEntity, TProperty>> propertyExpression)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))!;
        var property = entityType.FindProperty(propertyExpression.GetMemberAccess())!;
        return property.GetElementName();
    }

    class Customer
    {
        public int Id { get; set; }

        [Column("attributeSpecifiedName")]
        public string Name { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    class ModelBuilderSpecifiedDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().Property(c => c.Name).HasElementName("fluentSpecifiedName");
        }
    }
}
