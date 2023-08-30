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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class TemporaryDatabaseFixture : IDisposable
{
    public const string TestDatabasePrefix = "EFCoreTest-";

    private static readonly string __timeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private static int __count;

    private readonly IMongoClient _mongoClient;

    public TemporaryDatabaseFixture()
    {
        _mongoClient = TestServer.GetClient();
        MongoDatabase = _mongoClient.GetDatabase($"{TestDatabasePrefix}{__timeStamp}-{Interlocked.Increment(ref __count)}");
    }

    public IMongoDatabase MongoDatabase { get; }

    public IMongoCollection<T> CreateTemporaryCollection<T>([CallerMemberName] string? name = null)
    {
        if (name == ".ctor")
            name = GetLastConstructorTypeNameFromStack()
                   ?? throw new InvalidOperationException("Test was unable to determine a suitable collection name, please pass one to CreateTemporaryCollection");

        MongoDatabase.CreateCollection(name);
        return MongoDatabase.GetCollection<T>(name);
    }

    public void Dispose()
    {
        _mongoClient.DropDatabase(MongoDatabase.DatabaseNamespace.DatabaseName);
    }

    private static string? GetLastConstructorTypeNameFromStack()
    {
        return new System.Diagnostics.StackTrace()
            .GetFrames()
            .Select(f => f.GetMethod())
            .FirstOrDefault(f => f?.Name == ".ctor")
            ?.DeclaringType?.Name;
    }
}
