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

using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public sealed class MongoDbAtlasBuilder : ContainerBuilder<MongoDbAtlasBuilder, MongoDbAtlasContainer, IContainerConfiguration>
{
    private const string MongoDbAtlasImage = "mongodb/mongodb-atlas-local:8.0";
    public const ushort MongoDbAtlasPort = 27017;

    public MongoDbAtlasBuilder()
        : this(new())
        => DockerResourceConfiguration = Init().DockerResourceConfiguration;

    private MongoDbAtlasBuilder(ContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
        => DockerResourceConfiguration = resourceConfiguration;

    protected override ContainerConfiguration DockerResourceConfiguration { get; }

    public override MongoDbAtlasContainer Build()
    {
        Validate();

        var builder = WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new WaitIndicateReadiness()));

        return new(builder.DockerResourceConfiguration);
    }

    protected override MongoDbAtlasBuilder Init()
        => base.Init()
            .WithImage(MongoDbAtlasImage)
            .WithPortBinding(MongoDbAtlasPort, true);

    protected override MongoDbAtlasBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        => Merge(DockerResourceConfiguration, new ContainerConfiguration(resourceConfiguration));

    protected override MongoDbAtlasBuilder Clone(IContainerConfiguration resourceConfiguration)
        => Merge(DockerResourceConfiguration, new ContainerConfiguration(resourceConfiguration));

    protected override MongoDbAtlasBuilder Merge(IContainerConfiguration oldValue, IContainerConfiguration newValue)
        => new(new ContainerConfiguration(oldValue, newValue));


    private sealed class WaitIndicateReadiness : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            var connectionString = ((MongoDbAtlasContainer)container).GetConnectionString();

            using var client = new MongoClient(connectionString);
            var databaseName = Guid.NewGuid().ToString();
            var weGood = false;

            try
            {
                var database = client.GetDatabase(databaseName);
                var collectionName = Guid.NewGuid().ToString();
                await database.CreateCollectionAsync(collectionName);

                var model = new CreateSearchIndexModel(
                    Guid.NewGuid().ToString(),
                    SearchIndexType.VectorSearch,
                    BsonDocument.Parse(
                    """
                    {
                      "fields": [
                        {
                          "type": "vector",
                          "path": "Dummy",
                          "numDimensions": 8,
                          "similarity": "cosine"
                        }
                      ]
                    }
                    """));

                await database.GetCollection<BsonDocument>(collectionName).SearchIndexes.CreateOneAsync(model);
                weGood = true;
            }
            catch
            {
                // Intentionally ignored.
            }

            try
            {
                await client.DropDatabaseAsync(databaseName);
            }
            catch
            {
                // Intentionally ignored.
            }

            return weGood;
        }
    }
}
