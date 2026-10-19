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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A <c>$unwind</c> over an owned-collection element path (owned SelectMany). Distinct from
/// <see cref="MongoUnwindStage"/>, which unwinds a reference-Include <see cref="Expressions.LookupExpression"/>
/// join alias; this one flattens an embedded array by its stored element path and needs no lookup.
/// </summary>
internal sealed class MongoUnwindFieldStage : MongoPipelineStage
{
    public MongoUnwindFieldStage(string elementPath, string? includeArrayIndex = null)
    {
        ElementPath = elementPath;
        IncludeArrayIndex = includeArrayIndex;
    }

    public string ElementPath { get; }

    /// <summary>
    /// The output field to write the zero-based array index to (<c>includeArrayIndex</c>), or
    /// <see langword="null"/> to omit it. Set for a bare whole-inner-element owned SelectMany so a following
    /// <see cref="MongoReplaceRootStage"/> can carry the ordinal into the re-rooted element as the owned
    /// collection's synthesized ordinal key.
    /// </summary>
    public string? IncludeArrayIndex { get; }
}
