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
/// emitting a native <c>$group</c>. Mirrors <see cref="NativeCardinalityBinder"/>'s role for scalar aggregates.
/// The key is parsed first (<see cref="TryBindGroupKey"/>, from the <c>GroupBy</c> key selector) and stashed on
/// <see cref="MongoSelectDefinition.PendingGroupKey"/>; the projection is parsed second
/// (<see cref="TryBindGroupProjection"/>, from the <c>Select</c> result selector) and finalizes
/// <see cref="MongoSelectDefinition.Grouping"/>. Either step returns <see langword="false"/> when the shape is
/// not natively representable, so the caller marks the query non-native and falls back to driver-LINQ.
/// </summary>
internal static class NativeGroupByBinder
{
    // The reserved element name the grouping key occupies in the emitted $group document.
    private const string GroupIdFieldName = "_id";

    /// <summary>
    /// Parses the <c>GroupBy</c> key selector into <see cref="MongoSelectDefinition.PendingGroupKey"/>.
    /// A bare <see cref="MemberExpression"/> is a scalar (single, unnamed) key; a <see cref="NewExpression"/>
    /// with members (an anonymous type) or a <see cref="MemberInitExpression"/> (a DTO with an object
    /// initializer) is a composite key whose parts each carry the member name. Every part
    /// must be a plain member access translatable to a field-ref; anything else (a computed key such as
    /// <c>x =&gt; x.Date.Year</c>) returns <see langword="false"/> and leaves the pending state unset.
    /// </summary>
    internal static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)
    {
        var select = mongoQ.Select;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        // EF-322/EF-TBD: a GroupBy composed directly on top of an already-finalized prior grouping — a
        // projected Distinct, or an ordinary prior GroupBy(key).Select(aggregate) (Select.PriorGrouping, set by
        // SnapshotPriorGroupingForNestedGroupBy just before this call) — resolves its key selector against
        // that prior stage's own flattened output alias, never the entity — same rationale and mechanism as
        // NativeSlotPopulator's Where arm (MongoExpressionTranslator.DistinctAliasScope's own remarks).
        if (select.PriorGrouping is { } priorGrouping)
            translator.DistinctAliasScope = priorGrouping;

        var parts = new List<MongoGroupingKeyPart>();

        switch (keySelector.Body)
        {
            // A zero-argument new{} key (GroupBy(o => new { })) groups every row into a single group. Matched
            // on Arguments.Count, NOT Members: NewExpression.Members is null for EVERY non-anonymous-type
            // constructor call (e.g. new OrderKey(o.Country, o.Year) also has Members == null), so matching on
            // Members alone would ALSO bind a genuine multi-part constructor-based key as a zero-part one —
            // silently collapsing every row into one group. Arguments.Count == 0 correctly admits only a
            // true zero-member new{} (both presentations the compiler emits have zero constructor arguments)
            // and excludes any real key, which always has one argument per key part.
            case NewExpression { Arguments.Count: 0 }:
                break;

            case NewExpression { Members: { Count: > 0 } members } newExpr:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    // EF-322 SP1: each key part is translated via TryBindKeyPartValue — see its own remarks.
                    if (!TryBindKeyPartValue(newExpr.Arguments[i], translator, out var partValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(members[i].Name, partValue));
                }

                break;

            // A MemberInitExpression DTO key (GroupBy(o => new NominalType { A = ..., B = ... })) — a
            // composite key whose parts each carry the bound member's name, same as the anonymous-type
            // NewExpression case above, but for a real (non-anonymous) type with an object initializer. Each
            // binding must be a plain MemberAssignment (a nested MemberMemberBinding/MemberListBinding — e.g.
            // `new Foo { Bar = { Baz = 1 } }` — has no single translatable VALUE and declines the whole key,
            // not just that part). The base NewExpression must take zero constructor arguments — a DTO
            // combining a parameterized ctor with an initializer has no established key-part-naming
            // convention in this codebase and is declined rather than silently dropping the ctor args.
            case MemberInitExpression { NewExpression.Arguments.Count: 0 } memberInit:
                // EF-322 SP7 fix-wave (Finding C1): key parts are named after the bound CLR member, but g.Key
                // is read back through the DTO's OWN driver class-map serializer, which honors
                // [BsonElement]/naming conventions rather than the CLR member name. If the DTO's stored
                // element name differs from the member name (a [BsonElement] rename, or a convention like
                // camelCase), $group._id would be written under the CLR name while the readback deserializer
                // expects the element name — a FormatException at read time. Renaming the key part to the
                // element name instead is NOT a fix: TryGetKeyMemberPath's sub-member matching
                // (`part.Name == member.Member.Name`) reads g.Key.<Sub> by CLR member name, not element name.
                // So: decline the whole key (fall back to driver-LINQ) unless every bound member's stored
                // element name equals its own CLR member name.
                var dtoClassMap = global::MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(memberInit.NewExpression.Type);
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;

                    var memberMap = dtoClassMap.GetMemberMap(assignment.Member.Name);
                    if (memberMap is null || memberMap.ElementName != assignment.Member.Name)
                        return false;

                    // EF-322 SP7: each key part is translated via TryBindKeyPartValue — the SAME helper the
                    // anonymous-type composite-key case above uses, so a value-converted or non-default-
                    // represented key member declines for free (the guard lives inside TryTranslateValue).
                    if (!TryBindKeyPartValue(assignment.Expression, translator, out var memberPartValue))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(assignment.Member.Name, memberPartValue));
                }

                break;

            // A bare member, EF.Property(...) call, computed expression (date-part/arithmetic/cast), literal
            // constant, or captured query parameter. A genuine multi-argument constructor call (NewExpression
            // with Members == null, e.g. new OrderKey(o.Country, o.Year)) also reaches here as the WHOLE
            // NewExpression — see TryBindKeyPartValue's own remarks for why that still declines. A
            // MemberInitExpression DTO key is handled by its own case above, EF-322 SP7.
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
    /// Translates one GroupBy key-selector body (or one composite key part) via
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> — the SAME general "any translatable value"
    /// method <see cref="TryBindAccumulator"/> already uses for an accumulator's operand — then applies two
    /// guards <c>TryTranslateValue</c> alone does not: it is a VALUE-position translator, not a KEY-position
    /// one, and a GroupBy key has stricter requirements than an ordinary computed value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Guard 1 — no document-construction leaves.</b> <c>TryTranslateValue</c> DOES translate a
    /// <c>MemberInitExpression</c>/anonymous-type-equality operand into a <see cref="MongoDocumentConstructionExpression"/>
    /// (<c>MongoExpressionTranslator.TranslateOperand</c>'s <c>TryGetProjectionMembers</c> arm — used for a
    /// <c>new {...} == new {...}</c> structural-equality VALUE operand). That construction has no safe generic
    /// <c>_id</c> (sub-)document readback the way a genuine NAMED composite key does (this method's own
    /// <c>NewExpression</c>-with-<c>Members</c> caller) — it binds as a single, UNNAMED scalar key part here,
    /// with no per-part <c>Name</c> at all, so the shaper cannot read it back into the DTO's own member names.
    /// Building one anyway does not fail at translate time; it crashes at MATERIALIZATION time instead (a
    /// <c>FormatException</c> reading the flattened <c>_id</c> back into the DTO's CLR type) — reject it here
    /// so the query falls back to driver-LINQ instead, exactly as a <c>MemberInitExpression</c> key already did
    /// before this method started calling <c>TryTranslateValue</c> at all. (A DTO key going native properly,
    /// as a genuine named composite key, is Bucket J / a later slice's job — not this one's.)
    /// </para>
    /// <para>
    /// <b>Guard 2 — probe renderability, not just translatability.</b> <c>TryTranslateValue</c> only checks
    /// that an expression CAN be translated to a <see cref="MongoExpression"/> tree, not that the tree renders
    /// to a valid <see cref="MongoDB.Bson.BsonValue"/> without throwing (e.g. a captured <see cref="Guid"/>
    /// parameter translates cleanly but throws <see cref="ArgumentException"/> — "type cannot be mapped to a
    /// BsonValue" — at pipeline-BUILD time). <see cref="NativeSlotPopulator.TryProbeBareValueRenders"/> already
    /// exists for exactly this reason, for a bare value/parameter SORT key
    /// (<see cref="NativeSlotPopulator.TryTranslateComputedSortKey"/>) — reused here unchanged.
    /// </para>
    /// </remarks>
    private static bool TryBindKeyPartValue(
        Expression body, MongoExpressionTranslator translator, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!translator.TryTranslateValue(body, out var translated))
            return false;

        if (translated is MongoDocumentConstructionExpression)
            return false;

        if (!MongoAggregationExpressionRenderer.CanRender(translated))
            return false;

        if (!NativeSlotPopulator.TryProbeBareValueRenders(translated, NativeSlotPopulator.UnwrapBoxingToObjectType(body)))
            return false;

        result = translated;
        return true;
    }

    // A grouping key becomes the group's _id and is read back through a generic CLR-type serializer by the
    // grouped-row shaper (the flattened _id has no backing IProperty). That generic read only reproduces the
    // property's materialized value when the property serializes with the default/identity representation. A
    // property with a value converter (stored as the provider value, needing reverse conversion) or a
    // non-default BsonRepresentation (e.g. enum-as-string) would either throw at materialization or return
    // the raw stored value, diverging from the driver-LINQ path. Reject such keys so the query falls back
    // instead, preserving the Native == DriverLinq invariant. The accumulator operand is deliberately not
    // checked here: Sum/Min/Max over a represented field is a pre-existing shared caveat where native and
    // driver-LINQ are wrong the same way (no divergence).
    // Internal (not private) — also shared by the QMTEV's TranslateOfType discriminator guard, which rejects a
    // value-converted / non-default-BsonRepresentation discriminator for the identical generic-readback reason.
    internal static bool HasDefaultKeySerialization(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() == null;

    /// <summary>
    /// Parses the <c>Select</c> result selector against the pending key from <see cref="TryBindGroupKey"/>,
    /// finalizing <see cref="MongoSelectDefinition.Grouping"/>. The body is either a <see cref="NewExpression"/>
    /// (anonymous type) or <see cref="MemberInitExpression"/> (DTO) whose members, or (when the body carries no
    /// member name at all, e.g. <c>g.Sum(...)</c> with no wrapping <c>new {}</c>) the bare body itself, must
    /// each be either a grouping-key access (<c>g.Key</c> / <c>g.Key.&lt;Sub&gt;</c>) or a supported aggregate
    /// over the grouping (<c>g.Count()</c>/<c>g.LongCount()</c> → <c>$sum:1</c>;
    /// <c>g.Sum/Min/Max/Average(x =&gt; ...)</c> over any translatable value — a plain member, constant,
    /// cast, or computed arithmetic expression). A WRAPPED (non-bare) body with no accumulator at all — a
    /// key-only ctor-DTO or anonymous-type projection — is also admitted, as a "distinct keys" $group. Returns
    /// <see langword="false"/> for any other shape: a BARE key access with no wrapper (semantically a plain
    /// Distinct, out of scope here), or a zero-accumulator projection combined with a pending-ordering
    /// aggregate (an untested combination), so the caller falls back.
    /// </summary>
    /// <param name="mongoQ">The query whose <see cref="MongoSelectDefinition"/> is being populated.</param>
    /// <param name="resultSelector">The <c>Select</c> result selector lambda over the grouping.</param>
    /// <param name="bareLeafAlias">
    /// On success, the reserved alias (<see cref="NativeProjectionBinder.SyntheticBareProjectionAlias"/>) the
    /// bare-body case was bound under, so the caller can build a single-member shaper instead of walking
    /// <c>resultSelector.Body</c>'s (nonexistent) constructor members; <see langword="null"/> when the body was
    /// a wrapped anonymous/DTO projection (the caller's ordinary member-by-member shaper applies instead), and
    /// undefined when this method returns <see langword="false"/>.
    /// </param>
    internal static bool TryBindGroupProjection(
        MongoQueryExpression mongoQ, LambdaExpression resultSelector, out string? bareLeafAlias)
    {
        bareLeafAlias = null;
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        // EF-322 SP2: a HAVING Where stashed between GroupBy and this Select (e.g. GroupBy(key)
        // .Where(o => o.Count() > 4).Select(g => new { g.Key, Count = g.Count() })) — recognized by
        // NativeSlotPopulator's Where carve-out (NativeGroupByBinder.TryBindGroupWherePredicate). Consumed
        // here: its own accumulator (null for a bare key comparison) is folded into this grouping's
        // accumulators below, and its comparison becomes GroupHavingPredicate, emitted by MongoSelectLowerer
        // as a $match immediately after $group. Cleared unconditionally right away (one-shot, mirroring
        // TryBindGroupTerminalAggregate's own consume discipline) so no stale state survives if this method
        // declines later for an unrelated reason.
        var havingPredicate = select.PendingGroupPredicate;
        select.PendingGroupPredicate = null;

        // A BARE (non-`new {}`/DTO) result selector — e.g. `GroupBy(o => o.CustomerID)
        // .Select(g => g.Sum(o => o.OrderID))` — carries no member name, so it is projected under the
        // reserved `_v` alias, the SAME tier-2 (ProjectionAliasTier.Synthetic) convention
        // NativeProjectionBinder/NativeSelectManyBinder use for a bare computed/aggregate body: `_v` is
        // what the driver itself names a bare projection, so a late fallback (which leaves this query's
        // captured chain un-stripped) has the driver's own push-down write the very element the
        // alias-addressed shaper already reads by. The single-entry list is then walked by the SAME
        // key/accumulator loop below, so a bare body that is neither a key access nor a recognized
        // accumulator shape (e.g. a computed expression) still declines exactly as before — bareLeafAlias
        // is set only once every guard below has actually admitted this body (mirrors the "commit once
        // every gate has passed" discipline TryBuildGroupResultShaper's own remarks require).
        var isBareBody = !resultSelector.Body.TryGetProjectionMembers(out var bindings, allowPositionalConstructorArguments: true);
        if (isBareBody)
        {
            bindings = [(NativeProjectionBinder.SyntheticBareProjectionAlias, resultSelector.Body)];
        }

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        // EF-322/EF-TBD: an accumulator's operand selector (g.Sum(x => x.Field) etc.) over a GroupBy nested on
        // an already-finalized prior grouping — a projected Distinct or an ordinary prior GroupBy — resolves
        // against that prior stage's own flattened alias, never the entity — see TryBindGroupKey's own
        // carve-out above for the identical rationale.
        if (select.PriorGrouping is { } priorGrouping)
            translator.DistinctAliasScope = priorGrouping;

        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        // Resolve any OrderBy/ThenBy composed directly on the ungrouped GroupBy result (recorded by
        // NativeSlotPopulator's pending-ordering carve-out) BEFORE processing the Select's own bindings below,
        // so an ordering aggregate that the Select does NOT project (e.g. orders by Count() but projects
        // Sum(...)) still gets its own $group accumulator. Always allocates a FRESH accumulator field for an
        // ordering aggregate rather than detecting and reusing an identical one the Select also projects — a
        // harmless redundant $group field, not a correctness issue, and far simpler than structural
        // de-duplication.
        var orderAccumulators = new List<MongoGroupAccumulator>();
        var resolvedOrderings = new List<MongoOrdering>();
        if (select.PendingGroupOrderings is { } pendingOrderings)
        {
            var orderIndex = 0;
            foreach (var (ascending, keySelector) in pendingOrderings)
            {
                var groupParam = keySelector.Parameters[0];
                var body = keySelector.Body;

                // allowWholeKeyRead: false — ordering by the WHOLE composite key still declines, unlike the
                // Select-projection flatten below. A composite key's CLR type is a compiler-generated
                // anonymous type with no IComparable/IComparer, so `.OrderBy(g => g.Key)` over one is not a
                // shape the in-memory LINQ oracle can even execute (Comparer<T>.Default throws for it) — this
                // stays declined rather than emitting a $sort on the raw "_id" sub-document, which would give
                // a different (BSON field-order) comparison with no oracle to match against.
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

        // Flatten projection: each result member maps to a top-level output alias read back by the DOM
        // shaper. Key members read from the group _id (scalar → "_id", composite sub → "_id.<Name>");
        // accumulator members read from their own top-level output field. Emitted as a trailing $project
        // after the $group (MongoSelectLowerer) so the shaper never needs a nested-_id read.
        var accumulators = new List<MongoGroupAccumulator>();
        var flatten = new List<MongoProjection>();
        var isBareBodyKeyMember = false; // EF-322 SP4: tracks whether a bare body was bound as a bare key member
        foreach (var (memberName, valueExpr) in bindings)
        {
            if (TryGetKeyMemberPath(valueExpr, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite key cannot flatten to a single field

                flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(keyPath, Unwrap(valueExpr).Type)));
                if (isBareBody)
                    isBareBodyKeyMember = true; // EF-322 SP4: the bare body was a key member, not a computed expression
                continue;
            }

            if (TryBindAccumulator(valueExpr, memberName, groupingParameter, keyParts, isComposite, translator, out var acc, out var flattenRead))
            {
                accumulators.Add(acc);
                flatten.Add(new MongoProjection(memberName, flattenRead));
                continue;
            }

            // EF-322 SP4: a COMPUTED member value (a ternary or, from Task 2, a coalesce) combining a
            // g.Key/g.Key.Sub leaf with a constant — neither a bare key access nor a bare accumulator call.
            if (!TryTranslateGroupProjectionExpression(valueExpr, groupingParameter, keyParts, isComposite, translator, out var computed))
                return false;

            flatten.Add(new MongoProjection(memberName, computed));
        }

        // A zero-accumulator Select still admits a WRAPPED (ctor-only DTO or anonymous-type) key-only
        // projection — e.g. GroupBy(key).Select(g => new Result(g.Key)) — as a legitimate "distinct keys"
        // $group (a $group with only _id and no other accumulator fields is ordinary, valid MQL). Two shapes
        // still decline: a BARE g.Key projection (isBareBodyKeyMember — semantically a plain Distinct, out of scope
        // here), and a zero-Select-accumulator projection combined with a pending-ordering aggregate
        // (orderAccumulators.Count > 0 — an untested combination this plan does not attempt). A pending
        // ordering that instead resolves via a KEY access (adding to resolvedOrderings, not
        // orderAccumulators) is still admitted — only an ordering that resolves to its OWN $group
        // accumulator excludes this shape. EF-322 SP4: bare COMPUTED projections (ternaries, coalesces)
        // are now admitted — only bare KEY projections decline.
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
        if (isBareBody)
            bareLeafAlias = NativeProjectionBinder.SyntheticBareProjectionAlias;
        return true;
    }

    // Classifies a result-member value as a grouping-key access and, if so, yields the group-output element
    // path it reads from. Returns true for a key access; `path` is null for a bare g.Key that cannot resolve
    // to one field — a composite key when `allowWholeKeyRead` is false. A zero-part (empty new{}) key now
    // resolves to "_id" (the group's own empty document) like any other key. Returns false when the value is
    // not a key access (i.e. it is an accumulator).
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

        // g.Key — the whole key. For the Select-projection flatten (allowWholeKeyRead: true), flattenable for
        // any key (scalar, composite, or zero-part) by reading the group's own "_id" back wholesale — for a
        // composite key that is a sub-document whose fields already match the composite key type's member
        // names (the exact shape TryBindGroupKey wrote them in), so the same generic CLR-type readback that
        // already handles a scalar key materializes the composite type from it too. A zero-part (empty new{})
        // key's "_id" is itself the empty document — the correct readback for an empty anonymous-type key. The
        // ordering call site passes allowWholeKeyRead: false — see its own call-site remarks.
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

    // EF-322 SP4: translates a Select-projection member's value as a COMPUTED expression tree — a ternary or
    // (Task 2) a null-coalesce — whose leaves resolve to a g.Key/g.Key.Sub access or an ordinary translatable
    // value (constant, entity member, arithmetic — anything NOT referencing the grouping parameter). Runs in
    // the FLATTENING $project stage, AFTER $group has already produced "_id" — unlike SP3's
    // TryTranslateAccumulatorCondition (which runs INSIDE $group and must resolve a key reference to its own
    // raw per-input-document expression instead, to avoid a circular reference to $group's own not-yet-
    // computed output), a key leaf here correctly resolves via TryGetKeyMemberPath's ordinary "_id"[.Sub]
    // path, the SAME resolution the flatten loop's own bare-key-member arm already uses.
    private static bool TryTranslateGroupProjectionExpression(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
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

        // A ternary (test ? ifTrue : ifFalse) — every part recurses through this SAME method, so a branch
        // may itself be a nested conditional, a coalesce (Task 2), a key access, or an ordinary value.
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

        // A null-coalescing operator (`left ?? right`) — both operands recurse through this SAME method, so
        // the right operand may itself be a nested Coalesce (a ?? b ?? c is right-associative: a ?? (b ?? c),
        // matching BinaryExpression's own shape) — see this plan's own Review Focus.
        if (expr is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            if (!TryTranslateGroupProjectionExpression(coalesce.Left, groupingParameter, keyParts, isComposite, translator, out var left)
                || !TryTranslateGroupProjectionExpression(coalesce.Right, groupingParameter, keyParts, isComposite, translator, out var right))
                return false;

            result = new MongoCoalesceExpression(left, right);
            return true;
        }

        // Not a key access or a ternary — an ordinary expression (constant, entity member, arithmetic) that
        // must NOT reference the grouping parameter in any shape (an accumulator call, a mixed per-element
        // reference, etc.) — see this plan's own Review Focus. Declines rather than letting the ordinary
        // translator, which knows nothing about `g`, mis-resolve a same-named member against the wrong type
        // (the exact bug class SP3's final review found and fixed for its own accumulator conditions).
        if (ReferencesParameter(expr, groupingParameter))
            return false;

        return translator.TryTranslateValue(expr, out result);
    }

    // The BOOLEAN test of a ternary (e.g. `g.Key == null`) — a key-vs-constant comparison, recognized the
    // SAME way SP3's TryTranslateAccumulatorCondition recognizes one, but resolving the key side via
    // TryGetKeyMemberPath's ordinary post-$group "_id"[.Sub] path (this runs in $project, not inside $group —
    // see TryTranslateGroupProjectionExpression's own remarks on why that distinction matters here).
    private static bool TryTranslateGroupProjectionConditionOrValue(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
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

        // Not a key comparison — delegate to the SAME leaf/ternary/coalesce dispatch as an ordinary value.
        return TryTranslateGroupProjectionExpression(expr, groupingParameter, keyParts, isComposite, translator, out result);
    }

    // Resolves the matched key part's own serialization property from a "_id"[.Sub] path already confirmed by
    // TryGetKeyMemberPath — mirrors TryBindGroupSideOperand's identical resolution for the SAME reason (a
    // value-converted/non-default-represented key type like Guid needs its own property's serializer, not the
    // generic BsonValue.Create fallback, which throws for such types). Returns null for the admitted
    // whole-composite-key-vs-null case (keyPath == "_id" && isComposite) — a composite key has no single
    // backing property, and comparing it to null doesn't need one.
    private static IProperty? ResolveKeyMemberSerializationProperty(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite)
    {
        // EF-322 SP7: a zero-part (empty new{}) key now also resolves keyPath == "_id" (TryGetKeyMemberPath's
        // own fix, this plan's Task 2) but has no single backing property at all — same "no property" answer
        // a composite key's own whole-key comparison already returns, for the same reason (no single field).
        if (keyPath == "_id")
            return isComposite || keyParts.Count == 0 ? null : (keyParts[0].FieldRef as MongoFieldExpression)?.Property;

        var matchedPart = keyParts.First(p => keyPath == "_id." + p.Name);
        return (matchedPart.FieldRef as MongoFieldExpression)?.Property;
    }

    // EF-322 SP7 fix-wave (Finding I1): a zero-part (empty new{}) key's bare g.Key resolves to "_id" (Task 2)
    // with no single backing property (ResolveKeyMemberSerializationProperty returns null for it). Comparing
    // it against anything other than a literal null constant would otherwise reach
    // TryTranslateComparisonConstant/MongoConstantExpression with no serialization property to render the
    // OTHER operand through, falling back to the generic BsonValue.Create — which throws ArgumentException for
    // a non-BSON-mappable CLR type (an anonymous type, an arbitrary DTO instance) instead of declining
    // cleanly, the same way every other unsafe shape in this file declines. A composite key's whole-key read
    // is unaffected — it already declines earlier via allowWholeKeyRead: false at its own call sites, or (for
    // the allowWholeKeyRead: true flatten/ternary call sites) is not a keyPath == "_id" match with an empty
    // keyParts list, so this guard is a no-op for it.
    private static bool IsSafeZeroPartKeyComparison(
        string keyPath, IReadOnlyList<MongoGroupingKeyPart> keyParts, Expression otherOperand)
    {
        if (keyPath != "_id" || keyParts.Count != 0)
            return true;

        return Unwrap(otherOperand) is ConstantExpression { Value: null };
    }

    // Flatten a NewExpression (anonymous type) or MemberInitExpression (DTO) into (memberName, valueExpr) pairs.

    // Match g.Count()/g.LongCount() → ("$sum", null); g.Sum/Average/Min/Max(x => ...) over any translatable
    // value (member access, constant, cast, or computed arithmetic) → the matching operator + translated
    // operand. An untranslatable operand (e.g. a correlated method call) or unknown method returns false. The
    // aggregate's SOURCE (call.Arguments[0]) must be the grouping parameter itself — an
    // aggregate whose source is a DIFFERENT sequence (a correlated cross-collection subquery such as
    // Customers.Where(c => c.CustomerID == g.Key).Count(), a navigation, another collection) is NOT a grouped
    // accumulator and must NOT be bound to a $group accumulator (that would silently drop the real subquery
    // computation and return the group's row count instead). Reject it so the projection falls back to
    // driver-LINQ, preserving the Native == DriverLinq invariant.
    private static bool TryBindAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        // The $group document already carries the grouping key under the reserved "_id" field
        // (MongoPipelineFactory.RenderKeyedGroup). An accumulator whose output field is literally "_id"
        // (e.g. Select(g => new { _id = g.Count() })) would add a SECOND "_id" element to that document → a
        // BsonDocument duplicate-key throw at pipeline build, which is an unhandled crash rather than a clean
        // fallback. Reject it so the shape falls back to driver-LINQ (and throws only under NativeOnly). This
        // is scoped to accumulators: a KEY member projected to an "_id" alias reads the group's own "_id"
        // back and does NOT collide (that path never reaches here — it is handled as a key member).
        if (outputField == GroupIdFieldName)
            return false;

        if (Unwrap(expr) is not MethodCallExpression call)
            return false;

        // EF-322: g.Select(e => e.Field).Distinct().<Op>() — Count/LongCount/Average/Max/Min/Sum over the
        // DISTINCT projected values within the group, not every row. A completely separate shape from the
        // ordinary accumulators below (its own source is g.Select(...).Distinct(), never g directly), so it
        // is tried FIRST, before the IsGroupingSource(call.Arguments[0], ...) guard below that would
        // otherwise reject it outright.
        if (TryBindDistinctAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        // EF-TBD: a SELECTOR-LESS aggregate (g.Sum()/Average()/Min()/Max(), no lambda) over a GroupBy composed
        // with a two-arg key+elementSelector overload arrives here as `g.Select(elementSelector).Sum()` —
        // EF Core's normalizer re-expresses the parameterless aggregate as an ordinary Queryable.Select
        // wrapping the grouping parameter, rather than inlining the element selector into a same-shaped
        // aggregate lambda the way it does for a SELECTOR-carrying aggregate (see this method's own remarks
        // on g.Sum(e => e.Field) above). Bind it the SAME way as the distinct form just above minus the
        // dedup semantics — an ordinary $sum/$avg/$min/$max over the selected field, not $addToSet.
        if (TryBindElementSelectedAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        if (TryBindFilteredAccumulator(call, outputField, groupingParameter, keyParts, isComposite, translator, out accumulator, out flattenRead))
            return true;

        if (call.Arguments.Count == 0 || !IsGroupingSource(call.Arguments[0], groupingParameter))
            return false;

        var definition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;

        // Count / LongCount — g.Count() / g.LongCount() with no selector argument → $sum: 1. EF Core lowers a
        // grouped aggregate to the Queryable form over `g.AsQueryable()` (e.g. Queryable.Count(g.AsQueryable()));
        // a hand-authored Enumerable form is accepted too (used by the unit tests).
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

        // EF-322 SP3: g.Count(pred) / g.LongCount(pred) — a per-element PREDICATED count, reducing to
        // $sum: {$cond: [translatedPredicate, 1, 0]} (0, not $$REMOVE, matching the driver-LINQ fallback's
        // own existing baseline exactly — a non-matching element contributes 0 either way for a sum of 1s).
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

        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form). Any
        // translatable value — a bare member access, a constant, a cast, or ordinary arithmetic — is
        // accepted via TryTranslateValue, which resolves a bare member exactly as TryTranslateField did (same
        // underlying TryResolveMember), so no previously-working shape returns different data. TryTranslateValue
        // additionally requires AllFieldsDefaultSerialized, which TryTranslateField never checked, so a bare
        // member over a value-converted/non-default-represented property now falls back to driver-LINQ instead
        // of going native — arguably a correctness improvement, not a loss, since summing a raw converted or
        // represented value natively could otherwise silently aggregate the wrong representation. EF Core's own
        // query compiler inlines a two-arg GroupBy(key, elementSelector)'s element selector directly into this
        // aggregate's lambda before our translator ever runs, so `g.Sum(e => e.OrderID + 1)` composed from
        // `.GroupBy(o => o.CustomerID, o => new { o.OrderID })` arrives here as an ordinary computed
        // selector over the root entity — no separate two-arg handling is needed.
        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false; // untranslatable selector shape (e.g. a correlated method call) — fall back

        accumulator = new MongoGroupAccumulator(outputField, op, operand);
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    // EF-322 SP3: resolves g.Key / g.Key.Sub, inside an ACCUMULATOR's own condition/operand, to the key
    // part's OWN raw per-input-document expression (the SAME expression already used to compute _id's value)
    // — NEVER "_id" itself. An accumulator's condition/operand is evaluated PER INPUT DOCUMENT, inside the
    // SAME $group stage that computes _id as its OUTPUT — referencing "_id" here would be a circular
    // reference the server cannot evaluate. Contrast with NativeGroupByBinder's SP2 HAVING key comparison
    // (TryBindGroupSideOperand), which correctly DOES use "_id"[.path] — that one runs in a separate, LATER
    // $match stage, after $group has already produced _id.
    private static bool TryResolveKeyReferenceAsRawExpression(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        // KNOWN BUG (EF-TBD, pre-existing, out of scope for EF-322 SP7): a zero-part (empty new{}) key's
        // g.Key referenced INSIDE an accumulator's own per-element condition (e.g. g.Count(e => g.Key ==
        // null)) reaches the `keyParts[0]` read below with an EMPTY keyParts list — IndexOutOfRangeException.
        // Unlike TryGetKeyMemberPath (fixed by SP7's Task 2), this method resolves against the per-element
        // RAW expression, never through TryGetKeyMemberPath, so SP7's fix does not reach it. No target test
        // exercises this shape; fix it if a future slice needs it.
        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            if (isComposite)
                return false; // whole composite key has no single raw expression to compare against a scalar

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

    /// <summary>
    /// Translates a per-element accumulator CONDITION (from <c>g.Count(pred)</c>, <c>g.Where(pred).Op(...)</c>,
    /// or the leading <c>Where</c>/<c>Distinct</c> hop <see cref="TryBindDistinctAccumulator"/> also uses).
    /// Recognizes exactly two shapes — a comparison whose one side is a <c>g.Key</c>/<c>g.Key.Sub</c> access
    /// (resolved via <see cref="TryResolveKeyReferenceAsRawExpression"/>, since every element in a group
    /// shares the SAME key value, this is a valid per-element condition even though it never actually varies
    /// per element), or an ORDINARY per-element expression (delegated to <paramref name="translator"/>'s
    /// normal <c>TryTranslate</c>, the same predicate translator <c>Where</c> itself uses). Deliberately does
    /// NOT handle a predicate that COMBINES both in one condition (e.g. <c>e => e.Amount > 5 &amp;&amp;
    /// e.Key == "x"</c>) — no target shape needs it; declines so the whole query falls back rather than
    /// silently translating only half the condition.
    /// </summary>
    // A non-nullable value type (int, decimal, DateTime, ...) has no representation for "no value" — a null
    // read back from the server for one of these must not be silently defaulted. Nullable<T> and reference
    // types are unaffected: null IS their legitimate "no value".
    private static bool IsNonNullableValueType(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static bool TryTranslateAccumulatorCondition(
        Expression predicateBody,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
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
            if (TryResolveKeyReferenceAsRawExpression(bin.Left, groupingParameter, keyParts, isComposite, out var leftKey)
                && TryTranslateComparisonConstant(bin.Right, (leftKey as MongoFieldExpression)?.Property, out var rightConst))
            {
                result = new MongoBinaryExpression(MapComparisonOperator(bin.NodeType), leftKey, rightConst);
                isKeyOnlyCondition = true;
                return true;
            }

            if (TryResolveKeyReferenceAsRawExpression(bin.Right, groupingParameter, keyParts, isComposite, out var rightKey)
                && TryTranslateComparisonConstant(bin.Left, (rightKey as MongoFieldExpression)?.Property, out var leftConst))
            {
                result = new MongoBinaryExpression(MapComparisonOperator(FlipComparison(bin.NodeType)), rightKey, leftConst);
                isKeyOnlyCondition = true;
                return true;
            }
        }

        // Final-review fix: a predicate that references the grouping parameter in ANY shape the two arms
        // above didn't already recognize and consume (e.g. a MIXED condition like `e.Amount > 5 &&
        // g.Key == "x"`, or a same-parameter comparison the constant-side check rejected) must decline here
        // rather than fall through to the ordinary translator below — that translator resolves members by
        // NAME against the entity type, regardless of which parameter they hang off, so a `g.Key` reference
        // would be silently misresolved as a same-named member of the ELEMENT's own entity type if one
        // happens to exist (e.g. an entity with its own "Key" property) instead of declining.
        if (ReferencesParameter(predicateBody, groupingParameter))
            return false;

        // Not a g.Key comparison — an ordinary per-element predicate (e.g. e.Amount < 100,
        // e.OrderDate.HasValue). The ordinary translator resolves members against the entity type directly,
        // regardless of which lambda parameter name the predicate happens to use.
        return translator.TryTranslate(predicateBody, out result);
    }

    // Whether `expr` contains any reference (anywhere in the tree) to `parameter` — used to detect a
    // predicate that touches the grouping parameter in a shape TryTranslateAccumulatorCondition's own
    // recognized comparison arms didn't already consume, so it can decline instead of risking a
    // parameter-blind, name-based mis-resolution by the ordinary translator.
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

    // EF-322 SP3: resolves the OPTIONAL leading hop before g.Select(selector).Distinct().<Op>() — either
    // NOTHING (source is g directly), a bare g.Distinct() (a provable no-op for this shape — see this plan's
    // own Review Focus note — admitted with NO condition, not a trivially-true one), or a genuine g.Where(pred)
    // (a real per-element filter, admitted WITH a translated condition). Both Queryable and Enumerable forms
    // of Where/Distinct are accepted, same reasoning as TryBindFilteredAccumulator.
    private static bool TryResolveOptionalAccumulatorSourceCondition(
        Expression source,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
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
    /// EF-322: <c>g.Select(e =&gt; e.Field).Distinct().&lt;Op&gt;()</c> — Count/LongCount/Average/Max/Min/Sum
    /// over the DISTINCT projected values within the group, not every row. Binds the <c>$group</c> accumulator
    /// as <c>$addToSet</c> (collecting the group's distinct values into an array) and produces a
    /// <see cref="MongoSizeExpression"/> (Count/LongCount) or <see cref="MongoArrayReduceExpression"/>
    /// (Average/Max/Min/Sum) to reduce that array back to a scalar in the flattening <c>$project</c> — see
    /// those two types' own remarks. Both the Queryable form (EF Core's own nav-expansion normalizes a
    /// grouped aggregate to <c>Queryable.X(g.AsQueryable())</c>) and the Enumerable form (a hand-authored
    /// unit-test lambda over <c>IGrouping&lt;TKey,TElement&gt;</c>) are recognized — EF-322 SP3 widened this
    /// from Queryable-only. An optional leading <c>Where</c>/<c>Distinct</c> hop before the <c>Select</c> is
    /// also recognized (<see cref="TryResolveOptionalAccumulatorSourceCondition"/>) — SP3 again.
    /// </summary>
    private static bool TryBindDistinctAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (call.Arguments.Count != 1)
            return false;

        // Min/Max/Count/LongCount are true open generics (<TSource>); Average/Sum's without-selector
        // overloads are NOT generic — the numeric type is baked into the overload (e.g.
        // Average(IQueryable<int>)) — so only Min/Max/Count/LongCount can be compared via
        // GetGenericMethodDefinition(). Average/Sum must be matched directly against call.Method.
        //
        // EF-322 SP3: widened to accept BOTH the Queryable form (EF Core's own normalized shape,
        // g.AsQueryable().Max()) and the Enumerable form (a hand-authored unit test lambda over
        // IGrouping<TKey,TElement> — this method previously had no such unit-test path at all). Matched by
        // name + declaring type since call.Arguments.Count == 1 (checked above) already excludes the
        // with-selector overloads that share the same names.
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

        // The source must be g.Select(selector).Distinct() — a Distinct() call whose OWN source is a Select.
        // The Select's OWN source is either g directly, or ONE extra hop (g.Where(pred) or g.Distinct()) —
        // EF-322 SP3, see TryResolveOptionalAccumulatorSourceCondition's own remarks.
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

        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form) — same
        // plain-member-access-only restriction as the ordinary Sum/Average/Min/Max accumulators above; a
        // computed selector falls back.
        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out var selectorField))
            return false;

        MongoExpression operand = selectorField;

        // EF-322 SP3: a leading g.Where(pred) hop wraps the operand with $cond, using "$$REMOVE" as the else
        // branch so a non-matching element contributes nothing to the $addToSet at all (not a null entry —
        // see this plan's own "Verified design decision"). A leading g.Distinct() hop (or no hop at all)
        // needs no wrapping — elementCondition stays null.
        if (elementCondition is not null)
        {
            // Final-review fix: same emptiness risk as TryBindFilteredAccumulator's own guard — if the
            // filter excludes every element, the resulting empty array's external $min/$max/$avg reduce
            // produces null, which the shaper would read back as default(T) for a non-nullable result. Only
            // a REAL filter (elementCondition non-null, i.e. a genuine g.Where(pred) hop, not the no-op
            // g.Distinct() hop) can make the array empty, so the no-hop/Distinct-hop cases are unaffected.
            // Scoped to a genuine per-element predicate, same reasoning as TryBindFilteredAccumulator's own
            // guard — a pure g.Key comparison shares the same value across every element of a given group.
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
    /// A SELECTOR-LESS <c>Sum()</c>/<c>Average()</c>/<c>Min()</c>/<c>Max()</c> over a <c>GroupBy(key,
    /// elementSelector)</c>'s element sequence — e.g. <c>.GroupBy(o =&gt; 2, o =&gt; o.OrderID).Select(g =&gt;
    /// g.Sum())</c>. Unlike a selector-carrying aggregate (<c>g.Sum(x =&gt; x.Field)</c>, whose element
    /// selector composition is inlined directly into the aggregate's own lambda before this translator ever
    /// runs — see <see cref="TryBindAccumulator"/>'s own remarks), a parameterless aggregate has no lambda for
    /// EF to inline into, so its normalizer instead re-expresses it as an ordinary
    /// <c>g.AsQueryable().Select(elementSelector).Sum()</c> — the SAME shape <see cref="TryBindDistinctAccumulator"/>
    /// recognizes, minus the trailing <c>Distinct()</c>. Binds an ordinary <c>$sum</c>/<c>$avg</c>/<c>$min</c>/
    /// <c>$max</c> accumulator directly over the selected field (no <c>$addToSet</c>/dedup — every element
    /// contributes, not just distinct ones).
    /// </summary>
    private static bool TryBindElementSelectedAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        if (call.Arguments.Count != 1)
            return false;

        // Min/Max are true open generics (<TSource>); Average/Sum's without-selector overloads are NOT
        // generic — same asymmetry TryBindDistinctAccumulator's own remarks explain. Count/LongCount are
        // deliberately excluded: a bare Count doesn't need the element's value at all, so EF has no reason to
        // wrap it in a Select the way it does for a reducing aggregate.
        var reduceOp = QueryableMethods.IsAverageWithoutSelector(call.Method) ? "$avg"
            : call.Method.IsGenericMethod && call.Method.GetGenericMethodDefinition() == QueryableMethods.MinWithoutSelector ? "$min"
            : call.Method.IsGenericMethod && call.Method.GetGenericMethodDefinition() == QueryableMethods.MaxWithoutSelector ? "$max"
            : QueryableMethods.IsSumWithoutSelector(call.Method) ? "$sum"
            : null;

        if (reduceOp is null)
            return false;

        // The source must be g.Select(elementSelector) directly (never wrapped in a further Distinct() — that
        // shape is TryBindDistinctAccumulator's, tried first by the caller).
        if (Unwrap(call.Arguments[0]) is not MethodCallExpression { Method.IsGenericMethod: true } selectCall
            || selectCall.Method.GetGenericMethodDefinition() != QueryableMethods.Select
            || selectCall.Arguments.Count != 2
            || !IsGroupingSource(selectCall.Arguments[0], groupingParameter))
            return false;

        if (selectCall.Arguments[1].UnwrapLambdaFromQuote() is not { } selector
            || !translator.TryTranslateValue(selector.Body, out var operand))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, reduceOp, operand);
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    /// <summary>
    /// EF-322 SP3: <c>g.Where(pred).Sum/Min/Max/Average(selector)</c> — a per-element FILTERED aggregate,
    /// reducing to <c>{"$&lt;op&gt;": {"$cond": [translatedPred, translatedOperand, "$$REMOVE"]}}</c>.
    /// <c>"$$REMOVE"</c> (empirically verified — see this plan's own "Verified design decision") makes
    /// Min/Max/Sum/Average treat a non-matching element as though it contributed nothing at all.
    /// </summary>
    private static bool TryBindFilteredAccumulator(
        MethodCallExpression call,
        string outputField,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? flattenRead)
    {
        accumulator = null;
        flattenRead = null;

        // EF-322 SP5: g.Where(pred).Count() / g.Where(pred).LongCount() — a filtered COUNT with NO selector.
        // Tried before the call.Arguments.Count != 2 gate below (which the Sum/Average/Min/Max-with-selector
        // arm requires): Count/LongCount without a predicate argument have call.Arguments.Count == 1 (just
        // the Where-wrapped source), never 2. Reduces to $sum: {$cond: [translatedPredicate, 1, 0]} — the
        // SAME shape SP3's direct g.Count(pred) arm (further down in TryBindAccumulator) already emits; the
        // only difference is where the predicate comes from (the Where call's own lambda here, vs. Count's
        // own argument there). 0, not $$REMOVE, matches a filtered COUNT's own correct semantics: an
        // unmatched element contributes 0 to the sum-of-1s either way.
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

        // The source must be g.Where(pred) — a Where call whose OWN source is the grouping parameter
        // directly. Both the Queryable and Enumerable forms are accepted — see this plan's own Global
        // Constraints note on why (a hand-written unit test lambda produces the Enumerable form).
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

        // Final-review fix: if every element in the group fails `pred`, $$REMOVE leaves the $min/$max/$avg
        // accumulator null server-side, and the native shaper would read that back as default(T) for a
        // non-nullable result type — silently wrong (both LINQ-to-objects and the driver-LINQ fallback throw
        // InvalidOperationException for an empty sequence instead). $sum is unaffected (0 for an empty/all-
        // excluded sequence is the CORRECT answer both in-memory and via the fallback). Scoped to a genuine
        // PER-ELEMENT predicate (isKeyOnlyCondition false) — a pure g.Key comparison shares the SAME value
        // across every element in a given group, so whether it's satisfied is really a property of the
        // GROUP as a whole, and the target shape this binds (GroupBy_constant_with_where_on_grouping_with_
        // aggregate_operators) relies on exactly that invariant to stay supported (its predicate is
        // trivially, provably true for every element of its only possible group).
        if (!isKeyOnlyCondition && op is "$min" or "$max" or "$avg" && IsNonNullableValueType(call.Method.ReturnType))
            return false;

        accumulator = new MongoGroupAccumulator(outputField, op,
            new MongoConditionalExpression(condition, operand,
                new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, operand.Type)));
        flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
        return true;
    }

    // True when `source` (the `this`/source argument of an Enumerable/Queryable aggregate) is the grouping
    // parameter `g`. EF Core lowers a grouped aggregate to the Queryable form over `g.AsQueryable()`
    // (e.g. Queryable.Count(g.AsQueryable())); a hand-authored Enumerable form passes `g` directly. Unwrap
    // Convert/ConvertChecked and a single AsQueryable/AsEnumerable wrapper, then require reference equality
    // with the grouping parameter. Anything else (a subquery, navigation, or a different collection) is not
    // the grouping source.
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

    // Strip redundant Convert/ConvertChecked wrappers (a projection member typed `object` boxes its value).
    // Delegates to the shared peeler — this used to be a fourth byte-identical copy of it.
    private static Expression Unwrap(Expression e)
        => e.RemoveConvert();

    /// <summary>
    /// Attempts to bind a scalar aggregate terminal operator (<c>Count</c>/<c>LongCount</c>/<c>Any</c>/
    /// <c>All</c>) applied directly to a BARE <c>GroupBy(key)</c> result — no intervening <c>Select</c> — e.g.
    /// <c>GroupBy(o =&gt; o.CustomerID).Count()</c>, <c>.Any(g =&gt; g.Count() &gt; 1)</c>,
    /// <c>.All(g =&gt; g.Count() &gt; 1)</c> (the "GroupBy_without_aggregate" family, EF-449). Finalizes
    /// <see cref="MongoSelectDefinition.Grouping"/> from <see cref="MongoSelectDefinition.PendingGroupKey"/> and
    /// installs a matching <see cref="MongoSelectDefinition.Cardinality"/> atomically via
    /// <see cref="MongoSelectDefinition.SetGroupedTerminalAggregate"/>. Called from
    /// <see cref="NativeCardinalityBinder.TryBindAggregate"/> BEFORE its general post-terminal guard — bare
    /// <c>GroupBy</c> already sets <see cref="MongoSelectDefinition.IsGroupBy"/> unconditionally, so that guard
    /// would otherwise always decline this shape.
    /// </summary>
    /// <remarks>
    /// Scoped narrowly to what the EF Core spec suite's "without_aggregate" family actually needs: a bare
    /// terminal with NO predicate (a plain "how many/any groups"), or a predicate that is a SINGLE comparison
    /// of one group-level aggregate (<c>g.Count()</c>, or <c>g.Sum/Min/Max/Average(x =&gt; x.Field)</c> — the
    /// same accumulator shapes <see cref="TryBindAccumulator"/> already recognizes for the Select-projection
    /// case) against a constant/parameter. A compound (<c>&amp;&amp;</c>/<c>||</c>) predicate declines — no
    /// reachable test shape needs it, and it keeps the <c>All</c> negation (a single De Morgan step) exact.
    /// </remarks>
    internal static bool TryBindGroupTerminalAggregate(
        MongoQueryExpression mongoQ, MongoAggregateOperator op, LambdaExpression? predicate, Type resultType)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts || select.Grouping != null)
            return false;

        // EF-322 fix round: a Skip/Take already recorded into PendingGroupPaging earlier in this chain
        // (e.g. GroupBy(key).Skip(1).Count()) must decline — this bare-terminal-aggregate path has no
        // mechanism to apply paging before the aggregate (there is no Select/flatten stage for
        // GroupPagingOps to run against here), so silently proceeding would drop the paging entirely and
        // aggregate over every group instead of the paged subset. See this fix round's report for the
        // reproducing probe query.
        if (select.PendingGroupPaging != null)
            return false;

        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        if (op is not (MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All))
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var accumulators = new List<MongoGroupAccumulator>();
        MongoExpression? matchPredicate = null;
        MongoGroupAccumulator? accumulator = null;
        MongoExpression? comparisonNode = null;

        if (predicate != null)
        {
            // All(pred) is never rewritten by EF (Where(pred).All() would change semantics — see below), so
            // its predicate always arrives here directly. Count(pred)/Any(pred), by contrast, are USUALLY
            // rewritten by EF's normalizer to Where(pred).Count()/.Any() — but not unconditionally (measured:
            // both shapes reach this method with a non-null predicate depending on query form), so both must
            // be handled.
            if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                    keyParts, isComposite, out accumulator, out comparisonNode))
                return false;
        }
        else if (op is MongoAggregateOperator.All)
        {
            // LINQ's All() has no parameterless overload — unreachable in practice, declined defensively.
            return false;
        }
        else if (select.PendingGroupPredicate is { } pending)
        {
            // EF-449: Count(pred)/Any(pred) normalized to Where(pred).Count()/.Any() — the Where already
            // recognized and stashed the group-level comparison (NativeSlotPopulator's Where carve-out +
            // TryBindGroupWherePredicate below); consume it here instead of re-parsing a (now null) predicate.
            (accumulator, comparisonNode) = pending;
        }

        select.PendingGroupPredicate = null; // one-shot: consumed above, or never set for a bare terminal.

        // EF-322 SP2: gate on comparisonNode, not accumulator — a bare KEY comparison (g.Key == "ALFKI") has
        // no accumulator at all, but still produces a valid $match predicate. Gating on accumulator alone
        // would silently DROP a key-only predicate's filter here once TryBindGroupPredicateComparison's
        // key-access arm exists — a correctness regression, not just a missed capability.
        if (comparisonNode != null)
        {
            if (accumulator != null)
                accumulators.Add(accumulator);

            if (op is MongoAggregateOperator.All)
            {
                // All(pred) ≡ no group fails pred. Match the EXACT COMPLEMENT, mirroring
                // NativeCardinalityBinder.TryBindAggregate's row-level All handling: presence of any
                // surviving group (after $group + this $match) means at least one group failed pred.
                // NOT MongoExpressionNegator.TryNegate: its public entry point gates on
                // IsQueryDialectRenderable, which a MongoElementRefExpression comparison (aggregation-
                // expression-only, like MongoOuterFieldExpression/a computed leaf) never satisfies — this
                // node is ALWAYS rendered via $expr, so negate directly with the same $eq/$ne-invert,
                // relational-$not-wrap rule the negator's own aggregation-context arm applies.
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
        return true;
    }

    /// <summary>
    /// Recognizes a <c>Where</c> composed DIRECTLY on a BARE <c>GroupBy(key)</c> result as EF Core's own
    /// normalization of <c>Any(pred)</c>/<c>Count(pred)</c>/<c>LongCount(pred)</c> into
    /// <c>Where(pred).Any()</c>/<c>.Count()</c>/<c>.LongCount()</c> — <paramref name="predicate"/>'s parameter
    /// is typed <c>IGrouping&lt;TKey,TElement&gt;</c>, never the root entity. Stashes the recognized
    /// group-level comparison on <see cref="MongoSelectDefinition.PendingGroupPredicate"/> for the terminal
    /// aggregate to consume (<see cref="TryBindGroupTerminalAggregate"/>). Called from
    /// <see cref="NativeSlotPopulator.PopulateNativeSlots"/>'s dedicated Where carve-out, BEFORE the general
    /// Where arm (which would otherwise resolve this predicate's member access against the wrong — entity —
    /// type). Returns <see langword="false"/> for anything outside <see cref="TryBindGroupPredicateComparison"/>'s
    /// scope, so the caller marks the query non-native.
    /// </summary>
    internal static bool TryBindGroupWherePredicate(MongoQueryExpression mongoQ, LambdaExpression predicate)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                keyParts, isComposite, out var accumulator, out var comparisonNode))
            return false;

        select.PendingGroupPredicate = (accumulator, comparisonNode);
        return true;
    }

    // Recognizes `body` as a single comparison of one group-level operand — a KEY access (g.Key / g.Key.Sub,
    // no accumulator needed) or a group-level aggregate (bound via the SAME TryBindAccumulator shapes the
    // Select-projection path uses, output field "__agg0") — against a constant/parameter, in EITHER operand
    // order. Returns the bound accumulator (null for a key-access comparison — EF-322 SP2) and the translated
    // comparison node (the group-level operand's field-ref on the left, in normalized — not necessarily
    // source — operator direction).
    private static bool TryBindGroupPredicateComparison(
        Expression body,
        ParameterExpression groupingParameter,
        MongoExpressionTranslator translator,
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

    // EF-322 SP2: one side of a group predicate comparison — either a KEY access (g.Key / g.Key.Sub;
    // accumulator stays null, the reference reads "_id"[.Name] directly, no $group accumulator needed at
    // all) or a group-level ACCUMULATOR call (g.Count()/g.Sum(...) etc., bound to the reserved output field
    // "__agg0"). A composite WHOLE-key read (bare g.Key over a >1-part key) still declines — TryGetKeyMemberPath's
    // own allowWholeKeyRead: true default already encodes "no single field to compare" for this; passed
    // through unchanged.
    //
    // Final review fix: a KEY match also resolves the matched key part's own IProperty (null for a
    // computed/literal/parameter key part, which has no backing property) so the CALLER can serialize the
    // comparison's OTHER operand (the constant/parameter side) through the SAME property serializer the key's
    // stored value itself uses — without it, MongoValueRenderer.RenderValue falls back to the generic
    // BsonValue.Create for that operand, which throws for a type it can't map directly (e.g. Guid), instead
    // of declining cleanly. A key part is already known-safe here — TryBindGroupKey's own
    // HasDefaultKeySerialization check (via TryTranslateValue's AllFieldsDefaultSerialized) already rejected
    // a value-converted/non-default-represented key at bind time.
    private static bool TryBindGroupSideOperand(
        Expression side,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
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

        // allowWholeKeyRead: false — a WHOLE composite key comparison (g.Key == new {...}) still declines,
        // same reasoning as the ordering carve-out in TryBindGroupProjection: the compiler-generated
        // anonymous-type constant has no single backing property to serialize the comparison's OTHER side
        // against (every OTHER key match here is a single sub-field, always resolvable to exactly one
        // MongoGroupingKeyPart). A SCALAR key is unaffected — allowWholeKeyRead only gates the composite case.
        if (TryGetKeyMemberPath(side, groupingParameter, keyParts, isComposite, out var keyPath, allowWholeKeyRead: false))
        {
            if (keyPath == null)
                return false; // bare g.Key over a composite key — no single field to compare

            // EF-322 SP7: a zero-part key (keyParts.Count == 0) resolves here too now (TryGetKeyMemberPath's
            // fix, this plan's Task 2) but has no single backing property — same as the composite case just
            // above it, for the same reason.
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

    // The non-accumulator side of a group predicate comparison: a captured literal or a query parameter.
    // Mirrors MongoExpressionTranslator's own private TranslateValue (not accessible from here) — this operand
    // is never a member access, so the full translator is unnecessary. `forSerialization` is the OTHER side's
    // matched key property (null for an accumulator comparison, or a computed/literal/parameter key) — see
    // TryBindGroupSideOperand's own remarks for why this is needed.
    private static bool TryTranslateComparisonConstant(
        Expression expr, IProperty? forSerialization, [NotNullWhen(true)] out MongoExpression? result)
    {
        expr = Unwrap(expr);
        switch (expr)
        {
            case ConstantExpression constant:
                result = new MongoConstantExpression(constant.Value, forSerialization);
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

    // Negates a group-predicate comparison built by TryBindGroupPredicateComparison — or, via
    // NativeCardinalityBinder.TryBindAggregate's isPostGroupTerminalAggregate arm, the analogous bare-
    // accumulator-alias comparison MongoExpressionTranslator.TranslateComparisonCore builds for
    // All(pred) composed after GroupBy(key).Select(scalar-aggregate). Always aggregation-expression-only (the
    // left operand is a MongoElementRefExpression, never query-dialect renderable), so this cannot reuse
    // MongoExpressionNegator's public TryNegate (which gates on IsQueryDialectRenderable) — it applies the SAME
    // rule as that negator's own private aggregation-context arm: $eq/$ne are inverted (they partition every
    // value); the four relational operators are $not-wrapped, never inverted (the general reason — they don't
    // partition a missing/null value — doesn't strictly apply to an accumulator output field, which is never
    // missing/null, but wrapping is exact regardless and keeps one rule instead of a second, narrower one to
    // maintain).
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
    /// <c>Distinct(projection)</c>: the terminal <c>Select</c> already populated <see cref="MongoSelectDefinition.Projection"/>
    /// with (alias -&gt; field-ref) pairs. Convert them into a key-only grouping (group by the projected
    /// value, zero accumulators) and replace the projections with a flatten that reads the value
    /// back out of <c>_id</c>. Returns <see langword="false"/> (→ fall back) if there is no native projection, or any key
    /// is not a default-serialized field ref (generic <c>_id</c> readback would diverge from DriverLinq).
    /// </summary>
    internal static bool TryBindDistinctFromProjection(MongoQueryExpression mongoQ)
    {
        var select = mongoQ.Select;
        // A projected SelectMany is itself a terminal (its UnwindSource is set): converting its Projection
        // into a degenerate $group here would leave UnwindSource set alongside the new Grouping, and the
        // lowerer's UnwindSource branch runs before its Grouping branch and returns early — silently dropping
        // the $group and emitting a flatten $project that reads "_id.<alias>" fields that were never grouped
        // into existence. Decline so this falls back to driver-LINQ (or hard-fails, for the reference form,
        // which has no driver-LINQ baseline) instead of building a pipeline that silently returns nulls.
        //
        // For a projected-operand set op (SetOperation.OperandsProjected == true), select.Projection is
        // operand-1's own projection — emitted before the set-op stage by the lowerer, not a trailing
        // post-set-op projection — so converting it into a degenerate $group here would corrupt operand-1's
        // pipeline. Declining makes TranslateDistinct fall back gracefully instead. This must stay narrowed
        // to OperandsProjected: true, not a blanket SetOperation != null: a whole-entity set op with a
        // trailing projection (OperandsProjected == false — e.g. Union(A,B).Select(p).Distinct()) has its
        // Projection applied after the set-op stage as a genuine trailing projection, which this method
        // converts to a $group safely and correctly — a documented native capability that must be preserved.
        //
        // EF-395: a bare projection (Select(o => o.Country).Distinct()) is now ADMITTED — this no longer
        // declines on select.IsBareProjection. Binding it clears Projection, installs a Grouping and flips
        // Route to NativeRoute.GroupBy; the mechanical hazard that used to make this unsafe was
        // MongoQueryExpression.ApplyProjection's alias-override lookup being gated on `Route ==
        // NativeRoute.Projection`, which reverted the bare body's alias to null once Route flipped, crashing
        // the shaper. That lookup now ALSO fires when Select.IsDistinct is set (see ApplyProjection) —
        // IsDistinct is set nowhere but here, immediately below, alongside the flatten that re-adds the exact
        // same alias(es) the override describes, so the override is provably still valid whenever IsDistinct
        // is true. Pinned by NativeBareProjectionTests.
        //
        // EF-TBD: no longer declines on select.HasPaging. A source-side OrderBy/Skip/Take composed before the
        // Distinct call is recorded in PipelineOps and is emitted by the lowerer before the $group unconditionally
        // (see MongoSelectLowerer's "6b" comment), so paging still restricts the correct input row set and a
        // pre-existing ordering is otherwise a no-op ahead of a dedup — mirrors the identical guard removal in
        // NativeGroupByBinder.TryBindGroupKey.
        if (select.Projection.Count == 0 || select.Grouping != null || select.Cardinality != null
            || select.UnwindSource != null || select.SetOperation is { OperandsProjected: true })
            return false;

        var keyParts = new List<MongoGroupingKeyPart>();
        var flatten = new List<MongoProjection>();
        foreach (var projection in select.Projection)
        {
            // A bare field ref needs the default-key-serialization guard (its readback has no backing
            // IProperty — see the method's own remarks). A COMPUTED key part (e.g. c.CustomerID + c.City)
            // has no IProperty at all, so no value converter/BsonRepresentation can apply to it; it is
            // exactly as safe to read back via MongoElementRefExpression as any other computed projection
            // member already is on the ordinary (non-Distinct) projection path — same renderer, same
            // generic-CLR-type readback, just sourced from "_id.<alias>" instead of a top-level field.
            if (projection.Expression is MongoFieldExpression field && !HasDefaultKeySerialization(field.Property))
                return false;

            keyParts.Add(new MongoGroupingKeyPart(projection.Alias, projection.Expression));
            flatten.Add(new MongoProjection(projection.Alias,
                new MongoElementRefExpression("_id." + projection.Alias, projection.Expression.Type)));
        }

        select.ClearProjections();
        select.Grouping = new MongoGrouping(keyParts, []);
        // Record Distinct provenance (not IsGroupBy) so the post-group operator guards in NativeSlotPopulator
        // and NativeCardinalityBinder — both keyed on IsGroupBy || IsDistinct — also cover an operator applied
        // after this Distinct. A separate flag is deliberate: the QMTEV's join-decline path treats Distinct+Join
        // as a graceful fallback (driver-LINQ joins a flat row set correctly), whereas a genuine GroupBy+Join
        // is a hard decline (driver-LINQ returns silently-empty joins). See IsDistinct's doc on
        // MongoSelectDefinition and TranslateJoinCore.
        select.IsDistinct = true;
        foreach (var f in flatten)
            select.AddProjection(f);
        return true;
    }

    /// <summary>
    /// Resolves an <c>OrderBy</c>/<c>ThenBy</c> key selector composed AFTER a projected <c>Distinct()</c>
    /// against the Distinct's OWN flattened output schema — its grouping key part ALIASES — rather than the
    /// root entity. A projected Distinct's anonymous/DTO member names are independent of, but can coincide
    /// with, real entity property names (<c>Select(o => new { Country = o.City }).Distinct()</c> names its
    /// member "Country" while sourcing it from <c>City</c>), so resolving by entity-property name — what the
    /// ordinary <see cref="MongoExpressionTranslator"/> does — would silently sort by the WRONG field
    /// (the entity's real <c>Country</c>, never touched by this query). Only a bare single-hop member access
    /// on the key selector's OWN parameter (identity, not name — same discipline as
    /// <see cref="MongoExpressionTranslator.SelfParam"/> elsewhere in this file), naming one of the Distinct's
    /// key parts, is accepted. A key part backed by a real <see cref="MongoFieldExpression"/> resolves as one;
    /// a COMPUTED key part (e.g. <c>A = c.CustomerID + c.City</c>, EF-322 gap-2 — no backing
    /// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>) resolves against its own flattened
    /// output alias via a <see cref="MongoElementRefExpression"/> instead, mirroring
    /// <see cref="MongoExpressionTranslator.TryResolveDistinctAliasComputedField"/> on the Where side. Anything
    /// else — a member not among the key parts, or a genuinely COMPUTED expression over the key selector's own
    /// parameter (e.g. EF-322 gap-3's <c>x.IndexOf(term)</c> over a bare-scalar Distinct) — declines so the
    /// caller (<c>NativeSlotPopulator.PopulateSortSlot</c>) falls through to the general
    /// <see cref="MongoExpressionTranslator.DistinctAliasScope"/>-scoped translator instead, which that gap-3
    /// shape needs (a computed expression is not "a member naming a key part").
    /// </summary>
    internal static bool TryResolveDistinctOrderingKey(
        MongoGrouping grouping, ParameterExpression selfParam, Expression keySelectorBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        // A bare-scalar Distinct's own OrderBy key selector is necessarily the IDENTITY function
        // (Select(o => o.Country).Distinct().OrderBy(c => c)): the projected result IS the scalar directly,
        // so there is no member to access at all — the key selector body is exactly the parameter itself.
        // Matches the sole key part unconditionally (a bare-scalar Distinct always has exactly one).
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
    /// A scalar aggregate (Sum/Min/Max/Average) terminating DIRECTLY on a bare-scalar-projected
    /// <c>Distinct()</c> — no intervening Select — e.g. <c>Select(o => o.OrderID).Distinct().Max()</c>
    /// (EF-453). Mirrors <see cref="TryBindGroupTerminalAggregate"/>'s role for a bare <c>GroupBy(key)</c>,
    /// but the aggregate's operand is NOT translated against the entity: unlike
    /// <c>GroupBy(key).Select(g =&gt; g.Sum(x =&gt; x.Field))</c>, a bare <c>Distinct().Max()</c> carries no
    /// selector lambda at all — <c>Max()</c> reduces whatever <c>Distinct()</c> already flattened. The
    /// degenerate group <see cref="TryBindDistinctFromProjection"/> installs has zero accumulators and
    /// exactly one key part (the projected value itself), so the operand is simply that key's own flattened
    /// output alias, read back directly.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to Sum/Min/Max/Average — the only aggregates for which a bare, no-selector call
    /// (<c>Max()</c>, never <c>Max(x =&gt; ...)</c>) is even reachable, which C#'s <c>IComparable</c>
    /// constraint on the parameterless overloads restricts to a genuinely scalar-projected <c>Distinct()</c>.
    /// <c>Count()</c>/<c>LongCount()</c> take no selector at all and so are ALSO reachable over a
    /// multi-member <c>new {...}</c>-projected <c>Distinct()</c> (e.g.
    /// <c>Select(o =&gt; new { o.Country }).Distinct().Count()</c>, pinned by
    /// <c>NativeDistinctTests.Distinct_then_Count_throws_under_native_only</c> and
    /// <c>NorthwindAggregateOperatorsQueryMongoTest.Select_Select_Distinct_Count</c> as an intentionally
    /// out-of-scope fallback) — admitting them here would silently flip those pinned shapes to native too,
    /// which is a real (if likely safe) capability change this ticket does not attempt.
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

        // Sum/Min/Max/Average never take a predicate, and a selector here would mean reducing some OTHER
        // value than the one Distinct already flattened (e.g. a computed re-projection) — this binder does
        // not attempt that; decline so it falls back rather than silently aggregating the wrong value.
        if (selector != null)
            return false;

        var operand = new MongoElementRefExpression(keyPart.Name, keyPart.FieldRef.Type);

        NativeCardinalityBinder.BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);

        var cardinality = MongoCardinality.ForAggregate(
            op, operand, emptyBehavior, emptyValue, resultType, presenceOnly: false, presentValue: null);
        select.SetGroupedTerminalAggregate(grouping, cardinality, postGroupPredicate: null);
        return true;
    }
}
