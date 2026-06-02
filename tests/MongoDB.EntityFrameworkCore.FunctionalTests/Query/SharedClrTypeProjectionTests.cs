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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Proves the behaviour of aliased scalar projections when two owned entity types share the
/// same CLR type but map a property to a different BsonRepresentation. The collection-projection
/// inner-lambda parameter (<c>e.coll.Select(l =&gt; l.prop)</c>) resolves through the
/// <c>ParameterExpression</c> branch in <c>TryResolveFieldAccessSource</c>, which must resolve the
/// owned entity type that actually owns the projected element — not an arbitrary same-CLR-type one.
/// </summary>
[XUnitCollection("QueryTests")]
public class SharedClrTypeProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private record GeoPoint
    {
        public decimal lat { get; set; }
        public decimal lng { get; set; }
    }

    private record MultiSameTypeOwner
    {
        public ObjectId _id { get; set; }
        public string name { get; set; } = null!;

        // Two owned collections of the SAME CLR type, but `lng` is stored with a different
        // BsonRepresentation in each: primary as a string, secondary as the default decimal.
        public List<GeoPoint> primary { get; set; } = null!;
        public List<GeoPoint> secondary { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> ConfigureModel = mb =>
    {
        mb.Entity<MultiSameTypeOwner>(b =>
        {
            // Configured first → first in Model.GetEntityTypes() for CLR type GeoPoint.
            b.OwnsMany(e => e.primary, o =>
            {
                o.HasElementName("primary");
                o.Property(p => p.lng).HasElementName("lng").HasBsonRepresentation(BsonType.String);
            });
            // Configured second → the projected value below comes from here.
            b.OwnsMany(e => e.secondary, o =>
            {
                o.HasElementName("secondary");
                o.Property(p => p.lng).HasElementName("lng"); // default (decimal128)
            });
        });
    };

    private (ObjectId Id, string Collection) Seed()
    {
        var collectionName = nameof(SharedClrTypeProjectionTests) + Guid.NewGuid().ToString("N");
        var collection = database.MongoDatabase.GetCollection<MultiSameTypeOwner>(collectionName);
        var id = ObjectId.GenerateNewId();
        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        db.Entities.Add(new MultiSameTypeOwner
        {
            _id = id,
            name = "X",
            primary = [new GeoPoint { lat = 1.1m, lng = 10.5m }],
            secondary = [new GeoPoint { lat = 2.2m, lng = 20.5m }]
        });
        db.SaveChanges();
        return (id, collectionName);
    }

    [Fact]
    public void Collection_alias_projection_from_secondary_uses_its_own_serializer_member_access()
    {
        var (id, collectionName) = Seed();
        var collection = database.MongoDatabase.GetCollection<MultiSameTypeOwner>(collectionName);
        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);

        var found = db.Entities.AsNoTracking()
            .Where(e => e._id == id)
            .Select(e => new
            {
                e.secondary,
                Lngs = e.secondary.Select(l => new { Alias = l.lng }).ToList()
            })
            .Single();

        Assert.Equal(20.5m, found.Lngs.Single().Alias);
    }

    [Fact]
    public void Collection_alias_projection_from_secondary_uses_its_own_serializer_bare_scalar()
    {
        var (id, collectionName) = Seed();
        var collection = database.MongoDatabase.GetCollection<MultiSameTypeOwner>(collectionName);
        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);

        var found = db.Entities.AsNoTracking()
            .Where(e => e._id == id)
            .Select(e => new
            {
                e.secondary,
                Lngs = e.secondary.Select(l => l.lng).ToList()
            })
            .Single();

        Assert.Equal(20.5m, found.Lngs.Single());
    }

    private record SingleOwner
    {
        public ObjectId _id { get; set; }
        public List<GeoPoint> points { get; set; } = null!;
    }

    // Control: nested EF.Property inside a collection Select is an unrelated, pre-existing
    // translation limitation (crashes in MongoProjectionBindingExpressionVisitor) — it has
    // nothing to do with shared CLR types. This documents that the EF.Property crash is not
    // the shared-CLR-type serializer concern.
    [Fact]
    public void Control_nested_ef_property_in_collection_select_is_unsupported_even_single_owned()
    {
        var collectionName = nameof(Control_nested_ef_property_in_collection_select_is_unsupported_even_single_owned)
            + Guid.NewGuid().ToString("N");
        var collection = database.MongoDatabase.GetCollection<SingleOwner>(collectionName);
        var id = ObjectId.GenerateNewId();
        Action<ModelBuilder> configure = mb =>
            mb.Entity<SingleOwner>().OwnsMany(e => e.points, o => o.Property(p => p.lng).HasElementName("lng"));

        using (var db = SingleEntityDbContext.Create(collection, configure))
        {
            db.Entities.Add(new SingleOwner { _id = id, points = [new GeoPoint { lat = 1m, lng = 2m }] });
            db.SaveChanges();
        }

        using var db2 = SingleEntityDbContext.Create(collection, configure);
        Assert.ThrowsAny<Exception>(() => db2.Entities.AsNoTracking()
            .Where(e => e._id == id)
            .Select(e => new { e.points, Lngs = e.points.Select(l => new { Alias = EF.Property<decimal>(l, "lng") }).ToList() })
            .Single());
    }

    [Fact]
    public void Collection_alias_projection_from_primary_uses_its_own_serializer_member_access()
    {
        var (id, collectionName) = Seed();
        var collection = database.MongoDatabase.GetCollection<MultiSameTypeOwner>(collectionName);
        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);

        var found = db.Entities.AsNoTracking()
            .Where(e => e._id == id)
            .Select(e => new
            {
                e.primary,
                Lngs = e.primary.Select(l => new { Alias = l.lng }).ToList()
            })
            .Single();

        Assert.Equal(10.5m, found.Lngs.Single().Alias);
    }

    // ---------------------------------------------------------------------------------------
    // Top-level shared-type entities: two DbSets of the SAME CLR type, each mapping `lng` to a
    // different BsonRepresentation. Routes through the base (non-mixed) projection visitor, which
    // resolves the source entity type via TryResolveFieldAccessSource — including the loose
    // FirstOrDefault(e => e.ClrType == parameterExpression.Type) branch.
    // ---------------------------------------------------------------------------------------

    private record SharedDoc
    {
        public ObjectId _id { get; set; }
        public decimal lng { get; set; }
    }

    private class TwoSetContext(IMongoClient client, string dbName, string collA, string collB) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB(client, dbName)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            // "A" registered first → stored as STRING. Element name shared ("lng") to isolate serializer.
            mb.SharedTypeEntity<SharedDoc>("A", b =>
            {
                b.ToCollection(collA);
                b.Property(p => p.lng).HasElementName("lng").HasBsonRepresentation(BsonType.String);
            });
            // "B" registered second → stored as default decimal128.
            mb.SharedTypeEntity<SharedDoc>("B", b =>
            {
                b.ToCollection(collB);
                b.Property(p => p.lng).HasElementName("lng");
            });
        }

        public IQueryable<SharedDoc> A => Set<SharedDoc>("A");
        public IQueryable<SharedDoc> B => Set<SharedDoc>("B");
        public void AddToB(SharedDoc d) => Set<SharedDoc>("B").Add(d);
        public void AddToA(SharedDoc d) => Set<SharedDoc>("A").Add(d);
    }

    private (ObjectId Id, string CollA, string CollB) SeedTwoSets()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var collA = "SharedA" + suffix;
        var collB = "SharedB" + suffix;
        var client = database.MongoDatabase.Client;
        var dbName = database.MongoDatabase.DatabaseNamespace.DatabaseName;
        var id = ObjectId.GenerateNewId();
        using var db = new TwoSetContext(client, dbName, collA, collB);
        db.AddToA(new SharedDoc { _id = ObjectId.GenerateNewId(), lng = 10.5m });
        db.AddToB(new SharedDoc { _id = id, lng = 20.5m });
        db.SaveChanges();
        return (id, collA, collB);
    }

    [Fact]
    public void TopLevel_shared_type_alias_projection_from_B_member_access()
    {
        var (id, collA, collB) = SeedTwoSets();
        using var db = new TwoSetContext(database.MongoDatabase.Client,
            database.MongoDatabase.DatabaseNamespace.DatabaseName, collA, collB);

        var found = db.B.AsNoTracking().Where(e => e._id == id)
            .Select(e => new { Alias = e.lng })
            .Single();

        Assert.Equal(20.5m, found.Alias);
    }

    [Fact]
    public void TopLevel_shared_type_alias_projection_from_B_ef_property()
    {
        var (id, collA, collB) = SeedTwoSets();
        using var db = new TwoSetContext(database.MongoDatabase.Client,
            database.MongoDatabase.DatabaseNamespace.DatabaseName, collA, collB);

        var found = db.B.AsNoTracking().Where(e => e._id == id)
            .Select(e => new { Alias = EF.Property<decimal>(e, "lng") })
            .Single();

        Assert.Equal(20.5m, found.Alias);
    }

    [Fact]
    public void TopLevel_shared_type_mixed_projection_from_B_ef_property()
    {
        var (id, collA, collB) = SeedTwoSets();
        using var db = new TwoSetContext(database.MongoDatabase.Client,
            database.MongoDatabase.DatabaseNamespace.DatabaseName, collA, collB);

        var found = db.B.AsNoTracking().Where(e => e._id == id)
            .Select(e => new { Entity = e, Alias = EF.Property<decimal>(e, "lng") })
            .Single();

        Assert.Equal(20.5m, found.Alias);
        Assert.Equal(20.5m, found.Entity.lng);
    }
}
