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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> predicate to the
/// MongoDB <c>$match</c>-filter body <see cref="BsonValue"/>
/// (e.g. <c>{ Age: { $gt: 21 } }</c> — without the outer <c>{ $match: … }</c> wrapper,
/// which is the stage-walker's responsibility).
/// </summary>
/// <remarks>
/// <para>
/// This class is <em>pure</em>: it has no dependency on <c>IEntityType</c> or
/// <c>QueryContext</c>. All parity guards (nullable-equality rejection, numeric-cast
/// rejection, etc.) were applied by <see cref="MongoExpressionTranslator"/> upstream;
/// the renderer simply emits BSON.
/// </para>
/// <para>
/// <see cref="MongoConstantExpression"/> values are serialized inline using the
/// <see cref="IProperty"/> carried inside the node, and baked into the returned template.
/// <see cref="MongoParameterExpression"/> sites are recorded as placeholder sentinels in
/// the supplied <see cref="PlaceholderTable"/> for per-execution substitution by the pipeline
/// factory (<see cref="MongoPipelineFactory.Build"/>).
/// </para>
/// </remarks>
internal sealed class MongoQueryLanguageRenderer
{
    /// <summary>
    /// Renders <paramref name="predicate"/> to a <c>$match</c>-filter body.
    /// </summary>
    /// <param name="predicate">
    /// The root <see cref="MongoExpression"/> to render. Must be a predicate-shaped node
    /// (i.e. its runtime type must be <see cref="bool"/>).
    /// </param>
    /// <param name="placeholders">
    /// Receives one entry per <see cref="MongoParameterExpression"/> encountered.
    /// Each entry's corresponding sentinel is embedded in the returned <see cref="BsonValue"/>.
    /// </param>
    /// <returns>
    /// A <see cref="BsonDocument"/> representing the filter body, suitable for use as the
    /// value of a <c>$match</c> pipeline stage document.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type not handled by this renderer (defensive; should not happen
    /// for predicates that passed the translator's acceptance set).
    /// </exception>
    public BsonValue Render(MongoExpression predicate, PlaceholderTable placeholders)
        => RenderNode(predicate, placeholders);

    // ------------------------------------------------------------------
    // Core dispatch
    // ------------------------------------------------------------------

    private BsonValue RenderNode(MongoExpression node, PlaceholderTable placeholders)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
                => CombineAnd((BsonDocument)RenderNode(a.Left, placeholders), (BsonDocument)RenderNode(a.Right, placeholders)),
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
                => CombineOr((BsonDocument)RenderNode(o.Left, placeholders), (BsonDocument)RenderNode(o.Right, placeholders)),
            MongoBinaryExpression comparison when IsQueryNativeComparison(comparison)
                => RenderComparison(comparison, placeholders),
            MongoUnaryExpression unary => RenderUnary(unary, placeholders),
            MongoFieldExpression field => RenderBareField(field, placeholders),
            MongoInExpression inExpr => RenderIn(inExpr, placeholders),
            MongoRegexExpression regex => RenderRegex(regex, placeholders),
            MongoElemMatchExpression elemMatch => RenderElemMatch(elemMatch, placeholders),
            _ => RenderAsExpr(node, placeholders)
        };

    // ------------------------------------------------------------------
    // Query-native classification: comparisons are query-native only when the
    // left side is a bare field and the right side is a value (constant/parameter).
    // Field-to-field and arithmetic operands have no query-dialect form and must
    // be delegated to the $expr (aggregation-expression) renderer.
    // ------------------------------------------------------------------

    // Widened from private to internal so MongoExpressionNegator can share the ONE definition of
    // "query-native" rather than duplicating it — the negator must decline any comparison this returns false
    // for, because such a node has no query-dialect complement (see MongoExpressionNegator.TryNegate).
    internal static bool IsQueryNativeComparison(MongoBinaryExpression b)
        => b.Left is MongoFieldExpression && b.Right is MongoConstantExpression or MongoParameterExpression;

    private BsonDocument RenderAsExpr(MongoExpression node, PlaceholderTable placeholders)
        => new BsonDocument("$expr", MongoAggregationExpressionRenderer.Render(node, placeholders));

    private BsonDocument RenderComparison(MongoBinaryExpression binary, PlaceholderTable placeholders)
    {
        // MongoExpressionTranslator always places the MongoFieldExpression on the Left
        // with the operator already mirrored when necessary (see TranslateComparison).
        if (binary.Left is not MongoFieldExpression field)
            throw new NativeTranslationNotSupportedException(
                $"Expected MongoFieldExpression on the left side of a comparison; got '{binary.Left.GetType().Name}'.");

        var elementName = field.ElementName;
        var value = MongoValueRenderer.RenderValue(binary.Right, placeholders);

        var op = binary.Operator switch
        {
            MongoBinaryOperator.Equal => null,              // bare { field: value }
            MongoBinaryOperator.NotEqual => "$ne",
            MongoBinaryOperator.LessThan => "$lt",
            MongoBinaryOperator.LessThanOrEqual => "$lte",
            MongoBinaryOperator.GreaterThan => "$gt",
            MongoBinaryOperator.GreaterThanOrEqual => "$gte",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported comparison operator '{binary.Operator}'.")
        };

        return op is null
            ? new BsonDocument(elementName, value)
            : new BsonDocument(elementName, new BsonDocument(op, value));
    }

    // ------------------------------------------------------------------
    // Unary nodes (Not)
    // ------------------------------------------------------------------

    private BsonDocument RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException(
                $"Unsupported unary operator '{unary.Operator}'.");

        // !<query-native comparison> → { field: { $not: { <op>: value } } }.
        //
        // $not over an OPERATOR DOCUMENT is the exact set complement of that operator document — including
        // documents where the field is missing or explicitly null. That exactness is why
        // MongoExpressionNegator $not-wraps the four relational operators instead of inverting them:
        // neither { $gt: 5 } nor { $lte: 5 } matches a missing field, so the pair does NOT partition the
        // value space and an inversion would silently mis-answer All() for such a document.
        if (unary.Operand is MongoBinaryExpression comparison && IsQueryNativeComparison(comparison))
        {
            // Reuse RenderComparison so element naming and value serialization are identical to the
            // un-negated form (a parameter still records a placeholder in the shared table).
            var element = RenderComparison(comparison, placeholders).GetElement(0);

            // RenderComparison emits Equal as a BARE { field: value }; every other operator emits
            // { field: { $op: value } }. Only the latter is already an operator document — check for a
            // leading '$' rather than assuming.
            //
            // The reachable document-valued case here is NOT "equality against a document-valued property"
            // (the translator only ever hands this a mapped SCALAR IProperty leaf, so that input never
            // occurs) — it is PlaceholderTable's parameter sentinel, { __mongoef_param__: N }, which
            // RenderComparison's own value rendering produces whenever the compared value is a captured
            // local / EF query parameter rather than a constant (e.g. !(x.A == capturedLocal)). That
            // sentinel key is deliberately NOT '$'-prefixed, so the check above correctly treats it as a
            // bare value and wraps it in { $eq: … } rather than mistaking it for an already-built operator
            // document. This wrap is therefore correct only because PlaceholderTable.SentinelKey never
            // starts with '$' — see PlaceholderTable.SentinelKey's own doc comment for that invariant. If it
            // ever did, this branch would skip the $eq wrap for a parameterized equality and emit
            // { field: { $not: { __mongoef_param__: N } } } — the illegal bare-value-under-$not form below.
            //
            // THIS WRAP IS MANDATORY, NOT DEFENSIVE (spike-measured): { field: { $not: <bareValue> } } is a
            // HARD SERVER ERROR — "$not argument must be a regex or an object". It is reachable in practice
            // through !(x.A == 1), which EF does NOT normalize away. Emitting the bare form would fail every
            // such query at execution time, in every mode.
            var body = element.Value is BsonDocument candidate
                && candidate.ElementCount > 0
                && candidate.GetElement(0).Name.StartsWith('$')
                    ? candidate
                    : new BsonDocument("$eq", element.Value);

            return new BsonDocument(element.Name, new BsonDocument("$not", body));
        }

        if (unary.Operand is not MongoFieldExpression field)
            throw new NativeTranslationNotSupportedException(
                "MongoQueryLanguageRenderer only supports Not over a MongoFieldExpression or a query-native comparison.");

        // !boolProperty → { field: { $ne: true } }
        // (Matches driver-LINQ rendering; also matches missing/null-field semantics.)
        var trueValue = MongoValueRenderer.RenderValue(
            new MongoConstantExpression(true, field.Property), placeholders);
        return new BsonDocument(field.ElementName, new BsonDocument("$ne", trueValue));
    }

    // ------------------------------------------------------------------
    // Bare boolean field (used as a top-level predicate)
    // ------------------------------------------------------------------

    private BsonDocument RenderBareField(MongoFieldExpression field, PlaceholderTable placeholders)
    {
        // A bare bool property used as a predicate → { field: true }
        var trueValue = MongoValueRenderer.RenderValue(
            new MongoConstantExpression(true, field.Property), placeholders);
        return new BsonDocument(field.ElementName, trueValue);
    }

    // ------------------------------------------------------------------
    // Collection-membership ($in / $nin)
    // ------------------------------------------------------------------

    private BsonDocument RenderIn(MongoInExpression inExpr, PlaceholderTable placeholders)
    {
        var op = inExpr.Negated ? "$nin" : "$in";
        var array = RenderInValues(inExpr.Values, placeholders);
        return new BsonDocument(inExpr.Field.ElementName, new BsonDocument(op, array));
    }

    private BsonValue RenderInValues(MongoExpression values, PlaceholderTable placeholders)
    {
        switch (values)
        {
            case MongoConstantExpression { Value: System.Collections.IEnumerable items } constant:
            {
                var array = new BsonArray();
                foreach (var item in items)
                    array.Add(MongoValueRenderer.RenderValue(
                        new MongoConstantExpression(item, constant.ForSerialization!), placeholders));
                return array;
            }
            case MongoParameterExpression parameter:
            {
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization!);
                return placeholders.CreateArrayPlaceholder(parameter.Name, info.Serializer);
            }
            default:
                throw new NativeTranslationNotSupportedException("Unsupported $in values node.");
        }
    }

    // ------------------------------------------------------------------
    // String StartsWith/EndsWith/Contains ($regularExpression)
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a <see cref="MongoRegexExpression"/> to a <c>$regularExpression</c> filter, matching the
    /// shape the driver-LINQ v3 provider emits for <c>string.StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>:
    /// <c>{ field: { $regularExpression: { pattern: "...", options: "s" } } }</c> (negated via an
    /// enclosing <c>$not</c>). Only a constant search term is baked into a native pattern; a parameterized
    /// term is not supported here and must fall back to driver-LINQ.
    /// </summary>
    private BsonDocument RenderRegex(MongoRegexExpression regex, PlaceholderTable placeholders)
    {
        if (regex.Term is not MongoConstantExpression { Value: string literal })
            throw new NativeTranslationNotSupportedException(
                "Only constant regex terms are natively representable; parameterized string.StartsWith/EndsWith/Contains falls back to driver-LINQ.");

        var escaped = System.Text.RegularExpressions.Regex.Escape(literal);
        var pattern = regex.Kind switch
        {
            MongoRegexKind.StartsWith => "^" + escaped,
            MongoRegexKind.EndsWith => escaped + "$",
            MongoRegexKind.Contains => escaped,
            _ => throw new NativeTranslationNotSupportedException($"Unsupported regex kind '{regex.Kind}'.")
        };

        // Matches the driver-LINQ v3 rendering exactly: a BsonRegularExpression value (canonical extended
        // JSON: { $regularExpression: { pattern, options } }) with options "s" (dotall) — captured
        // empirically by observing the translation under MongoQueryMode.DriverLinq.
        BsonValue body = new BsonRegularExpression(pattern, "s");

        return regex.Negated
            ? new BsonDocument(regex.Field.ElementName, new BsonDocument("$not", body))
            : new BsonDocument(regex.Field.ElementName, body);
    }

    // ------------------------------------------------------------------
    // Existential quantifier over an embedded array ($elemMatch)
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a <see cref="MongoElemMatchExpression"/>.
    /// <para>
    /// With an element predicate: <c>{ path: { $elemMatch: &lt;child&gt; } }</c>, negated as
    /// <c>{ path: { $not: { $elemMatch: &lt;child&gt; } } }</c>. The child goes through the same
    /// <see cref="RenderNode"/> dispatch and its field names stay ELEMENT-RELATIVE — they are deliberately
    /// not prefixed with the array path, which is exactly what <c>$elemMatch</c> expects. Multi-condition
    /// children merge into one document via <see cref="CombineAnd"/>, so all conditions must hold for the
    /// SAME element.
    /// </para>
    /// <para>
    /// Without one (bare <c>Any()</c>): <c>{ "path.0": { $exists: true } }</c> — index-usable, and true for
    /// exactly those documents whose array has at least one element. A missing field and an empty array both
    /// correctly yield false, whereas <c>{ path: { $ne: [] } }</c> would wrongly match a missing field.
    /// Negated: <c>$exists: false</c>.
    /// </para>
    /// <para>
    /// The child is guaranteed to have a query-dialect rendering because
    /// <see cref="IsQueryDialectRenderable"/> gates node construction in
    /// <c>MongoExpressionTranslator</c> — <c>$expr</c> is not usable inside <c>$elemMatch</c>.
    /// </para>
    /// </summary>
    private BsonDocument RenderElemMatch(MongoElemMatchExpression elemMatch, PlaceholderTable placeholders)
    {
        if (elemMatch.ElementPredicate is null)
            return new BsonDocument(
                elemMatch.ArrayPath + ".0", new BsonDocument("$exists", !elemMatch.Negated));

        var body = new BsonDocument(
            "$elemMatch", (BsonDocument)RenderNode(elemMatch.ElementPredicate, placeholders));

        return elemMatch.Negated
            ? new BsonDocument(elemMatch.ArrayPath, new BsonDocument("$not", body))
            : new BsonDocument(elemMatch.ArrayPath, body);
    }

    // ------------------------------------------------------------------
    // Query-dialect renderability — MUST STAY IN SYNC WITH RenderNode ABOVE
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns whether <paramref name="node"/> has a QUERY-dialect rendering: whether
    /// <see cref="RenderNode"/> would render it without falling through to <see cref="RenderAsExpr"/> (which
    /// emits <c>$expr</c>, the aggregation dialect) and without throwing.
    /// </summary>
    /// <remarks>
    /// Used by <c>MongoExpressionTranslator</c> to decline an <c>$elemMatch</c> whose element predicate has
    /// no query-dialect form. This is a <b>correctness</b> gate, not an indexing preference:
    /// <c>$expr</c> inside <c>$elemMatch</c> is a hard server error — <c>Command find failed: $expr can only
    /// be applied to the top-level document</c> — so a child that slipped through to the <c>$expr</c>
    /// catch-all would make the whole query throw at execution time, under <c>Native</c> as well as
    /// <c>NativeOnly</c>. Declining at translate time falls the query back to driver-LINQ instead.
    /// <b>This method and <see cref="RenderNode"/> must be changed together:</b> a node this method admits
    /// but <see cref="RenderNode"/> sends to <c>$expr</c> (or throws on) becomes exactly that runtime
    /// failure.
    /// </remarks>
    public static bool IsQueryDialectRenderable(MongoExpression node)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
                => IsQueryDialectRenderable(a.Left) && IsQueryDialectRenderable(a.Right),
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
                => IsQueryDialectRenderable(o.Left) && IsQueryDialectRenderable(o.Right),
            MongoBinaryExpression comparison => IsQueryNativeComparison(comparison),
            // RenderUnary supports Not over a bare field, and over a QUERY-NATIVE comparison; it throws for
            // anything else (e.g. Not over a conjunction, or over a field-to-field comparison).
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoBinaryExpression cmp }
                => IsQueryNativeComparison(cmp),
            MongoFieldExpression => true,
            // RenderInValues throws for any values node other than a constant enumerable or a parameter.
            MongoInExpression inExpr
                => inExpr.Values is MongoConstantExpression { Value: System.Collections.IEnumerable }
                    or MongoParameterExpression,
            // RenderRegex throws for a parameterized term — only a constant is baked into a pattern.
            MongoRegexExpression { Term: MongoConstantExpression { Value: string } } => true,
            MongoElemMatchExpression { ElementPredicate: null } => true,
            MongoElemMatchExpression elemMatch => IsQueryDialectRenderable(elemMatch.ElementPredicate),
            _ => false
        };

    // ------------------------------------------------------------------
    // AND / OR combining helpers (ported verbatim from the spike MongoPredicateTranslator)
    // ------------------------------------------------------------------

    /// <summary>
    /// Combines two filter documents with AND, merging fields into a single document when all top-level
    /// keys are distinct and non-operator. Falls back to an explicit <c>$and</c> array when keys collide
    /// and operator sub-documents cannot be merged (e.g. two <c>$gt</c> on the same field), or when
    /// either document contains multiple elements or an operator key at the top level.
    /// Nested <c>$and</c> operands are flattened so chained predicates do not nest redundantly.
    /// Ported verbatim from the spike.
    /// </summary>
    private static BsonDocument CombineAnd(BsonDocument left, BsonDocument right)
    {
        var clauses = new List<BsonDocument>();
        AddAndOperand(clauses, left);
        AddAndOperand(clauses, right);

        var merged = new BsonDocument();
        foreach (var clause in clauses)
        {
            // A clause is mergeable only if it is a single-field document whose key is not an operator.
            if (clause.ElementCount != 1 || clause.GetElement(0).Name.StartsWith('$'))
                return new BsonDocument("$and", new BsonArray(clauses));

            var element = clause.GetElement(0);
            if (!merged.Contains(element.Name))
            {
                merged.Add(element);
                continue;
            }

            // Same field appears twice (e.g. x > a && x < b). Merge the operator sub-documents when
            // possible: { x: { $gt: a, $lt: b } }. Fall back to $and on conflict or non-operator values.
            if (TryMergeOperatorDocs(merged[element.Name], element.Value, out var combined))
                merged[element.Name] = combined;
            else
                return new BsonDocument("$and", new BsonArray(clauses));
        }

        return merged;
    }

    private static bool TryMergeOperatorDocs(BsonValue existing, BsonValue addition, out BsonValue combined)
    {
        combined = BsonNull.Value;
        if (existing is not BsonDocument ed || addition is not BsonDocument ad)
            return false;
        if (!IsAllOperators(ed) || !IsAllOperators(ad))
            return false;

        var result = new BsonDocument();
        result.AddRange(ed);
        foreach (var op in ad)
        {
            if (result.Contains(op.Name))
                return false; // overlapping operator (e.g. two $gt) cannot merge
            result.Add(op);
        }

        combined = result;
        return true;
    }

    private static bool IsAllOperators(BsonDocument doc)
    {
        if (doc.ElementCount == 0)
            return false;
        foreach (var e in doc)
        {
            if (!e.Name.StartsWith('$'))
                return false;
        }

        return true;
    }

    private static void AddAndOperand(List<BsonDocument> clauses, BsonDocument doc)
    {
        if (doc.ElementCount == 1 && doc.GetElement(0).Name == "$and" && doc[0] is BsonArray array)
        {
            foreach (var item in array)
                clauses.Add((BsonDocument)item);
        }
        else
        {
            clauses.Add(doc);
        }
    }

    /// <summary>
    /// Combines two filter documents with OR into a flat <c>$or</c> array,
    /// flattening any nested <c>$or</c> operands to match driver-LINQ rendering.
    /// Ported verbatim from the spike.
    /// </summary>
    private static BsonDocument CombineOr(BsonDocument left, BsonDocument right)
    {
        var clauses = new BsonArray();
        AddOrOperand(clauses, left);
        AddOrOperand(clauses, right);
        return new BsonDocument("$or", clauses);
    }

    private static void AddOrOperand(BsonArray clauses, BsonDocument doc)
    {
        if (doc.ElementCount == 1 && doc.GetElement(0).Name == "$or" && doc[0] is BsonArray array)
        {
            foreach (var item in array)
                clauses.Add(item);
        }
        else
        {
            clauses.Add(doc);
        }
    }
}
