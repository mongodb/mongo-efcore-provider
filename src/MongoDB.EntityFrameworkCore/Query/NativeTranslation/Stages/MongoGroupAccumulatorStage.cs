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

using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A single-accumulator <c>$group</c> over the whole input (<c>_id: null</c>), the Sum/Min/Max/Average shape:
/// <c>{ _id: null, &lt;OutputField&gt;: { &lt;acc&gt;: &lt;operand&gt; } }</c>.
/// </summary>
internal sealed class MongoGroupAccumulatorStage : MongoPipelineStage
{
    public MongoGroupAccumulatorStage(string accumulator, MongoExpression operand, string outputField, bool wrapsOperand = false)
    {
        Accumulator = accumulator;
        Operand = operand;
        OutputField = outputField;
        WrapsOperand = wrapsOperand;
    }

    public string Accumulator { get; }

    public MongoExpression Operand { get; }

    public string OutputField { get; }

    /// <summary>
    /// Whether the accumulator reduces <c>{_v: operand}</c> documents instead of the bare operand
    /// (<see cref="NativeAggregateReadBack.ReducesWrappedValue"/>), so a MISSING or null operand takes part in the
    /// ordering rather than being skipped.
    /// </summary>
    public bool WrapsOperand { get; }
}
