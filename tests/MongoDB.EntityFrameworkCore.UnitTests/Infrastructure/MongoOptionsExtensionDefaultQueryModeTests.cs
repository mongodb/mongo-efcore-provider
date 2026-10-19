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
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

/// <summary>
/// Tests that mutate process-wide query-mode state (<c>MongoOptionsExtension.DefaultQueryMode</c>, environment
/// variables). UnitTests run in parallel, so these must not overlap with any other test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideQueryModeStateCollection
{
    public const string Name = "Process-wide query-mode state";
}

[Collection(ProcessWideQueryModeStateCollection.Name)]
public class MongoOptionsExtensionDefaultQueryModeTests
{
    [Fact]
    public void New_extension_uses_DefaultQueryMode_and_explicit_mode_wins()
    {
        var saved = MongoOptionsExtension.DefaultQueryMode;
        try
        {
            MongoOptionsExtension.DefaultQueryMode = MongoQueryMode.NativeOnly;
            var ext = new MongoOptionsExtension();
            Assert.Equal(MongoQueryMode.NativeOnly, ext.QueryMode);
            Assert.Equal(MongoQueryMode.DriverLinq, ext.WithQueryMode(MongoQueryMode.DriverLinq).QueryMode);
        }
        finally
        {
            MongoOptionsExtension.DefaultQueryMode = saved;
        }
    }
}
