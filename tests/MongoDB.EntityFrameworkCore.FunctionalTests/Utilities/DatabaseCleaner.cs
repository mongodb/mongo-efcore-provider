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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class DatabaseCleaner(TemporaryDatabaseFixture fixture)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact(Skip = "Manually run to clean up the database")]
    public async Task CleanDatabase()
    {
        var client = fixture.TestServer.Client;

        var databaseNameCursor = await client.ListDatabaseNamesAsync();
        while (await databaseNameCursor.MoveNextAsync())
        {
            foreach (var databaseName in databaseNameCursor.Current.Where(d => !d.StartsWith("Test")))
            {
                await client.DropDatabaseAsync(databaseName);
            }
        }
    }
}
