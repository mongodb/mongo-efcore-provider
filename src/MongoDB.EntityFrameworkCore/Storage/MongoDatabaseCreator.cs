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

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Creates and deletes databases on MongoDB servers.
/// </summary>
/// <remarks>
/// This class is not typically used directly from application code.
/// </remarks>
public class MongoDatabaseCreator(
    IMongoClientWrapper clientWrapper,
    IDesignTimeModel designTimeModel,
    IUpdateAdapterFactory updateAdapterFactory,
    IDatabase database,
    ICurrentDbContext currentDbContext)
    : IDatabaseCreator
{
    /// <inheritdoc/>
    public bool EnsureDeleted()
        => clientWrapper.DeleteDatabase();

    /// <inheritdoc/>
    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
        => clientWrapper.DeleteDatabaseAsync(cancellationToken);

    /// <inheritdoc/>
    public bool EnsureCreated()
    {
        var created = clientWrapper.CreateDatabase(currentDbContext.Context.GetService<IDesignTimeModel>());
        if (created)
        {
            SeedFromModel();
        }
        return created;
    }

    /// <inheritdoc/>
    public async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var created = await clientWrapper.CreateDatabaseAsync(currentDbContext.Context.GetService<IDesignTimeModel>(), cancellationToken).ConfigureAwait(false);
        if (created)
        {
            await SeedFromModelAsync(cancellationToken).ConfigureAwait(false);
        }
        return created;
    }

    /// <summary>
    /// Inserts any data that was added to the model using an overload of <see cref="EntityTypeBuilder{TEntity}.HasData(TEntity[])"/>
    /// </summary>
    public void SeedFromModel()
        => database.SaveChanges(AddModelData().GetEntriesToSave());

    /// <summary>
    /// Inserts any data that was added to the model using an overload of <see cref="EntityTypeBuilder{TEntity}.HasData(TEntity[])"/>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token</param>
    /// <returns>A task representing this async operation.</returns>
    public async Task SeedFromModelAsync(CancellationToken cancellationToken = default)
        => await database.SaveChangesAsync(AddModelData().GetEntriesToSave(), cancellationToken).ConfigureAwait(false);

    private IUpdateAdapter AddModelData()
    {
        var updateAdapter = updateAdapterFactory.CreateStandalone();
        foreach (var entityType in designTimeModel.Model.GetEntityTypes())
        {
            foreach (var targetSeed in entityType.GetSeedData())
            {
                var runtimeEntityType = updateAdapter.Model.FindEntityType(entityType.Name)!;
                var entry = updateAdapter.CreateEntry(targetSeed, runtimeEntityType);
                entry.EntityState = EntityState.Added;
            }
        }

        return updateAdapter;
    }

    /// <inheritdoc/>
    public bool CanConnect()
    {
        try
        {
            // Do anything that causes an actual database connection with no side effects
            clientWrapper.DatabaseExists();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Do anything that causes an actual database connection with no side effects
            await clientWrapper.DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
