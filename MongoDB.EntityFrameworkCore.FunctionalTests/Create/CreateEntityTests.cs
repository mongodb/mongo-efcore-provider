using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Create
{
    public class CreateEntityTests
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

        private static void CreateMoons()
        {
            var moonCollection = __db.Moons;
            moonCollection.AddRange(new[]
            {
            new Moon { _id = ObjectId.GenerateNewId(), name = "Moon", planetId = __db.Planets.FirstOrDefault(p => p.name == "Earth")!._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Phobos", planetId = __db.Planets.FirstOrDefault(p => p.name == "Mars") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Deimos", planetId = __db.Planets.FirstOrDefault(p => p.name == "Mars") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Io", planetId = __db.Planets.FirstOrDefault(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Europa", planetId = __db.Planets.FirstOrDefault(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Ganymede", planetId = __db.Planets.FirstOrDefault(p => p.name == "Jupiter") !._id },
            new Moon { _id = ObjectId.GenerateNewId(), name = "Callisto", planetId = __db.Planets.FirstOrDefault(p => p.name == "Jupiter") !._id },
        });

            foreach (var planet in __db.Planets)
            {
                planet.moons = new List<Moon> { moonCollection.FirstOrDefault(m => m._id == planet._id)! };
                __db.Planets.FirstOrDefault(p => p.name == planet.name)!.moons = planet.moons;
            }
            __db.SaveChanges();
        }

        [Fact]
        public void CreatePlanetsTest()
        {
            CreatePlanets();
            var planetsCollection = __db.Planets;
            var planets = planetsCollection.OrderBy(p => p.name).ToList();
            var result = planetsCollection.OrderBy(p => p.name).ToList();
            Assert.Multiple(() =>
            {
                Assert.NotNull(result);
                Assert.NotEmpty(result);
                Assert.Equal(planets.Count, result.Count);
            });
        }

        [Fact]
        public void CreateMoonsTest()
        {
            var planetsCollection = __db.Planets;
            CreateMoons();
            var moonsCollection = __db.Moons;
            var moons = moonsCollection.OrderBy(m => m.name).ToList();
            var result = moonsCollection.OrderBy(m => m.name).ToList();
            Assert.Multiple(() =>
            {
                Assert.NotNull(result);
                Assert.NotEmpty(result);
                Assert.Equal(moons.Count, result.Count);
                foreach (var moon in moons)
                {
                    Assert.Equal(moon.planetId, result.FirstOrDefault(m => m._id == moon._id)!.planetId);
                    Assert.Equal(moon.name, result.FirstOrDefault(m => m._id == moon._id)!.name);
                }
            });
        }
    }
}
