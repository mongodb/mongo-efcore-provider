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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class CompositeKeyQueryTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _temporaryDatabase;

    public CompositeKeyQueryTests(TemporaryDatabaseFixture fixture)
    {
        _temporaryDatabase = fixture;
    }

    [Fact]
    public void Should_query_by_key_component()
    {
        var dbContext = CreateContext();

        var result = dbContext.Entitites.Where(e => e.Key1 == "two").ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("two", e.Key1));
    }

    [Fact]
    public void Should_query_by_key_components()
    {
        var dbContext = CreateContext();

        var result = dbContext.Entitites.Where(e => e.Key1 == "two" && e.Key2 == 3).ToList();

        Assert.Single(result);
        Assert.All(result, e => Assert.Equal("two", e.Key1));
        Assert.All(result, e => Assert.Equal(3, e.Key2));
    }

    [Fact]
    public void Should_query_by_key_component_with_gt()
    {
        var dbContext = CreateContext();

        var result = dbContext.Entitites.Where(e => e.Key2 > 2).ToList();

        Assert.Single(result);
        Assert.All(result, e => Assert.True(e.Key2 > 2));
    }

    private SingleEntityDbContext<Entity> CreateContext([CallerMemberName] string? name = null)
    {
        var collection = _temporaryDatabase.CreateTemporaryCollection<Entity>(name);

        {
            var context = SingleEntityDbContext.Create(collection, ConfigureContext);
            context.Entitites.AddRange(
                new[]
                {
                    new Entity {Key1 = "one", Key2 = 1}, new Entity {Key1 = "two", Key2 = 2},
                    new Entity {Key1 = "two", Key2 = 3},
                });

            context.SaveChanges();
        }

        return SingleEntityDbContext.Create(collection, ConfigureContext);

        void ConfigureContext(ModelBuilder builder)
        {
            builder.Entity<Entity>()
                .HasKey(nameof(Entity.Key1), nameof(Entity.Key2));
        }
    }

    public class Entity
    {
        public string Key1 { get; set; }

        public int Key2 { get; set; }
    }
}
