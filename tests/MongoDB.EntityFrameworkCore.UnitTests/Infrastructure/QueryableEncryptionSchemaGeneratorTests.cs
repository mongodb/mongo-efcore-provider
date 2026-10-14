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

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

public static class QueryableEncryptionSchemaGeneratorTests
{
    private static Guid Key(int n) => new($"{n:D8}-2222-3333-4444-555555555555");

    class Root
    {
        public ObjectId _id { get; set; }
        public string Secret { get; set; }
        public Address Home { get; set; }
        public Outer Nested { get; set; }
        public List<Item> Items { get; set; }
    }

    class Address
    {
        public Root Parent { get; set; }
        public string Street { get; set; }
    }

    class Outer
    {
        public Root Parent { get; set; }
        public string OuterSecret { get; set; }
        public Inner Child { get; set; }
        public List<Item> Items { get; set; }
    }

    class Inner
    {
        public Outer Parent { get; set; }
        public string InnerSecret { get; set; }
    }

    class Item
    {
        public object Parent { get; set; }
        public string ItemSecret { get; set; }
        public List<Leaf> Leaves { get; set; }
    }

    class Leaf
    {
        public Item Parent { get; set; }
        public string LeafSecret { get; set; }
    }

    private static List<string> Paths(Action<ModelBuilder> configure)
    {
        using var db = SingleEntityDbContext.Create<Root>(configure);
        var schemas = QueryableEncryptionSchemaGenerator.GenerateSchemas(db.Model);
        return schemas["Root"]["fields"].AsBsonArray.Select(f => f["path"].AsString).ToList();
    }

    [Fact]
    public static void Owned_with_back_reference_to_root_owner()
    {
        var paths = Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Property(x => x.Secret).IsEncrypted(Key(1));
            e.OwnsOne(x => x.Home, h =>
            {
                h.WithOwner(a => a.Parent);
                h.Property(a => a.Street).IsEncrypted(Key(2));
            });
            e.Ignore(x => x.Nested);
            e.Ignore(x => x.Items);
        }));

        Assert.Equal(["Secret", "Home.Street"], paths);
    }

    [Fact]
    public static void Nested_owned_with_back_reference_to_owned_owner()
    {
        var paths = Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.Property(n => n.OuterSecret).IsEncrypted(Key(1));
                o.Ignore(n => n.Items);
                o.OwnsOne(n => n.Child, i =>
                {
                    i.WithOwner(c => c.Parent);
                    i.Property(c => c.InnerSecret).IsEncrypted(Key(2));
                });
            });
        }));

        Assert.Equal(["Nested.OuterSecret", "Nested.Child.InnerSecret"], paths);
    }

    [Fact]
    public static void Owned_collection_with_back_reference_to_owner()
    {
        // Encryption inside arrays is not supported, so only the root property is encrypted;
        // the nested owned collections (with back-references) must still be walked safely.
        var paths = Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Property(x => x.Secret).IsEncrypted(Key(1));
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Nested);
            e.OwnsMany(x => x.Items, i =>
            {
                i.WithOwner();
                i.Ignore(x => x.Parent);
                i.OwnsMany(x => x.Leaves, l => l.WithOwner(x => x.Parent));
            });
        }));

        Assert.Equal(["Secret"], paths);
    }

    [Fact]
    public static void Encrypted_owned_navigation_with_back_reference()
    {
        var paths = Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.IsEncrypted(Key(1));
                o.Ignore(n => n.Items);
                o.OwnsOne(n => n.Child, i => i.WithOwner(c => c.Parent));
            });
        }));

        Assert.Equal(["Nested"], paths);
    }
}
