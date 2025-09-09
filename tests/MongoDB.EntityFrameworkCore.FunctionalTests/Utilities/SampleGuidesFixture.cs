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

[CollectionDefinition(nameof(SampleGuidesFixture))]
public class SampleGuidesFixtureCollection : ICollectionFixture<SampleGuidesFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

public class SampleGuidesFixture : TemporaryDatabaseFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        SampleGuides.Populate(MongoDatabase);
    }
}
