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

using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata;

public class VectorIndexOptionsTests
{
    [Fact]
    public void Parameterless_constructor_creates_uninitialized_options()
    {
        var options = new VectorIndexOptions();

        Assert.Equal((VectorSimilarity)(-1), options.Similarity);
        Assert.Equal(0, options.Dimensions);
        Assert.Null(options.Quantization);
        Assert.Null(options.HnswMaxEdges);
        Assert.Null(options.HnswNumEdgeCandidates);
        Assert.Null(options.FilterPaths);
    }

    [Fact]
    public void Parameterized_constructor_sets_all_properties()
    {
        var options = new VectorIndexOptions(VectorSimilarity.Cosine, 1536);

        Assert.Equal(VectorSimilarity.Cosine, options.Similarity);
        Assert.Equal(1536, options.Dimensions);
    }
}
