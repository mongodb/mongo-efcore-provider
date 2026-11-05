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
/// A <c>$replaceRoot</c> promoting a field to the root. With <see cref="MergeOwnerKeySentinels"/> (owned bare-element
/// SelectMany) the owner key and array ordinal are merged in so the owned element's shadow keys materialize:
/// <c>{ newRoot: { $mergeObjects: [ "$&lt;NewRoot&gt;", { __mongoef_shadow: { __ownerKey: "$_id", __ord: "$__ord" } } ] } }</c>.
/// Nesting under one reserved <see cref="ShadowField"/> limits collisions with stored fields (which
/// <c>$mergeObjects</c> would silently overwrite) to that name, which the translator declines
/// (<c>MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable</c>). A reference bare-entity
/// SelectMany has a stored key and gets the plain form.
/// </summary>
internal sealed class MongoReplaceRootStage : MongoPipelineStage
{
    public MongoReplaceRootStage(string newRoot, bool mergeOwnerKeySentinels = true)
    {
        NewRoot = newRoot;
        MergeOwnerKeySentinels = mergeOwnerKeySentinels;
    }

    public string NewRoot { get; }

    public bool MergeOwnerKeySentinels { get; }

    /// <summary>The reserved top-level field the sentinel merge adds; <see cref="OwnerKeyField"/> and <see cref="OrdinalField"/> nest under it.</summary>
    public const string ShadowField = "__mongoef_shadow";

    public const string OwnerKeyField = "__ownerKey";
    public const string OrdinalField = "__ord";
}
