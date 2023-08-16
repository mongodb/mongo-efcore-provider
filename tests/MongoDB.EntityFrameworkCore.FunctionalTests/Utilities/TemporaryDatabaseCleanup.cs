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

public class TemporaryDatabaseCleanup
{
    // [Fact()] // Uncomment this and manually run to clean up any left-over test databases
    public async Task Delete_all_EFCoreTest_databases()
    {
        var client = TestServer.GetClient();

        if (TestServer.TestDatabasePrefix.Length < 6)
            throw new InvalidOperationException("Prefix is too short, might delete non-test data");

        var databaseNames = await client.ListDatabaseNamesAsync();
        foreach (var databaseName in await databaseNames.ToListAsync())
        {
            if (databaseName.StartsWith(TestServer.TestDatabasePrefix))
                await client.DropDatabaseAsync(databaseName);
        }
    }
}
