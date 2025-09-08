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

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Vector similarity function to use to search for top K-nearest neighbors.
/// </summary>
public enum VectorSimilarity
{
    /// <summary>
    /// Measures the distance between ends of vectors.
    /// </summary>
    Euclidean,

    /// <summary>
    /// Measures similarity based on the angle between vectors.
    /// </summary>
    Cosine,

    /// <summary>
    /// mMasures similarity like cosine, but takes into account the magnitude of the vector.
    /// </summary>
    DotProduct,
}
