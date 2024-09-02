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

using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// MongoDB-specific extension methods for <see cref="IReadOnlyNavigation" />.
/// </summary>
public static class MongoNavigationExtensions
{
    /// <summary>
    /// Determine whether a navigation is embedded or not.
    /// </summary>
    /// <param name="navigation">The <see cref="IReadOnlyNavigation"/> to consider.</param>
    /// <returns>
    /// <see langword="true"/> if the navigation is embedded,
    /// <see langword="false"/> if it is not.
    /// </returns>
    public static bool IsEmbedded(this IReadOnlyNavigation navigation)
        => !navigation.IsOnDependent
           && !navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot();
}
