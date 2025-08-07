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

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class MongoApiConsistencyTest(MongoApiConsistencyTest.MongoApiConsistencyFixture fixture)
    : ApiConsistencyTestBase<MongoApiConsistencyTest.MongoApiConsistencyFixture>(fixture)
{
    protected override void AddServices(ServiceCollection serviceCollection)
        => serviceCollection.AddEntityFrameworkMongoDB();

    public override void Public_inheritable_apis_should_be_virtual()
    {
        // Ignored for now.
    }

    protected override Assembly TargetAssembly
        => typeof(MongoDatabaseWrapper).Assembly;

    public class MongoApiConsistencyFixture : ApiConsistencyFixtureBase
    {
        public override HashSet<Type> FluentApiTypes { get; } =
        [
            typeof(MongoDbContextOptionsBuilder),
            typeof(MongoDbContextOptionsExtensions),
            typeof(MongoEntityTypeBuilderExtensions),
            typeof(MongoIndexBuilderExtensions),
            typeof(MongoPropertyBuilderExtensions),
            typeof(MongoServiceCollectionExtensions)
        ];

public override
            Dictionary<Type,
                (Type? ReadonlyExtensions,
                Type? MutableExtensions,
                Type? ConventionExtensions,
                Type? ConventionBuilderExtensions,
                Type? RuntimeExtensions)> MetadataExtensionTypes { get; }
            = new()
            {
                {
                    typeof(IReadOnlyModel), (
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                },
                {
                    typeof(IReadOnlyEntityType), (
                        typeof(MongoEntityTypeExtensions),
                        typeof(MongoEntityTypeExtensions),
                        typeof(MongoEntityTypeExtensions),
                        typeof(MongoEntityTypeBuilderExtensions),
                        null
                    )
                },
                {
                    typeof(IReadOnlyKey), (
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                },
                {
                    typeof(IReadOnlyProperty), (
                        typeof(MongoPropertyExtensions),
                        null, // typeof(MongoPropertyExtensions), TODO: Should be able to override base here: see https://github.com/dotnet/efcore/issues/36521
                        null, // typeof(MongoPropertyExtensions), TODO: Should be able to override base here: see https://github.com/dotnet/efcore/issues/36521
                        typeof(MongoPropertyBuilderExtensions),
                        null
                    )
                },
                {
                    typeof(IReadOnlyIndex), (
                        typeof(MongoIndexExtensions),
                        typeof(MongoIndexExtensions),
                        typeof(MongoIndexExtensions),
                        typeof(MongoIndexBuilderExtensions),
                        null
                    )
                },
                {
                    typeof(IReadOnlyElementType), (
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                }
            };
    }
}
