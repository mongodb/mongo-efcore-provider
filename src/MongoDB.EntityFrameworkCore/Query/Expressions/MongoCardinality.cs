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

using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The cardinality reducer applied to an entity result.</summary>
internal enum MongoReducerKind { First, FirstOrDefault, Single, SingleOrDefault, Last, LastOrDefault }

/// <summary>The scalar aggregate applied to a query.</summary>
internal enum MongoAggregateOperator { Count, LongCount, Sum, Min, Max, Average, Any, All }

/// <summary>What a scalar aggregate yields when the server returns no rows (empty input).</summary>
internal enum MongoEmptyAggregateBehavior { DefaultValue, ReturnNull, Throw }

/// <summary>
/// Native IR for a terminal cardinality/aggregate operator: exactly one of <see cref="Reducer"/> (First/Single/
/// Last) or <see cref="Aggregate"/> is set, enforced by the <see cref="ForReducer"/>/<see cref="ForAggregate"/>
/// factories. Populated by <c>NativeCardinalityBinder</c>; read by the gate, lowerer and shaper.
/// </summary>
internal sealed class MongoCardinality
{
    private MongoCardinality(
        MongoReducerKind? reducer,
        MongoAggregateOperator? aggregate,
        MongoExpression? selector,
        MongoEmptyAggregateBehavior emptyBehavior,
        object? emptyValue,
        Type resultType,
        bool presenceOnly,
        object? presentValue,
        bool unorderedLastRow = false)
    {
        Reducer = reducer;
        Aggregate = aggregate;
        Selector = selector;
        EmptyBehavior = emptyBehavior;
        EmptyValue = emptyValue;
        ResultType = resultType;
        PresenceOnly = presenceOnly;
        PresentValue = presentValue;
        UnorderedLastRow = unorderedLastRow;
    }

    /// <summary>Reducer kind for entity reducers; null for scalar aggregates.</summary>
    public MongoReducerKind? Reducer { get; }

    /// <summary>Aggregate operator for scalar aggregates; null for entity reducers.</summary>
    public MongoAggregateOperator? Aggregate { get; }

    /// <summary>The aggregate selector field ref (Sum(x=>x.Price) → "$price"), or null.</summary>
    public MongoExpression? Selector { get; }

    /// <summary>How the scalar path resolves an empty result set.</summary>
    public MongoEmptyAggregateBehavior EmptyBehavior { get; }

    /// <summary>The typed value yielded on empty input when <see cref="EmptyBehavior"/> is DefaultValue.</summary>
    public object? EmptyValue { get; }

    /// <summary>The CLR result type of the terminal operator.</summary>
    public Type ResultType { get; }

    /// <summary>
    /// <see langword="true"/> for Any/All: the result is determined solely by whether a row survived the
    /// terminal stage (<see cref="PresentValue"/>), not by deserializing a field from it.
    /// </summary>
    public bool PresenceOnly { get; }

    /// <summary>The result value to yield when a row survives, for a <see cref="PresenceOnly"/> aggregate.</summary>
    public object? PresentValue { get; }

    /// <summary>
    /// <see langword="true"/> for Last/LastOrDefault with no prior sort to flip. <c>MongoSelectLowerer</c> then
    /// emits <c>$group{_id:null,_last:{$last:"$$ROOT"}}</c> + <c>$replaceRoot</c> after any <c>$lookup</c> (so an
    /// included collection is in "$$ROOT"), instead of sort-flip + <c>$limit</c>.
    /// </summary>
    public bool UnorderedLastRow { get; }

    /// <summary>Builds the IR for an entity-reducer terminal operator (First/FirstOrDefault/Single/SingleOrDefault).</summary>
    public static MongoCardinality ForReducer(MongoReducerKind kind, Type resultType, bool unorderedLastRow = false)
        => new(kind, null, null, default, null, resultType, presenceOnly: false, presentValue: null, unorderedLastRow);

    /// <summary>Builds the IR for a scalar-aggregate terminal operator.</summary>
    public static MongoCardinality ForAggregate(
        MongoAggregateOperator op,
        MongoExpression? selector,
        MongoEmptyAggregateBehavior emptyBehavior,
        object? emptyValue,
        Type resultType,
        bool presenceOnly,
        object? presentValue)
        => new(null, op, selector, emptyBehavior, emptyValue, resultType, presenceOnly, presentValue);
}
