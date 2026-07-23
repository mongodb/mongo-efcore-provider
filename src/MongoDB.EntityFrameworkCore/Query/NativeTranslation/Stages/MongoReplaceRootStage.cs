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
/// A <c>$replaceRoot</c> that promotes a field (an already-<c>$unwind</c>'d element) to the root
/// document, merging in the owner key and array ordinal under sentinel field names so the re-rooted
/// owned element's shadow key properties materialize non-null:
/// <c>{ $replaceRoot: { newRoot: { $mergeObjects: [ "$&lt;NewRoot&gt;", { __ownerKey: "$_id", __ord: "$__ord" } ] } } }</c>.
/// Used by a bare whole-inner-element owned SelectMany (EF-347) so the unwound owned element becomes
/// the query's root result document.
/// </summary>
internal sealed class MongoReplaceRootStage : MongoPipelineStage
{
    public MongoReplaceRootStage(string newRoot) => NewRoot = newRoot;
    public string NewRoot { get; }
    public const string OwnerKeyField = "__ownerKey";
    public const string OrdinalField = "__ord";
}
