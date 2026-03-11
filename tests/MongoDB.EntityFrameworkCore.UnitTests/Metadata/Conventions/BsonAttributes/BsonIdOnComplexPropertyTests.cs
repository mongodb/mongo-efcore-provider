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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonIdOnComplexPropertyTests
{
    [Fact]
    public static void BsonId_on_complex_type_property_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var db = new DbContextWithBsonIdOnComplex();
            _ = db.Model;
        });
    }

    class Owner
    {
        public int Id { get; set; }
        public Detail Detail { get; set; }
    }

    class Detail
    {
        [BsonId]
        public int DetailId { get; set; }
        public string Name { get; set; }
    }

    class DbContextWithBsonIdOnComplex : DbContext
    {
        public DbSet<Owner> Owners { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>().ComplexProperty(o => o.Detail);
        }
    }
}
