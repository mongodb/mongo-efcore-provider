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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A terminal SelectMany over an owned (embedded) collection: the document element path to <c>$unwind</c>
/// before the result-selector <c>$project</c> (EF-347 slice 3). Whole native SelectMany is terminal-only.
/// </summary>
internal sealed class MongoUnwindSource
{
    public MongoUnwindSource(string elementPath, IEntityType innerEntityType)
    {
        ElementPath = elementPath;
        InnerEntityType = innerEntityType;
    }

    /// <summary>The owned-collection element path to unwind (e.g. <c>"Items"</c>), rendered as <c>$Items</c>.</summary>
    public string ElementPath { get; }

    /// <summary>The owned (inner) entity type unwound from <see cref="ElementPath"/> — used to resolve
    /// ti.Inner member accesses to element names in the trailing SelectMany projection (EF-347 slice 4).</summary>
    public IEntityType InnerEntityType { get; }
}
