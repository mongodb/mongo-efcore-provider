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
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Bare join-scope leaves over reference navigations, over ragged data: a bare inner ENTITY leaf
/// (<c>clients.Select(c =&gt; c.Company)</c>, null for an unmatched left-outer side) and a bare scalar read through a
/// chain of the same self-referencing navigation (<c>e.Manager.Manager.Name</c>; an unmatched level reads
/// <c>default(T)</c>, an explicit null a throw, as driver-LINQ's push-down did).
/// </summary>
public class NativeJoinScopeLeafTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Company
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Client
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? CompanyId { get; set; }
        public Company? Company { get; set; }
    }

    public class Employee
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public ObjectId? ManagerId { get; set; }
        public Employee? Manager { get; set; }
    }

    [Fact]
    public void A_bare_inner_entity_leaf_materializes_the_joined_company_or_null()
    {
        var names = Names();
        var c1 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(names.Companies).InsertOne(
            new BsonDocument { { "_id", c1 }, { "Name", "Acme" } });
        database.MongoDatabase.GetCollection<BsonDocument>(names.Clients).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "with" }, { "CompanyId", c1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "null-fk" }, { "CompanyId", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "missing-fk" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "dangling" }, { "CompanyId", ObjectId.GenerateNewId() } }
        ]);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(names, mode);
                return db.Set<Client>().Select(c => c.Company).AsEnumerable().Select(c => c?.Name ?? "-").OrderBy(n => n).ToList();
            },
            ["-", "-", "-", "Acme"]);
    }

    [Fact]
    public void A_scalar_read_through_a_self_referencing_chain_matches_driver_LINQ_on_ragged_data()
    {
        var names = Names();
        var e1 = ObjectId.GenerateNewId();
        var e2 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(names.Employees).InsertMany(
        [
            new BsonDocument { { "_id", e1 }, { "Name", "E1" }, { "Rank", 7 } },
            new BsonDocument { { "_id", e2 }, { "Name", "E2" }, { "Rank", 5 }, { "ManagerId", e1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "E3" }, { "Rank", 3 }, { "ManagerId", e2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "E4" }, { "Rank", 1 }, { "ManagerId", ObjectId.GenerateNewId() } }
        ]);

        // E1 has no manager, E2's manager has none, E3 reaches E1, E4's manager dangles.
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(names, mode);
                return db.Set<Employee>().Select(e => e.Manager!.Manager!.Name).AsEnumerable().Select(n => n ?? "-").OrderBy(n => n).ToList();
            },
            ["-", "-", "-", "E1"]);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(names, mode);
                return db.Set<Employee>().Select(e => e.Manager!.Manager!.Rank).AsEnumerable().OrderBy(r => r).ToList();
            },
            [0, 0, 0, 7]);
    }

    private sealed record CollectionNames(string Clients, string Companies, string Employees);

    private static CollectionNames Names()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CollectionNames(
            TemporaryDatabaseFixtureBase.CreateCollectionName("jlClients") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("jlCompanies") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("jlEmployees") + suffix);
    }

    private SingleContext Create(CollectionNames names, MongoQueryMode mode)
    {
        var builder = new DbContextOptionsBuilder<SingleContext>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new SingleContext(builder.Options, names);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class SingleContext(DbContextOptions<SingleContext> options, CollectionNames names) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Company>().ToCollection(names.Companies);
            mb.Entity<Client>(b =>
            {
                b.ToCollection(names.Clients);
                b.HasOne(c => c.Company).WithMany().HasForeignKey(c => c.CompanyId);
            });
            mb.Entity<Employee>(b =>
            {
                b.ToCollection(names.Employees);
                b.HasOne(e => e.Manager).WithMany().HasForeignKey(e => e.ManagerId);
            });
        }
    }
}
