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
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Infrastructure;

public class MongoModelValidatorTests : IDisposable
{
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();
    public void Dispose() => _tempDatabase.Dispose();

    class EntityWithTwoUnderscoreIds
    {
        [Column("_id")]
        public int key1 { get; set; }

        [Column("_id")]
        public string key2 { get; set; }
    }

    class DoubleNamedEntity
    {
        public int _id { get; set; }

        public string name1 { get; set; }
        public string name2 { get; set; }
    }

    class ConfiguredIdNamedEntity
    {
        public string ThisWillBePrimaryKey { get; set; }

        public string SomethingElse { get; set; }
    }


    [Fact]
    public void Validate_throws_when_duplicate_properties_attributed_to_same_element_name()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<EntityWithTwoUnderscoreIds>();
        var db = SingleEntityDbContext.Create(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'EntityWithTwoUnderscoreIds'", ex.Message);
        Assert.Contains("'_id'", ex.Message);
        Assert.Contains("'key1'", ex.Message);
        Assert.Contains("'key2'", ex.Message);
    }

    [Fact]
    public void Validate_throws_when_duplicate_properties_configured_to_same_element_name()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("name");
            dneBuilder.Property(p => p.name2).HasElementName("name");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'name'", ex.Message);
        Assert.Contains("'name1'", ex.Message);
        Assert.Contains("'name2'", ex.Message);
    }

    [Fact]
    public void Validate_throws_when_property_element_name_starts_with_dollar_sign()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("$something");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'name1'", ex.Message);
        Assert.Contains("'$something'", ex.Message);
    }

    [Fact]
    public void Validate_succeeds_if_property_element_name_ends_with_dollar_sign()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("something$");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public void Validate_succeeds_if_property_element_name_contains_dollar_sign_not_at_the_start()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("some$thing");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public void Validate_throws_when_property_element_name_starts_with_dot()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName(".something");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'name1'", ex.Message);
        Assert.Contains("'.something'", ex.Message);
    }

    [Fact]
    public void Validate_throws_when_property_element_name_ends_with_dot()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("something.");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'name1'", ex.Message);
        Assert.Contains("'something.'", ex.Message);
    }

    [Fact]
    public void Validate_throws_when_property_element_name_contains_dot()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("some.thing");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'name1'", ex.Message);
        Assert.Contains("'some.thing'", ex.Message);
    }

    [Fact]
    public void Validate_succeeds_when_primary_key_configured_correctly()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<ConfiguredIdNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<ConfiguredIdNamedEntity>();
            dneBuilder.HasKey(e => e.ThisWillBePrimaryKey);
            dneBuilder.Property(p => p.ThisWillBePrimaryKey).HasElementName("_id");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public void Validate_throws_when_primary_key_conflicts_with_different_underscore_id_mapped_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<ConfiguredIdNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<ConfiguredIdNamedEntity>();
            dneBuilder.HasKey(e => e.ThisWillBePrimaryKey);
            dneBuilder.Property(p => p.SomethingElse).HasElementName("_id");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains("'ConfiguredIdNamedEntity'", ex.Message);
        Assert.Contains("'_id'", ex.Message);
        Assert.Contains("'ThisWillBePrimaryKey'", ex.Message);
    }

    [Fact]
    public void Validate_throws_when_entity_has_shadow_properties()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<DoubleNamedEntity>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property<DateTime>("ShadowDateTime");
        });

        var ex = Assert.Throws<NotSupportedException>(() => db.Model);
        Assert.Contains("'DoubleNamedEntity'", ex.Message);
        Assert.Contains("'ShadowDateTime'", ex.Message);
    }

}
