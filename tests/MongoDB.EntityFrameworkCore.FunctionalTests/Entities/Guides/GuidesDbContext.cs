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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

internal class GuidesDbContext : DbContext
{
    public DbSet<Moon> Moons { get; init; }
    public DbSet<Planet> Planets { get; init; }

    public static GuidesDbContext Create(IMongoDatabase database) =>
        new (new DbContextOptionsBuilder<GuidesDbContext>()
            .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName)
            .Options);

    public GuidesDbContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Moon>()
            .ToCollection("moons")
            .HasKey(nameof(Moon.planetId), nameof(Moon.label));
        modelBuilder.Entity<Planet>().ToCollection("planets");
    }
}
