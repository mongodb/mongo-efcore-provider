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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

internal class GuidesDbContext(DbContextOptions options)
    : DbContext(options)
{
    public DbSet<Moon> Moons { get; init; }
    public DbSet<Planet> Planets { get; init; }

    public static GuidesDbContext Create(
        IMongoDatabase database,
        Action<string>? logAction = null,
        ILoggerFactory? loggerFactory = null,
        bool sensitiveDataLogging = true) =>
        new(new DbContextOptionsBuilder<GuidesDbContext>()
            .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .LogTo(l => logAction?.Invoke(l))
            .UseLoggerFactory(loggerFactory)
            .EnableSensitiveDataLogging(sensitiveDataLogging)
            .Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var planetId = ObjectId.GenerateNewId();

        modelBuilder.Entity<Moon>(b =>
        {
            b.ToCollection("moons");
            b.HasKey(nameof(Moon.planetId), nameof(Moon.label));
            b.HasData(new Moon { planetId = planetId,  name = "Endor", label = "Forest", yearOfDiscovery = 1983 });
        });

        modelBuilder.Entity<Planet>(b =>
        {
            b.ToCollection("planets");
            b.HasData(new Planet { _id = planetId, name = "Tatooine", hasRings = false });
        });
    }
}
