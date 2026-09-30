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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates <see cref="MongoSelectDefinition.Grouping"/> for a <c>GroupBy(key).Select(aggregate)</c> shape,
/// emitting a native <c>$group</c>. The key is parsed first (<see cref="TryBindGroupKey"/>, stashed on
/// <see cref="MongoSelectDefinition.PendingGroupKey"/>), then the projection (<see cref="TryBindGroupProjection"/>).
/// Either returns <see langword="false"/> for a non-native shape, and the caller falls back to driver-LINQ.
/// </summary>
internal static class NativeGroupByBinder
{
    // The reserved element name the grouping key occupies in the emitted $group document.
    private const string GroupIdFieldName = "_id";

    // Join mode (GroupByJoinScope) and PriorGrouping are mutually exclusive: TranslateGroupBy sets the scope only
    // when there is no finalized prior grouping.
    private static MongoGroupElementTranslator CreateElementTranslator(MongoQueryExpression mongoQ)
        => new(mongoQ.CollectionExpression.EntityType, mongoQ.Select.GroupByJoinScope, mongoQ.Select.PriorGrouping);

    // Registers the grouped join chain's $lookup(s) once the grouping has fully bound. Deferred to here, the commit
    // point, so a decline leaves the join an unconfirmed candidate (Route stays Fallback) and the driver-LINQ
    // fallback keeps its unconfirmed document shape. Skipped when an earlier operator already forced a fallback, for
    // the same reason.
    private static void ConfirmGroupByJoinScope(MongoQueryExpression mongoQ)
    {
        if (mongoQ.Select.GroupByJoinScope is { } scope && !mongoQ.Select.HasUnsupportedOperator)
            NativeJoinScopeProjectionBinder.ConfirmEntireChain(mongoQ, scope);
    }

    /// <summary>
    /// Parses the <c>GroupBy</c> key selector into <see cref="MongoSelectDefinition.PendingGroupKey"/>: a scalar
    /// (unnamed) key, or a composite anonymous-type/DTO key whose parts carry member names. Returns
    /// <see langword="false"/> and leaves the pending state unset for an untranslatable key.
    /// </summary>
    internal static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)
    {
        var select = mongoQ.Select;

        // A GroupBy over a prior finalized grouping (Select.PriorGrouping) resolves its key against that stage's
        // flattened output alias, never the entity (see MongoExpressionTranslator.DistinctAliasScope); the
        // PriorGrouping handling lives in the MongoGroupElementTranslator constructor.
        var translator = CreateElementTranslator(mongoQ);

        var parts = new List<MongoGroupingKeyPart>();

        switch (keySelector.Body)
        {
            // A zero-argument new{} groups every row into one group. Match on Arguments.Count, not Members:
            // Members is null for every non-anonymous constructor call too, so matching on it would collapse a real
            // constructor-based key (new OrderKey(o.Country, o.Year)) into one group.
            case NewExpression { Arguments.Count: 0 }:
                break;

            case NewExpression { Members: { Count: > 0 } members } newExpr:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    if (!TryBindKeyPartValue(newExpr.Arguments[i], translator, out var partValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(members[i].Name, partValue));
                }

                break;

            // A MemberInitExpression DTO key. Each binding must be a plain MemberAssignment (a nested
            // MemberMemberBinding/MemberListBinding has no single value), and the ctor must take no arguments (a ctor
            // plus initializer has no key-part naming convention); otherwise the whole key declines.
            case MemberInitExpression { NewExpression.Arguments.Count: 0 } memberInit:
                // Parts are named by CLR member (TryGetKeyMemberPath matches g.Key.<Sub> by CLR name), but g.Key is
                // read back through the DTO's class map, which uses element names. Decline unless every element name
                // equals its member name, or readback throws FormatException.
                var dtoClassMap = global::MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(memberInit.NewExpression.Type);
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;

                    var memberMap = dtoClassMap.GetMemberMap(assignment.Member.Name);
                    if (memberMap is null || memberMap.ElementName != assignment.Member.Name)
                        return false;

                    // Value-converted/non-default-represented members decline inside TryBindKeyPartValue.
                    if (!TryBindKeyPartValue(assignment.Expression, translator, out var memberPartValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(assignment.Member.Name, memberPartValue));
                }

                break;

            // A bare member, EF.Property call, computed expression, constant, or parameter. A multi-argument
            // constructor key (Members == null) also lands here and declines in TryBindKeyPartValue.
            default:
                if (!TryBindKeyPartValue(keySelector.Body, translator, out var scalarValue))
                    return false;
                parts.Add(new MongoGroupingKeyPart(null, scalarValue));
                break;
        }

        select.PendingGroupKey = parts;
        return true;
    }

    /// <summary>
    /// Translates one GroupBy key (or composite key part) via <see
    /// cref="MongoGroupElementTranslator.TryTranslateValue"/>, adding the stricter checks a key needs.
    /// </summary>
    /// <remarks>
    /// Rejects a <see cref="MongoDocumentConstructionExpression"/>: it binds as a single unnamed key part that the
    /// shaper can't read back into the DTO's members (a <c>FormatException</c> at materialization). Also probes
    /// renderability via <see cref="NativeSlotPopulator.TryProbeBareValueRenders"/>, since a translatable value
    /// can still throw at pipeline-build time (e.g. a captured <see cref="Guid"/> parameter).
    /// </remarks>
    private static bool TryBindKeyPartValue(
        Expression body, MongoGroupElementTranslator translator, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!translator.TryTranslateValue(body, out var translated))
            return false;

        if (translated is MongoDocumentConstructionExpression)
            return false;

        if (!MongoAggregationExpressionRenderer.CanRender(translated))
            return false;

        // A non-nullable Length/IndexOf key over a possibly-null string would group its null rows under 0.
        if (MongoAggregationExpressionRenderer.ReadsNullAsDefault(NativeSlotPopulator.UnwrapBoxingToObjectType(body), translated))
            return false;

        if (!NativeSlotPopulator.TryProbeBareValueRenders(translated, NativeSlotPopulator.UnwrapBoxingToObjectType(body)))
            return false;

        // An unmatched outer row of a left join has no value for the inner side, so its _id part is missing/null,
        // read back as default(T) for a non-nullable key (Owners.GroupJoin(Orders).DefaultIfEmpty() grouped by
        // o.Total: an order-less owner silently becomes a 0 group, or merges with a real one). Decline; a nullable
        // spelling ((decimal?)o.Total) groups it under a null key and stays native. A key containing a condition over
        // that side (o.Total < 15 ? "cheap" : "dear") declines too: the unmatched value is missing but its CLR type is
        // non-nullable, so the aggregation renderer's null guard (MayBeNull) can't see it and $expr's null ordering
        // would decide the condition. A nullable/reference key computed by an operator that doesn't propagate the
        // missing value ((decimal?)Math.Max(o.Total, 7m): $max answers 7 where EF answers null) declines too.
        if ((IsNonNullableValueType(NativeSlotPopulator.UnwrapBoxingToObjectType(body)) || ConditionFinder.Contains(body))
            && translator.MayReadAnUnmatchedJoinSide(body))
            return false;

        if (translator.MayMiscomputeAnUnmatchedJoinSide(body, translated))
            return false;

        // A nullable key over that side ((decimal?)o.Total) keeps its CLR type only in the LINQ tree: the translated
        // field is the non-nullable Total, which MayBeNull can't see is missing for an unmatched row. Read it null-safe
        // so the $group normalizes missing to null like any other nullable part (see
        // MongoPipelineFactory.RenderCompositeKeyPart); otherwise "_id.<Name>" is missing and `g.Key.T == null` is false.
        if (!MongoAggregationExpressionRenderer.MayBeNull(translated) && translator.MayReadAnUnmatchedJoinSide(body))
        {
            translated = translated is MongoFieldExpression field
                ? new MongoFieldExpression(field.Property, field.ElementName, nullSafe: true)
                : new MongoCoalesceExpression(translated, new MongoConstantExpression(null, forSerialization: null));
        }

        result = translated;
        return true;
    }

    // The key is read back from _id through a generic CLR-type serializer (no backing IProperty), which only
    // reproduces the value for default/identity serialization. A value converter or non-default
    // BsonRepresentation would throw or return the raw stored value, diverging from driver-LINQ, so such keys
    // fall back. Accumulator operands aren't checked: native and driver-LINQ are wrong the same way there.
    // Also used by TranslateOfType's discriminator guard for the same reason.
    internal static bool HasDefaultKeySerialization(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() == null;

    /// <summary>
    /// Parses the <c>Select</c> result selector against the pending key, finalizing
    /// <see cref="MongoSelectDefinition.Grouping"/>. Each member (or the bare body) must be a key access
    /// (<c>g.Key</c> / <c>g.Key.&lt;Sub&gt;</c>), a supported aggregate (<c>Count</c>/<c>LongCount</c>/<c>Sum</c>/
    /// <c>Min</c>/<c>Max</c>/<c>Average</c> over a translatable value), or a supported computed/nested shape. A
    /// wrapped key-only projection is admitted as a "distinct keys" <c>$group</c>; a bare key access (a plain
    /// Distinct) and a zero-accumulator projection with an ordering aggregate decline.
    /// </summary>
    /// <param name="mongoQ">The query whose <see cref="MongoSelectDefinition"/> is being populated.</param>
    /// <param name="resultSelector">The <c>Select</c> result selector lambda over the grouping.</param>
    /// <param name="bareLeafAlias">
    /// On success, <see cref="NativeProjectionBinder.SyntheticBareProjectionAlias"/> when the body was bare (so the
    /// caller builds a single-member shaper); <see langword="null"/> for a wrapped projection.
    /// </param>
    internal static bool TryBindGroupProjection(
        MongoQueryExpression mongoQ, LambdaExpression resultSelector, out string? bareLeafAlias)
    {
        bareLeafAlias = null;
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        // A HAVING Where between GroupBy and this Select (see TryBindGroupWherePredicate): its accumulator is folded
        // in below and its comparison becomes GroupHavingPredicate, a $match after $group. Cleared immediately so no
        // stale state survives a later decline.
        var havingPredicate = select.PendingGroupPredicate;
        select.PendingGroupPredicate = null;

        // A bare result selector (e.g. .Select(g => g.Sum(o => o.OrderID))) has no member name, so it's projected
        // under the reserved `_v` alias, which the driver also uses, so a late fallback writes the element the shaper
        // reads. bareLeafAlias is set only once every guard below has admitted the body.
        var isBareBody = !resultSelector.Body.TryGetProjectionMembers(out var bindings, allowPositionalConstructorArguments: true);
        if (isBareBody)
        {
            bindings = [(NativeProjectionBinder.SyntheticBareProjectionAlias, resultSelector.Body)];
        }

        // Accumulator operands over a prior grouping resolve against its flattened alias, as in TryBindGroupKey;
        // the PriorGrouping handling lives in the MongoGroupElementTranslator constructor.
        var translator = CreateElementTranslator(mongoQ);

        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        // Resolve OrderBy/ThenBy on the ungrouped GroupBy result (the pending-ordering carve-out) first, so an
        // ordering aggregate the Select doesn't project still gets its own accumulator. Always a fresh field, even if
        // the Select projects the same aggregate (redundant but harmless).
        var orderAccumulators = new List<MongoGroupAccumulator>();
        var resolvedOrderings = new List<MongoOrdering>();
        if (select.PendingGroupOrderings is { } pendingOrderings)
        {
            var orderIndex = 0;
            foreach (var (ascending, keySelector) in pendingOrderings)
            {
                var groupParam = keySelector.Parameters[0];
                var body = keySelector.Body;

                // allowWholeKeyRead: false — ordering by a whole composite key declines: the anonymous key type isn't
                // comparable in LINQ-to-Objects, and a $sort on the "_id" sub-document would compare by BSON field
                // order.
                if (TryGetKeyMemberPath(body, groupParam, keyParts, isComposite, out var keyPath, allowWholeKeyRead: false))
                {
                    if (keyPath == null)
                        return false; // bare g.Key over a composite key — no single field to sort by

                    resolvedOrderings.Add(new MongoOrdering(
                        new MongoElementRefExpression(keyPath, Unwrap(body).Type), ascending));
                    continue;
                }

                var syntheticField = $"_orderAgg{orderIndex++}";
                if (!TryBindAccumulator(body, syntheticField, groupParam, keyParts, isComposite, translator, out var acc, out var flattenRead))
                    return false; // unsupported ordering shape — fall back to driver-LINQ

                orderAccumulators.Add(acc);
                resolvedOrderings.Add(new MongoOrdering(flattenRead, ascending));
            }
        }

        var groupingParameter = resultSelector.Parameters[0];

        // Each result member maps to a top-level output alias: key members read "_id" / "_id.<Name>", accumulators
        // their own field. Emitted as a trailing $project after $group so the shaper never reads a nested _id.
        var accumulators = new List<MongoGroupAccumulator>();
        var flatten = new List<MongoProjection>();
        var isBareBodyKeyMember = false;
        var hasCaseMappedMember = false;
        var nestedAccumulatorCounter = 0;
        foreach (var (memberName, projectedValue) in bindings)
        {
            // A client case mapping (`g.Key.ToUpper()`) stages only its receiver; the shaper re-applies the call
            // (MongoQueryableMethodTranslatingExpressionVisitor.BindGroupMember), since $toUpper/$toLower are ASCII-only.
            var valueExpr = PeelResultMemberCaseMapping(projectedValue);
            hasCaseMappedMember |= !ReferenceEquals(valueExpr, projectedValue);

            if (TryGetKeyMemberPath(valueExpr, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite key cannot flatten to a single field

                flatten.Add(new MongoProjection(memberName, ReadKeyMember(select, keyPath, Unwrap(valueExpr).Type, keyParts, isComposite)));
                if (isBareBody)
                    isBareBodyKeyMember = true;
                continue;
            }

            if (TryBindAccumulator(valueExpr, memberName, groupingParameter, keyParts, isComposite, translator, out var acc, out var flattenRead)
                || TryBindPushAccumulator(valueExpr, memberName, groupingParameter, translator, out acc, out flattenRead))
            {
                accumulators.Add(acc);
                flatten.Add(new MongoProjection(memberName, flattenRead));
                continue;
            }

            if (TryBindNestedGroupProjectionConstruction(
                    valueExpr, groupingParameter, keyParts, isComposite, translator, accumulators,
                    ref nestedAccumulatorCounter, select, out var construction))
            {
                flatten.Add(new MongoProjection(memberName, construction));
                continue;
            }

            // A computed member (ternary, coalesce) combining g.Key leaves with constants.
            if (!TryTranslateGroupProjectionExpression(valueExpr, groupingParameter, keyParts, isComposite, translator, out var computed))
                return false;

            flatten.Add(new MongoProjection(memberName, computed));
        }

        // A zero-accumulator wrapped key-only projection is a valid "distinct keys" $group. Decline a bare g.Key (a
        // plain Distinct) and a combination with an ordering aggregate accumulator; a key-resolved
        // ordering is fine.
        if (accumulators.Count == 0 && (isBareBodyKeyMember || orderAccumulators.Count > 0))
            return false;

        var havingAccumulators = havingPredicate?.Accumulator is { } havingAcc
            ? new[] { havingAcc }
            : Array.Empty<MongoGroupAccumulator>();
        select.Grouping = new MongoGrouping(keyParts, [..orderAccumulators, ..havingAccumulators, ..accumulators]);
        select.GroupHavingPredicate = havingPredicate?.Comparison;
        select.GroupOrderOp = resolvedOrderings.Count > 0 ? new MongoSortOp(resolvedOrderings) : null;
        select.PendingGroupOrderings = null;
        select.GroupPagingOps = select.PendingGroupPaging ?? [];
        select.PendingGroupPaging = null;
        foreach (var projection in flatten)
            select.AddProjection(projection);
        if (hasCaseMappedMember)
            select.HasClientCaseMappingProjectionLeaf = true;
        if (isBareBody)
            bareLeafAlias = NativeProjectionBinder.SyntheticBareProjectionAlias;
        ConfirmGroupByJoinScope(mongoQ);
        return true;
    }

    /// <summary>
    /// The value a grouped result member (or bare result body) stages when it projects a client case mapping
    /// (<c>g.Key.ToUpper()</c>, <c>g.Max(x =&gt; x.S).ToLower()</c>, also under a <c>Convert</c>): the mapping's receiver,
    /// whose raw value the <c>$group</c> output holds while the shaper re-applies the .NET call. Otherwise
    /// <paramref name="value"/> itself.
    /// </summary>
    /// <remarks>
    /// The one predicate for a case-mapped grouped result: <see cref="TryBindGroupProjection"/> stages the receiver,
    /// the result shaper re-applies the call over it. Driver-LINQ execution, which would compute it with the ASCII-only,
    /// null-as-<c>""</c> <c>$toUpper</c>/<c>$toLower</c>, is declined by <see cref="ContainsCaseMappingCall"/>.
    /// </remarks>
    internal static Expression PeelResultMemberCaseMapping(Expression value)
        => value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
           && PeelResultMemberCaseMapping(convert.Operand) is var receiver
           && !ReferenceEquals(receiver, convert.Operand)
            ? receiver
            : NativeProjectionBinder.PeelCaseMapping(value);

    /// <summary>
    /// True when <paramref name="expression"/> holds a case-mapping call (<see cref="NativeProjectionBinder.IsCaseMappingCall"/>)
    /// anywhere, the predicate <see cref="PeelResultMemberCaseMapping"/> peels by. Driver-LINQ would compute such a call
    /// with the ASCII-only, null-as-<c>""</c> <c>$toUpper</c>/<c>$toLower</c>; the shaped-query compiler declines a
    /// grouped query holding one rather than run it there.
    /// </summary>
    internal static bool ContainsCaseMappingCall(Expression expression)
    {
        var finder = new CaseMappingCallFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class CaseMappingCallFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
            => Found ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (NativeProjectionBinder.IsCaseMappingCall(node))
            {
                Found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }

        // Query roots and other provider nodes carry no case mapping of their own, and not all can visit children.
        protected override Expression VisitExtension(Expression node)
            => node;
    }

    // The flattening read of a key member. A whole composite g.Key ("_id" over named parts) is normally lifted as one
    // sub-document and read back through the key type's class map, whose DateTime members ignore a configured
    // DateTimeKind. When a key part is kind-sensitive (NativeDateTimeKindReadBack), it is rebuilt member by member as a
    // document construction instead, so each part reads back through the kind-aware construction-member reader. If the
    // key type's shape can't be rebuilt, the class-map read stays and NativeDateTimeKindReadBack.HasUnreproducibleReadBack
    // declines it. Otherwise the whole-key read (and its MQL) is unchanged.
    private static MongoExpression ReadKeyMember(
        MongoSelectDefinition select, string keyPath, Type type, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite)
    {
        if (keyPath != GroupIdFieldName || !isComposite
            || !keyParts.Any(p => NativeDateTimeKindReadBack.IsKindSensitiveKeyPart(select, p.FieldRef))
            || !TryBuildKeyShape(type, keyParts, out var shape, out var memberTypes))
            return new MongoElementRefExpression(keyPath, type);

        var members = new List<(string, MongoExpression)>(keyParts.Count);
        for (var i = 0; i < keyParts.Count; i++)
            members.Add((keyParts[i].Name!, new MongoElementRefExpression(GroupIdFieldName + "." + keyParts[i].Name, memberTypes[i])));

        return new MongoDocumentConstructionExpression(shape, members);
    }

    // Rebuilds the construction shape of a composite key type from its part names: an anonymous type (one constructor
    // whose parameters are the parts, in order) or a DTO (a parameterless constructor plus one settable member per
    // part). Only the constructor and member infos are consumed (MongoProjectionBindingRemovingExpressionVisitor
    // .BuildDocumentConstructionExpression); the argument/binding values are placeholders.
    private static bool TryBuildKeyShape(
        Type keyType, IReadOnlyList<MongoGroupingKeyPart> keyParts, [NotNullWhen(true)] out Expression? shape,
        [NotNullWhen(true)] out Type[]? memberTypes)
    {
        shape = null;
        memberTypes = null;
        if (keyParts.Any(p => p.Name is null))
            return false;

        var constructors = keyType.GetConstructors();
        if (constructors is [var ctor] && ctor.GetParameters() is { } parameters && parameters.Length == keyParts.Count
            && parameters.Select(p => p.Name).SequenceEqual(keyParts.Select(p => p.Name)))
        {
            var properties = new System.Reflection.MemberInfo[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (keyType.GetProperty(parameters[i].Name!) is not { } property || property.PropertyType != parameters[i].ParameterType)
                    return false;
                properties[i] = property;
            }

            memberTypes = parameters.Select(p => p.ParameterType).ToArray();
            shape = Expression.New(ctor, memberTypes.Select(t => (Expression)Expression.Default(t)), properties);
            return true;
        }

        if (keyType.GetConstructor(Type.EmptyTypes) is not { } defaultCtor)
            return false;

        var bindings = new List<MemberBinding>(keyParts.Count);
        var types = new Type[keyParts.Count];
        for (var i = 0; i < keyParts.Count; i++)
        {
            var member = (System.Reflection.MemberInfo?)keyType.GetProperty(keyParts[i].Name!) ?? keyType.GetField(keyParts[i].Name!);
            var memberType = member switch
            {
                System.Reflection.PropertyInfo { CanWrite: true } property => property.PropertyType,
                System.Reflection.FieldInfo field => field.FieldType,
                _ => null
            };
            if (memberType is null)
                return false;
            types[i] = memberType;
            bindings.Add(Expression.Bind(member!, Expression.Default(memberType)));
        }

        memberTypes = types;
        shape = Expression.MemberInit(Expression.New(defaultCtor), bindings);
        return true;
    }

    // Classifies a value as a grouping-key access, yielding the group-output path it reads. `path` is null for a
    // whole composite key when `allowWholeKeyRead` is false. Returns false for a non-key value (an accumulator).
    private static bool TryGetKeyMemberPath(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        out string? path,
        bool allowWholeKeyRead = true)
    {
        path = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        // g.Key: read "_id" wholesale. For a composite key that sub-document's fields already match the key type's
        // member names, so the generic readback materializes it; a zero-part key's "_id" is the empty document.
        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            path = isComposite && !allowWholeKeyRead ? null : "_id";
            return true;
        }

        // g.Key.<Sub> — a composite sub-member whose name matches a parsed key part.
        if (member.Expression is MemberExpression { Member.Name: "Key" } inner
            && inner.Expression == groupingParameter)
        {
            foreach (var part in keyParts)
            {
                if (part.Name == member.Member.Name)
                {
                    path = "_id." + part.Name;
                    return true;
                }
            }
        }

        return false;
    }

    // A nested construction as a member value (e.g. `Container = new LastInChain { Name = "x", Value = g.Sum(...) }`).
    // Each member goes through the same key/accumulator/computed dispatch as the outer loop, recursing for deeper
    // nesting. Accumulators can only run in $group, so each gets its own top-level $group field in the shared
    // `accumulators` list; `nestedAccumulatorCounter` keeps synthetic names distinct (a user member literally
    // named `_nestedAgg1` fails loudly with a duplicate-element error, not silently).
    //
    // A decline here marks the whole query Fallback (Route checks _hasUnsupportedOperator first), and the GroupBy
    // route never uses the mixed reader, so grouping is always all-native or all-fallback.
    private static bool TryBindNestedGroupProjectionConstruction(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        List<MongoGroupAccumulator> accumulators,
        ref int nestedAccumulatorCounter,
        MongoSelectDefinition select,
        [NotNullWhen(true)] out MongoDocumentConstructionExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        if (!expr.TryGetProjectionMembers(out var nestedMembers))
            return false;

        var translatedMembers = new List<(string, MongoExpression)>();
        foreach (var (nestedMemberName, nestedValueRaw) in nestedMembers)
        {
            var nestedValue = Unwrap(nestedValueRaw);

            if (TryGetKeyMemberPath(nestedValue, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite/zero-part key inside a nested construction

                translatedMembers.Add((nestedMemberName, ReadKeyMember(select, keyPath, nestedValue.Type, keyParts, isComposite)));
                continue;
            }

            var syntheticField = $"_nestedAgg{nestedAccumulatorCounter++}";
            if (TryBindAccumulator(
                    nestedValue, syntheticField, groupingParameter, keyParts, isComposite, translator,
                    out var acc, out var flattenRead)
                || TryBindPushAccumulator(nestedValue, syntheticField, groupingParameter, translator, out acc, out flattenRead))
            {
                accumulators.Add(acc);
                translatedMembers.Add((nestedMemberName, flattenRead));
                continue;
            }

            if (TryBindNestedGroupProjectionConstruction(
                    nestedValue, groupingParameter, keyParts, isComposite, translator, accumulators,
                    ref nestedAccumulatorCounter, select, out var deeperConstruction))
            {
                translatedMembers.Add((nestedMemberName, deeperConstruction));
                continue;
            }

            if (!TryTranslateGroupProjectionExpression(
                    nestedValue, groupingParameter, keyParts, isComposite, translator, out var computed))
                return false;

            translatedMembers.Add((nestedMemberName, computed));
        }

        result = new MongoDocumentConstructionExpression(expr, translatedMembers);
        return true;
    }

    // Translates a computed member value (ternary, coalesce) whose leaves are g.Key/g.Key.Sub accesses or
    // ordinary values. Runs in the flattening $project after $group, so a key leaf reads "_id"[.Sub] (unlike
    // TryTranslateAccumulatorCondition, which runs inside $group).
    private static bool TryTranslateGroupProjectionExpression(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        // A bare g.Key / g.Key.Sub leaf.
        if (TryGetKeyMemberPath(expr, groupingParameter, keyParts, isComposite, out var keyPath))
        {
            if (keyPath == null)
                return false; // bare g.Key over a zero-part key — no single field to read

            result = new MongoElementRefExpression(keyPath, expr.Type);
            return true;
        }

        // A ternary: every part recurses, so branches may be nested conditionals, coalesces, keys or values.
        if (expr is ConditionalExpression conditional)
        {
            if (!TryTranslateGroupProjectionConditionOrValue(
                    conditional.Test, groupingParameter, keyParts, isComposite, translator, out var test)
                || !TryTranslateGroupProjectionExpression(
                    conditional.IfTrue, groupingParameter, keyParts, isComposite, translator, out var ifTrue)
                || !TryTranslateGroupProjectionExpression(
                    conditional.IfFalse, groupingParameter, keyParts, isComposite, translator, out var ifFalse))
                return false;

            result = new MongoConditionalExpression(test, ifTrue, ifFalse);
            return true;
        }

        // `left ?? right`: both recurse (a ?? b ?? c is right-associative).
        if (expr is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            if (!TryTranslateGroupProjectionExpression(coalesce.Left, groupingParameter, keyParts, isComposite, translator, out var left)
                || !TryTranslateGroupProjectionExpression(coalesce.Right, groupingParameter, keyParts, isComposite, translator, out var right))
                return false;

            result = new MongoCoalesceExpression(left, right);
            return true;
        }

        // An ordinary value must not reference `g` at all: the ordinary translator knows nothing about the grouping
        // and could mis-resolve a same-named member against the wrong type.
        if (ReferencesParameter(expr, groupingParameter))
            return false;

        return translator.TryTranslateValue(expr, out result);
    }

    // The ternary test (e.g. `g.Key == null`): a key-vs-constant comparison, with the key read via the
    // post-$group "_id"[.Sub] path.
    private static bool TryTranslateGroupProjectionConditionOrValue(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        var unwrappedExpr = Unwrap(expr);
        if (unwrappedExpr is BinaryExpression bin)
        {
            if (bin.NodeType == ExpressionType.Equal
                || bin.NodeType == ExpressionType.NotEqual
                || bin.NodeType == ExpressionType.GreaterThan
                || bin.NodeType == ExpressionType.GreaterThanOrEqual
                || bin.NodeType == ExpressionType.LessThan
                || bin.NodeType == ExpressionType.LessThanOrEqual)
            {
                if (TryGetKeyMemberPath(bin.Left, groupingParameter, keyParts, isComposite, out var leftPath, allowWholeKeyRead: true)
                    && leftPath != null
                    && IsSafeZeroPartKeyComparison(leftPath, keyParts, bin.Right)
                    && TryTranslateComparisonConstant(
                        bin.Right, ResolveKeyMemberSerializationProperty(leftPath, keyParts, isComposite), out var rightConst))
                {
                    result = new MongoBinaryExpression(
                        MapComparisonOperator(bin.NodeType), new MongoElementRefExpression(leftPath, Unwrap(bin.Left).Type), rightConst);
                    return true;
                }

                if (TryGetKeyMemberPath(bin.Right, groupingParameter, keyParts, isComposite, out var rightPath, allowWholeKeyRead: true)
                    && rightPath != null
                    && IsSafeZeroPartKeyComparison(rightPath, keyParts, bin.Left)
                    && TryTranslateComparisonConstant(
                        bin.Left, ResolveKeyMemberSerializationProperty(rightPath, keyParts, isComposite), out var leftConst))
                {
                    result = new MongoBinaryExpression(
                        MapComparisonOperator(FlipComparison(bin.NodeType)), new MongoElementRefExpression(rightPath, Unwrap(bin.Right).Type), leftConst);
                    return true;
                }
            }
        }

        // Not a key comparison: an ordinary leaf/ternary/coalesce value.
        return TryTranslateGroupProjectionExpression(expr, groupingParameter, keyParts, isComposite, translator, out result);
    }

    // The matched key part's serialization property (a value-converted key like Guid needs it; BsonValue.Create
    // throws). Null when there's no single backing property (whole composite or zero-part key).
    private static IProperty? ResolveKeyMemberSerializationProperty(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite)
    {
        if (keyPath == "_id")
            return isComposite || keyParts.Count == 0 ? null : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

        var matchedPart = keyParts.First(p => keyPath == "_id." + p.Name);
        return (matchedPart.FieldRef as MongoFieldExpression)?.Property;
    }

    // A zero-part key's g.Key ("_id") has no serialization property, so a comparison against anything but a
    // null literal would reach BsonValue.Create, which throws for non-BSON-mappable types. Decline instead.
    private static bool IsSafeZeroPartKeyComparison(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, Expression otherOperand)
    {
        if (keyPath != "_id" || keyParts.Count != 0)
            return true;

        return Unwrap(otherOperand) is ConstantExpression { Value: null };
    }

    // g.Count()/g.LongCount() → $sum: 1; g.Sum/Average/Min/Max(x => ...) over a translatable value → the operator
    // plus translated operand. The aggregate's source must be the grouping parameter itself: binding a correlated
    // subquery (e.g. Customers.Where(c => c.CustomerID == g.Key).Count()) as a $group accumulator would silently
    // return the group's row count.
    private static bool TryBindAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        // "_id" already holds the key, so an accumulator output named "_id" would duplicate it and throw at pipeline
        // build. Decline (a key member aliased "_id" is handled as a key member and doesn't reach here).
        if (outputField == GroupIdFieldName)
            return false;

        if (Unwrap(expr) is not MethodCallExpression call)
            return false;

        // g.Select(e => e.Field).Distinct().<Op>(): its source isn't g, so try it before the IsGroupingSource guard.
        if (TryBindDistinctAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        // A selector-less aggregate over a key+elementSelector GroupBy arrives as `g.Select(elementSelector).Sum()`;
        // bind as an ordinary $sum/$avg/$min/$max over the selected field.
        if (TryBindElementSelectedAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        if (TryBindFilteredAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        if (call.Arguments.Count == 0 || !IsGroupingSource(call.Arguments[0], groupingParameter))
            return false;

        var definition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;

        // g.Count()/g.LongCount() → $sum: 1. EF lowers to the Queryable form over g.AsQueryable(); the Enumerable
        // form is accepted too.
        if ((definition == EnumerableMethods.CountWithoutPredicate
             || definition == EnumerableMethods.LongCountWithoutPredicate
             || definition == QueryableMethods.CountWithoutPredicate
             || definition == QueryableMethods.LongCountWithoutPredicate)
            && call.Arguments.Count == 1)
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum", null);
            flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
            return true;
        }

        // g.Count(pred) → $sum: {$cond: [pred, 1, 0]}.
        if (call.Arguments.Count == 2 && MongoExpressionTranslator.IsCanonicalCountWithPredicate(call.Method)
            && call.Arguments[1].UnwrapLambdaFromQuote() is { } countPred
            && TryTranslateAccumulatorCondition(countPred.Body, groupingParameter, keyParts, isComposite, translator, out _, out var countCond))
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum",
                new MongoConditionalExpression(countCond, new MongoConstantExpression(1, null), new MongoConstantExpression(0, null)));
            flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
            return true;
        }

        // Sum / Average / Min / Max with a selector — g.Sum(x => x.Field) etc. (Enumerable or Queryable form).
        string? op = null;
        if (EnumerableMethods.IsSumWithSelector(call.Method) || QueryableMethods.IsSumWithSelector(call.Method))
            op = "$sum";
        else if (EnumerableMethods.IsAverageWithSelector(call.Method) || QueryableMethods.IsAverageWithSelector(call.Method))
            op = "$avg";
        else if (EnumerableMethods.IsMinWithSelector(call.Method) || definition == QueryableMethods.MinWithSelector)
            op = "$min";
        else if (EnumerableMethods.IsMaxWithSelector(call.Method) || definition == QueryableMethods.MaxWithSelector)
            op = "$max";

        if (op is null || call.Arguments.Count != 2)
            return false;

        // Any translatable selector value (member, constant, cast, arithmetic). TryTranslateValue requires default
        // serialization, so a value-converted member falls back rather than aggregating the raw stored value. EF
        // inlines a GroupBy(key, elementSelector)'s element selector into this lambda, so no separate handling is
        // needed.
        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false; // untranslatable selector shape (e.g. a correlated method call) — fall back

        if (ReducesPossiblyUnmatchedJoinSideToDefault(op, call.Method.ReturnType, selector.Body, operand, translator))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, op, operand);
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    /// <summary>
    /// A list projection member, <c>g.Select(e =&gt; e.X).ToList()</c> / <c>.ToArray()</c> (EF's form is
    /// <c>Enumerable.ToList(Queryable.Select(g.AsQueryable(), selector))</c>), bound as a <c>$push</c> accumulator
    /// read back as the method's <c>List&lt;T&gt;</c>/<c>T[]</c>. Only called for projection members: a list isn't a
    /// sort key or a HAVING operand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Element order inside the list is the <c>$group</c>'s input order, which is unspecified without a prior sort
    /// (as for driver-LINQ's own <c>$push</c>). Accepted: a caller needing an order sorts the list.
    /// </para>
    /// <para>
    /// Declines:
    /// <list type="bullet">
    /// <item>A non-scalar element type (a whole entity, <c>g.Select(e =&gt; e)</c>, an owned or anonymous type): pushing
    /// entities would need per-element materialization and tracking. <c>g.ToList()</c> itself never reaches here (its
    /// source is <c>g</c>, not a <c>Select</c>). Today <see cref="MongoGroupElementTranslator.TryTranslateValue"/> also
    /// rejects every entity-typed operand, so this gate is the explicit statement of the rule rather than the only
    /// wall.</item>
    /// <item>An untranslatable selector, including a value-converted or non-default-represented property:
    /// <see cref="MongoGroupElementTranslator.TryTranslateValue"/> requires default serialization, which the list's
    /// generic CLR readback depends on.</item>
    /// <item>A <c>DateTime</c> element that isn't a plain field with no configured <c>DateTimeKind</c> (see the
    /// inline comment).</item>
    /// <item>A non-nullable element (or one containing a condition) that may read an unmatched left-outer join side:
    /// its value is missing, so there is no correct element to push.</item>
    /// </list>
    /// </para>
    /// <para>
    /// A bare <c>$push</c> skips an element whose value is missing (measured: <c>{$push: "$F"}</c> over F = 1, null,
    /// missing is <c>[1, null]</c>), while C# yields <c>null</c> for both. A nullable or reference-typed element is
    /// therefore read null-safe (<c>$ifNull: [operand, null]</c>), decided from the LINQ element type: over a left
    /// join, <c>(decimal?)x.Total</c> translates to the non-nullable <c>Total</c> field, missing for an unmatched row.
    /// </para>
    /// </remarks>
    private static bool TryBindPushAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (outputField == GroupIdFieldName)
            return false;

        if (Unwrap(expr) is not MethodCallExpression { Method.IsGenericMethod: true, Arguments.Count: 1 } call)
            return false;

        var definition = call.Method.GetGenericMethodDefinition();
        if (definition != EnumerableMethods.ToList && definition != EnumerableMethods.ToArray)
            return false;

        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true, Arguments.Count: 2 } selectCall
            || (selectCall.Method.GetGenericMethodDefinition() != QueryableMethods.Select
                && selectCall.Method.GetGenericMethodDefinition() != EnumerableMethods.Select)
            || !IsGroupingSource(selectCall.Arguments[0], groupingParameter)
            || selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { } selector)
            return false;

        var elementType = selector.ReturnType;
        if (!IsPushableScalarElementType(elementType))
            return false;

        if (!translator.TryTranslateValue(selector.Body, out var operand))
            return false;

        // The list's generic DateTime readback is the serializer of a property with no configured DateTimeKind. A
        // HasDateTimeKind property reads back differently (Local silently became Utc), and a computed date has no
        // property to ask, so a DateTime element must be a plain field with the default kind.
        if ((Nullable.GetUnderlyingType(elementType) ?? elementType) == typeof(DateTime)
            && !(operand is MongoFieldExpression dateField && dateField.Property.GetDateTimeKind() == DateTimeKind.Unspecified))
            return false;

        if ((IsNonNullableValueType(elementType) || ConditionFinder.Contains(selector.Body))
            && translator.MayReadAnUnmatchedJoinSide(selector.Body))
            return false;

        // A non-nullable Length/IndexOf element over a possibly-null string would push its null, read back as 0.
        if (MongoAggregationExpressionRenderer.ReadsNullAsDefault(elementType, operand))
            return false;

        // An unmatched element must push EF's null, not e.g. Math.Max's other operand.
        if (translator.MayMiscomputeAnUnmatchedJoinSide(selector.Body, operand))
            return false;

        // A non-nullable element keeps the bare operand, so a missing value (a malformed document) is skipped, the
        // same as driver-LINQ's $push.
        if (!IsNonNullableValueType(elementType))
        {
            operand = operand switch
            {
                MongoConstantExpression or MongoParameterExpression or MongoFieldExpression { NullSafe: true } => operand,
                MongoFieldExpression field => new MongoFieldExpression(field.Property, field.ElementName, nullSafe: true),
                _ => new MongoCoalesceExpression(operand, new MongoConstantExpression(null, forSerialization: null))
            };
        }

        accumulator = new MongoGroupAccumulator(outputField, "$push", operand);
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    // Scalar element types whose generic CLR serializer (BsonSerializerFactory.CreateTypeSerializer, used to read the
    // pushed list back) reproduces a default-serialized property's stored form. Anything else, notably an entity or
    // other class/struct element, declines.
    private static bool IsPushableScalarElementType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(short)
               || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double)
               || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(global::MongoDB.Bson.ObjectId)
               || type.IsEnum;
    }

    // Over a left join, an unmatched outer row (Owners.GroupJoin(Orders).DefaultIfEmpty() grouped by owner, for an
    // order-less owner) has no value for the inner side. Declines an accumulator operand reading that side when:
    // - it's $min/$max/$avg with a non-nullable result: an all-unmatched group reduces to null, read back as default(T)
    //   (a plausible, wrong 0), while LINQ throws and the driver fails to deserialize (as TryBindFilteredAccumulator
    //   does for an all-removed group); $sum's 0 matches EF;
    // - it contains a condition (g.Sum(x => x.Total < 15 ? 1 : 0)): $expr orders null below every value, so
    //   `null < 15` is true where EF's null semantics make it false. Structural, not per operator;
    // - it's computed by an operator that doesn't propagate the missing value (g.Sum(x => (decimal?)Math.Max(x.Total, 7m)):
    //   $max answers 7 for the unmatched element, where EF's element value is null). See
    //   MongoGroupElementTranslator.MayMiscomputeAnUnmatchedJoinSide.
    private static bool ReducesPossiblyUnmatchedJoinSideToDefault(
        string op, Type resultType, Expression operandBody, MongoExpression operand, MongoGroupElementTranslator translator)
        => (((op is "$min" or "$max" or "$avg" && IsNonNullableValueType(resultType))
                || ConditionFinder.Contains(operandBody))
               && translator.MayReadAnUnmatchedJoinSide(operandBody))
           || translator.MayMiscomputeAnUnmatchedJoinSide(operandBody, operand)
           // Not only over a join: a non-nullable $min/$max/$avg of a Length/IndexOf over a possibly-null string
           // reduces an all-null group to null, read back as 0 (MayBeNullBehindNonNullableType). $sum skips a null
           // as EF's SUM does.
           || (op is "$min" or "$max" or "$avg" && MongoAggregationExpressionRenderer.ReadsNullAsDefault(resultType, operand));

    // Finds a condition (ternary, comparison, logical operator or negation) anywhere in an operand.
    private sealed class ConditionFinder : ExpressionVisitor
    {
        private bool _found;

        internal static bool Contains(Expression body)
        {
            var finder = new ConditionFinder();
            finder.Visit(body);
            return finder._found;
        }

        public override Expression? Visit(Expression? node)
        {
            if (_found || node is null)
                return node;

            if (node.NodeType is ExpressionType.Conditional or ExpressionType.Equal or ExpressionType.NotEqual
                or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.AndAlso or ExpressionType.OrElse
                or ExpressionType.Not)
            {
                _found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    // Resolves g.Key / g.Key.Sub inside an accumulator's condition/operand to the key part's raw per-document
    // expression, never "_id": accumulators run in the same $group that produces _id, so "_id" would be circular.
    // (The HAVING comparison in TryBindGroupSideOperand runs in a later $match and does use "_id".)
    // otherOperand is the comparand: a zero-part key only resolves against a literal null (see below).
    private static bool TryResolveKeyReferenceAsRawExpression(
        Expression expr,
        Expression otherOperand,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            if (isComposite)
                return false; // whole composite key has no single raw expression to compare against a scalar

            if (keyParts.Count == 0)
            {
                // A zero-part (empty new{}) key is the constant empty document _id: {}, so its raw expression is
                // an empty {} literal (never null). Only a literal-null comparand is safe: anything else has no
                // backing property to serialize through (BsonValue.Create throws), so decline, as
                // IsSafeZeroPartKeyComparison does for HAVING.
                if (!IsSafeZeroPartKeyComparison("_id", keyParts, otherOperand))
                    return false;

                result = new MongoDocumentConstructionExpression(member, []);
                return true;
            }

            result = keyParts[0].FieldRef;
            return true;
        }

        if (member.Expression is MemberExpression { Member.Name: "Key" } inner && inner.Expression == groupingParameter)
        {
            foreach (var part in keyParts)
            {
                if (part.Name == member.Member.Name)
                {
                    result = part.FieldRef;
                    return true;
                }
            }
        }

        return false;
    }

    // A key-only accumulator condition reads the key part's raw per-row value, not "_id" (see
    // TryResolveKeyReferenceAsRawExpression), so it misses the $group's missing-to-null normalization
    // (MongoPipelineFactory.RenderCompositeKeyPart). A field that may be null is read null-safe ($ifNull) instead, so
    // a missing value compares as the null C# sees; the relational null guard then treats it as null.
    private static MongoExpression NullSafeKeyRead(MongoExpression rawKey)
        => rawKey is MongoFieldExpression { NullSafe: false } field && MongoAggregationExpressionRenderer.MayBeNull(field)
            ? new MongoFieldExpression(field.Property, field.ElementName, nullSafe: true)
            : rawKey;

    // A null read back for a non-nullable value type must not be silently defaulted.
    private static bool IsNonNullableValueType(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    /// <summary>
    /// Translates a per-element accumulator condition (<c>g.Count(pred)</c>, <c>g.Where(pred).Op(...)</c>). Handles
    /// a <c>g.Key</c>/<c>g.Key.Sub</c>-vs-constant comparison (via <see cref="TryResolveKeyReferenceAsRawExpression"/>;
    /// constant within a group) or an ordinary per-element predicate. A condition mixing both declines rather than
    /// translating half of it.
    /// </summary>
    private static bool TryTranslateAccumulatorCondition(
        Expression predicateBody,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        out bool isKeyOnlyCondition,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        isKeyOnlyCondition = false;

        if (Unwrap(predicateBody) is BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            // A key-only comparison reads the key part's raw per-row value, which over a left join may be an
            // unmatched inner side's missing value ($expr orders null below every value; see the per-element arm
            // below). The raw key expression no longer says which scope it came from, so a key-only comparison
            // declines whenever the grouped join has a left-outer level.
            if (TryResolveKeyReferenceAsRawExpression(bin.Left, bin.Right, groupingParameter, keyParts, isComposite, out var leftKey)
                && TryTranslateComparisonConstant(bin.Right, (leftKey as MongoFieldExpression)?.Property, out var rightConst))
            {
                if (translator.HasLeftOuterJoinLevel)
                    return false;

                result = new MongoBinaryExpression(MapComparisonOperator(bin.NodeType), NullSafeKeyRead(leftKey), rightConst);
                isKeyOnlyCondition = true;
                return true;
            }

            if (TryResolveKeyReferenceAsRawExpression(bin.Right, bin.Left, groupingParameter, keyParts, isComposite, out var rightKey)
                && TryTranslateComparisonConstant(bin.Left, (rightKey as MongoFieldExpression)?.Property, out var leftConst))
            {
                if (translator.HasLeftOuterJoinLevel)
                    return false;

                result = new MongoBinaryExpression(MapComparisonOperator(FlipComparison(bin.NodeType)), NullSafeKeyRead(rightKey), leftConst);
                isKeyOnlyCondition = true;
                return true;
            }
        }

        // Any other reference to `g` (e.g. a mixed `e.Amount > 5 && g.Key == "x"`) declines: the ordinary translator
        // resolves members by name against the entity, so `g.Key` could silently bind to an entity "Key" property.
        if (ReferencesParameter(predicateBody, groupingParameter))
            return false;

        // An ordinary per-element predicate (e.g. e.Amount < 100).
        if (!translator.TryTranslate(predicateBody, out result))
            return false;

        // A condition that may read an unmatched left-outer join side: for an unmatched row EF's null semantics make
        // `x.Total < 15` false, but $expr orders null below every value, so the $cond counts the row. Declined
        // structurally for every condition over that side, not per operator.
        if (translator.MayReadAnUnmatchedJoinSide(predicateBody))
        {
            result = null;
            return false;
        }

        return true;
    }

    // Detects any reference to `parameter` in the tree.
    private sealed class ParameterReferenceFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        [return: NotNullIfNotNull(nameof(node))]
        public override Expression? Visit(Expression? node)
        {
            if (Found || node is null)
                return node;

            if (ReferenceEquals(node, parameter))
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    private static bool ReferencesParameter(Expression expr, ParameterExpression parameter)
    {
        var finder = new ParameterReferenceFinder(parameter);
        finder.Visit(expr);
        return finder.Found;
    }

    // Resolves the optional hop before g.Select(selector).Distinct().<Op>(): none, g.Distinct() (a no-op here,
    // so no condition), or g.Where(pred) (a translated condition). Queryable and Enumerable forms both accepted.
    private static bool TryResolveOptionalAccumulatorSourceCondition(
        Expression source,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        out bool isKeyOnlyCondition,
        out MongoExpression? condition)
    {
        condition = null;
        isKeyOnlyCondition = false;
        source = Unwrap(source);

        if (IsGroupingSource(source, groupingParameter))
            return true; // bare g — no condition

        if (source is not MethodCallExpression { Method: { Name: var methodName, DeclaringType: var declaringType } } call
            || (declaringType != typeof(Queryable) && declaringType != typeof(Enumerable)))
            return false;

        if (methodName == nameof(Queryable.Distinct)
            && call.Arguments.Count == 1
            && IsGroupingSource(call.Arguments[0], groupingParameter))
        {
            return true; // g.Distinct() before Select(...).Distinct() — no condition needed
        }

        if (methodName == nameof(Queryable.Where)
            && call.Arguments.Count == 2
            && IsGroupingSource(call.Arguments[0], groupingParameter)
            && call.Arguments[1].UnwrapLambdaFromQuote() is { } pred)
        {
            return TryTranslateAccumulatorCondition(
                pred.Body, groupingParameter, keyParts, isComposite, translator, out isKeyOnlyCondition, out condition);
        }

        return false;
    }

    /// <summary>
    /// <c>g.Select(e =&gt; e.Field).Distinct().&lt;Op&gt;()</c>: aggregates the group's distinct values. Binds a
    /// <c>$addToSet</c> accumulator and reduces the array in the flattening <c>$project</c> via
    /// <see cref="MongoSizeExpression"/> (Count/LongCount) or <see cref="MongoArrayReduceExpression"/>. Accepts
    /// Queryable and Enumerable forms and an optional leading <c>Where</c>/<c>Distinct</c> hop.
    /// </summary>
    private static bool TryBindDistinctAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (call.Arguments.Count != 1)
            return false;

        // Average/Sum's selector-less overloads aren't generic, so match by name + declaring type (Arguments.Count
        // == 1 already excludes the with-selector overloads). Queryable is EF's normalized form; Enumerable comes
        // from hand-written lambdas.
        var declaringOk = call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable);
        var isSize = declaringOk && call.Method.Name is nameof(Queryable.Count) or nameof(Queryable.LongCount);
        var reduceOp = !declaringOk || isSize ? null
            : call.Method.Name switch
            {
                nameof(Queryable.Average) => "$avg",
                nameof(Queryable.Min) => "$min",
                nameof(Queryable.Max) => "$max",
                nameof(Queryable.Sum) => "$sum",
                _ => null
            };

        if (!isSize && reduceOp is null)
            return false;

        // The source must be g.Select(selector).Distinct(), with the Select over g or one Where/Distinct hop.
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Distinct), DeclaringType: var distinctDeclaring },
                Arguments.Count: 1
            } distinctCall
            || (distinctDeclaring != typeof(Queryable) && distinctDeclaring != typeof(Enumerable)))
            return false;

        if (Unwrap(distinctCall.Arguments[0]) is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring },
                Arguments.Count: 2
            } selectCall
            || (selectDeclaring != typeof(Queryable) && selectDeclaring != typeof(Enumerable))
            || !TryResolveOptionalAccumulatorSourceCondition(
                selectCall.Arguments[0], groupingParameter, keyParts, isComposite, translator,
                out var isKeyOnlyElementCondition, out var elementCondition))
            return false;

        // Plain member access only; a computed selector falls back.
        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out var selectorField))
            return false;

        MongoExpression operand = selectorField;

        // A Where hop wraps the operand in $cond with "$$REMOVE" as else, so non-matching elements add nothing to
        // the set.
        if (elementCondition is not null)
        {
            // If the filter excludes every element, $min/$max/$avg over the empty array is null, read back as
            // default(T) for a non-nullable result; decline. A key-only condition is uniform across the group, so it's
            // exempt.
            if (!isKeyOnlyElementCondition && reduceOp is "$min" or "$max" or "$avg" && IsNonNullableValueType(call.Method.ReturnType))
                return false;

            operand = new MongoConditionalExpression(
                elementCondition, operand, new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, operand.Type));
        }

        accumulator = new MongoGroupAccumulator(outputField, "$addToSet", operand);
        flattenRead = isSize
            ? new MongoSizeExpression(outputField, call.Method.ReturnType)
            : new MongoArrayReduceExpression(reduceOp!, outputField, call.Method.ReturnType);
        return true;
    }

    /// <summary>
    /// A selector-less <c>Sum()</c>/<c>Average()</c>/<c>Min()</c>/<c>Max()</c> over a <c>GroupBy(key,
    /// elementSelector)</c>, e.g. <c>.GroupBy(o =&gt; 2, o =&gt; o.OrderID).Select(g =&gt; g.Sum())</c>. With no
    /// lambda to inline into, EF re-expresses it as <c>g.AsQueryable().Select(elementSelector).Sum()</c>. Binds an
    /// ordinary accumulator over the selected value (no dedup).
    /// </summary>
    private static bool TryBindElementSelectedAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (call.Arguments.Count != 1)
            return false;

        // Count/LongCount are excluded: EF doesn't wrap them in a Select since they don't need element values.
        var reduceOp = QueryableMethods.IsAverageWithoutSelector(call.Method) ? "$avg"
            : call.Method.IsGenericMethod && call.Method.GetGenericMethodDefinition() == QueryableMethods.MinWithoutSelector ? "$min"
            : call.Method.IsGenericMethod && call.Method.GetGenericMethodDefinition() == QueryableMethods.MaxWithoutSelector ? "$max"
            : QueryableMethods.IsSumWithoutSelector(call.Method) ? "$sum"
            : null;

        if (reduceOp is null)
            return false;

        // The source must be g.Select(elementSelector) directly (the Distinct form is TryBindDistinctAccumulator's).
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } selectCall
            || selectCall.Method.GetGenericMethodDefinition() != QueryableMethods.Select
            || selectCall.Arguments.Count != 2
            || !IsGroupingSource(selectCall.Arguments[0], groupingParameter))
            return false;

        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false;

        if (ReducesPossiblyUnmatchedJoinSideToDefault(reduceOp, call.Method.ReturnType, selector.Body, operand, translator))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, reduceOp, operand);
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    /// <summary>
    /// <c>g.Where(pred).Sum/Min/Max/Average(selector)</c>, reducing to
    /// <c>{"$&lt;op&gt;": {"$cond": [pred, operand, "$$REMOVE"]}}</c> so non-matching elements contribute nothing.
    /// </summary>
    private static bool TryBindFilteredAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        // g.Where(pred).Count()/LongCount() (one argument, so checked before the Arguments.Count != 2 gate) →
        // $sum: {$cond: [pred, 1, 0]}, same as g.Count(pred).
        var countDefinition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;
        if (call.Arguments.Count == 1
            && (countDefinition == EnumerableMethods.CountWithoutPredicate
                || countDefinition == EnumerableMethods.LongCountWithoutPredicate
                || countDefinition == QueryableMethods.CountWithoutPredicate
                || countDefinition == QueryableMethods.LongCountWithoutPredicate)
            && Unwrap(call.Arguments[0]) is MethodCallExpression
                {
                    Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring0 },
                    Arguments.Count: 2
                } whereCall0
            && (whereDeclaring0 == typeof(Queryable) || whereDeclaring0 == typeof(Enumerable))
            && IsGroupingSource(whereCall0.Arguments[0], groupingParameter)
            && whereCall0.Arguments[1].UnwrapLambdaFromQuote() is { } wherePred0
            && TryTranslateAccumulatorCondition(
                wherePred0.Body, groupingParameter, keyParts, isComposite, translator, out _, out var countCond0))
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum",
                new MongoConditionalExpression(
                    countCond0, new MongoConstantExpression(1, null), new MongoConstantExpression(0, null)));
            flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
            return true;
        }

        if (call.Arguments.Count != 2)
            return false;

        var definition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;
        string? op = EnumerableMethods.IsSumWithSelector(call.Method) || QueryableMethods.IsSumWithSelector(call.Method) ? "$sum"
            : EnumerableMethods.IsAverageWithSelector(call.Method) || QueryableMethods.IsAverageWithSelector(call.Method) ? "$avg"
            : EnumerableMethods.IsMinWithSelector(call.Method) || definition == QueryableMethods.MinWithSelector ? "$min"
            : EnumerableMethods.IsMaxWithSelector(call.Method) || definition == QueryableMethods.MaxWithSelector ? "$max"
            : null;

        if (op is null)
            return false;

        // The source must be g.Where(pred) (Queryable or Enumerable form).
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments.Count: 2
            } whereCall
            || (whereDeclaring != typeof(Queryable) && whereDeclaring != typeof(Enumerable))
            || !IsGroupingSource(whereCall.Arguments[0], groupingParameter))
            return false;

        if (whereCall.Arguments[1].UnwrapLambdaFromQuote() is not { } wherePred
            || !TryTranslateAccumulatorCondition(
                wherePred.Body, groupingParameter, keyParts, isComposite, translator, out var isKeyOnlyCondition, out var condition))
            return false;

        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false;

        // If every element fails `pred`, $$REMOVE leaves $min/$max/$avg null, read back as default(T) for a
        // non-nullable result, while LINQ throws for an empty sequence; decline. $sum's 0 is correct. A key-only
        // condition is uniform across the group, so it's exempt.
        if (!isKeyOnlyCondition && op is "$min" or "$max" or "$avg" && IsNonNullableValueType(call.Method.ReturnType))
            return false;

        if (ReducesPossiblyUnmatchedJoinSideToDefault(op, call.Method.ReturnType, selector.Body, operand, translator))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, op,
            new MongoConditionalExpression(condition, operand,
                new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, operand.Type)));
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    // True when `source` is the grouping parameter `g`, after unwrapping converts and one AsQueryable/AsEnumerable
    // (EF emits Queryable.Count(g.AsQueryable()); the Enumerable form passes g directly).
    private static bool IsGroupingSource(Expression source, ParameterExpression groupingParameter)
    {
        source = Unwrap(source);

        if (source is MethodCallExpression { Method: { IsGenericMethod: true } method } call
            && call.Arguments.Count == 1
            && (method.GetGenericMethodDefinition() == QueryableMethods.AsQueryable
                || method.Name == nameof(Enumerable.AsEnumerable)))
            source = Unwrap(call.Arguments[0]);

        return source == groupingParameter;
    }

    // Strip Convert/ConvertChecked wrappers (an `object`-typed member boxes its value).
    private static Expression Unwrap(Expression e)
        => e.RemoveConvert();

    /// <summary>
    /// Binds <c>Count</c>/<c>LongCount</c>/<c>Any</c>/<c>All</c> applied directly to a bare <c>GroupBy(key)</c>,
    /// e.g. <c>GroupBy(o =&gt; o.CustomerID).Count()</c> or <c>.Any(g =&gt; g.Count() &gt; 1)</c>. Finalizes the
    /// grouping and cardinality atomically via <see cref="MongoSelectDefinition.SetGroupedTerminalAggregate"/>.
    /// Called from <see cref="NativeCardinalityBinder.TryBindAggregate"/> before its post-terminal guard, which
    /// would otherwise always decline (bare <c>GroupBy</c> sets <see cref="MongoSelectDefinition.IsGroupBy"/>).
    /// </summary>
    /// <remarks>
    /// Only no predicate, or a single comparison of one group-level aggregate or key against a constant/parameter.
    /// Compound predicates decline, which keeps the <c>All</c> negation exact.
    /// </remarks>
    internal static bool TryBindGroupTerminalAggregate(
        MongoQueryExpression mongoQ, MongoAggregateOperator op, LambdaExpression? predicate, Type resultType)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts || select.Grouping != null)
            return false;

        // Recorded paging (e.g. GroupBy(key).Skip(1).Count()) declines: this path has no stage to apply it, so it
        // would aggregate over every group.
        if (select.PendingGroupPaging != null)
            return false;

        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        if (op is not (MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All))
            return false;

        // select.PriorGrouping can be non-null here (a nested GroupBy(key1).Select(...).GroupBy(key2).Count() ends
        // its outer GroupBy as a bare terminal aggregate, never a Select), but this site deliberately doesn't set
        // DistinctAliasScope: pass priorGrouping: null (not CreateElementTranslator). GroupByJoinScope is only set
        // when there is no prior grouping, so passing it here can't combine the two.
        var translator = new MongoGroupElementTranslator(
            mongoQ.CollectionExpression.EntityType, mongoQ.Select.GroupByJoinScope, priorGrouping: null);
        var accumulators = new List<MongoGroupAccumulator>();
        MongoExpression? matchPredicate = null;
        MongoGroupAccumulator? accumulator = null;
        MongoExpression? comparisonNode = null;

        if (predicate != null)
        {
            // All(pred) always arrives with its predicate; Count(pred)/Any(pred) are usually rewritten to
            // Where(pred).Count()/.Any(), but not always, so handle both.
            if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                    keyParts, isComposite, out accumulator, out comparisonNode))
                return false;
        }
        else if (op is MongoAggregateOperator.All)
        {
            // All() has no parameterless overload; declined defensively.
            return false;
        }
        else if (select.PendingGroupPredicate is { } pending)
        {
            // Count(pred)/Any(pred) normalized to Where(pred).X(): consume the comparison the Where stashed
            // (TryBindGroupWherePredicate).
            (accumulator, comparisonNode) = pending;
        }

        select.PendingGroupPredicate = null; // one-shot: consumed above, or never set for a bare terminal.

        // Gate on comparisonNode, not accumulator: a key-only comparison (g.Key == "ALFKI") has no accumulator but
        // still needs its $match, or the filter would be silently dropped.
        if (comparisonNode != null)
        {
            if (accumulator != null)
                accumulators.Add(accumulator);

            if (op is MongoAggregateOperator.All)
            {
                // All(pred) ≡ no group fails pred: match the exact complement; any surviving group means All is false.
                // Not MongoExpressionNegator.TryNegate: it gates on IsQueryDialectRenderable, which this $expr-only
                // element-ref comparison never satisfies. Negate directly with the same $eq/$ne-invert, $not-wrap rule.
                if (!TryNegateGroupComparison(comparisonNode, out var negated))
                    return false;
                matchPredicate = negated;
            }
            else
            {
                matchPredicate = comparisonNode;
            }
        }

        NativeCardinalityBinder.BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);
        var presenceOnly = op is MongoAggregateOperator.Any or MongoAggregateOperator.All;
        object? presentValue = op switch
        {
            MongoAggregateOperator.Any => true,
            MongoAggregateOperator.All => false,
            _ => null
        };

        var cardinality = MongoCardinality.ForAggregate(
            op, selector: null, emptyBehavior, emptyValue, resultType, presenceOnly, presentValue);
        var grouping = new MongoGrouping(keyParts, accumulators);

        select.SetGroupedTerminalAggregate(grouping, cardinality, matchPredicate);
        ConfirmGroupByJoinScope(mongoQ);
        return true;
    }

    /// <summary>
    /// Recognizes a <c>Where</c> directly on a bare <c>GroupBy(key)</c> (EF's normalization of
    /// <c>Any(pred)</c>/<c>Count(pred)</c> to <c>Where(pred).Any()</c>/<c>.Count()</c>; the parameter is an
    /// <c>IGrouping</c>) and stashes the comparison on <see cref="MongoSelectDefinition.PendingGroupPredicate"/> for
    /// <see cref="TryBindGroupTerminalAggregate"/>. Called from <see cref="NativeSlotPopulator.PopulateNativeSlots"/>
    /// before the general Where arm, which would resolve members against the entity type.
    /// </summary>
    internal static bool TryBindGroupWherePredicate(MongoQueryExpression mongoQ, LambdaExpression predicate)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        // select.PriorGrouping can be non-null here for the same nested-GroupBy reason as
        // TryBindGroupTerminalAggregate; pass priorGrouping: null. Only stages the predicate: the join is confirmed
        // by the TryBindGroupTerminalAggregate/TryBindGroupProjection that consumes it.
        var translator = new MongoGroupElementTranslator(
            mongoQ.CollectionExpression.EntityType, mongoQ.Select.GroupByJoinScope, priorGrouping: null);
        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                keyParts, isComposite, out var accumulator, out var comparisonNode))
            return false;

        select.PendingGroupPredicate = (accumulator, comparisonNode);
        return true;
    }

    // Recognizes `body` as a single comparison of a group-level operand (a key access, or an accumulator bound to
    // "__agg0") against a constant/parameter, in either order. The accumulator is null for a key comparison; the
    // group-level operand is normalized to the left.
    private static bool TryBindGroupPredicateComparison(
        Expression body,
        ParameterExpression groupingParameter,
        MongoGroupElementTranslator translator,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? comparisonNode)
    {
        accumulator = null;
        comparisonNode = null;

        const string outputField = "__agg0";

        if (Unwrap(body) is not BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            return false;
        }

        if (TryBindGroupSideOperand(bin.Left, groupingParameter, keyParts, isComposite, translator, outputField,
                out accumulator, out var leftKeyProperty, out var leftKeyPath, out var leftRef)
            && (leftKeyPath == null || IsSafeZeroPartKeyComparison(leftKeyPath, keyParts, bin.Right))
            && TryTranslateComparisonConstant(bin.Right, leftKeyProperty, out var rightNode))
        {
            comparisonNode = new MongoBinaryExpression(MapComparisonOperator(bin.NodeType), leftRef, rightNode);
            return true;
        }

        if (TryBindGroupSideOperand(bin.Right, groupingParameter, keyParts, isComposite, translator, outputField,
                out accumulator, out var rightKeyProperty, out var rightKeyPath, out var rightRef)
            && (rightKeyPath == null || IsSafeZeroPartKeyComparison(rightKeyPath, keyParts, bin.Left))
            && TryTranslateComparisonConstant(bin.Left, rightKeyProperty, out var leftNode))
        {
            comparisonNode = new MongoBinaryExpression(
                MapComparisonOperator(FlipComparison(bin.NodeType)), rightRef, leftNode);
            return true;
        }

        return false;
    }

    // One side of a group predicate comparison: a key access (reads "_id"[.Name], no accumulator) or an
    // accumulator bound to "__agg0". A key match also returns the key part's IProperty so the caller serializes
    // the other operand through it (BsonValue.Create throws for e.g. Guid); key parts already passed
    // HasDefaultKeySerialization at bind time.
    private static bool TryBindGroupSideOperand(
        Expression side,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoGroupElementTranslator translator,
        string accumulatorOutputField,
        out MongoGroupAccumulator? accumulator,
        out IProperty? keySerializationProperty,
        out string? matchedKeyPath,
        [NotNullWhen(true)] out MongoExpression? reference)
    {
        accumulator = null;
        keySerializationProperty = null;
        matchedKeyPath = null;
        reference = null;

        // allowWholeKeyRead: false — a whole composite key comparison (g.Key == new {...}) declines: there's no
        // single property to serialize the anonymous constant against.
        if (TryGetKeyMemberPath(side, groupingParameter, keyParts, isComposite, out var keyPath, allowWholeKeyRead: false))
        {
            if (keyPath == null)
                return false; // bare g.Key over a composite key — no single field to compare

            // A zero-part key also has no single backing property.
            keySerializationProperty = isComposite || keyParts.Count == 0
                ? null
                : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

            matchedKeyPath = keyPath;
            reference = new MongoElementRefExpression(keyPath, Unwrap(side).Type);
            return true;
        }

        if (TryBindAccumulator(side, accumulatorOutputField, groupingParameter, keyParts, isComposite, translator, out var acc, out _))
        {
            accumulator = acc;
            reference = new MongoElementRefExpression(accumulatorOutputField, Unwrap(side).Type);
            return true;
        }

        return false;
    }

    // The non-accumulator side of a group comparison: a literal or query parameter. `forSerialization` is the
    // other side's key property, if any (see TryBindGroupSideOperand).
    private static bool TryTranslateComparisonConstant(
        Expression expr, IProperty? forSerialization, [NotNullWhen(true)] out MongoExpression? result)
    {
        expr = Unwrap(expr);
        switch (expr)
        {
            case ConstantExpression constant:
                // A null literal renders as BSON null directly: the key property may be non-nullable (a nullable-cast
                // key, `(decimal?)o.Total`), and its serializer throws on null.
                result = new MongoConstantExpression(constant.Value, constant.Value is null ? null : forSerialization);
                return true;
            default:
                if (NativeQueryParameter.TryGetQueryParameterName(expr, out var name))
                {
                    result = new MongoParameterExpression(name, forSerialization);
                    return true;
                }

                result = null;
                return false;
        }
    }

    private static MongoBinaryOperator MapComparisonOperator(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.Equal => MongoBinaryOperator.Equal,
        ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
        ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => MongoBinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(nodeType))
    };

    // The comparison `constant OP accumulator` is equivalent to `accumulator OP' constant` for the flipped
    // relational operator OP' (equality/inequality are already symmetric).
    private static ExpressionType FlipComparison(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => nodeType
    };

    // Negates a group comparison (from TryBindGroupPredicateComparison, or the post-GroupBy All(pred) comparison
    // NativeCardinalityBinder.TryBindAggregate passes). These are $expr-only, so MongoExpressionNegator.TryNegate
    // (gated on IsQueryDialectRenderable) can't be used; apply its aggregation rule: invert $eq/$ne, $not-wrap the
    // relational operators.
    internal static bool TryNegateGroupComparison(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = node is MongoBinaryExpression comparison
            ? comparison.Operator switch
            {
                MongoBinaryOperator.Equal =>
                    new MongoBinaryExpression(MongoBinaryOperator.NotEqual, comparison.Left, comparison.Right),
                MongoBinaryOperator.NotEqual =>
                    new MongoBinaryExpression(MongoBinaryOperator.Equal, comparison.Left, comparison.Right),
                MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual
                    or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual =>
                    new MongoUnaryExpression(MongoUnaryOperator.Not, comparison),
                _ => null
            }
            : null;

        return negated != null;
    }

    /// <summary>
    /// <c>Distinct(projection)</c>: converts the terminal <c>Select</c>'s projection into a key-only grouping and a
    /// flatten reading each value back from <c>_id</c>. Returns <see langword="false"/> if there's no native
    /// projection or a field key isn't default-serialized (generic <c>_id</c> readback would diverge), unless it is a
    /// bare value-converted key (<see cref="IsBareValueConvertedDistinctKey"/>).
    /// </summary>
    internal static bool TryBindDistinctFromProjection(MongoQueryExpression mongoQ)
    {
        var select = mongoQ.Select;
        // A string-sequence leaf (AsEnumerable/ToList/ToArray over a string) pushes down the raw string and applies
        // the .NET call in the shaper, so a $group would dedupe the underlying strings, not the projected values.
        if (select.HasStringSequenceProjectionLeaf)
            return false;

        // An owned-array or owned-reference entity leaf forces the owner _id into the projected document (see
        // NativeProjectionBinder), so a $group over it would dedup by owner identity rather than projected value.
        // Same reason IsPlainProjectedSelect declines it as a set-op operand; the driver-LINQ fallback dedups by value.
        if (select.HasArrayProjectionLeaf)
            return false;

        // Declines:
        // - A projected SelectMany (UnwindSource set): the lowerer's UnwindSource branch runs first and returns
        //   early, silently dropping the $group and reading never-grouped "_id.<alias>" fields as nulls.
        // - A projected-operand set op (OperandsProjected): Projection is operand-1's own, emitted before the set-op
        //   stage, so converting it would corrupt that operand. Keep this narrow: a whole-entity set op's trailing
        //   projection (e.g. Union(A,B).Select(p).Distinct()) converts safely.
        // A bare projection is admitted: ApplyProjection's alias override also fires when IsDistinct is set, so the
        // alias survives the Route flip. Source-side paging/ordering in PipelineOps is
        // emitted before the $group, so it restricts the right input rows.
        if (select.Projection.Count == 0 || select.Grouping != null || select.Cardinality != null
            || select.UnwindSource != null || select.SetOperation is { OperandsProjected: true })
            return false;

        var keyParts = new List<MongoGroupingKeyPart>();
        var flatten = new List<MongoProjection>();
        foreach (var projection in select.Projection)
        {
            // A bare field needs the default-serialization guard (or is a bare converted key, see
            // IsBareValueConvertedDistinctKey); a computed key part has no IProperty, so no converter can apply and it
            // reads back from "_id.<alias>" like any computed projection member.
            if (projection.Expression is MongoFieldExpression field && !HasDefaultKeySerialization(field.Property)
                && !IsBareValueConvertedDistinctKey(select, field))
                return false;

            // ThrowsOnNull carries over to both: the deduped value is the same possibly-null value, read back from
            // "_id.<alias>" by the flatten, and by operators over the Distinct through the key part (DistinctAliasScope).
            keyParts.Add(new MongoGroupingKeyPart(projection.Alias, projection.Expression, projection.ThrowsOnNull));
            flatten.Add(new MongoProjection(projection.Alias,
                new MongoElementRefExpression("_id." + projection.Alias, projection.Expression.Type),
                ThrowsOnNull: projection.ThrowsOnNull));
        }

        select.ClearProjections();
        select.Grouping = new MongoGrouping(keyParts, []);
        // IsDistinct (not IsGroupBy) so the post-group guards keyed on IsGroupBy || IsDistinct apply, while
        // Distinct+Join stays a graceful fallback (GroupBy+Join hard-declines: driver-LINQ returns empty joins). See
        // MongoSelectDefinition.IsDistinct and TranslateJoinCore.
        select.IsDistinct = true;
        foreach (var f in flatten)
            select.AddProjection(f);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="field"/>, a non-default-serialized key part of a projected <c>Distinct()</c>, can still
    /// go native: the projection is a bare field (<c>Select(o =&gt; o.Status).Distinct()</c>, not a wrapper such as
    /// <c>new { o.Status }</c>) and the property has a value converter but no <c>BsonRepresentation</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>$group</c> dedups the stored (provider) values, exactly as driver-LINQ's Distinct does. The distinct value
    /// is flattened back under the bare projection's alias, and the shaper reads it through the property's own
    /// serializer (its <c>ProjectionMapping</c> leaf is still the <c>o.Status</c> member, resolved by
    /// <c>TryResolveFieldAccess</c>), so the converter applies. Downstream the sole key part stays a
    /// <see cref="MongoFieldExpression"/> over the property (see <c>MongoExpressionTranslator.TryResolveMember</c>'s
    /// bare-parameter arm), so comparisons serialize their constant/parameter through the converter, and ordering and
    /// relational comparisons act on the stored value, as they do for the same property in an ordinary native
    /// <c>Where</c>/<c>OrderBy</c> and on driver-LINQ. Operators that can't honour a converter decline through their
    /// existing guards (<c>MongoExpressionTranslator.AllFieldsDefaultSerialized</c>; the selector-less aggregates in
    /// <see cref="TryBindDistinctTerminalAggregate"/>).
    /// </para>
    /// <para>
    /// Both remaining conjuncts limit scope rather than fix a known wrong result: a wrapper projection
    /// (<c>new { o.Status }</c>, whose member-level post-Distinct consumers haven't been audited for converters) and a
    /// <c>BsonRepresentation</c> key stay on the fallback.
    /// </para>
    /// </remarks>
    private static bool IsBareValueConvertedDistinctKey(MongoSelectDefinition select, MongoFieldExpression field)
        => select.IsBareProjection && field.Property.GetBsonRepresentation() == null;

    /// <summary>
    /// Resolves an <c>OrderBy</c>/<c>ThenBy</c> key after a projected <c>Distinct()</c> against the Distinct's key
    /// part aliases, not the entity: <c>Select(o => new { Country = o.City }).Distinct()</c> names a member
    /// "Country" sourced from <c>City</c>, so resolving by entity property name would silently sort by the wrong
    /// field. Accepts only the identity selector (bare-scalar Distinct) or a single-hop member on the selector's
    /// own parameter (by identity) naming a key part; field-backed parts resolve as a field, computed ones as a
    /// <see cref="MongoElementRefExpression"/> on their alias. Anything else declines, so
    /// <c>NativeSlotPopulator.PopulateSortSlot</c> falls through to the
    /// <see cref="MongoExpressionTranslator.DistinctAliasScope"/>-scoped translator (needed for computed keys).
    /// </summary>
    internal static bool TryResolveDistinctOrderingKey(
        MongoGrouping grouping, ParameterExpression selfParam, Expression keySelectorBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // A bare-scalar Distinct's OrderBy selector is the identity (OrderBy(c => c)), matching its sole key part.
        if (ReferenceEquals(keySelectorBody, selfParam) && grouping.Key is [var soleKeyPart])
        {
            result = soleKeyPart.FieldRef is MongoFieldExpression soleField
                ? new MongoFieldExpression(soleField.Property, soleKeyPart.Name!)
                : new MongoElementRefExpression(soleKeyPart.Name!, soleKeyPart.FieldRef.Type);
            return true;
        }

        if (keySelectorBody is not MemberExpression { Expression: ParameterExpression param } member
            || !ReferenceEquals(param, selfParam))
            return false;

        foreach (var part in grouping.Key)
        {
            if (part.Name != member.Member.Name)
                continue;

            result = part.FieldRef is MongoFieldExpression field
                ? new MongoFieldExpression(field.Property, part.Name)
                : new MongoElementRefExpression(part.Name!, part.FieldRef.Type);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A selector-less Sum/Min/Max/Average directly on a bare-scalar projected <c>Distinct()</c>, e.g.
    /// <c>Select(o => o.OrderID).Distinct().Max()</c>. The operand is the degenerate group's sole key part's
    /// flattened alias.
    /// </summary>
    /// <remarks>
    /// <c>Count()</c>/<c>LongCount()</c> are excluded: they're also reachable over multi-member projected Distincts,
    /// which stay on the fallback path.
    /// </remarks>
    internal static bool TryBindDistinctTerminalAggregate(
        MongoQueryExpression mongoQ, MongoAggregateOperator op, LambdaExpression? selector, Type resultType)
    {
        var select = mongoQ.Select;
        if (select.Grouping is not { Accumulators.Count: 0, Key.Count: 1 } grouping || select.Cardinality != null)
            return false;

        if (op is not (MongoAggregateOperator.Sum or MongoAggregateOperator.Min or MongoAggregateOperator.Max
                or MongoAggregateOperator.Average))
            return false;

        var keyPart = grouping.Key[0];
        if (keyPart.Name == null)
            return false; // no flattened alias to read back (not reachable via TryBindDistinctFromProjection today)

        // A selector would reduce some other value than the one Distinct flattened; decline.
        if (selector != null)
            return false;

        // A value-converted key (admitted by IsBareValueConvertedDistinctKey) would reduce the stored values and read
        // the result back through a generic CLR serializer: Max over an enum stored as a string throws, Average over an
        // int stored as a string answers 0. The ungrouped Select(o => o.X).Max() declines the same operand.
        if (keyPart.FieldRef is MongoFieldExpression keyField && !HasDefaultKeySerialization(keyField.Property))
            return false;

        var operand = new MongoElementRefExpression(keyPart.Name, keyPart.FieldRef.Type, throwsOnNull: keyPart.ThrowsOnNull);

        // A non-nullable Min/Max/Average over a null-propagated key reduces an all-null input to null, read back as 0
        // where EF throws; as in NativeCardinalityBinder.TryBindAggregate. Sum skips a null, as EF's SUM does.
        if (op is not MongoAggregateOperator.Sum && MongoAggregationExpressionRenderer.ReadsNullAsDefault(resultType, operand))
            return false;

        NativeCardinalityBinder.BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);

        var cardinality = MongoCardinality.ForAggregate(
            op, operand, emptyBehavior, emptyValue, resultType, presenceOnly: false, presentValue: null);
        select.SetGroupedTerminalAggregate(grouping, cardinality, postGroupPredicate: null);
        return true;
    }
}
