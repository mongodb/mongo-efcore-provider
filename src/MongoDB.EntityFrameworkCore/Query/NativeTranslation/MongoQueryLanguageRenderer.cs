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
/// factory (<c>MongoPipelineFactory.Build</c>).
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
            MongoBinaryExpression sizeComparison
                when TryRenderSizeComparison(sizeComparison) is { } arrayIndexForm => arrayIndexForm,
            MongoUnaryExpression unary => RenderUnary(unary, placeholders),
            MongoFieldExpression field => RenderBareField(field, placeholders),
            MongoInExpression inExpr => RenderIn(inExpr, placeholders),
            MongoArrayContainsExpression arrayContains => RenderArrayContains(arrayContains, placeholders),
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
        // $not over an operator document is the exact set complement of that document, including documents
        // where the field is missing or explicitly null. That exactness is why MongoExpressionNegator
        // $not-wraps the four relational operators instead of inverting them: neither { $gt: 5 } nor
        // { $lte: 5 } matches a missing field, so the pair does not partition the value space and an
        // inversion would silently mis-answer All() for such a document.
        if (unary.Operand is MongoBinaryExpression comparison && IsQueryNativeComparison(comparison))
        {
            // Reuse RenderComparison so element naming and value serialization are identical to the
            // un-negated form (a parameter still records a placeholder in the shared table).
            var element = RenderComparison(comparison, placeholders).GetElement(0);

            // RenderComparison emits Equal as a bare { field: value }; every other operator emits
            // { field: { $op: value } }, an operator document — detected by a leading '$' rather than
            // assumed. A parameterized equality renders as PlaceholderTable's sentinel,
            // { __mongoef_param__: N }, whose key is deliberately not '$'-prefixed, so it is correctly
            // treated as a bare value here too and wrapped in { $eq: … } rather than mistaken for an
            // operator document (see PlaceholderTable.SentinelKey's invariant).
            //
            // The wrap is mandatory: { field: { $not: <bareValue> } } is a hard server error ("$not
            // argument must be a regex or an object"), reachable via !(x.A == 1), which EF does not
            // normalize away.
            var body = element.Value is BsonDocument candidate
                && candidate.ElementCount > 0
                && candidate.GetElement(0).Name.StartsWith('$')
                    ? candidate
                    : new BsonDocument("$eq", element.Value);

            return new BsonDocument(element.Name, new BsonDocument("$not", body));
        }

        if (unary.Operand is not MongoFieldExpression field)
        {
            // Neither a query-native comparison nor a bare field: this subtree has no query-dialect form at
            // all (e.g. a field-to-field comparison, arithmetic, or an $in). Delegate to $expr the same way
            // RenderNode's own catch-all does for any other unrenderable node — MongoUnaryExpression is
            // otherwise matched explicitly in RenderNode's switch (see its MongoUnaryExpression arm), so
            // without this branch a Not never reaches that catch-all and always either succeeds above or
            // throws here.
            if (MongoAggregationExpressionRenderer.CanRender(unary.Operand))
            {
                return new BsonDocument("$expr",
                    new BsonDocument("$not", new BsonArray { MongoAggregationExpressionRenderer.Render(unary.Operand, placeholders) }));
            }

            throw new NativeTranslationNotSupportedException(
                "MongoQueryLanguageRenderer only supports Not over a MongoFieldExpression, a query-native comparison, "
                + "or a subtree the aggregation-expression renderer can express.");
        }

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
    // Array-field-contains-value ({ field: value } — the implicit array-element match)
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders an <c>arrayField.Contains(constant)</c> test as MongoDB's implicit array-element-equality
    /// match: <c>{ field: value }</c>. Negation uses <c>$ne</c>, the exact complement (same reasoning as
    /// <c>$eq</c>/<c>$ne</c> in <see cref="RenderComparison"/>: both partition every BSON value including
    /// missing/null, so inversion is exact rather than merely close).
    /// </summary>
    private BsonDocument RenderArrayContains(MongoArrayContainsExpression contains, PlaceholderTable placeholders)
    {
        var value = MongoValueRenderer.RenderValue(contains.Value, placeholders);
        return contains.Negated
            ? new BsonDocument(contains.Field.ElementName, new BsonDocument("$ne", value))
            : new BsonDocument(contains.Field.ElementName, value);
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
    /// The child is guaranteed to have a query-dialect rendering because
    /// <see cref="IsQueryDialectRenderable"/> gates node construction in
    /// <c>MongoExpressionTranslator</c> — <c>$expr</c> is not usable inside <c>$elemMatch</c>.
    /// </para>
    /// </summary>
    private BsonDocument RenderElemMatch(MongoElemMatchExpression elemMatch, PlaceholderTable placeholders)
    {
        var body = new BsonDocument(
            "$elemMatch", (BsonDocument)RenderNode(elemMatch.ElementPredicate, placeholders));

        return elemMatch.Negated
            ? new BsonDocument(elemMatch.ArrayPath, new BsonDocument("$not", body))
            : new BsonDocument(elemMatch.ArrayPath, body);
    }

    // ------------------------------------------------------------------
    // Array cardinality — the query-dialect array-index existence form
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a comparison between an array's element count and an INTEGER CONSTANT to the query dialect,
    /// or returns <see langword="null"/> when the comparison has no query-dialect form (a parameterized
    /// or degenerate threshold routes to <c>$expr</c> instead; a non-integral threshold is rejected by
    /// <c>TryGetIntegerThreshold</c> but is reachable only from a hand-built expression tree — see the
    /// reachability note below).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The form is an array-index existence test: <c>{"path.k": {$exists: true}}</c> is true for exactly those
    /// documents whose array has more than <c>k</c> elements, and <c>$exists: false</c> for at most <c>k</c>.
    /// Every operator is expressed by choosing <c>k</c>; <c>==</c> and <c>!=</c> combine two of them through
    /// <see cref="CombineAnd"/>/<see cref="CombineOr"/>.
    /// </para>
    /// <para>
    /// <b>Why not query-dialect <c>$size</c>:</b> it can only express an exact size, cannot use an index, and
    /// <c>{path: {$size: 0}}</c> does not match a document whose array is missing or explicitly <c>null</c>,
    /// where LINQ's <c>Count == 0</c> is <see langword="true"/> (EF materializes a missing embedded array as
    /// an empty list). The index form is correct for all three states for free, since none has an element at
    /// any index.
    /// </para>
    /// <para>
    /// <see cref="IsQueryDialectRenderable"/> calls this method rather than re-deriving the condition, so the
    /// classifier and the renderer cannot drift — required by the negator/classifier/renderer contract (see
    /// <see cref="MongoExpressionNegator"/>).
    /// </para>
    /// <para>
    /// A rejected threshold needs no clamping and no decline: a tautology, a contradiction, and a
    /// parameterized threshold all fall through to <see cref="RenderAsExpr"/>, which renders each correctly.
    /// Returning <see langword="null"/> here therefore loses only the index, never correctness — including
    /// for a non-integral threshold (<c>Count &gt; 2.5</c>), which a <see cref="MongoConvertExpression"/> arm
    /// upstream now allows to reach this point; it renders correctly via <c>$expr</c> with a <c>$toDouble</c>
    /// cast, just without the index form.
    /// </para>
    /// </remarks>
    private static BsonDocument? TryRenderSizeComparison(MongoBinaryExpression binary)
    {
        if (binary.Left is not MongoSizeExpression size
            || binary.Right is not MongoConstantExpression { Value: { } rawThreshold }
            || !TryGetIntegerThreshold(rawThreshold, out var n))
        {
            return null;
        }

        // MoreThan(k) ⇔ count >= k + 1;  AtMost(k) ⇔ count <= k.
        return binary.Operator switch
        {
            MongoBinaryOperator.GreaterThan when n >= 0 => MoreThan(size.FieldName, n),
            MongoBinaryOperator.GreaterThanOrEqual when n >= 1 => MoreThan(size.FieldName, n - 1),
            MongoBinaryOperator.LessThan when n >= 1 => AtMost(size.FieldName, n - 1),
            MongoBinaryOperator.LessThanOrEqual when n >= 0 => AtMost(size.FieldName, n),
            // C == 0 needs only the upper bound; C == n (n >= 1) is "more than n-1 AND at most n".
            MongoBinaryOperator.Equal when n == 0 => AtMost(size.FieldName, 0),
            MongoBinaryOperator.Equal when n >= 1
                => CombineAnd(MoreThan(size.FieldName, n - 1), AtMost(size.FieldName, n)),
            // C != 0 needs only the lower bound; C != n (n >= 1) is "at most n-1 OR more than n".
            MongoBinaryOperator.NotEqual when n == 0 => MoreThan(size.FieldName, 0),
            MongoBinaryOperator.NotEqual when n >= 1
                => CombineOr(AtMost(size.FieldName, n - 1), MoreThan(size.FieldName, n)),
            _ => null
        };
    }

    private static BsonDocument MoreThan(string arrayPath, int index)
        => new($"{arrayPath}.{index}", new BsonDocument("$exists", true));

    private static BsonDocument AtMost(string arrayPath, int index)
        => new($"{arrayPath}.{index}", new BsonDocument("$exists", false));

    // An array index is a path SEGMENT, so only an integral, in-range threshold has an index form. A
    // floating-point threshold is rejected even when whole-valued (2.0) — the $expr tier renders it correctly,
    // and accepting it here would add a rounding decision for no coverage gain.
    private static bool TryGetIntegerThreshold(object raw, out int value)
    {
        switch (raw)
        {
            case int i: value = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
            case short s: value = s; return true;
            case byte b: value = b; return true;
            case sbyte sb: value = sb; return true;
            case ushort us: value = us; return true;
            case uint ui when ui <= int.MaxValue: value = (int)ui; return true;
            default: value = 0; return false;
        }
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
            // An array-count comparison against an admissible integer constant. Calls the renderer rather than
            // re-deriving its condition, so the two cannot drift. A parameterized or degenerate threshold
            // answers false here, which is exactly what declines it inside $elemMatch.
            MongoBinaryExpression sizeComparison when TryRenderSizeComparison(sizeComparison) is not null
                => true,
            MongoBinaryExpression comparison => IsQueryNativeComparison(comparison),
            // These two are the ONLY forms of Not that have a QUERY-DIALECT rendering: a bare field
            // ({ field: { $ne: true } }) and a query-native comparison ({ field: { $not: { $op: value } } }).
            // Every other operand (a conjunction, a field-to-field comparison, arithmetic, an $in) is still
            // RENDERABLE by RenderUnary — since EF-396 it falls through to MongoAggregationExpressionRenderer
            // and emits { $expr: { $not: [ … ] } } rather than throwing — but must NOT be admitted here,
            // because $expr is a hard server error inside $elemMatch, which is the position this classifier
            // gates. "Renderable" is therefore not the test; "has a query-dialect rendering" is. Pinned by
            // MongoQueryLanguageRendererTests
            // .IsQueryDialectRenderable_still_rejects_but_Render_now_falls_to_expr_for_Not_over_a_field_to_field_comparison.
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoBinaryExpression cmp }
                => IsQueryNativeComparison(cmp),
            MongoFieldExpression => true,
            // Explicit rather than left to the catch-all: a $toX conversion has no query-dialect form, so
            // admitting one here would put $expr inside $elemMatch, a hard server error. A comparison over a
            // convert is excluded separately by IsQueryNativeComparison's requirement that the left operand
            // be a bare MongoFieldExpression.
            MongoConvertExpression => false,
            // RenderInValues throws for any values node other than a constant enumerable or a parameter.
            MongoInExpression inExpr
                => inExpr.Values is MongoConstantExpression { Value: System.Collections.IEnumerable }
                    or MongoParameterExpression,
            // RenderArrayContains always has a query-dialect form ({ field: value } / { field: { $ne: value } });
            // MongoExpressionTranslator only ever constructs this node with a Value it has already resolved
            // and rendered, so there is no unrenderable sub-shape to exclude here (unlike MongoInExpression's
            // Values, which can carry an unsupported node the renderer would throw on).
            MongoArrayContainsExpression => true,
            // RenderRegex throws for a parameterized term — only a constant is baked into a pattern.
            MongoRegexExpression { Term: MongoConstantExpression { Value: string } } => true,
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
