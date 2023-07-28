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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class FirstSingleTests: IDisposable
{
    private static GuidesDbContext __db = GuidesDbContext.Create(TestServer.GetClient());
    public void Dispose() => __db.Dispose();

    private static void CreatePlanets()
    {
        __db = GuidesDbContext.Create(TestServer.GetClient());
        var planetsCollection = __db.Planets;

        planetsCollection.AddRange(
            new Planet { _id = ObjectId.GenerateNewId(), name = "Mercury", orderFromSun = 1 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Venus", orderFromSun = 2 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Earth", orderFromSun = 3 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Mars", orderFromSun = 4 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Jupiter", orderFromSun = 5 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Saturn", orderFromSun = 6 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Uranus", orderFromSun = 7 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Neptune", orderFromSun = 8 },
            new Planet { _id = ObjectId.GenerateNewId(), name = "Pluto", orderFromSun = 9 }
        );
        __db.SaveChanges();
    }

    private static void CreateMoons(List<Planet> planetsCollection)
    {
        var moonCollection = __db.Moons;
        moonCollection.AddRange(new[]
        {
            new Moon { _id = ObjectId.GenerateNewId(), name = "Moon", planetId = planetsCollection.Find(p => p.name == "Earth")!._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Phobos", planetId = planetsCollection.Find(p => p.name == "Mars") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Deimos", planetId = planetsCollection.Find(p => p.name == "Mars") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Io", planetId = planetsCollection.Find(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Europa", planetId = planetsCollection.Find(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Ganymede", planetId = planetsCollection.Find(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Callisto", planetId = planetsCollection.Find(p => p.name == "Jupiter") !._id },
        });

        foreach (var planet in planetsCollection)
        {
            planet.moons = new List<Moon> { moonCollection.FirstOrDefault(m => m._id == planet._id)! };
            planetsCollection.Find(p => p.name == planet.name)!.moons = planet.moons;
        }
        __db.SaveChanges();
    }

    [Fact]
    public static void First()
    {
        CreatePlanets();
        var planets = __db.Planets.OrderBy(p => p.name).ToList();
        var result = __db.Planets.OrderBy(p => p.name).First();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public static void First_with_no_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(100).First());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void First_predicate()
    {
        var result = __db.Planets.First(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public static void First_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.First(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single()
    {
        var result = __db.Planets.Where(p => p.name == "Earth").Single();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public static void Single_with_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(100).Single());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(4).Single());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public static void Single_predicate()
    {
        var result = __db.Planets.Single(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public static void Single_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Single(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single_predicate_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Single(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }
}
