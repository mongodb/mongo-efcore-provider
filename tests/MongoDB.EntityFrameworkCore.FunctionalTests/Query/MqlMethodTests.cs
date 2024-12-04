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

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class MqlMethodTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Where_Mql_Exists_does_not_return_entities_with_missing_property()
    {
        database.CreateCollection<Basic>().InsertOne(_expectedBasicWithNav);
        var collection = database.CreateCollection<Full>();
        collection.InsertOne(_expectedFull);
        using var db = SingleEntityDbContext.Create(collection);

        AssertQuery(db.Entities.Where(p => Mql.Exists(p.AnOptionalString)));
        AssertQuery(db.Entities.Where(p => Mql.Exists(EF.Property<string>(p, "AnOptionalString"))));
        AssertQuery(db.Entities.Where(p => Mql.Exists(p.AnOptionalDecimal)));
        AssertQuery(db.Entities.Where(p => Mql.Exists(p.AnOptionalArray)));
        AssertQuery(db.Entities.Where(p => Mql.Exists(p.AnOptionalOwnedEntity)));
        AssertQuery(db.Entities.Where(p => Mql.Exists(p.AnOptionalListOfOwnedEntities)));

        void AssertQuery(IQueryable<Full> queryable)
        {
            var actual = queryable.ToList();
            Assert.Single(actual);
            Assert.Single(actual, a => a.Id == _expectedFull.Id);
        }
    }

    [Fact]
    public void Where_Mql_IsMissing_returns_entities_with_missing_property()
    {
        database.CreateCollection<Full>().InsertOne(_expectedFull);
        database.CreateCollection<Basic>().InsertOne(_expectedBasic);
        using var db = SingleEntityDbContext.Create(database.CreateCollection<BasicWithOptional>());

        AssertExpected(db.Entities.Where(p => Mql.IsMissing(p.AnOptionalString)));
        AssertExpected(db.Entities.Where(p => Mql.IsMissing(EF.Property<string>(p, "AnOptionalString"))));
        AssertExpected(db.Entities.Where(p => Mql.IsMissing(p.AnOptionalDecimal)));
        AssertExpected(db.Entities.Where(p => Mql.IsMissing(p.AnOptionalString)));
        AssertExpected(db.Entities.Where(p => Mql.IsMissing(p.AnOptionalOwnedEntity)));

        void AssertExpected(IQueryable<BasicWithOptional> query)
        {
            var actual = query.ToList();
            Assert.Single(actual);
            Assert.Single(actual, a => a.Id == _expectedBasic.Id);
        }
    }

    [Fact]
    public void Where_Mql_IsMissingOrNull_returns_entities_with_null_or_missing_property()
    {
        database.CreateCollection<Basic>().InsertOne(_expectedBasic);
        database.CreateCollection<Full>().InsertOne(_expectedFull);
        database.CreateCollection<BasicWithOptional>().InsertOne(_expectedOptionalNotSet);

        using var db = SingleEntityDbContext.Create(database.CreateCollection<BasicWithOptional>());

        AssertExpected(db.Entities.Where(p => Mql.IsNullOrMissing(p.AnOptionalString)));
        AssertExpected(db.Entities.Where(p => Mql.IsNullOrMissing(EF.Property<string>(p, "AnOptionalString"))));
        AssertExpected(db.Entities.Where(p => Mql.IsNullOrMissing(p.AnOptionalDecimal)));
        AssertExpected(db.Entities.Where(p => Mql.IsNullOrMissing(p.AnOptionalArray)));
        AssertExpected(db.Entities.Where(p => Mql.IsNullOrMissing(p.AnOptionalOwnedEntity)));

        void AssertExpected(IQueryable<BasicWithOptional> query)
        {
            var actual = query.ToList();
            Assert.Equal(2, actual.Count);
            Assert.Single(actual, a => a.Id == _expectedBasic.Id);
            Assert.Single(actual, a => a.Id == _expectedOptionalNotSet.Id);
        }
    }

    // Note: We do not have tests for missing navigations with IsMissing or IsMissingOrNull
    // as we do not support deserializing entities with missing navigation collections at this
    // time. Being tracked as part of EF-164.

    [Fact]
    public void Where_Mql_Exists_does_not_return_entities_with_missing_navigation()
    {
        database.CreateCollection<Keyed>().InsertOne(new Keyed());
        var collection = database.CreateCollection<Nav>();
        collection.InsertOne(_expectedNav);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(p => Mql.Exists(p.AListOfOwnedEntities)).ToList();
        Assert.Single(actual);
        Assert.Single(actual, a => a.Id == _expectedNav.Id);
    }

    private readonly Full _expectedFull = new()
    {
        Id = ObjectId.GenerateNewId(),
        AString = nameof(Full),
        AnOptionalString = "B",
        ADecimal = 123.456m,
        AnOptionalDecimal = -344.34m,
        AnArray = [1, 2, 3],
        AnOptionalArray = [4, 5, 6],
        AnOwned = new Owned {SomeString = "AA", SomeNullableInt = 7},
        AnOptionalOwnedEntity = new Owned {SomeString = "BB", SomeNullableInt = 8},
        AListOfOwnedEntities = [new Owned {SomeString = "CC", SomeNullableInt = 9}],
        AnOptionalListOfOwnedEntities = [new Owned {SomeString = "DD", SomeNullableInt = 10}]
    };

    private readonly Basic _expectedBasic = new()
    {
        Id = ObjectId.GenerateNewId(), AString = nameof(Basic.AString), ADecimal = 456.789m, AnArray = [10, 20, 30],
    };

    private readonly BasicWithNav _expectedBasicWithNav = new()
    {
        Id = ObjectId.GenerateNewId(),
        AString = nameof(BasicWithNav),
        ADecimal = 456.789m,
        AnArray = [10, 20, 30],
        AnOwned = new Owned {SomeString = "EE"},
        AListOfOwnedEntities = [new Owned {SomeString = "FF"}],
    };

    private readonly BasicWithOptional _expectedOptionalNotSet = new()
    {
        Id = ObjectId.GenerateNewId(), AString = nameof(BasicWithNav), ADecimal = 456.789m, AnArray = [10, 20, 30],
    };

    private readonly Nav _expectedNav =
        new() {Id = ObjectId.GenerateNewId(), AListOfOwnedEntities = [new Owned {SomeString = "GG"}]};

    class Keyed
    {
        [Key]
        public ObjectId Id { get; set; }
    }

    class Basic : Keyed
    {
        public string AString { get; set; }
        public decimal ADecimal { get; set; }
        public int[] AnArray { get; set; }
    }

    class BasicWithNav : Basic
    {
        public Owned AnOwned { get; set; }
        public List<Owned> AListOfOwnedEntities { get; set; }
    }

    class BasicWithOptional : Basic
    {
        public string? AnOptionalString { get; set; }
        public decimal? AnOptionalDecimal { get; set; }
        public int[]? AnOptionalArray { get; set; }
        public Owned? AnOptionalOwnedEntity { get; set; }
    }

    class Full : BasicWithNav
    {
        public string? AnOptionalString { get; set; }
        public decimal? AnOptionalDecimal { get; set; }
        public int[]? AnOptionalArray { get; set; }
        public Owned? AnOptionalOwnedEntity { get; set; }
        public List<Owned>? AnOptionalListOfOwnedEntities { get; set; }
    }

    class Nav : Keyed
    {
        public List<Owned> AListOfOwnedEntities { get; set; }
    }

    class Owned
    {
        public string SomeString { get; set; }
        public int? SomeNullableInt { get; set; }
    }
}
