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
/// The shaper may touch the document parameter solely through the <see cref="BsonBinding"/> reads with a constant alias (scalars, entities,
/// owned and joined values, lists, dotted paths, and the row's whole document) that the planner takes over. Any other use of the document
/// makes <see cref="TryPlan"/> return <see langword="null"/> and the query keeps the DOM shaper. The shaper itself still runs per row at
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
    private static readonly MethodInfo PropertyAtPathMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.PropertyAtPath))!;
    private static readonly MethodInfo ElementMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.Element))!;

    // The BsonBinding reads that are taken over, by name. They are resolved by name because they are internal and generic.
    private const string ScalarProjectionName = "GetScalarProjectionValueAtElement";
    private const string PropertyAtElementName = "GetPropertyValueAtElement";
    private const string PropertyAtPathName = "GetPropertyValueAtPath";
    private const string ElementAtPathName = "GetElementValueAtPath";
    private const string KindAwareElementAtPathName = "GetKindAwareElementValueAtPath";
    private const string ElementName = "GetElementValue";
    private const string KindAwareElementName = "GetKindAwareElementValue";
    private const string StreamedValueName = "GetStreamedValueAtElement";

    /// <summary>The alias of an entity slot that is the row's whole document, not one of its elements.</summary>
    internal const string WholeRowAlias = "$row";

    private static readonly MethodInfo StreamedListMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.StreamedList))!;
    private static readonly MethodInfo StreamedEntityMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.StreamedEntity))!;

    /// <summary>
    /// Plans the streaming form of <paramref name="shaperBody"/>, a DOM shaper over <paramref name="bsonDoc"/>, or returns
    /// <see langword="null"/> when it reads the document in any way the planner does not take over.
    /// </summary>
    public static Plan? TryPlan(
        Expression shaperBody, ParameterExpression bsonDoc, ParameterExpression queryContext, bool trackedQuery = false,
        string? singleOutputField = null)
    {
        var collector = new SlotVisitor(bsonDoc, rowParameter: null, valuesType: null, slots: []);
        collector.Visit(shaperBody);
        // The row's whole document is read once, into one slot.
        if (collector.Ineligible || collector.Slots.Count > MaxSlots || collector.Slots.Count(s => s.Alias == WholeRowAlias) > 1)
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
        var rowReader = BuildRowReader(slots, collector.Includes, rowType, valuesType, tracksEntities, singleOutputField);

        return new Plan(rowType, shaper, rowReader, shaperBody.Type, tracksEntities);
    }

    /// <summary>An Include between two slots (<see cref="MongoStreamedIncludeExpression"/>), done by the row reader once both are read.</summary>
    private sealed record StreamedInclude(string EntityAlias, string RelatedAlias, Microsoft.EntityFrameworkCore.Metadata.INavigation Navigation, bool SetLoaded);

    private static bool IsEntitySerializer(IBsonSerializer serializer)
        => serializer.GetType().IsGenericType && serializer.GetType().GetGenericTypeDefinition() == typeof(MongoEntityLeafSerializer<>);

    private sealed class Slot(string alias, IBsonSerializer serializer)
    {
        public string Alias { get; } = alias;
        public IBsonSerializer Serializer { get; } = serializer;
        public Type ValueType { get; } = serializer.ValueType;

        /// <summary>An entity read from a sub-document (see <see cref="MongoEntityLeafSerializer{T}"/>): constructed with tracking deferred when the query tracks.</summary>
        public bool IsEntity { get; } = serializer.GetType().IsGenericType
                                         && serializer.GetType().GetGenericTypeDefinition()
                                             is var definition
                                         && (definition == typeof(MongoEntityLeafSerializer<>) || definition == typeof(MongoEntityListSerializer<>));
    }

    // Routes each entity slot's value through the replay's remap, wherever the rewritten shaper reads it.
    private sealed class RemapVisitor(ParameterExpression remap) : System.Linq.Expressions.ExpressionVisitor
    {
        private static readonly MethodInfo CanonicalMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.Canonical))!;
        private static readonly MethodInfo CanonicalListMethod = typeof(MongoProjectedRowReads).GetMethod(nameof(MongoProjectedRowReads.CanonicalList))!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod && node.Method.GetGenericMethodDefinition() == StreamedEntityMethod)
            {
                var type = node.Method.GetGenericArguments()[0];
                return Expression.Call(CanonicalMethod.MakeGenericMethod(type), remap, node);
            }

            // A list of entities: each element the state manager resolved to a tracked one is replaced in the list.
            if (node.Method.IsGenericMethod && node.Method.GetGenericMethodDefinition() == StreamedListMethod
                && !node.Method.GetGenericArguments()[0].IsValueType
                && node.Method.GetGenericArguments()[0] != typeof(string))
            {
                var elementType = node.Method.GetGenericArguments()[0];
                return Expression.Call(CanonicalListMethod.MakeGenericMethod(elementType), remap, node);
            }

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// With no <c>rowParameter</c> it collects the slots and decides eligibility; with one it replaces each scalar read with the slot's
    /// state and value. Every document parameter use other than the reads the planner takes over makes the shaper ineligible.
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

        public List<StreamedInclude> Includes { get; } = [];

        protected override Expression VisitExtension(Expression node)
        {
            if (node is not MongoStreamedIncludeExpression include)
            {
                return base.VisitExtension(node);
            }

            var entity = Visit(include.Entity);
            Visit(include.Related);
            if (rowParameter is not null)
            {
                // The row reader did the fix-up; the shaper reads the entity.
                return entity;
            }

            if (FindStreamedAlias(include.Entity) is { } entityAlias && FindStreamedAlias(include.Related) is { } relatedAlias)
            {
                Includes.Add(new StreamedInclude(entityAlias, relatedAlias, include.Navigation, include.SetLoaded));
            }
            else
            {
                Ineligible = true;
            }

            return node;
        }

        // The alias of the streamed value a (binding-removed) expression reads: the taken-over read, possibly converted, or the entity an Include is on.
        private static string? FindStreamedAlias(Expression expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                        expression = convert.Operand;
                        continue;
                    case MongoStreamedIncludeExpression nested:
                        expression = nested.Entity;
                        continue;
                    case MethodCallExpression { Method.Name: StreamedValueName, Arguments: [_, ConstantExpression { Value: string alias }, ..] }:
                        return alias;
                    default:
                        return null;
                }
            }
        }

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

            // A property read through a path of sub-documents (`Home.City`) is the slot at the dotted alias; the row reader descends to it.
            string? alias;
            if (name is PropertyAtPathName or ElementAtPathName or KindAwareElementAtPathName && TryGetConstant<string[]>(node.Arguments, 1, out var path))
            {
                alias = string.Join(".", path!);
            }
            else if (!TryGetConstant(node.Arguments, 1, out alias))
            {
                Ineligible = true;
                return node;
            }

            IBsonSerializer serializer;
            PropertyReadInfo? property = null;
            switch (name)
            {
                case ScalarProjectionName or PropertyAtElementName or PropertyAtPathName when node.Arguments.Count == 3
                                                                         && TryGetConstant(node.Arguments, 2, out property):
                    serializer = property!.Serializer;
                    break;

                case ElementName or ElementAtPathName when node.Arguments.Count == 2:
                    serializer = BsonSerializerFactory.CreateTypeSerializer(type);
                    break;

                case KindAwareElementName or KindAwareElementAtPathName when node.Arguments.Count == 3 && TryGetConstant<IBsonSerializer>(node.Arguments, 2, out var kindSerializer):
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
                PropertyAtPathName => Expression.Call(
                    PropertyAtPathMethod.MakeGenericMethod(type, slot.ValueType),
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
            var sameAlias = false;
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Alias != alias)
                {
                    continue;
                }

                sameAlias = true;
                if (slots[i].ValueType == serializer.ValueType && slots[i].Serializer.Equals(serializer))
                {
                    return i;
                }
            }

            // A sub-document read twice in two ways (an entity twice in an untracked projection, each use its own instance): the row reader copies
            // the document and reads each slot from the copy. Anything else cannot be read twice from one element.
            if (sameAlias && (!IsEntitySerializer(serializer) || rowParameter is not null))
            {
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
    private static Delegate BuildRowReader(
        List<Slot> slots, List<StreamedInclude> includes, Type rowType, Type valuesType, bool defersTracking, string? singleOutputField)
    {
        var queryContext = Expression.Parameter(typeof(QueryContext), "queryContext");
        var reader = Expression.Parameter(typeof(IBsonReader), "reader");
        var context = Expression.Parameter(typeof(BsonDeserializationContext), "context");

        var values = slots.Select((s, i) => Expression.Variable(s.ValueType, "v" + i)).ToArray();
        var states = slots.Select((_, i) => Expression.Variable(typeof(int), "s" + i)).ToArray();

        // An entity that is the whole row document (an entity read with no `$project`, beside others read from its lookups) is read from a copy of the
        // document, and the other slots from a second one.
        var wholeRowIndex = slots.FindIndex(s => s.Alias == WholeRowAlias);
        var rowReader = wholeRowIndex < 0 ? reader : Expression.Variable(typeof(IBsonReader), "rowReader");
        var rowContext = wholeRowIndex < 0 ? context : Expression.Variable(typeof(BsonDeserializationContext), "rowContext");
        var rest = slots.Select((s, i) => (Index: i, Path: s.Alias.Split('.'))).Where(e => e.Index != wholeRowIndex).ToList();
        var loop = BuildNameLoop(rest, 0, slots, values, states, queryContext, rowReader, rowContext);

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

        var copies = new List<ParameterExpression>();
        ParameterExpression? rowBuffer = null;
        if (wholeRowIndex >= 0)
        {
            var copy = rowBuffer = Expression.Variable(typeof(IByteBuffer), "rowCopy");
            var entityReader = Expression.Variable(typeof(BsonBinaryReader), "entityReader");
            var entityContext = Expression.Variable(typeof(BsonDeserializationContext), "entityContext");
            copies.AddRange([copy, entityReader, entityContext, (ParameterExpression)rowReader, (ParameterExpression)rowContext]);
            steps.Add(Expression.Assign(copy, Expression.Call(reader, ReadRawBsonDocumentMethod)));
            steps.Add(Expression.Assign(entityReader, Expression.New(BinaryReaderConstructor, Expression.New(ByteBufferStreamConstructor, copy, Expression.Constant(false)))));
            steps.Add(Expression.Assign(entityContext, Expression.Call(CreateRootMethod, entityReader, Expression.Constant(null, typeof(Action<BsonDeserializationContext.Builder>)))));
            steps.Add(Expression.Call(entityReader, ReadBsonTypeMethod));
            steps.Add(ReadSlot(slots[wholeRowIndex], values[wholeRowIndex], states[wholeRowIndex], queryContext, entityReader, entityContext));
            steps.Add(Expression.Call(entityReader, DisposeMethod));
            steps.Add(Expression.Assign(rowReader, Expression.New(BinaryReaderConstructor, Expression.New(ByteBufferStreamConstructor, copy, Expression.Constant(false)))));
            steps.Add(Expression.Assign(rowContext, Expression.Call(CreateRootMethod, rowReader, Expression.Constant(null, typeof(Action<BsonDeserializationContext.Builder>)))));
        }

        steps.Add(Expression.Call(rowReader, ReadStartDocumentMethod));
        if (slots.Count == 1 && singleOutputField != null && string.Equals(singleOutputField, slots[0].Alias, StringComparison.Ordinal))
        {
            steps.Add(Expression.IfThen(
                Expression.NotEqual(Expression.Call(rowReader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    Expression.Call(rowReader, SkipNameMethod),
                    ReadSlot(slots[0], values[0], states[0], queryContext, (ParameterExpression)rowReader, (ParameterExpression)rowContext))));
        }
        else if (rest.Count == 0)
        {
            // Nothing is read from the elements: skip whatever the document has.
            var skipDone = Expression.Label("skipDone");
            steps.Add(Expression.Loop(
                Expression.IfThenElse(
                    Expression.NotEqual(Expression.Call(rowReader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                    Expression.Block(Expression.Call(rowReader, SkipNameMethod), Expression.Call(rowReader, SkipValueMethod)),
                    Expression.Break(skipDone)),
                skipDone));
        }
        else
        {
            steps.Add(loop);
        }

        steps.Add(Expression.Call(rowReader, ReadEndDocumentMethod));
        if (rowBuffer != null)
        {
            // The copy holds references on the reply's pooled chunks.
            steps.Add(Expression.Call(rowReader, DisposeMethod));
            steps.Add(Expression.Call(rowBuffer, DisposeMethod));
        }

        // The Includes between slots, while the row's tracking is still deferred: a tracked row is wired when it is replayed (MongoIncludeFixups).
        foreach (var include in includes)
        {
            var entityIndex = slots.FindIndex(s => s.Alias == include.EntityAlias);
            var relatedIndex = slots.FindIndex(s => s.Alias == include.RelatedAlias);
            Expression related = values[relatedIndex];
            if (include.Navigation.IsCollection)
            {
                related = Expression.Coalesce(related, Expression.New(related.Type));
            }

            if (defersTracking)
            {
                steps.Add(
                    Expression.Call(
                        mongoQueryContext,
                        typeof(MongoQueryContext).GetMethod("TrackAfterIncluded", BindingFlags.NonPublic | BindingFlags.Instance)!,
                        Expression.Convert(values[entityIndex], typeof(object))));
            }

#pragma warning disable EF1001 // Internal EF Core API usage.
            steps.Add(
                MongoIncludeFixups.CreateIncludeCall(
                    include.Navigation,
                    Expression.Constant(null, typeof(Microsoft.EntityFrameworkCore.ChangeTracking.Internal.InternalEntityEntry)),
                    values[entityIndex],
                    Expression.Constant(include.Navigation.DeclaringEntityType, typeof(Microsoft.EntityFrameworkCore.Metadata.IEntityType)),
                    related,
                    include.SetLoaded,
                    defersTracking ? mongoQueryContext : null));
#pragma warning restore EF1001 // Internal EF Core API usage.
        }

        steps.Add(row);

        var body = Expression.Block(
            rowType,
            values.Cast<ParameterExpression>().Concat(states).Concat(copies),
            steps);

        return Expression.Lambda(
            typeof(Func<,,,>).MakeGenericType(typeof(QueryContext), typeof(IBsonReader), typeof(BsonDeserializationContext), rowType),
            body,
            queryContext,
            reader,
            context).Compile();
    }

    // The loop over one document's elements, dispatching each on its name to the slot it fills. A slot whose alias is a dotted path
    // (an owned collection under an owned reference, `Home.Notes`) is read from the sub-document its first segment names, one level down.
    private static Expression BuildNameLoop(
        List<(int Index, string[] Path)> entries, int depth, List<Slot> slots, ParameterExpression[] values, ParameterExpression[] states,
        ParameterExpression queryContext, ParameterExpression reader, ParameterExpression context)
    {
        var name = Expression.Variable(typeof(string), "name" + depth);
        var cases = new List<SwitchCase>();
        foreach (var group in entries.GroupBy(e => e.Path[depth]))
        {
            var directs = group.Where(e => e.Path.Length == depth + 1).ToList();
            Expression body;
            if (directs.Count == 1)
            {
                var slot = directs[0];
                body = ReadSlot(slots[slot.Index], values[slot.Index], states[slot.Index], queryContext, reader, context);
            }
            else if (directs.Count > 1)
            {
                body = ReadSlotsFromCopy(directs.Select(d => d.Index).ToList(), slots, values, states, queryContext, reader);
            }
            else
            {
                body = Expression.IfThenElse(
                    Expression.Equal(Expression.Call(reader, GetCurrentBsonTypeMethod), Expression.Constant(BsonType.Document, typeof(BsonType))),
                    Expression.Block(
                        Expression.Call(reader, ReadStartDocumentMethod),
                        BuildNameLoop(group.ToList(), depth + 1, slots, values, states, queryContext, reader, context),
                        Expression.Call(reader, ReadEndDocumentMethod)),
                    Expression.Call(reader, SkipValueMethod));
            }

            cases.Add(Expression.SwitchCase(body, Expression.Constant(group.Key)));
        }

        Expression dispatch = cases.Count == 0
            ? Expression.Empty()
            : Expression.Switch(typeof(void), name, Expression.Call(reader, SkipValueMethod), comparison: null, cases);

        var done = Expression.Label("done" + depth);
        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.NotEqual(Expression.Call(reader, ReadBsonTypeMethod), Expression.Constant(BsonType.EndOfDocument, typeof(BsonType))),
                Expression.Block(
                    Expression.Assign(
                        name,
                        Expression.Call(
                            reader, ReadNameMethod,
                            Expression.Constant(new MongoElementNameDecoder(entries.Select(e => e.Path[depth])), typeof(INameDecoder)))),
                    dispatch),
                Expression.Break(done)),
            done);
        return Expression.Block(typeof(void), [name], loop);
    }

    private static readonly MethodInfo DisposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;
    private static readonly MethodInfo ReadRawBsonDocumentMethod = typeof(IBsonReader).GetMethod(nameof(IBsonReader.ReadRawBsonDocument))!;
    private static readonly ConstructorInfo BinaryReaderConstructor = typeof(BsonBinaryReader).GetConstructor([typeof(System.IO.Stream)])!;
    private static readonly ConstructorInfo ByteBufferStreamConstructor = typeof(ByteBufferStream).GetConstructor([typeof(IByteBuffer), typeof(bool)])!;

    private static readonly MethodInfo CreateRootMethod =
        typeof(BsonDeserializationContext).GetMethod(
            nameof(BsonDeserializationContext.CreateRoot), [typeof(IBsonReader), typeof(Action<BsonDeserializationContext.Builder>)])!;

    // One sub-document read by several slots (an entity used twice, each use its own instance): copied once, then each slot reads its own reader
    // over the copy. A null is every slot's null.
    private static Expression ReadSlotsFromCopy(
        List<int> slotIndexes, List<Slot> slots, ParameterExpression[] values, ParameterExpression[] states,
        ParameterExpression queryContext, ParameterExpression reader)
    {
        var bytes = Expression.Variable(typeof(IByteBuffer), "copy");
        var reads = new List<Expression> { Expression.Assign(bytes, Expression.Call(reader, ReadRawBsonDocumentMethod)) };
        foreach (var index in slotIndexes)
        {
            var copyReader = Expression.Variable(typeof(BsonBinaryReader), "copyReader");
            var copyContext = Expression.Variable(typeof(BsonDeserializationContext), "copyContext");
            reads.Add(
                Expression.Block(
                    [copyReader, copyContext],
                    Expression.Assign(copyReader, Expression.New(BinaryReaderConstructor, Expression.New(ByteBufferStreamConstructor, bytes, Expression.Constant(false)))),
                    Expression.Assign(
                        copyContext,
                        Expression.Call(CreateRootMethod, copyReader, Expression.Constant(null, typeof(Action<BsonDeserializationContext.Builder>)))),
                    Expression.Call(copyReader, ReadBsonTypeMethod),
                    ReadSlot(slots[index], values[index], states[index], queryContext, copyReader, copyContext),
                    Expression.Call(copyReader, DisposeMethod)));
        }

        var nulls = new List<Expression> { Expression.Call(reader, ReadNullMethod) };
        foreach (var index in slotIndexes)
        {
            nulls.Add(Expression.Assign(states[index], Expression.Constant(MongoProjectedRowState.Null)));
            nulls.Add(Expression.Assign(values[index], Expression.Default(slots[index].ValueType)));
        }

        // The slice holds references on the reply's pooled chunks, which go back to the pool only when it is disposed.
        reads.Add(Expression.Call(bytes, DisposeMethod));

        return Expression.IfThenElse(
            Expression.Equal(Expression.Call(reader, GetCurrentBsonTypeMethod), Expression.Constant(BsonType.Document, typeof(BsonType))),
            Expression.Block([bytes], reads),
            Expression.IfThenElse(
                Expression.Equal(Expression.Call(reader, GetCurrentBsonTypeMethod), Expression.Constant(BsonType.Null, typeof(BsonType))),
                Expression.Block(nulls),
                Expression.Call(reader, SkipValueMethod)));
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
