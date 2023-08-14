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

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

[CollectionDefinition(nameof(SampleGuidesFixture))]
public class SampleGuidesFixtureCollection : ICollectionFixture<SampleGuidesFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

public class SampleGuidesFixture : IDisposable
{
    private readonly TemporaryDatabase _db;

    public SampleGuidesFixture()
    {
        _db = TestServer.CreateTemporaryDatabase();
        _db.MongoDatabase.GetCollection<Planet>("planets")
            .BulkWrite(Data.Select(p => new InsertOneModel<Planet>(p)));
    }

    public void Dispose() => _db.Dispose();

    public IMongoDatabase Database => _db.MongoDatabase;

    private static Planet[] Data = new[]
    {
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65c"),
            name = "Mercury",
            orderFromSun = 1,
            hasRings = false,
            mainAtmosphere = Array.Empty<string>()
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb662"),
            name = "Venus",
            orderFromSun = 2,
            hasRings = false,
            mainAtmosphere = new[] { "CO2", "N" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb661"),
            name = "Earth",
            orderFromSun = 3,
            hasRings = false,
            mainAtmosphere = new[] { "N", "O2", "Ar" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65e"),
            name = "Mars",
            orderFromSun = 4,
            hasRings = false,
            mainAtmosphere = new[] { "CO2", "Ar", "N" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb660"),
            name = "Jupiter",
            orderFromSun = 5,
            hasRings = true,
            mainAtmosphere = new[] { "H2", "He", "CH4" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb663"),
            name = "Saturn",
            orderFromSun = 6,
            hasRings = true,
            mainAtmosphere = new[] { "H2", "He", "CH4" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65d"),
            name = "Uranus",
            orderFromSun = 7,
            hasRings = true,
            mainAtmosphere = new[] { "H2", "He", "CH4" }
        },
        new Planet
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65f"),
            name = "Neptune",
            orderFromSun = 8,
            hasRings = true,
            mainAtmosphere = new[] { "H2", "He", "CH4" }
        }
    };
}
