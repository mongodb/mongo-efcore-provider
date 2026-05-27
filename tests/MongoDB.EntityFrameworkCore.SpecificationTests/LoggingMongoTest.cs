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
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.SpecificationTests;

public class LoggingMongoTest : LoggingTestBase
{
    protected override DbContextOptionsBuilder CreateOptionsBuilder(IServiceCollection services)
        => new DbContextOptionsBuilder()
            .UseMongoDB("mongodb://localhost:27017", "LoggingMongoTest")
            .UseInternalServiceProvider(services.AddEntityFrameworkMongoDB().BuildServiceProvider(validateScopes: true));

    protected override TestLogger CreateTestLogger()
        => new TestLogger<MongoLoggingDefinitions>();

    protected override string ProviderName
        => "MongoDB.EntityFrameworkCore";

    protected override string ProviderVersion
        => typeof(MongoOptionsExtension).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion!;

    protected override string DefaultOptions
        => "ConnectionString=mongodb://localhost DatabaseName=LoggingMongoTest ";
}
