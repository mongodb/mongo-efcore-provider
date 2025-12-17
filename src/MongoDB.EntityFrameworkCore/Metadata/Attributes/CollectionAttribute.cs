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

// ReSharper disable once CheckNamespace
namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// Specifies the collection that an entity is mapped to.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class CollectionAttribute : Attribute
{
    /// <summary>
    /// Creates a <see cref="CollectionAttribute"/> with the required collection name.
    /// </summary>
    /// <param name="name">Name of the collection to map the attributed type to.</param>
    public CollectionAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"The argument '{nameof(name)}' cannot be null, empty or contain only whitespace.",
                nameof(name));
        }

        Name = name;
    }

    /// <summary>
    /// The name of the collection the type is mapped to.
    /// </summary>
    public string Name { get; }
}
