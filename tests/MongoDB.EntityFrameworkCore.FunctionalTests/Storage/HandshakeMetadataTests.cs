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

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

public class HandshakeMetadataTests : IClassFixture<TemporaryDatabaseFixture>
{
    public HandshakeMetadataTests(TemporaryDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    private TemporaryDatabaseFixture Fixture { get; }

    [Fact]
    public void Declares_efcore_alongside_a_library_declared_by_the_caller()
    {
        var clientSettings = MongoClientSettings.FromConnectionString(Fixture.TestServer.ConnectionString);
        clientSettings.LibraryInfo = new LibraryInfo("something-else", "1.2.3");

        using var context = CreateContext(builder =>
            builder.UseMongoDB(clientSettings, TestDatabaseNamer.GetUniqueDatabaseName()));

        var libraries = GetLibraries(context);

        Assert.Contains(libraries, library => library.Name == "something-else");

        var provider = Assert.Single(libraries, library => library.Name == "efcore");
        Assert.NotEmpty(provider.Version);

        Assert.DoesNotContain('+', provider.Version);
    }

    [Fact]
    public void Declares_efcore_on_a_preconfigured_client()
    {
        var client = new MongoClient(Fixture.TestServer.ConnectionString);

        using var context = CreateContext(builder =>
            builder.UseMongoDB(client, TestDatabaseNamer.GetUniqueDatabaseName()));

        Assert.Contains(GetLibraries(context), library => library.Name == "efcore");
    }

    private static TestContext CreateContext(Action<DbContextOptionsBuilder<TestContext>> configure)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestContext>()
            .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        configure(optionsBuilder);

        return new TestContext(optionsBuilder.Options);
    }

    private static LibraryInfo[] GetLibraries(DbContext context)
    {
        var client = context.GetService<IMongoClientWrapper>().Client;
        var clientMetadata = GetPrivateField(client.Cluster, "_clientMetadata");

        return (LibraryInfo[]?)GetPrivateField(clientMetadata!, "_libraryInfos") ?? [];
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found on '{target.GetType()}'.");
    }

    private class TestContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<BasicEntity> Entities { get; init; }
    }

    private class BasicEntity
    {
        public ObjectId _id { get; set; }
    }
}
