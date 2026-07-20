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
/// <see cref="MongoSelectDefinition"/>. The second operand is captured as its own plain whole-entity
/// <see cref="MongoSelectDefinition"/> (rendered as the nested <c>$unionWith</c> pipeline against
/// <see cref="OperandCollectionName"/>). Whole-entity, terminal-only (EF-347 slice 2).
/// </summary>
internal sealed class MongoSetOperation
{
    public MongoSetOperation(MongoSetOperationKind kind, MongoSelectDefinition operandSelect, string operandCollectionName)
    {
        Kind = kind;
        OperandSelect = operandSelect;
        OperandCollectionName = operandCollectionName;
    }

    public MongoSetOperationKind Kind { get; }
    public MongoSelectDefinition OperandSelect { get; }
    public string OperandCollectionName { get; }
}
