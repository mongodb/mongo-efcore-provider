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

/// <summary>
/// Tests for <see cref="QueryableEncryptionSchemaGenerator"/> against models whose owned types have
/// dependent-to-principal (<c>WithOwner</c>) navigations. Before EF-477 the generator followed those
/// navigations back up to a nested owned owner and recursed until the host crashed with a stack overflow.
/// </summary>
/// <remarks>
/// <para>
/// A regression of the <c>IsOnDependent</c> guard does not fail an assertion in the tests with a nested owned
/// owner (nested owned type, owned collection, both encrypted-navigation tests, equality-in-nested): it crashes
/// the whole test host with a stack overflow. The root-owner, same-type-twice, bypassed-validation and throwing
/// tests do not exercise a nested owner and pass without the guard.
/// </para>
/// <para>
/// Expected field order is: each type's encrypted properties first, then its owned children (recursively), and
/// for an encrypted owned navigation the children's fields come BEFORE the navigation's own "object" field
/// because the recursion runs before that field is added.
/// </para>
/// <para>
/// All models map Root with <c>ToCollection("Root")</c> so the collection name is a fixed key into the generated schemas.
/// </para>
/// </remarks>
public static class QueryableEncryptionSchemaGeneratorTests
{
    private static Guid Key(int n) => new($"{n:D8}-2222-3333-4444-555555555555");

    private static BsonBinaryData KeyId(int n) => new(Key(n), GuidRepresentation.Standard);

    class Root
    {
        public ObjectId _id { get; set; }
        public string Secret { get; set; }
        public Address Home { get; set; }
        public Address Work { get; set; }
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
        public string ItemSecret { get; set; }
        public List<Leaf> Leaves { get; set; }
    }

    class Leaf
    {
        public Item Parent { get; set; }
        public string LeafSecret { get; set; }
    }

    private static List<BsonDocument> Fields(Action<ModelBuilder> configure)
    {
        using var db = SingleEntityDbContext.Create<Root>(configure);
        var schemas = QueryableEncryptionSchemaGenerator.GenerateSchemas(db.Model);
        return schemas["Root"]["fields"].AsBsonArray.Select(f => f.AsBsonDocument).ToList();
    }

    private static List<string> Paths(Action<ModelBuilder> configure)
        => Fields(configure).Select(f => f["path"].AsString).ToList();

    private static BsonDocument Field(int key, string path, string bsonType)
        => new() { { "keyId", KeyId(key) }, { "path", path }, { "bsonType", bsonType } };

    [Fact]
    public static void Owned_with_back_reference_to_root_owner()
    {
        // Does NOT discriminate the EF-477 fix: Root is not owned so this passes without the guard too.
        // It guards that models which already worked produce unchanged schemas.
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Property(x => x.Secret).IsEncrypted(Key(1));
            e.OwnsOne(x => x.Home, h =>
            {
                h.WithOwner(a => a.Parent);
                h.Property(a => a.Street).IsEncrypted(Key(2));
            });
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Nested);
            e.Ignore(x => x.Items);
        }));

        Assert.Equal(
            [Field(1, "Secret", "string"), Field(2, "Home.Street", "string")],
            fields);
    }

    [Fact]
    public static void Nested_owned_with_back_reference_to_owned_owner()
    {
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
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

        Assert.Equal(
            [Field(1, "Nested.OuterSecret", "string"), Field(2, "Nested.Child.InnerSecret", "string")],
            fields);
    }

    [Fact]
    public static void Owned_collection_with_back_reference_to_owner()
    {
        // Encryption inside arrays is rejected by the model validator, so only the root property is encrypted.
        // This only proves the collection and its back-references do not crash the generator; see
        // Generator_walks_owned_collections_when_validation_is_bypassed for proof that collections are walked.
        var paths = Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Property(x => x.Secret).IsEncrypted(Key(1));
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Nested);
            e.OwnsMany(x => x.Items, i =>
            {
                i.WithOwner();
                i.OwnsMany(x => x.Leaves, l => l.WithOwner(x => x.Parent));
            });
        }));

        Assert.Equal(["Secret"], paths);
    }

    [Fact]
    public static void Encrypted_property_in_owned_collection_is_rejected_by_model_validation()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Paths(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Nested);
            e.OwnsMany(x => x.Items, i =>
            {
                i.WithOwner();
                i.Property(x => x.ItemSecret).IsEncrypted(Key(1));
                i.Ignore(x => x.Leaves);
            });
        })));

        Assert.Contains("inside an array", exception.Message);
    }

    [Fact]
    public static void Generator_walks_owned_collections_when_validation_is_bypassed()
    {
        // The validator rejects encrypted properties inside collections, so a validated model can never show
        // whether the generator walks collection navigations. Run it over the unvalidated mutable model to pin
        // that it does (and that a back-reference inside the collection does not recurse).
        List<BsonDocument>? fields = null;
        using var db = SingleEntityDbContext.Create<Root>(mb =>
        {
            mb.Entity<Root>(e =>
            {
                e.ToCollection("Root");
                e.Ignore(x => x.Home);
                e.Ignore(x => x.Work);
                e.Ignore(x => x.Nested);
                e.OwnsMany(x => x.Items, i =>
                {
                    i.WithOwner();
                    i.Property(x => x.ItemSecret).IsEncrypted(Key(1));
                    i.Ignore(x => x.Leaves);
                });
            });

            // Generate inside OnModelCreating, before the model is finalized and validated.
            var schemas = QueryableEncryptionSchemaGenerator.GenerateSchemas(mb.Model);
            fields = schemas["Root"]["fields"].AsBsonArray.Select(f => f.AsBsonDocument).ToList();
        });

        // The finished model is rejected by validation; the generator output above was taken before that.
        Assert.Throws<InvalidOperationException>(() => db.Model);

        Assert.Equal([Field(1, "Items.ItemSecret", "string")], fields);
    }

    [Fact]
    public static void Encrypted_owned_navigation_with_back_reference()
    {
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.IsEncrypted(Key(1));
                o.Ignore(n => n.Items);
                o.Property(n => n.OuterSecret).IsEncrypted(Key(2));
                o.OwnsOne(n => n.Child, i =>
                {
                    i.WithOwner(c => c.Parent);
                    i.Property(c => c.InnerSecret).IsEncrypted(Key(3));
                });
            });
        }));

        // Children come before the navigation's own "object" field: recursion runs before fields.Add.
        Assert.Equal(
            [
                Field(2, "Nested.OuterSecret", "string"),
                Field(3, "Nested.Child.InnerSecret", "string"),
                Field(1, "Nested", "object")
            ],
            fields);
    }

    [Fact]
    public static void Encrypted_owned_navigation_nested_under_owned_navigation_uses_full_prefix()
    {
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.Ignore(n => n.Items);
                o.OwnsOne(n => n.Child, i =>
                {
                    i.WithOwner(c => c.Parent);
                    i.IsEncrypted(Key(1));
                    i.Property(c => c.InnerSecret).IsEncrypted(Key(2));
                });
            });
        }));

        Assert.Equal(
            [Field(2, "Nested.Child.InnerSecret", "string"), Field(1, "Nested.Child", "object")],
            fields);
    }

    [Fact]
    public static void Same_owned_type_on_two_navigations_yields_distinct_paths()
    {
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Nested);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Home, h =>
            {
                h.WithOwner(a => a.Parent);
                h.Property(a => a.Street).IsEncrypted(Key(1));
            });
            e.OwnsOne(x => x.Work, w =>
            {
                w.WithOwner(a => a.Parent);
                w.Property(a => a.Street).IsEncrypted(Key(2));
            });
        }));

        Assert.Equal(
            [Field(1, "Home.Street", "string"), Field(2, "Work.Street", "string")],
            fields.OrderBy(f => f["path"].AsString));
    }

    [Theory]
    [InlineData(QueryableEncryptionType.Equality)]
    [InlineData(QueryableEncryptionType.Range)]
    public static void Equality_or_range_on_owned_navigation_throws(QueryableEncryptionType type)
    {
        using var db = SingleEntityDbContext.Create<Root>(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.Ignore(n => n.Items);
                o.Ignore(n => n.Child);
                o.Metadata.SetQueryableEncryptionType(type);
            });
        }));

        var exception = Assert.Throws<NotSupportedException>(() => QueryableEncryptionSchemaGenerator.GenerateSchemas(db.Model));
        Assert.Equal("Equality and Range queries are not supported for owned entities.", exception.Message);
    }

    [Fact]
    public static void Equality_encrypted_property_in_nested_owned_type_has_prefixed_path_and_queries()
    {
        var fields = Fields(mb => mb.Entity<Root>(e =>
        {
            e.ToCollection("Root");
            e.Ignore(x => x.Home);
            e.Ignore(x => x.Work);
            e.Ignore(x => x.Items);
            e.OwnsOne(x => x.Nested, o =>
            {
                o.WithOwner(n => n.Parent);
                o.Ignore(n => n.Items);
                o.OwnsOne(n => n.Child, i =>
                {
                    i.WithOwner(c => c.Parent);
                    i.Property(c => c.InnerSecret).IsEncryptedForEquality(Key(1), b => b.WithContention(4));
                });
            });
        }));

        var field = Field(1, "Nested.Child.InnerSecret", "string");
        field["queries"] = new BsonDocument { { "queryType", "equality" }, { "contention", 4L } };
        Assert.Equal([field], fields);
    }
}
