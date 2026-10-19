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
/// Represents a single-accumulator <c>$group</c> over the whole input (<c>_id: null</c>) — the shape used
/// by Sum/Min/Max/Average. Produces one document <c>{ _id: null, &lt;OutputField&gt;: { &lt;acc&gt;: &lt;operand&gt; } }</c>.
/// </summary>
internal sealed class MongoGroupAccumulatorStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoGroupAccumulatorStage"/> class.
    /// </summary>
    /// <param name="accumulator">The MQL accumulator operator ("$sum" / "$min" / "$max" / "$avg").</param>
    /// <param name="operand">The value expression fed to the accumulator.</param>
    /// <param name="outputField">The output field name (conventionally "v").</param>
    public MongoGroupAccumulatorStage(string accumulator, MongoExpression operand, string outputField)
    {
        Accumulator = accumulator;
        Operand = operand;
        OutputField = outputField;
    }

    /// <summary>The MQL accumulator operator ("$sum" / "$min" / "$max" / "$avg").</summary>
    public string Accumulator { get; }

    /// <summary>The value expression fed to the accumulator (field ref, or a constant for count-style sums).</summary>
    public MongoExpression Operand { get; }

    /// <summary>The output field name (conventionally "v").</summary>
    public string OutputField { get; }
}
