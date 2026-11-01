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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Turns the DOM shaper of a scalar projection into a one-pass one: instead of building a <see cref="BsonDocument"/> per row and reading
/// it by alias, the cursor's serializer reads each projected element straight into a typed slot of a <see cref="MongoProjectedRow{TValues, TStates}"/>,
/// and the same shaper, now reading slots, runs when the enumerator reaches the row.
/// </summary>
/// <remarks>
/// Only the simple case is taken: the shaper may touch the document parameter solely through the four <see cref="BsonBinding"/> scalar reads
/// (<c>GetScalarProjectionValueAtElement</c>, <c>GetPropertyValueAtElement</c>, <c>GetElementValue</c>, <c>GetKindAwareElementValue</c>) with a
/// constant alias. Anything else (a nested document or array, an owned or joined entity, a dotted path, the whole document) makes
/// <see cref="TryPlan"/> return <see langword="null"/> and the query keeps the DOM shaper. The shaper itself still runs per row at
/// <c>MoveNext</c>, so client-side code in the projection runs exactly when, and as often as, it did.
/// </remarks>
internal static class MongoProjectionStreamingPlanner
{
    // Two state bits per slot in a ulong.
    private const int MaxSlots = 32;

    // ValueTuple holds seven items before nesting through Rest.
    private const int TupleWidth = 7;

    /// <summary>The streaming form of a projection.</summary>
    /// <param name="RowType">The <see cref="MongoProjectedRow{TValues, TStates}"/> (or tracked form) the cursor yields.</param>
    /// <param name="Shaper">A <c>Func&lt;QueryContext, TRow, TResult&gt;</c> run per row by the enumerator.</param>
    /// <param name="RowReader">A <c>Func&lt;QueryContext, IBsonReader, BsonDeserializationContext, TRow&gt;</c> run by the cursor's serializer.</param>
    /// <param name="ResultType">The projection's result type.</param>
    /// <param name="TracksEntities">Whether the row reader defers tracking (a tracked query whose projection holds entities): the cursor needs
    /// <see cref="MongoTrackedProjectionSerializer{TRow}"/>.</param>
    public sealed record Plan(Type RowType, Delegate Shaper, Delegate RowReader, Type ResultType, bool TracksEntities = false);

    private static readonly MethodInfo ReadStartDocumentMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadStartDocument))!;
    private static readonly MethodInfo ReadEndDocumentMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadEndDocument))!;
    private static readonly MethodInfo ReadBsonTypeMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadBsonType))!;
    private static readonly MethodInfo ReadNameMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadName), [typeof(INameDecoder)])!;
    private static readonly MethodInfo GetCurrentBsonTypeMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.GetCurrentBsonType))!;
    private static readonly MethodInfo ReadNullMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadNull))!;
    private static readonly MethodInfo SkipValueMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.SkipValue))!;
    private static readonly MethodInfo SkipNameMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.SkipName))!;

    private static readonly MethodInfo DeserializeMethod =
        typeof(IBsonSerializer).GetMethod(
            nameof(IBsonSerializer.Deserialize), [typeof(BsonDeserializationContext), typeof(BsonDeserializationArgs)])!;

    private static readonly MethodInfo ScalarProjectionMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.ScalarProjection))!;
    private static readonly MethodInfo PropertyAtElementMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.PropertyAtElement))!;
    private static readonly MethodInfo ElementMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.Element))!;

    // The BsonBinding reads that are taken over, by name. They are resolved by name because they are internal and generic.
    private const string ScalarProjectionName = "GetScalarProjectionValueAtElement";
    private const string PropertyAtElementName = "GetPropertyValueAtElement";
    private const string ElementName = "GetElementValue";
    private const string KindAwareElementName = "GetKindAwareElementValue";
    private const string StreamedValueName = "GetStreamedValueAtElement";

    private static readonly MethodInfo StreamedListMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.StreamedList))!;
    private static readonly MethodInfo StreamedEntityMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.StreamedEntity))!;

    /// <summary>
    /// Plans the streaming form of <paramref name="shaperBody"/>, a DOM shaper over <paramref name="bsonDoc"/>, or returns
    /// <see langword="null"/> when it reads the document in any way other than the simple scalar reads.
    /// </summary>
    public static Plan? TryPlan(
        Expression shaperBody, ParameterExpression bsonDoc, ParameterExpression queryContext, bool trackedQuery = false,
        string? singleOutputField = null)
    {
        var collector = new SlotVisitor(bsonDoc, rowParameter: null, valuesType: null, slots: []);
        collector.Visit(shaperBody);
        if (collector.Ineligible || collector.Slots.Count > MaxSlots)
        {
            return null;
        }

        var slots = collector.Slots;
        var valuesType = BuildTupleType(slots.Select(s => s.ValueType).ToArray(), 0);

        // A tracked query's entity slots were constructed with their tracking deferred: the row carries it (see below).
        var tracksEntities = trackedQuery && slots.Any(s => s.IsEntity);
        var statesType = slots.Count <= 16 ? typeof(uint) : typeof(ulong);
        var rowType = (tracksEntities ? typeof(MongoTrackedProjectedRow<,>) : typeof(MongoProjectedRow<,>)).MakeGenericType(valuesType, statesType);
        var rowParameter = Expression.Parameter(rowType, "row");

        var rewriter = new SlotVisitor(bsonDoc, rowParameter, valuesType, slots);
        var rewritten = rewriter.Visit(shaperBody);
        if (rewriter.Ineligible)
        {
            return null;
        }

        // Replay the deferred tracking before the shaper reads the row, and read each entity slot through the remap of any the state manager
        // resolved to an already tracked instance.
        if (tracksEntities)
        {
            var remap = Expression.Variable(typeof(MongoEntityRemap), "remap");
            rewritten = Expression.Block(
                rewritten.Type,
                [remap],
                Expression.Assign(
                    remap,
                    Expression.Call(
                        typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.Replay))!,
                        queryContext,
                        Expression.Field(rowParameter, nameof(MongoTrackedProjectedRow<object, uint>.Deferred)))),
                new RemapVisitor(remap).Visit(rewritten));
        }

        var shaper = Expression.Lambda(rewritten, queryContext, rowParameter).Compile();
        var rowReader = BuildRowReader(slots, rowType, valuesType, tracksEntities, singleOutputField);

        return new Plan(rowType, shaper, rowReader, shaperBody.Type, tracksEntities);
    }

    private sealed class Slot(string alias, IBsonSerializer serializer)
    {
        public string Alias { get; } = alias;
        public IBsonSerializer Serializer { get; } = serializer;
        public Type ValueType { get; } = serializer.ValueType;

        /// <summary>An entity read from a sub-document (see <see cref="MongoEntityLeafSerializer{T}"/>): constructed with tracking deferred when the query tracks.</summary>
        public bool IsEntity { get; } = serializer.GetType().IsGenericType
                                         && serializer.GetType().GetGenericTypeDefinition() == typeof(MongoEntityLeafSerializer<>);
    }

    // Routes each entity slot's value through the replay's remap, wherever the rewritten shaper reads it.
    private sealed class RemapVisitor(ParameterExpression remap) : System.Linq.Expressions.ExpressionVisitor
    {
        private static readonly MethodInfo CanonicalMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.Canonical))!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod && node.Method.GetGenericMethodDefinition() == StreamedEntityMethod)
            {
                var type = node.Method.GetGenericArguments()[0];
                return Expression.Call(CanonicalMethod.MakeGenericMethod(type), remap, node);
            }

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// With no <c>rowParameter</c> it collects the slots and decides eligibility; with one it replaces each scalar read with the slot's
    /// state and value. Every document parameter use other than the four reads makes the shaper ineligible.
    /// </summary>
    private sealed class SlotVisitor(
        ParameterExpression bsonDoc,
        ParameterExpression? rowParameter,
        Type? valuesType,
        List<Slot> slots)
        : System.Linq.Expressions.ExpressionVisitor
    {
        public bool Ineligible { get; private set; }

        public List<Slot> Slots => slots;

        protected override Expression VisitParameter(ParameterExpression node)
        {
            // Reached only for a use of the document that is not the receiver of a taken-over read (those never visit it).
            if (node == bsonDoc)
            {
                Ineligible = true;
            }

            return node;
        }

        protected override Expression VisitBlock(BlockExpression node)
        {
            // A BsonDocument/BsonArray variable means the shaper navigates sub-documents.
            if (node.Variables.Any(v => typeof(BsonValue).IsAssignableFrom(v.Type)))
            {
                Ineligible = true;
            }

            return base.VisitBlock(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Arguments.Count == 0 || node.Arguments[0] != bsonDoc)
            {
                return base.VisitMethodCall(node);
            }

            // The shaper's tests of whether an alias is missing / null, taken from the slot's state; the slot is the one its value read makes.
            if (node.Method.DeclaringType == typeof(MongoProjectionBindingRemovingExpressionVisitor)
                && node.Method.Name is nameof(MongoProjectionBindingRemovingExpressionVisitor.IsElementAbsentOrNull)
                    or nameof(MongoProjectionBindingRemovingExpressionVisitor.IsElementAbsent)
                && TryGetConstant<string>(node.Arguments, 1, out var testedAlias))
            {
                if (rowParameter is null)
                {
                    return node;
                }

                var testedSlot = slots.FindIndex(s => s.Alias == testedAlias);
                if (testedSlot < 0)
                {
                    Ineligible = true;
                    return node;
                }

                var testedStates = Expression.Field(rowParameter, nameof(MongoProjectedRow<object, uint>.States));
                return Expression.Call(
                    typeof(MongoProjectedRowState).GetMethod(
                        node.Method.Name == nameof(MongoProjectionBindingRemovingExpressionVisitor.IsElementAbsent)
                            ? nameof(MongoProjectedRowState.IsAbsent)
                            : nameof(MongoProjectedRowState.IsAbsentOrNull))!,
                    testedStates.Type == typeof(ulong) ? testedStates : Expression.Convert(testedStates, typeof(ulong)),
                    Expression.Constant(testedSlot));
            }

            if (node.Method.DeclaringType != typeof(BsonBinding) || !node.Method.IsGenericMethod)
            {
                Ineligible = true;
                return node;
            }

            var name = node.Method.Name;
            var type = node.Method.GetGenericArguments()[0];

            if (!TryGetConstant<string>(node.Arguments, 1, out var alias))
            {
                Ineligible = true;
                return node;
            }

            IBsonSerializer serializer;
            PropertyReadInfo? property = null;
            switch (name)
            {
                case ScalarProjectionName or PropertyAtElementName when node.Arguments.Count == 3
                                                                         && TryGetConstant(node.Arguments, 2, out property):
                    serializer = property!.Serializer;
                    break;

                case ElementName when node.Arguments.Count == 2:
                    serializer = BsonSerializerFactory.CreateTypeSerializer(type);
                    break;

                case KindAwareElementName when node.Arguments.Count == 3 && TryGetConstant<IBsonSerializer>(node.Arguments, 2, out var kindSerializer):
                    serializer = kindSerializer!;
                    break;

                case StreamedValueName when node.Arguments.Count == 4 && TryGetConstant<IBsonSerializer>(node.Arguments, 2, out var streamedSerializer):
                    serializer = streamedSerializer!;
                    break;

                default:
                    Ineligible = true;
                    return node;
            }

            var slotIndex = GetOrAddSlot(alias!, serializer);
            if (slotIndex < 0 || rowParameter is null)
            {
                return node;
            }

            var slot = slots[slotIndex];
            var rowType = rowParameter.Type;
            var packedStates = Expression.Field(rowParameter, nameof(MongoProjectedRow<object, uint>.States));
            var state = Expression.Call(
                typeof(MongoProjectedRowState).GetMethod(nameof(MongoProjectedRowState.StateOf))!,
                packedStates.Type == typeof(ulong) ? packedStates : Expression.Convert(packedStates, typeof(ulong)),
                Expression.Constant(slotIndex));
            var value = SlotValue(rowParameter, valuesType!, slotIndex);

            Expression call = name switch
            {
                ScalarProjectionName => Expression.Call(
                    ScalarProjectionMethod.MakeGenericMethod(type, slot.ValueType),
                    state, value, Expression.Constant(alias), Expression.Constant(property!.Property, typeof(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty)),
                    Expression.Constant(property.Serializer is MongoDB.Bson.Serialization.Serializers.BooleanSerializer)),
                StreamedValueName when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) => Expression.Call(
                    StreamedListMethod.MakeGenericMethod(type.GetGenericArguments()[0]), state, value),
                StreamedValueName => Expression.Call(StreamedEntityMethod.MakeGenericMethod(type), state, value, node.Arguments[3]),
                PropertyAtElementName => Expression.Call(
                    PropertyAtElementMethod.MakeGenericMethod(type, slot.ValueType),
                    state, value, Expression.Constant(alias), Expression.Constant(property!.Property, typeof(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty))),
                _ => Expression.Call(
                    ElementMethod.MakeGenericMethod(type, slot.ValueType),
                    state, value, Expression.Constant(alias)),
            };

            // The BsonBinding read is typed T? (an annotation only); the rewritten call returns the same T.
            return call.Type == node.Type ? call : Expression.Convert(call, node.Type);
        }

        // The slot for (alias, serializer), added on first use. One element is read once, so two reads of an alias must agree on how it is
        // read; if they do not, the shaper stays on the DOM.
        private int GetOrAddSlot(string alias, IBsonSerializer serializer)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Alias != alias)
                {
                    continue;
                }

                // The rewrite pass trusts the collection pass, which already checked that every read of the alias agrees.
                if (rowParameter is not null
                    || (slots[i].ValueType == serializer.ValueType && slots[i].Serializer.Equals(serializer)))
                {
                    return i;
                }

                Ineligible = true;
                return -1;
            }

            if (rowParameter is not null)
            {
                // Rewriting a read the collection pass did not see cannot happen unless the tree changed between passes.
                Ineligible = true;
                return -1;
            }

            slots.Add(new Slot(alias, serializer));
            return slots.Count - 1;
        }

        private static bool TryGetConstant<T>(IReadOnlyList<Expression> arguments, int index, out T? value)
        {
            if (arguments[index] is ConstantExpression { Value: T constant })
            {
                value = constant;
                return true;
            }

            value = default;
            return false;
        }
    }

    // ---- the nested value tuple holding the slots ----

    private static Type BuildTupleType(Type[] types, int start)
    {
        var remaining = types.Length - start;
        if (remaining == 0 && start == 0)
        {
            // A projection that reads nothing from the document (`Select(o => new Dto())`): the row holds no values.
            return typeof(ValueTuple);
        }

        if (remaining > TupleWidth)
        {
            var items = types.Skip(start).Take(TupleWidth).Append(BuildTupleType(types, start + TupleWidth)).ToArray();
            return typeof(ValueTuple<,,,,,,,>).MakeGenericType(items);
        }

        return TupleDefinition(remaining).MakeGenericType(types.Skip(start).ToArray());
    }

    private static Type TupleDefinition(int arity)
        => arity switch
        {
            1 => typeof(ValueTuple<>),
            2 => typeof(ValueTuple<,>),
            3 => typeof(ValueTuple<,,>),
            4 => typeof(ValueTuple<,,,>),
            5 => typeof(ValueTuple<,,,,>),
            6 => typeof(ValueTuple<,,,,,>),
            7 => typeof(ValueTuple<,,,,,,>),
            _ => throw new ArgumentOutOfRangeException(nameof(arity)),
        };

    private static Expression BuildTupleNew(Type tupleType, IReadOnlyList<Expression> values, int start)
    {
        if (tupleType == typeof(ValueTuple))
        {
            return Expression.Default(tupleType);
        }

        var remaining = values.Count - start;
        var arguments = new List<Expression>();
        if (remaining > TupleWidth)
        {
            arguments.AddRange(values.Skip(start).Take(TupleWidth));
            arguments.Add(BuildTupleNew(tupleType.GetGenericArguments()[TupleWidth], values, start + TupleWidth));
        }
        else
        {
            arguments.AddRange(values.Skip(start));
        }

        return Expression.New(tupleType.GetConstructor(tupleType.GetGenericArguments())!, arguments);
    }

    // row.Values.Item{n}, descending through Rest for slots past the first seven.
    private static Expression SlotValue(Expression row, Type valuesType, int slotIndex)
    {
        Expression current = Expression.Field(row, nameof(MongoProjectedRow<object, uint>.Values));
        while (slotIndex >= TupleWidth)
        {
            current = Expression.Field(current, "Rest");
            slotIndex -= TupleWidth;
        }

        return Expression.Field(current, "Item" + (slotIndex + 1));
    }

    // ---- the cursor-side reader ----

    // singleOutputField: the one field the pipeline's documents hold (MongoPipelineFactory.SingleOutputField). A projection of exactly that
    // field is read without the element loop or the name match: the document is that element or empty, which the server guarantees.
    private static Delegate BuildRowReader(List<Slot> slots, Type rowType, Type valuesType, bool defersTracking, string? singleOutputField)
    {
        var queryContext = Expression.Parameter(typeof(QueryContext), "queryContext");
        var reader = Expression.Parameter(typeof(IBsonReader), "reader");
        var context = Expression.Parameter(typeof(BsonDeserializationContext), "context");
        var name = Expression.Variable(typeof(string), "name");

        var values = slots.Select((s, i) => Expression.Variable(s.ValueType, "v" + i)).ToArray();
        var states = slots.Select((_, i) => Expression.Variable(typeof(int), "s" + i)).ToArray();

        var cases = new List<SwitchCase>();
        for (var i = 0; i < slots.Count; i++)
        {
            cases.Add(Expression.SwitchCase(
                ReadSlot(slots[i], values[i], states[i], queryContext, reader, context),
                Expression.Constant(slots[i].Alias)));
        }

        Expression dispatch = cases.Count == 0
            ? Expression.Empty()
            : Expression.Switch(typeof(void), name, Expression.Call(reader, SkipValueMethod), comparison: null, cases);

        var done = Expression.Label("done");
        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.NotEqual(Expression.Call(reader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    Expression.Assign(
                        name,
                        Expression.Call(
                            reader, ReadNameMethod,
                            Expression.Constant(new MongoElementNameDecoder(slots.Select(s => s.Alias)), typeof(INameDecoder)))),
                    dispatch),
                Expression.Break(done)),
            done);

        Expression packedStates = Expression.Constant(0UL);
        for (var i = 0; i < slots.Count; i++)
        {
            packedStates = Expression.Or(
                packedStates,
                Expression.LeftShift(Expression.Convert(states[i], typeof(ulong)), Expression.Constant(2 * i)));
        }

        // The deferral brackets the whole row: the entity slots record their tracking as they are constructed, and it is handed to the row.
        var mongoQueryContext = Expression.Convert(queryContext, typeof(MongoQueryContext));
        var statesType = rowType.GetGenericArguments()[1];
        Expression statesValue = statesType == typeof(ulong) ? packedStates : Expression.Convert(packedStates, statesType);
        var row = defersTracking
            ? Expression.New(
                rowType.GetConstructor([valuesType, statesType, typeof(object)])!,
                BuildTupleNew(valuesType, values, 0),
                statesValue,
                Expression.Call(
                    mongoQueryContext,
                    typeof(MongoQueryContext).GetMethod("EndRowDeferral", BindingFlags.NonPublic | BindingFlags.Instance)!))
            : Expression.New(
                rowType.GetConstructor([valuesType, statesType])!,
                BuildTupleNew(valuesType, values, 0),
                statesValue);

        var steps = new List<Expression>();
        if (defersTracking)
        {
            steps.Add(Expression.Call(
                mongoQueryContext, typeof(MongoQueryContext).GetMethod("BeginDeferral", BindingFlags.NonPublic | BindingFlags.Instance)!));
        }

        steps.Add(Expression.Call(reader, ReadStartDocumentMethod));
        if (slots.Count == 1 && singleOutputField != null && string.Equals(singleOutputField, slots[0].Alias, StringComparison.Ordinal))
        {
            steps.Add(Expression.IfThen(
                Expression.NotEqual(Expression.Call(reader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    Expression.Call(reader, SkipNameMethod),
                    ReadSlot(slots[0], values[0], states[0], queryContext, reader, context))));
        }
        else if (slots.Count == 0)
        {
            // Nothing is read: skip whatever elements the document has.
            var skipDone = Expression.Label("skipDone");
            steps.Add(Expression.Loop(
                Expression.IfThenElse(
                    Expression.NotEqual(Expression.Call(reader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                    Expression.Block(Expression.Call(reader, SkipNameMethod), Expression.Call(reader, SkipValueMethod)),
                    Expression.Break(skipDone)),
                skipDone));
        }
        else
        {
            steps.Add(loop);
        }

        steps.Add(Expression.Call(reader, ReadEndDocumentMethod));
        steps.Add(row);

        var body = Expression.Block(
            rowType,
            values.Cast<ParameterExpression>().Concat(states).Append(name),
            steps);

        return Expression.Lambda(
            typeof(Func<,,,>).MakeGenericType(typeof(QueryContext), typeof(IBsonReader), typeof(BsonDeserializationContext), rowType),
            body,
            queryContext,
            reader,
            context).Compile();
    }

    // One element: BSON null is recorded as such (the shaper decides what a null means), anything else is read through the slot's serializer.
    private static Expression ReadSlot(
        Slot slot, ParameterExpression value, ParameterExpression state, ParameterExpression queryContext, ParameterExpression reader,
        ParameterExpression context)
    {
        // As the driver's own Deserialize(context) extension: the nominal type is the serializer's value type. A class-map serializer (a
        // sub-document leaf) needs it to find the actual type.
        var args = Expression.Constant(
            new BsonDeserializationArgs { NominalType = slot.ValueType }, typeof(BsonDeserializationArgs));
        var genericSerializerType = typeof(IBsonSerializer<>).MakeGenericType(slot.ValueType);
        var queryContextSerializerType = typeof(IMongoQueryContextSerializer<>).MakeGenericType(slot.ValueType);
        Expression deserialize = queryContextSerializerType.IsInstanceOfType(slot.Serializer)
            ? Expression.Call(
                Expression.Constant(slot.Serializer, queryContextSerializerType),
                queryContextSerializerType.GetMethod(nameof(IMongoQueryContextSerializer<object>.Deserialize))!,
                queryContext,
                context)
            : genericSerializerType.IsInstanceOfType(slot.Serializer)
            ? Expression.Call(
                Expression.Constant(slot.Serializer, genericSerializerType),
                genericSerializerType.GetMethod(
                    nameof(IBsonSerializer.Deserialize), [typeof(BsonDeserializationContext), typeof(BsonDeserializationArgs)])!,
                context,
                args)
            : Expression.Convert(
                Expression.Call(
                    Expression.Constant(slot.Serializer, typeof(IBsonSerializer)),
                    DeserializeMethod,
                    context,
                    args),
                slot.ValueType);

        return Expression.IfThenElse(
            Expression.Equal(Expression.Call(reader, GetCurrentBsonTypeMethod), Expression.Constant(BsonType.Null, typeof(BsonType))),
            Expression.Block(
                Expression.Call(reader, ReadNullMethod),
                Expression.Assign(state, Expression.Constant(MongoProjectedRowState.Null)),
                Expression.Assign(value, Expression.Default(slot.ValueType))),
            Expression.Block(
                Expression.Assign(value, deserialize),
                Expression.Assign(state, Expression.Constant(MongoProjectedRowState.Present))));
    }
}
