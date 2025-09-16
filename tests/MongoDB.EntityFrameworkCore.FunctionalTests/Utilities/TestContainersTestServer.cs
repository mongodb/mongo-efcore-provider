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

using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class TestContainersTestServer : TestServer
{
    private MongoClient? _client;
    private MongoDbAtlasContainer? _container;

    public override string ConnectionString
        => _container.GetConnectionString();

    public override MongoClient Client
        => _client!;

    public override async Task InitializeAsync()
    {
        _container = new MongoDbAtlasBuilder().WithImage("mongodb/mongodb-atlas-local:8.0").Build();

        await _container.StartAsync().ConfigureAwait(false);

        _client = new(ConnectionString);
    }

    public override Task DisposeAsync()
        => _container == null ? Task.CompletedTask : _container.DisposeAsync().AsTask();
}
