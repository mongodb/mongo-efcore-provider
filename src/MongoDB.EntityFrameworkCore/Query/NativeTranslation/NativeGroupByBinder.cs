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
    /// with members (an anonymous type) is a composite key whose parts each carry the member name. Every part
    /// must be a plain member access translatable to a field-ref; anything else (a computed key such as
    /// <c>x =&gt; x.Date.Year</c>) returns <see langword="false"/> and leaves the pending state unset.
    /// </summary>
    internal static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)
    {
        var select = mongoQ.Select;

        // Post-group paging / ordering on top of a pre-existing select is out of scope; fall back.
        if (select.HasPaging || select.HasOrdering)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var parts = new List<MongoGroupingKeyPart>();

        switch (keySelector.Body)
        {
            case NewExpression { Members: { Count: > 0 } members } newExpr:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    if (newExpr.Arguments[i] is not MemberExpression
                        || !translator.TryTranslateField(newExpr.Arguments[i], out var field)
                        || !HasDefaultKeySerialization(field.Property))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(members[i].Name, field));
                }

                break;

            case MemberExpression:
                if (!translator.TryTranslateField(keySelector.Body, out var scalarField)
                    || !HasDefaultKeySerialization(scalarField.Property))
                    return false;
                parts.Add(new MongoGroupingKeyPart(null, scalarField));
                break;

            default:
                return false; // computed / unsupported key shape
        }

        select.PendingGroupKey = parts;
        return true;
    }

    // A grouping key becomes the group's _id and is read back through a GENERIC CLR-type serializer by the
    // grouped-row shaper (the flattened _id has no backing IProperty, so MongoProjectionBindingRemovingExpression
    // Visitor takes the raw CreateGetElementValue path). That generic read only reproduces the property's
    // materialized value when the property serializes with the default/identity representation. A property with
    // a value converter (its stored form is the provider value, needing reverse conversion) or a non-default
    // BsonRepresentation (e.g. enum-as-string, Guid-as-string) would either throw at materialization or return
    // the raw stored value — diverging from the driver-LINQ path. Reject such keys so the query falls back
    // (and throws only under NativeOnly), preserving the Native == DriverLinq invariant. The accumulator
    // OPERAND is deliberately NOT checked here: Sum/Min/Max over a represented field is the pre-existing,
    // documented EF-337 shared caveat (Native and DriverLinq are wrong the same way — no divergence).
    // Internal (not private) — also shared by the QMTEV's TranslateOfType discriminator guard, which rejects a
    // value-converted / non-default-BsonRepresentation discriminator for the identical generic-readback reason.
    internal static bool HasDefaultKeySerialization(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() == null;

    /// <summary>
    /// Parses the <c>Select</c> result selector against the pending key from <see cref="TryBindGroupKey"/>,
    /// finalizing <see cref="MongoSelectDefinition.Grouping"/>. The body must be a <see cref="NewExpression"/>
    /// (anonymous type) or <see cref="MemberInitExpression"/> (DTO) where every member is either a grouping-key
    /// access (<c>g.Key</c> / <c>g.Key.&lt;Sub&gt;</c>) or a supported aggregate over the grouping
    /// (<c>g.Count()</c>/<c>g.LongCount()</c> → <c>$sum:1</c>; <c>g.Sum/Min/Max/Average(x =&gt; x.Field)</c>
    /// over a plain member selector). Returns <see langword="false"/> for any other shape, or when no
    /// accumulator is produced, so the caller falls back.
    /// </summary>
    /// <param name="mongoQ">The query whose <see cref="MongoSelectDefinition"/> is being populated.</param>
    /// <param name="resultSelector">The <c>Select</c> result selector lambda over the grouping.</param>
    internal static bool TryBindGroupProjection(
        MongoQueryExpression mongoQ, LambdaExpression resultSelector)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        if (!TryGetProjectionBindings(resultSelector.Body, out var bindings))
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var groupingParameter = resultSelector.Parameters[0];
        var accumulators = new List<MongoGroupAccumulator>();
        var isComposite = keyParts.Count > 1 || keyParts[0].Name != null;

        // Flatten projection: each result member maps to a top-level output alias read back by the DOM
        // shaper. Key members read from the group _id (scalar → "_id", composite sub → "_id.<Name>");
        // accumulator members read from their own top-level output field. Emitted as a trailing $project
        // after the $group (MongoSelectLowerer) so the shaper never needs a nested-_id read.
        var flatten = new List<MongoProjection>();

        foreach (var (memberName, valueExpr) in bindings)
        {
            if (TryGetKeyMemberPath(valueExpr, groupingParameter, keyParts, isComposite, out var keyPath))
            {
                if (keyPath == null)
                    return false; // bare g.Key over a composite key cannot flatten to a single field

                flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(keyPath, Unwrap(valueExpr).Type)));
                continue;
            }

            if (!TryBindAccumulator(valueExpr, memberName, groupingParameter, translator, out var acc))
                return false;
            accumulators.Add(acc);
            flatten.Add(new MongoProjection(memberName, new MongoElementRefExpression(acc.OutputField, Unwrap(valueExpr).Type)));
        }

        if (accumulators.Count == 0)
            return false; // pure key regroup with no aggregate — unsupported here, falls back

        select.Grouping = new MongoGrouping(keyParts, accumulators);
        foreach (var projection in flatten)
            select.AddProjection(projection);
        return true;
    }

    // Classifies a result-member value as a grouping-key access and, if so, yields the group-output element
    // path it reads from. Returns true for a key access; `path` is null only for the unsupported bare-g.Key
    // over a composite key (whole anonymous key object — cannot flatten to one field). Returns false when the
    // value is not a key access (i.e. it is an accumulator).
    private static bool TryGetKeyMemberPath(
        Expression expr,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        out string? path)
    {
        path = null;
        expr = Unwrap(expr);

        if (expr is not MemberExpression member)
            return false;

        // g.Key — the whole key. Only flattenable when the key is scalar (single unnamed part).
        if (member.Member.Name == "Key" && member.Expression == groupingParameter)
        {
            path = isComposite ? null : "_id";
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

    // Flatten a NewExpression (anonymous type) or MemberInitExpression (DTO) into (memberName, valueExpr) pairs.
    private static bool TryGetProjectionBindings(
        Expression body, [NotNullWhen(true)] out List<(string MemberName, Expression Value)>? bindings)
    {
        bindings = null;

        switch (body)
        {
            case NewExpression { Members: { } members } newExpr:
                bindings = [];
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                    bindings.Add((members[i].Name, newExpr.Arguments[i]));
                return true;

            case MemberInitExpression memberInit:
                bindings = [];
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false; // list/nested member bindings are not supported
                    bindings.Add((assignment.Member.Name, assignment.Expression));
                }

                // The MemberInit's own NewExpression must be a parameterless ctor (no positional args to bind).
                return memberInit.NewExpression.Arguments.Count == 0;

            default:
                return false;
        }
    }

    // Match g.Count()/g.LongCount() → ("$sum", null); g.Sum/Average/Min/Max(x => x.Field) over a plain member
    // selector → the matching operator + field-ref operand. Any other shape (computed operand, unknown method)
    // returns false. The aggregate's SOURCE (call.Arguments[0]) must be the grouping parameter itself — an
    // aggregate whose source is a DIFFERENT sequence (a correlated cross-collection subquery such as
    // Customers.Where(c => c.CustomerID == g.Key).Count(), a navigation, another collection) is NOT a grouped
    // accumulator and must NOT be bound to a $group accumulator (that would silently drop the real subquery
    // computation and return the group's row count instead). Reject it so the projection falls back to
    // driver-LINQ, preserving the Native == DriverLinq invariant.
    private static bool TryBindAccumulator(
        Expression expr,
        string outputField,
        ParameterExpression groupingParameter,
        MongoExpressionTranslator translator,
        [NotNullWhen(true)] out MongoGroupAccumulator? accumulator)
    {
        accumulator = null;

        // The $group document already carries the grouping key under the reserved "_id" field
        // (MongoPipelineFactory.RenderKeyedGroup). An accumulator whose output field is literally "_id"
        // (e.g. Select(g => new { _id = g.Count() })) would add a SECOND "_id" element to that document → a
        // BsonDocument duplicate-key throw at pipeline build, which is an unhandled crash rather than a clean
        // fallback. Reject it so the shape falls back to driver-LINQ (and throws only under NativeOnly). This
        // is scoped to accumulators: a KEY member projected to an "_id" alias reads the group's own "_id"
        // back and does NOT collide (that path never reaches here — it is handled as a key member).
        if (outputField == GroupIdFieldName)
            return false;

        if (Unwrap(expr) is not MethodCallExpression { Method.IsGenericMethod: true } call
            || call.Arguments.Count == 0
            || !IsGroupingSource(call.Arguments[0], groupingParameter))
            return false;

        var definition = call.Method.GetGenericMethodDefinition();

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

        // The selector is a bare lambda (Enumerable form) or a quoted lambda (Queryable form).
        if (call.Arguments[1].UnwrapLambdaFromQuote() is not { Body: MemberExpression } selector
            || !translator.TryTranslateField(selector.Body, out var operand))
            return false; // computed / non-member selector — fall back

        accumulator = new MongoGroupAccumulator(outputField, op, operand);
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
    private static Expression Unwrap(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            ? Unwrap(u.Operand)
            : e;

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
        // lowerer's UnwindSource branch runs BEFORE its Grouping branch and returns early — silently dropping
        // the $group and emitting a flatten $project that reads "_id.<alias>" fields that were never grouped
        // into existence (EF-347 slice 5 fix: silent-null Distinct-after-SelectMany). Decline so this falls
        // back to driver-LINQ (or hard-fails, for the reference form, which has no driver-LINQ baseline)
        // instead of building a pipeline that silently returns nulls.
        if (select.Projection.Count == 0 || select.Grouping != null || select.Cardinality != null || select.HasPaging
            || select.UnwindSource != null)
            return false;

        var keyParts = new List<MongoGroupingKeyPart>();
        var flatten = new List<MongoProjection>();
        foreach (var projection in select.Projection)
        {
            if (projection.Expression is not MongoFieldExpression field || !HasDefaultKeySerialization(field.Property))
                return false;
            keyParts.Add(new MongoGroupingKeyPart(projection.Alias, field));
            flatten.Add(new MongoProjection(projection.Alias,
                new MongoElementRefExpression("_id." + projection.Alias, field.Type)));
        }

        select.ClearProjections();
        select.Grouping = new MongoGrouping(keyParts, []);
        // Record DISTINCT provenance (NOT IsGroupBy) so the post-group operator guards in NativeSlotPopulator
        // (slot ops) and NativeCardinalityBinder (aggregates/reducers) — both keyed on IsGroupBy || IsDistinct
        // — also cover an operator applied AFTER this Distinct (e.g. a Where whose member name happens to
        // collide with a real entity property, which would otherwise resolve against the entity and be hoisted
        // to a pre-group $match). A separate flag (not IsGroupBy) is deliberate: the QMTEV's join-decline path
        // must treat Distinct+Join as a GRACEFUL fallback (driver-LINQ joins a flat row set correctly), whereas
        // a genuine GroupBy+Join is a HARD decline (driver-LINQ returns silently-empty joins). See IsDistinct's
        // doc on MongoSelectDefinition and TranslateJoinCore.
        select.IsDistinct = true;
        foreach (var f in flatten)
            select.AddProjection(f);
        return true;
    }
}
