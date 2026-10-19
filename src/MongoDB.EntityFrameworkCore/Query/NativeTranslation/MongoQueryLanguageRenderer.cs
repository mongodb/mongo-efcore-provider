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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a <see cref="MongoExpression"/> predicate to a query-dialect <c>$match</c> filter body (without the
/// <c>$match</c> wrapper), wrapping only subtrees with no query-dialect form in <c>$expr</c>.
/// </summary>
/// <remarks>
/// Pure BSON emission: all parity guards were applied by <see cref="MongoExpressionTranslator"/>. Constants are
/// serialized inline via the node's <see cref="IProperty"/>; parameters become <see cref="PlaceholderTable"/>
/// sentinels substituted per execution by <c>MongoPipelineFactory.Build</c>.
/// </remarks>
internal sealed class MongoQueryLanguageRenderer
{
    /// <summary>
    /// Renders a bool-typed <paramref name="predicate"/> to a <c>$match</c> filter body, recording parameter
    /// sites in <paramref name="placeholders"/>.
    /// </summary>
    public BsonValue Render(MongoExpression predicate, PlaceholderTable placeholders)
        => RenderNode(predicate, placeholders);

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
            MongoLookupNullCheckExpression lookupNullCheck => RenderLookupNullCheck(lookupNullCheck),
            MongoNumericTypeBracketExpression bracket => RenderNumericTypeBracket(bracket),
            MongoInExpression inExpr => RenderIn(inExpr, placeholders),
            MongoArrayContainsExpression arrayContains => RenderArrayContains(arrayContains, placeholders),
            // Shares IsQueryDialectRegex with IsQueryDialectRenderable; anything else falls to $expr/$regexMatch.
            MongoRegexExpression regex when IsQueryDialectRegex(regex)
                => RenderRegex(regex, placeholders),
            MongoElemMatchExpression elemMatch => RenderElemMatch(elemMatch, placeholders),
            // Literal bool root: `true` is an empty (match-all) body; `false` uses the impossible-$type idiom
            // (as MongoPipelineFactory's $limit:0 rewrite does), which unlike $expr: false is legal in $elemMatch.
            MongoConstantExpression { Value: bool boolValue } => boolValue
                ? new BsonDocument()
                : AlwaysFalseFilter(),
            _ => RenderAsExpr(node, placeholders)
        };

    /// <summary>
    /// The always-false filter body <c>{ _id: { $type: -1 } }</c> (an impossible <c>$type</c>). Legal anywhere a
    /// query-dialect filter is, including inside <c>$elemMatch</c> where <c>$expr: false</c> is not.
    /// </summary>
    internal static BsonDocument AlwaysFalseFilter()
        => new("_id", new BsonDocument("$type", -1));

    /// <summary>
    /// Whether a <see cref="MongoRegexExpression"/> has the query-dialect <c>{ path: /re/ }</c> form. The single
    /// predicate behind both <see cref="RenderNode"/>'s regex arm and <see cref="IsQueryDialectRenderable"/>, so
    /// the two cannot drift. Requires:
    /// <list type="bullet">
    /// <item>a literal term (constant string or parameter): <c>$regularExpression</c> needs a literal pattern, so a
    /// field-to-field term goes to <c>$expr</c>;</item>
    /// <item>a <see cref="MongoFieldExpression"/> or <see cref="MongoElementRefExpression"/> receiver: a computed
    /// receiver (<c>(c.A + "").Contains("1")</c>) has no document path, and <c>{ path: /re/ }</c> over a bogus path
    /// silently matches nothing;</item>
    /// <item>not <see cref="MongoRegexKind.IsMatch"/>: its Field is the pattern, not the tested value, so
    /// <see cref="RenderRegex"/> would be backwards.</item>
    /// </list>
    /// </summary>
    internal static bool IsQueryDialectRegex(MongoRegexExpression regex)
        => regex is
        {
            Kind: not MongoRegexKind.IsMatch,
            Field: MongoFieldExpression or MongoElementRefExpression,
            Term: MongoConstantExpression { Value: string } or MongoParameterExpression
        }
        // A Pattern with a parameter/field pattern (EF-247) needs $regexMatch: $regularExpression requires a literal.
        && (regex.Kind != MongoRegexKind.Pattern || regex.Term is MongoConstantExpression);

    // Query-native classification: bare field on the left, constant/parameter on the right. Field-to-field
    // and arithmetic operands have no query-dialect form and go to $expr.

    // Shared with MongoExpressionNegator, which must decline any comparison this rejects (no query-dialect
    // complement exists).
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

    /// <summary>
    /// Renders a reference-Include null check as <c>{ alias: null }</c> / <c>{ alias: { $ne: null } }</c> on the
    /// <c>$lookup</c> alias; correct because <c>preserveNullAndEmptyArrays</c> leaves an explicit null on no match.
    /// </summary>
    private static BsonDocument RenderLookupNullCheck(MongoLookupNullCheckExpression node)
        => node.IsNotNull
            ? new BsonDocument(node.LookupAlias, new BsonDocument("$ne", BsonNull.Value))
            : new BsonDocument(node.LookupAlias, BsonNull.Value);

    /// <summary>
    /// Renders <c>{ field: { $type: "number" } }</c>; see <see cref="MongoNumericTypeBracketExpression"/>.
    /// </summary>
    private static BsonDocument RenderNumericTypeBracket(MongoNumericTypeBracketExpression bracket)
        => new BsonDocument(bracket.Field.ElementName, new BsonDocument("$type", "number"));

    private BsonDocument RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException(
                $"Unsupported unary operator '{unary.Operator}'.");

        // !<query-native comparison> → { field: { $not: { <op>: value } } }. $not is the exact complement,
        // including missing/null fields, which is why the negator $not-wraps relational operators instead of
        // inverting them.
        if (unary.Operand is MongoBinaryExpression comparison && IsQueryNativeComparison(comparison))
        {
            var element = RenderComparison(comparison, placeholders).GetElement(0);

            // Equal renders as a bare value (including the non-'$' parameter sentinel — see
            // PlaceholderTable.SentinelKey), which must be wrapped in $eq: { $not: <bareValue> } is a server
            // error, reachable via !(x.A == 1).
            var body = element.Value is BsonDocument candidate
                && candidate.ElementCount > 0
                && candidate.GetElement(0).Name.StartsWith('$')
                    ? candidate
                    : new BsonDocument("$eq", element.Value);

            return new BsonDocument(element.Name, new BsonDocument("$not", body));
        }

        if (unary.Operand is not MongoFieldExpression field)
        {
            // No query-dialect form (field-to-field, arithmetic, $in, ...): delegate to $expr, since RenderNode
            // matches Not explicitly and it never reaches the catch-all. CanRender is called on the operand, not
            // the Not, so its truthiness guard must be repeated here: a non-default-serialized bool would
            // otherwise be truthiness-tested and answer wrongly.
            if (MongoAggregationExpressionRenderer.CanRender(unary.Operand)
                && !MongoExpressionTranslator.IsUnsafeTruthinessRoot(unary.Operand, out _))
            {
                return new BsonDocument("$expr",
                    new BsonDocument("$not", new BsonArray { MongoAggregationExpressionRenderer.Render(unary.Operand, placeholders) }));
            }

            throw new NativeTranslationNotSupportedException(
                "MongoQueryLanguageRenderer only supports Not over a MongoFieldExpression, a query-native comparison, "
                + "or a subtree the aggregation-expression renderer can express.");
        }

        // !boolProperty → { field: { $ne: true } }; matches driver-LINQ and missing/null-field semantics.
        var trueValue = MongoValueRenderer.RenderValue(
            new MongoConstantExpression(true, field.Property), placeholders);
        return new BsonDocument(field.ElementName, new BsonDocument("$ne", trueValue));
    }

    private BsonDocument RenderBareField(MongoFieldExpression field, PlaceholderTable placeholders)
    {
        var trueValue = MongoValueRenderer.RenderValue(
            new MongoConstantExpression(true, field.Property), placeholders);
        return new BsonDocument(field.ElementName, trueValue);
    }

    private BsonDocument RenderIn(MongoInExpression inExpr, PlaceholderTable placeholders)
    {
        var op = inExpr.Negated ? "$nin" : "$in";
        var array = MongoValueRenderer.RenderInValues(inExpr.Values, placeholders);
        return new BsonDocument(inExpr.Field.ElementName, new BsonDocument(op, array));
    }

    /// <summary>
    /// Renders <c>arrayField.Contains(value)</c> as the implicit element match <c>{ field: value }</c>; negation
    /// uses <c>$ne</c>, the exact complement including missing/null.
    /// </summary>
    private BsonDocument RenderArrayContains(MongoArrayContainsExpression contains, PlaceholderTable placeholders)
    {
        var value = MongoValueRenderer.RenderValue(contains.Value, placeholders);
        return contains.Negated
            ? new BsonDocument(contains.Field.ElementName, new BsonDocument("$ne", value))
            : new BsonDocument(contains.Field.ElementName, value);
    }

    /// <summary>
    /// Renders a <see cref="MongoRegexExpression"/> to <c>{ field: /pattern/s }</c> (negated via <c>$not</c>),
    /// matching driver-LINQ. A constant term is baked into the pattern now; a parameter becomes a regex
    /// placeholder (<see cref="PlaceholderTable.CreateRegexPlaceholder"/>) resolved per execution.
    /// </summary>
    private BsonDocument RenderRegex(MongoRegexExpression regex, PlaceholderTable placeholders)
    {
        BsonValue body;
        switch (regex.Term)
        {
            case MongoConstantExpression { Value: string literal }:
                var pattern = MongoRegexPatternBuilder.BuildPattern(literal, regex.Kind);

                // "s" matches driver-LINQ and is inert (patterns are escaped, so no bare "."). Like has no
                // driver-LINQ precedent; it is case-insensitive to match SQL LIKE collation and EF's spec suite.
                // Pattern carries its own options verbatim (mapped from RegexOptions).
                body = new BsonRegularExpression(
                    pattern,
                    regex.Kind == MongoRegexKind.Pattern ? regex.PatternOptions
                    : regex.Kind == MongoRegexKind.Like ? "is"
                    : regex.CaseInsensitive ? "is" : "s");
                break;

            case MongoParameterExpression parameter:
                body = placeholders.CreateRegexPlaceholder(parameter.Name, regex.Kind, regex.CaseInsensitive);
                break;

            default:
                throw new NativeTranslationNotSupportedException(
                    "Only constant or parameterized string regex terms are natively representable.");
        }

        var fieldPath = GetRegexFieldPath(regex.Field);
        return regex.Negated
            ? new BsonDocument(fieldPath, new BsonDocument("$not", body))
            : new BsonDocument(fieldPath, body);
    }

    /// <summary>
    /// The path <see cref="RenderRegex"/> matches: a field's element name, or a
    /// <see cref="MongoElementRefExpression"/> path (a projected <c>Distinct()</c>'s computed alias). Any other
    /// shape is an invariant violation, not a shape to decline.
    /// </summary>
    private static string GetRegexFieldPath(MongoExpression field)
        => field switch
        {
            MongoFieldExpression f => f.ElementName,
            MongoElementRefExpression e => e.Path,
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported regex field expression: {field.GetType().Name}.")
        };

    /// <summary>
    /// Renders <c>{ path: { $elemMatch: child } }</c> (negated via <c>$not</c>). Child field names stay
    /// element-relative, and all conditions merge into one document so they hold for the same element. The
    /// translator gates construction on <see cref="IsQueryDialectRenderable"/>, since <c>$expr</c> is illegal here.
    /// </summary>
    private BsonDocument RenderElemMatch(MongoElemMatchExpression elemMatch, PlaceholderTable placeholders)
    {
        var body = new BsonDocument(
            "$elemMatch", (BsonDocument)RenderNode(elemMatch.ElementPredicate, placeholders));

        return elemMatch.Negated
            ? new BsonDocument(elemMatch.ArrayPath, new BsonDocument("$not", body))
            : new BsonDocument(elemMatch.ArrayPath, body);
    }

    /// <summary>
    /// Renders an array-count comparison against an integer constant as an array-index existence test, or
    /// returns <see langword="null"/> (parameterized, degenerate, or non-integral threshold) so it falls to
    /// <c>$expr</c>, which is still correct, just unindexed.
    /// </summary>
    /// <remarks>
    /// <c>{"path.k": {$exists: true}}</c> holds iff the array has more than <c>k</c> elements; <c>==</c>/<c>!=</c>
    /// combine two such tests. Not query-dialect <c>$size</c>: it is unindexable and <c>{$size: 0}</c> misses a
    /// missing/null array, where LINQ <c>Count == 0</c> is true. <see cref="IsQueryDialectRenderable"/> calls
    /// this method so the classifier and renderer cannot drift.
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

    // An array index is a path segment, so only integral in-range thresholds qualify. Floating-point (even 2.0)
    // is rejected; $expr handles it without a rounding decision.
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

    // Query-dialect renderability — must stay in sync with RenderNode

    /// <summary>
    /// Whether <see cref="RenderNode"/> renders <paramref name="node"/> without falling to <c>$expr</c> or
    /// throwing.
    /// </summary>
    /// <remarks>
    /// A correctness gate for <c>$elemMatch</c> children: <c>$expr</c> there is a server error, so admitting a
    /// node that <see cref="RenderNode"/> sends to <c>$expr</c> makes the query throw at execution instead of
    /// falling back. Change this and <see cref="RenderNode"/> together.
    /// </remarks>
    public static bool IsQueryDialectRenderable(MongoExpression node)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
                => IsQueryDialectRenderable(a.Left) && IsQueryDialectRenderable(a.Right),
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
                => IsQueryDialectRenderable(o.Left) && IsQueryDialectRenderable(o.Right),
            // Delegates to the renderer so the two cannot drift; a parameterized threshold answers false.
            MongoBinaryExpression sizeComparison when TryRenderSizeComparison(sizeComparison) is not null
                => true,
            MongoBinaryExpression comparison => IsQueryNativeComparison(comparison),
            // Only Not over a bare field or a query-native comparison has a query-dialect form. RenderUnary also
            // renders other operands, but via $expr, so they must not be admitted (pinned by
            // MongoQueryLanguageRendererTests).
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoBinaryExpression cmp }
                => IsQueryNativeComparison(cmp),
            MongoFieldExpression => true,
            // { field: { $type: "number" } }. Only produced beside an $expr sibling (so its AndAlso answers false),
            // but true on its own merits.
            MongoNumericTypeBracketExpression => true,
            // No query-dialect form; listed explicitly rather than left to the catch-all.
            MongoConvertExpression => false,
            MongoConditionalExpression => false,
            MongoCoalesceExpression => false,
            MongoDatePartExpression => false,
            MongoDateAddExpression => false,
            MongoStringIndexOfExpression => false,
            MongoStringLengthExpression => false,
            MongoMathExpression => false,
            MongoTrimExpression => false,
            MongoSubstringExpression => false,
            MongoReplaceExpression => false,
            MongoStringCompareExpression => false,
            MongoStringFirstOrLastExpression => false,
            MongoDateTimeOffsetLocalExpression => false,
            MongoOuterFieldExpression => false,
            MongoQuantifierExpression => false,
            // The value shapes RenderInValues accepts; it throws on any other.
            MongoInExpression inExpr => MongoValueRenderer.IsRenderableInValues(inExpr.Values),
            // The translator only builds this with an already-renderable Value.
            MongoArrayContainsExpression => true,
            MongoRegexExpression regex => IsQueryDialectRegex(regex),
            MongoElemMatchExpression elemMatch => IsQueryDialectRenderable(elemMatch.ElementPredicate),
            // Literal bool root has a query-dialect form (see RenderNode).
            MongoConstantExpression { Value: bool } => true,
            _ => false
        };

    /// <summary>
    /// ANDs two filter documents, merging into one document when keys are distinct non-operators (or same-field
    /// operator documents merge without overlap); otherwise emits a flattened <c>$and</c> array.
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
        // An empty document (literal `true`) is the AND identity; drop it so `x && true` doesn't force $and.
        if (doc.ElementCount == 0)
            return;

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
    /// ORs two filter documents into a flat <c>$or</c> array, matching driver-LINQ rendering.
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
