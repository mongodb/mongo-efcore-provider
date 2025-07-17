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

using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public class MongoTestStoreFactory : ITestStoreFactory
{
    public static MongoTestStoreFactory Instance { get; } = new();

    protected MongoTestStoreFactory()
    {
    }

    public IServiceCollection AddProviderServices(IServiceCollection serviceCollection)
        => serviceCollection.AddEntityFrameworkMongoDB().AddSingleton<ILoggerFactory>(new TestMqlLoggerFactory());

    public TestStore Create(string storeName)
        => MongoTestStore.Create(storeName);

    public virtual TestStore GetOrCreate(string storeName)
        => Create(storeName);

    public virtual ListLoggerFactory CreateListLoggerFactory(Func<string, bool> shouldLogCategory)
        => new TestMqlLoggerFactory(shouldLogCategory);
}
