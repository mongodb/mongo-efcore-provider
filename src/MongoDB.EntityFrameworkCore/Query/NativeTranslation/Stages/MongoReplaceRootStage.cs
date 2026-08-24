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
/// A <c>$replaceRoot</c> stage that promotes a field to the root document.
/// <para>
/// When <see cref="MergeOwnerKeySentinels"/> is <see langword="true"/> (owned bare-element SelectMany):
/// merges in the owner key and array ordinal under sentinel field names so the re-rooted owned element's
/// shadow key properties materialize non-null:
/// <c>{ $replaceRoot: { newRoot: { $mergeObjects: [ "$&lt;NewRoot&gt;", { __ownerKey: "$_id", __ord: "$__ord" } ] } } }</c>.
/// </para>
/// <para>
/// When <see cref="MergeOwnerKeySentinels"/> is <see langword="false"/> (reference bare-entity SelectMany):
/// a plain <c>{ $replaceRoot: { newRoot: "$&lt;NewRoot&gt;" } }</c> — a reference entity carries its own real
/// stored key, so no sentinel merge is needed.
/// </para>
/// </summary>
internal sealed class MongoReplaceRootStage : MongoPipelineStage
{
    public MongoReplaceRootStage(string newRoot, bool mergeOwnerKeySentinels = true)
    {
        NewRoot = newRoot;
        MergeOwnerKeySentinels = mergeOwnerKeySentinels;
    }

    public string NewRoot { get; }

    /// <summary>
    /// Selects which of the two forms described in the class-level <see cref="MongoReplaceRootStage"/> summary
    /// to render: <see langword="true"/> for the owned sentinel-merge form, <see langword="false"/> for the
    /// plain <c>$replaceRoot</c> form.
    /// </summary>
    public bool MergeOwnerKeySentinels { get; }

    public const string OwnerKeyField = "__ownerKey";
    public const string OrdinalField = "__ord";
}
