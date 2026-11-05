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

using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// The entities of a replayed row that the state manager resolved to one already tracked, by the instance the row constructed. A row
/// usually has one, so the first pair is held inline and a dictionary is only made for more.
/// </summary>
internal sealed class MongoEntityRemap(object constructed, object tracked)
{
    private Dictionary<object, object>? _more;

    /// <summary>Adds another constructed instance that resolved to <paramref name="trackedInstance"/>.</summary>
    public void Add(object instance, object trackedInstance)
        => (_more ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance))[instance] = trackedInstance;

    /// <summary>The tracked instance for <paramref name="value"/>, or <paramref name="value"/> itself when it was tracked as constructed.</summary>
    public object Resolve(object value)
        => ReferenceEquals(value, constructed) ? tracked : _more != null && _more.TryGetValue(value, out var other) ? other : value;
}
