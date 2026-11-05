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


using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

public class MongoEntityRemapTests
{
    [Fact]
    public void Resolves_the_first_pair_without_a_dictionary_and_leaves_others_alone()
    {
        object a = new(), tracked = new(), other = new();
        var remap = new MongoEntityRemap(a, tracked);

        Assert.Same(tracked, remap.Resolve(a));
        Assert.Same(other, remap.Resolve(other));
    }

    [Fact]
    public void Resolves_further_pairs_by_reference()
    {
        object a = new(), trackedA = new(), b = new(), trackedB = new();
        var remap = new MongoEntityRemap(a, trackedA);
        remap.Add(b, trackedB);

        Assert.Same(trackedA, remap.Resolve(a));
        Assert.Same(trackedB, remap.Resolve(b));
    }
}
