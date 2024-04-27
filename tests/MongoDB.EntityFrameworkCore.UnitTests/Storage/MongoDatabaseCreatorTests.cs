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

using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public class MongoDatabaseCreatorTests
{
    [Fact]
    public async Task Does_not_support_anything()
    {
        var databaseCreator = new MongoDatabaseCreator();

        Assert.Throws<NotSupportedException>(() => databaseCreator.CanConnect());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await databaseCreator.CanConnectAsync());

        Assert.Throws<NotSupportedException>(() => databaseCreator.EnsureCreated());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await databaseCreator.EnsureCreatedAsync());

        Assert.Throws<NotSupportedException>(() => databaseCreator.EnsureDeleted());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await databaseCreator.EnsureDeletedAsync());
    }
}
