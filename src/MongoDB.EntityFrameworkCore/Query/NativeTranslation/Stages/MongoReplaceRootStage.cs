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
/// merges in the owner key and array ordinal so the re-rooted owned element's shadow key properties
/// materialize non-null. Both sentinels are nested one level under a SINGLE reserved wrapper field
/// (<see cref="ShadowField"/>), never as two individually-named top-level keys:
/// <c>{ $replaceRoot: { newRoot: { $mergeObjects: [ "$&lt;NewRoot&gt;",
/// { __mongoef_shadow: { __ownerKey: "$_id", __ord: "$__ord" } } ] } } }</c>.
/// The nesting is what keeps an ordinary stored property from colliding with the sentinels: because
/// <c>$mergeObjects</c> merges the sentinel document AFTER the unwound element, a same-named real field
/// would be silently overwritten — with the wrapper, only the one reserved <see cref="ShadowField"/> name
/// can collide (and the translator declines that shape; see
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable</c>).
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

    /// <summary>
    /// The single reserved top-level field the sentinel merge adds to the re-rooted document. The owner-key
    /// and ordinal sentinels are nested one level UNDER it (<c>__mongoef_shadow.__ownerKey</c> /
    /// <c>__mongoef_shadow.__ord</c>), so an ordinary stored property can only ever collide with this ONE
    /// name — never with <see cref="OwnerKeyField"/>/<see cref="OrdinalField"/> individually, which are not
    /// top-level keys of the merged document.
    /// </summary>
    public const string ShadowField = "__mongoef_shadow";

    public const string OwnerKeyField = "__ownerKey";
    public const string OrdinalField = "__ord";
}
