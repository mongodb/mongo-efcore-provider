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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Allows MongoDB-specific configuration to be performed on <see cref="DbContextOptions"/>.
/// </summary>
public class MongoDbContextOptionsBuilder : IMongoDbContextOptionsBuilderInfrastructure
{
    /// <summary>
    /// Creates a <see cref="MongoDbContextOptionsBuilder" /> with the required options builder.
    /// </summary>
    /// <param name="optionsBuilder">The <see cref="DbContextOptionsBuilder"/> to start from.</param>
    public MongoDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    {
        OptionsBuilder = optionsBuilder;
    }

    /// <summary>
    /// Clones the configuration in this builder.
    /// </summary>
    /// <returns>The cloned configuration.</returns>
    protected virtual DbContextOptionsBuilder OptionsBuilder { get; }

    DbContextOptionsBuilder IMongoDbContextOptionsBuilderInfrastructure.OptionsBuilder => OptionsBuilder;

    /// <summary>
    /// Specifies how the provider translates LINQ queries to MongoDB.
    /// </summary>
    /// <param name="queryMode">The <see cref="MongoQueryMode"/> to use.</param>
    /// <returns>This builder, so that further configuration can be chained.</returns>
    public virtual MongoDbContextOptionsBuilder UseQueryMode(MongoQueryMode queryMode)
    {
        var extension = (OptionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithQueryMode(queryMode);
        ((IDbContextOptionsBuilderInfrastructure)OptionsBuilder).AddOrUpdateExtension(extension);
        return this;
    }
}
