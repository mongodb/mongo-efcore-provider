﻿/* Copyright 2023-present MongoDB Inc.
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

public static class SampleGuides
{
    public static void Populate(IMongoDatabase mongoDatabase)
    {
        mongoDatabase.CreateCollection("planets");
        mongoDatabase.CreateCollection("moons");

        using var session = mongoDatabase.Client.StartSession();
        mongoDatabase.GetCollection<Planet>("planets")
            .BulkWrite(session, PlanetData.Select(p => new InsertOneModel<Planet>(p)));
        mongoDatabase.GetCollection<InternalMoon>("moons")
            .BulkWrite(session, MoonData.Select(m => new InsertOneModel<InternalMoon>(m)));
    }

    private static readonly Planet[] PlanetData =
    [
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65c"),
            name = "Mercury",
            orderFromSun = 1,
            hasRings = false,
            mainAtmosphere = Array.Empty<string>()
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb662"),
            name = "Venus",
            orderFromSun = 2,
            hasRings = false,
            mainAtmosphere =
            [
                "CO2", "N"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb661"),
            name = "Earth",
            orderFromSun = 3,
            hasRings = false,
            mainAtmosphere =
            [
                "N", "O2", "Ar"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65e"),
            name = "Mars",
            orderFromSun = 4,
            hasRings = false,
            mainAtmosphere =
            [
                "CO2", "Ar", "N"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb660"),
            name = "Jupiter",
            orderFromSun = 5,
            hasRings = true,
            mainAtmosphere =
            [
                "H2", "He", "CH4"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb663"),
            name = "Saturn",
            orderFromSun = 6,
            hasRings = true,
            mainAtmosphere =
            [
                "H2", "He", "CH4"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65d"),
            name = "Uranus",
            orderFromSun = 7,
            hasRings = true,
            mainAtmosphere =
            [
                "H2", "He", "CH4"
            ]
        },
        new()
        {
            _id = ObjectId.Parse("621ff30d2a3e781873fcb65f"),
            name = "Neptune",
            orderFromSun = 8,
            hasRings = true,
            mainAtmosphere =
            [
                "H2", "He", "CH4"
            ]
        }
    ];

    class InternalMoon
    {
        public InternalMoonKey _id { get; set; }
        public string name { get; set; }
        public int? yearOfDiscovery { get; set; }
    }

    class InternalMoonKey(ObjectId planetId, string label)
    {
        public ObjectId planetId { get; set; } = planetId;
        public string label { get; set; } = label;
    }

    private static readonly InternalMoon[] MoonData =
    [
        new() {_id = new InternalMoonKey(ObjectId.Parse("621ff30d2a3e781873fcb65f"), "I"), name = "Triton", yearOfDiscovery = 1846},
        new()
        {
            _id = new InternalMoonKey(ObjectId.Parse("621ff30d2a3e781873fcb65f"), "II"), name = "Nereid", yearOfDiscovery = 1949
        },
        new()
        {
            _id = new InternalMoonKey(ObjectId.Parse("621ff30d2a3e781873fcb661"), "I"),
            name = "The Moon",
            yearOfDiscovery = null
        },
        new() {_id = new InternalMoonKey(ObjectId.Parse("621ff30d2a3e781873fcb663"), "I"), name = "Mimas", yearOfDiscovery = 1789},
        new() {_id = new InternalMoonKey(ObjectId.Parse("621ff30d2a3e781873fcb663"), "VI"), name = "Titan", yearOfDiscovery = 1655}
    ];
}
