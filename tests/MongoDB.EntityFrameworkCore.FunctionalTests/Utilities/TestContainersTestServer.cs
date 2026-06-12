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
        // Dispose any container left over from a prior failed start before allocating a new one.
        if (_container != null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }

        var container = new MongoDbAtlasBuilder().Build();

        try
        {
            // Bound startup so a stuck readiness probe (e.g. the Atlas Search Index Management
            // service never coming up) fails fast instead of hanging the run indefinitely.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await container.StartAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _container = container;
        _client = new(ConnectionString);
    }

    public override Task DisposeAsync()
        => _container == null ? Task.CompletedTask : _container.DisposeAsync().AsTask();
}
