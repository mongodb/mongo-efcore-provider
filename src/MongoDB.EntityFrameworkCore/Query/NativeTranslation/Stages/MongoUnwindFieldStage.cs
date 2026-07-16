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
/// A <c>$unwind</c> over an owned-collection element path (EF-347 slice 3 — owned SelectMany). Distinct from
/// <see cref="MongoUnwindStage"/>, which unwinds a reference-Include <see cref="Expressions.LookupExpression"/>
/// join alias; this one flattens an embedded array by its stored element path and needs no lookup.
/// </summary>
internal sealed class MongoUnwindFieldStage : MongoPipelineStage
{
    public MongoUnwindFieldStage(string elementPath) => ElementPath = elementPath;
    public string ElementPath { get; }
}
