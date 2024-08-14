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

using System.Collections;
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class TemporaryDatabaseFixture : IDisposable, IAsyncDisposable
{
    public const string TestDatabasePrefix = "EFCoreTest-";

    private static readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private static int Count;

    public TemporaryDatabaseFixture()
    {
        Client = TestServer.GetClient();
        MongoDatabase = Client.GetDatabase($"{TestDatabasePrefix}{TimeStamp}-{Interlocked.Increment(ref Count)}");
#pragma warning disable CS0618 // Type or member is obsolete
        BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3; // We sometimes insert with C# Driver before firing up EF Provider
#pragma warning restore CS0618 // Type or member is obsolete
    }

    public IMongoDatabase MongoDatabase { get; }

    public IMongoCollection<T> CreateTemporaryCollection<T>(string prefix, params object[] values)
    {
        var valuesSuffix = string.Join('+', values.Select(v =>
        {
            switch (v)
            {
                case null:
                    return "null";
                case IEnumerable enumerable:
                    return string.Join('+', enumerable.Cast<object>().Select(i => i == null ? "null" : i.ToString()));
            }

            var result = v.ToString();
            if (v is Type && result != null)
            {
                // If parameter is a type - shrink the full name, to avoid max length limitation.
                result = result
                    .Replace("MongoDB.EntityFrameworkCore.FunctionalTests.", string.Empty)
                    .Replace("System.Collections.Generic.", string.Empty)
                    .Replace("System.Collections.", string.Empty);
            }

            return result;
        }));

        return CreateTemporaryCollection<T>($"{prefix}_{valuesSuffix}");
    }

    public IMongoCollection<T> CreateTemporaryCollection<T>([CallerMemberName] string? name = null)
    {
        if (name == ".ctor")
            name = GetLastConstructorTypeNameFromStack()
                   ?? throw new InvalidOperationException(
                       "Test was unable to determine a suitable collection name, please pass one to CreateTemporaryCollection");

        MongoDatabase.CreateCollection(name);
        return MongoDatabase.GetCollection<T>(name);
    }

    public IMongoCollection<T> GetExistingTemporaryCollection<T>([CallerMemberName] string? name = null)
    {
        if (name == ".ctor")
            name = GetLastConstructorTypeNameFromStack()
                   ?? throw new InvalidOperationException(
                       "Test was unable to determine a suitable collection name, please pass one to CreateTemporaryCollection");
        return MongoDatabase.GetCollection<T>(name);
    }

    public IMongoClient Client { get; }

    private static string? GetLastConstructorTypeNameFromStack()
        => new System.Diagnostics.StackTrace()
            .GetFrames()
            .Select(f => f.GetMethod())
            .FirstOrDefault(f => f?.Name == ".ctor")
            ?.DeclaringType?.Name;


    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
