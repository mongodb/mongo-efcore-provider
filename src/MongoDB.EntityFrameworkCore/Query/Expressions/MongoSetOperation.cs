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

/// <summary>The kind of set operation captured on a <see cref="MongoSelectDefinition"/>.</summary>
internal enum MongoSetOperationKind
{
    /// <summary><c>Concat</c> — <c>$unionWith</c> with no de-duplication.</summary>
    Concat,

    /// <summary><c>Union</c> — <c>$unionWith</c> followed by full-document (<c>$$ROOT</c>) de-duplication.</summary>
    Union,

    /// <summary><c>Intersect</c> — documents present (by full-document value) in BOTH operands (deduped).</summary>
    Intersect,

    /// <summary><c>Except</c> — distinct documents of the first operand not present in the second.</summary>
    Except
}

/// <summary>
/// A terminal set operation (<c>Union</c>/<c>Concat</c>) attached to the outer
/// <see cref="MongoSelectDefinition"/>. The second operand is captured as its own
/// <see cref="MongoSelectDefinition"/> (rendered as the nested <c>$unionWith</c> pipeline against
/// <see cref="OperandCollectionName"/>) — either whole-entity, or projected (same collection; see
/// <see cref="OperandsProjected"/>). Terminal-only.
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
    /// The operand's OWN entity type. For a whole-entity set op this equals the outer query's root entity
    /// type (<c>TryTranslateSetOperation</c> requires the two to be equal), but for a PROJECTED-operand set op
    /// the operands may be different entity types entirely — and the operand's own pipeline (its
    /// <c>$match</c>/<c>$sort</c>/<c>$skip</c>/<c>$limit</c> ops and its <c>$project</c>) is emitted inside the
    /// nested <c>$unionWith</c>/set-difference pipeline against ITS collection. The lowerer's synthetic
    /// <c>$set</c> sort-field allocator therefore has to reserve the operand type's top-level element names
    /// too: a computed sort on the operand allocates from the SAME allocator, and a <c>$set</c> of a name that
    /// collides with one of the operand's own mapped elements silently clobbers it (EF-408 gap 1, measured
    /// reachable — see NativeComputedSortTests.Synthetic_sort_field_does_not_clobber_a_set_op_operands_own_element).
    /// </summary>
    public IEntityType OperandEntityType { get; }

    /// <summary>
    /// <c>true</c> when both operands were plain projected selects (<see cref="MongoSelectDefinition.Projection"/>
    /// populated) at the time the set op was attached, so each operand's own <c>$project</c> is part of ITS
    /// pipeline and must be emitted BEFORE the combine — source1's ahead of the set-op stage, the operand's
    /// inside the nested pipeline (see <c>MongoSelectLowerer.Lower</c>). <c>false</c> for a whole-entity set op
    /// or a trailing projection composed after a set op, where any <c>$project</c> is emitted AFTER the combine.
    /// </summary>
    public bool OperandsProjected { get; }
}
