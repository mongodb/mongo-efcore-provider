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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A reducer over a covariant DTO query (<c>IQueryable&lt;BaseDto&gt; q = …Select(r =&gt; new DerivedDto { … }); q.First()</c>)
/// — EF's <c>Return_type_of_singular_operator_is_preserved</c>. Nav-expansion wraps the projection in a reference
/// upcast, which the native projection binder looks through.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeUpcastProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    public class IdDto
    {
        public string Name { get; set; } = "";
    }

    public class IdAndCityDto : IdDto
    {
        public string City { get; set; } = "";
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Name = "a", City = "London" },
            new Row { Id = ObjectId.GenerateNewId(), Name = "b", City = "Berlin" },
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    [Fact]
    public void First_over_covariant_dto_query_goes_native()
    {
        var collection = Seed(nameof(First_over_covariant_dto_query_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            IQueryable<IdDto> query = db.Entities.Where(r => r.Name == "b")
                .Select(r => new IdAndCityDto { Name = r.Name, City = r.City });
            var first = query.First();
            return new List<(string, string)> { (first.Name, ((IdAndCityDto)first).City) };
        });
        Assert.Equal([("b", "Berlin")], result);
    }

    [Fact]
    public void Last_over_covariant_dto_query_goes_native()
    {
        var collection = Seed(nameof(Last_over_covariant_dto_query_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            IQueryable<IdDto> query = db.Entities.OrderBy(r => r.Name)
                .Select(r => new IdAndCityDto { Name = r.Name, City = r.City });
            return new List<string> { query.Last().Name };
        });
        Assert.Equal(["b"], result);
    }
}
