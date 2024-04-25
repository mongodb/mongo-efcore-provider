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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class DiscriminatorTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Vehicle
    {
        public ObjectId _id { get; set; }
        public string VehicleType { get; set; }
    }

    class VehicleDbContext : DbContext
    {
        private readonly Action<ModelBuilder> _modelConfigurator;

        public VehicleDbContext(Action<ModelBuilder> modelConfigurator)
        {
            _modelConfigurator = modelConfigurator;
        }

        public DbSet<Vehicle> Vehicles { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            _modelConfigurator(modelBuilder);
        }
    }

    [Fact]
    public void Discriminators_throw_not_supported_if_configured()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Vehicle>();
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Vehicle>().HasDiscriminator(v => v.VehicleType);
        });

        var ex = Assert.Throws<NotSupportedException>(() => db.Entitites.FirstOrDefault());
        Assert.Contains(nameof(Vehicle), ex.Message);
    }
}
