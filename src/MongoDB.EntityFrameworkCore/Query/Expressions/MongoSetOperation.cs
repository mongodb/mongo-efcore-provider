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
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The kind of set operation captured on a <see cref="MongoSelectDefinition"/>.</summary>
internal enum MongoSetOperationKind
{
    /// <summary><c>Concat</c> — <c>$unionWith</c> with no de-duplication.</summary>
    Concat,

    /// <summary><c>Union</c> — <c>$unionWith</c> followed by full-document (<c>$$ROOT</c>) de-duplication.</summary>
    Union,

    /// <summary><c>Intersect</c> — distinct documents (by full-document value) present in both operands.</summary>
    Intersect,

    /// <summary><c>Except</c> — distinct documents of the first operand not present in the second.</summary>
    Except
}

/// <summary>
/// A terminal set operation attached to the outer <see cref="MongoSelectDefinition"/>. The second operand is its own
/// <see cref="MongoSelectDefinition"/>, rendered as a nested pipeline against <see cref="OperandCollectionName"/>,
/// either whole-entity or projected (see <see cref="OperandsProjected"/>).
/// </summary>
internal sealed class MongoSetOperation
{
    public MongoSetOperation(
        MongoSetOperationKind kind, MongoSelectDefinition operandSelect, string operandCollectionName,
        IEntityType operandEntityType, bool operandsProjected = false)
    {
        Kind = kind;
        OperandSelect = operandSelect;
        OperandCollectionName = operandCollectionName;
        OperandEntityType = operandEntityType;
        OperandsProjected = operandsProjected;
    }

    public MongoSetOperationKind Kind { get; }
    public MongoSelectDefinition OperandSelect { get; }
    public string OperandCollectionName { get; }

    /// <summary>
    /// The operand's own entity type, which may differ from the outer root for a projected-operand set op. The
    /// lowerer's synthetic <c>$set</c> sort-field allocator must reserve this type's top-level element names too, or a
    /// computed sort on the operand silently clobbers one of its mapped elements.
    /// </summary>
    public IEntityType OperandEntityType { get; }

    /// <summary>
    /// <c>true</c> when both operands were projected selects when the set op was attached, so each operand's
    /// <c>$project</c> is emitted before the combine (see <c>MongoSelectLowerer.Lower</c>). <c>false</c> for a
    /// whole-entity set op or a projection composed after it, where <c>$project</c> follows the combine.
    /// </summary>
    public bool OperandsProjected { get; }

    /// <summary>
    /// Outer-select filter/sort/page ops recorded between the previous link and this one (e.g. the
    /// <c>OrderBy</c>/<c>Take</c> in <c>A.Union(B).OrderBy(..).Take(1).Union(C)</c>), emitted just before this link's
    /// stage so they apply to the result combined so far. Empty for the first link.
    /// </summary>
    /// <remarks>
    /// Written only by <see cref="MongoSelectDefinition.AppendSetOperation"/>, which moves accumulated
    /// <c>TrailingOps</c> here, so <c>TrailingOps</c> always means "after the last link".
    /// </remarks>
    public IReadOnlyList<MongoSelectOp> PrecedingOps { get; private set; } = [];

    internal void SetPrecedingOps(IReadOnlyList<MongoSelectOp> precedingOps)
        => PrecedingOps = precedingOps;
}
