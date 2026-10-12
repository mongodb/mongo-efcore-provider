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
/// Pure BSON emission: all parity guards were applied by <see cref="MongoExpressionTranslator"/>. Parameters become
/// <see cref="PlaceholderTable"/> sentinels substituted per execution by <c>MongoPipelineFactory.Build</c>.
/// </remarks>
internal sealed class MongoQueryLanguageRenderer
{
    /// <summary>
    /// Renders a bool-typed <paramref name="predicate"/> to a <c>$match</c> filter body.
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
            MongoFieldPresenceExpression presence when IsQueryDialectPresence(presence) => RenderFieldPresence(presence),
            MongoDictionaryContainsKeyExpression containsKey
                => new BsonDocument(containsKey.Path, new BsonDocument("$exists", !containsKey.Negated)),
            MongoCompositeKeyInExpression compositeKeyIn => RenderCompositeKeyIn(compositeKeyIn, placeholders),
            MongoArrayContainsExpression arrayContains => RenderArrayContains(arrayContains, placeholders),
            // Shares IsQueryDialectRegex with IsQueryDialectRenderable; anything else falls to $expr/$regexMatch.
            MongoRegexExpression regex when IsQueryDialectRegex(regex)
                => RenderRegex(regex, placeholders),
            MongoElemMatchExpression elemMatch => RenderElemMatch(elemMatch, placeholders),
            // Literal bool root: `true` is match-all; `false` uses the impossible-$type idiom (as the $limit:0 rewrite
            // does), since $expr: false is illegal in $elemMatch.
            MongoConstantExpression { Value: bool boolValue } => boolValue
                ? new BsonDocument()
                : AlwaysFalseFilter(),
            _ => RenderAsExpr(node, placeholders)
        };

    /// <summary>
    /// The always-false filter <c>{ _id: { $type: -1 } }</c>; legal inside <c>$elemMatch</c>, where <c>$expr: false</c> is not.
    /// </summary>
    internal static BsonDocument AlwaysFalseFilter()
        => new("_id", new BsonDocument("$type", -1));

    /// <summary>
    /// Whether a <see cref="MongoRegexExpression"/> has the query-dialect <c>{ path: /re/ }</c> form; shared by
    /// <see cref="RenderNode"/> and <see cref="IsQueryDialectRenderable"/> so they cannot drift. Requires:
    /// <list type="bullet">
    /// <item>a literal term (constant string or parameter), since <c>$regularExpression</c> needs a literal pattern;</item>
    /// <item>a <see cref="MongoFieldExpression"/> or <see cref="MongoElementRefExpression"/> receiver: a computed
    /// receiver has no document path, and <c>{ path: /re/ }</c> over a bogus path silently matches nothing;</item>
    /// <item>not <see cref="MongoRegexKind.IsMatch"/>, whose Field is the pattern, not the tested value.</item>
    /// </list>
    /// </summary>
    internal static bool IsQueryDialectRegex(MongoRegexExpression regex)
        => regex is
        {
            Kind: not MongoRegexKind.IsMatch,
            Field: MongoFieldExpression or MongoElementRefExpression,
            Term: MongoConstantExpression { Value: string } or MongoParameterExpression
        }
        // A Pattern with a parameter/field pattern (EF-247) needs $regexMatch.
        && (regex.Kind != MongoRegexKind.Pattern || regex.Term is MongoConstantExpression);

    // Bare field on the left, constant/parameter on the right; anything else goes to $expr. Shared with
    // MongoExpressionNegator, which must decline any comparison this rejects.
    internal static bool IsQueryNativeComparison(MongoBinaryExpression b)
        => b.Left is MongoFieldExpression or MongoDictionaryEntryExpression
           && b.Right is MongoConstantExpression or MongoParameterExpression;

    private BsonDocument RenderAsExpr(MongoExpression node, PlaceholderTable placeholders)
        => new BsonDocument("$expr", MongoAggregationExpressionRenderer.Render(node, placeholders));

    private BsonDocument RenderComparison(MongoBinaryExpression binary, PlaceholderTable placeholders)
    {
        // The translator always puts the field (or dictionary entry) on the Left, mirroring the operator if needed.
        var elementName = binary.Left switch
        {
            MongoFieldExpression field => field.ElementName,
            MongoDictionaryEntryExpression entry => entry.Path,
            _ => throw new NativeTranslationNotSupportedException(
                $"Expected MongoFieldExpression on the left side of a comparison; got '{binary.Left.GetType().Name}'.")
        };
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

    // The null forms ({ path: null } / { path: { $ne: null } }) also match an array that CONTAINS null, so over a path
    // that may hold an array they render through $expr instead.
    private static bool IsQueryDialectPresence(MongoFieldPresenceExpression presence)
        => !(presence.TestsNull && presence.PathMayBeArray);

    private static BsonDocument RenderFieldPresence(MongoFieldPresenceExpression presence)
        => presence.Kind switch
        {
            MongoFieldPresence.Exists => new BsonDocument(presence.Path, new BsonDocument("$exists", true)),
            MongoFieldPresence.Missing => new BsonDocument(presence.Path, new BsonDocument("$exists", false)),
            MongoFieldPresence.NullOrMissing => new BsonDocument(presence.Path, BsonNull.Value),
            _ => new BsonDocument(presence.Path, new BsonDocument("$ne", BsonNull.Value))
        };

    // { $or: [ { "_id.K1": v1, "_id.K2": v2 }, ... ] } ($nor when negated); a parameter list is built per execution.
    private static BsonDocument RenderCompositeKeyIn(MongoCompositeKeyInExpression compositeKeyIn, PlaceholderTable placeholders)
        => new(
            compositeKeyIn.Negated ? "$nor" : "$or",
            compositeKeyIn.Values switch
            {
                MongoConstantExpression { Value: System.Collections.IEnumerable items }
                    => CompositeKeyClauses.Build(items, compositeKeyIn.KeyProperties),
                MongoParameterExpression parameter
                    => placeholders.CreateCompositeKeyArrayPlaceholder(parameter.Name, compositeKeyIn.KeyProperties),
                _ => throw new NativeTranslationNotSupportedException("Unsupported composite-key $in values node.")
            });

    // Correct because preserveNullAndEmptyArrays leaves an explicit null on the $lookup alias when nothing matches.
    private static BsonDocument RenderLookupNullCheck(MongoLookupNullCheckExpression node)
        => node.IsNotNull
            ? new BsonDocument(node.LookupAlias, new BsonDocument("$ne", BsonNull.Value))
            : new BsonDocument(node.LookupAlias, BsonNull.Value);

    private static BsonDocument RenderNumericTypeBracket(MongoNumericTypeBracketExpression bracket)
        => new BsonDocument(bracket.Field.ElementName, new BsonDocument("$type", "number"));

    private BsonDocument RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException(
                $"Unsupported unary operator '{unary.Operator}'.");

        // $not is the exact complement including missing/null, which is why the negator $not-wraps relational
        // operators instead of inverting them.
        if (unary.Operand is MongoBinaryExpression comparison && IsQueryNativeComparison(comparison))
        {
            var element = RenderComparison(comparison, placeholders).GetElement(0);

            // Equal renders as a bare value (including the non-'$' parameter sentinel, see
            // PlaceholderTable.SentinelKey), which must be wrapped in $eq: { $not: <bareValue> } is a server error.
            var body = element.Value is BsonDocument candidate
                && candidate.ElementCount > 0
                && candidate.GetElement(0).Name.StartsWith('$')
                    ? candidate
                    : new BsonDocument("$eq", element.Value);

            return new BsonDocument(element.Name, new BsonDocument("$not", body));
        }

        if (unary.Operand is not MongoFieldExpression field)
        {
            // No query-dialect form: delegate to $expr (RenderNode matches Not explicitly, so it never reaches the
            // catch-all). CanRender checks the operand, not the Not, so the truthiness guard is repeated here: a
            // non-default-serialized bool would otherwise be truthiness-tested wrongly.
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

        // !boolProperty -> { field: { $ne: true } }; matches driver-LINQ and missing/null-field semantics.
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

    // arrayField.Contains(value) is the implicit element match { field: value }; negation uses $ne, the exact
    // complement including missing/null.
    private BsonDocument RenderArrayContains(MongoArrayContainsExpression contains, PlaceholderTable placeholders)
    {
        var value = MongoValueRenderer.RenderValue(contains.Value, placeholders);
        return contains.Negated
            ? new BsonDocument(contains.Field.ElementName, new BsonDocument("$ne", value))
            : new BsonDocument(contains.Field.ElementName, value);
    }

    // { field: /pattern/s } (negated via $not), matching driver-LINQ. A parameter term becomes a regex placeholder
    // resolved per execution.
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

    // A field's element name, or a MongoElementRefExpression path (a projected Distinct()'s computed alias). Any other
    // shape is an invariant violation, not a shape to decline.
    private static string GetRegexFieldPath(MongoExpression field)
        => field switch
        {
            MongoFieldExpression f => f.ElementName,
            MongoElementRefExpression e => e.Path,
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported regex field expression: {field.GetType().Name}.")
        };

    // { path: { $elemMatch: child } } (negated via $not). Child field names stay element-relative. The translator
    // gates construction on IsQueryDialectRenderable, since $expr is illegal here.
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
    /// <see langword="null"/> (parameterized, degenerate or non-integral threshold) to fall to <c>$expr</c>.
    /// </summary>
    /// <remarks>
    /// <c>{"path.k": {$exists: true}}</c> holds iff the array has more than <c>k</c> elements. Not <c>$size</c>: it is
    /// unindexable and <c>{$size: 0}</c> misses a missing/null array, where LINQ <c>Count == 0</c> is true.
    /// <see cref="IsQueryDialectRenderable"/> calls this method so classifier and renderer cannot drift.
    /// </remarks>
    private static BsonDocument? TryRenderSizeComparison(MongoBinaryExpression binary)
    {
        if (binary.Left is not MongoSizeExpression size
            || binary.Right is not MongoConstantExpression { Value: { } rawThreshold }
            || !TryGetIntegerThreshold(rawThreshold, out var n))
        {
            return null;
        }

        // MoreThan(k) <=> count >= k + 1;  AtMost(k) <=> count <= k.
        return binary.Operator switch
        {
            MongoBinaryOperator.GreaterThan when n >= 0 => MoreThan(size.FieldName, n),
            MongoBinaryOperator.GreaterThanOrEqual when n >= 1 => MoreThan(size.FieldName, n - 1),
            MongoBinaryOperator.LessThan when n >= 1 => AtMost(size.FieldName, n - 1),
            MongoBinaryOperator.LessThanOrEqual when n >= 0 => AtMost(size.FieldName, n),
            MongoBinaryOperator.Equal when n == 0 => AtMost(size.FieldName, 0),
            MongoBinaryOperator.Equal when n >= 1
                => CombineAnd(MoreThan(size.FieldName, n - 1), AtMost(size.FieldName, n)),
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

    // An array index is a path segment, so only integral in-range thresholds qualify (not even 2.0).
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

    /// <summary>
    /// Whether <see cref="RenderNode"/> renders <paramref name="node"/> without falling to <c>$expr</c> or throwing.
    /// </summary>
    /// <remarks>
    /// A correctness gate for <c>$elemMatch</c> children: admitting a node <see cref="RenderNode"/> sends to
    /// <c>$expr</c> makes the query throw at execution instead of falling back. Change both together.
    /// </remarks>
    public static bool IsQueryDialectRenderable(MongoExpression node)
        => node switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
                => IsQueryDialectRenderable(a.Left) && IsQueryDialectRenderable(a.Right),
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
                => IsQueryDialectRenderable(o.Left) && IsQueryDialectRenderable(o.Right),
            MongoBinaryExpression sizeComparison when TryRenderSizeComparison(sizeComparison) is not null
                => true,
            MongoBinaryExpression comparison => IsQueryNativeComparison(comparison),
            // RenderUnary also renders other Not operands, but via $expr, so they must not be admitted (pinned by
            // MongoQueryLanguageRendererTests).
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoFieldExpression } => true,
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not, Operand: MongoBinaryExpression cmp }
                => IsQueryNativeComparison(cmp),
            MongoFieldExpression => true,
            MongoDictionaryContainsKeyExpression => true,
            MongoFieldPresenceExpression presence => IsQueryDialectPresence(presence),
            MongoCompositeKeyInExpression { Values: MongoConstantExpression or MongoParameterExpression } => true,
            // Only produced beside an $expr sibling (so its AndAlso answers false), but true on its own merits.
            MongoNumericTypeBracketExpression => true,
            // No query-dialect form; listed explicitly so a new node type is a conscious choice.
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
            MongoInExpression inExpr => MongoValueRenderer.IsRenderableInValues(inExpr.Values),
            MongoArrayContainsExpression => true,
            MongoRegexExpression regex => IsQueryDialectRegex(regex),
            MongoElemMatchExpression elemMatch => IsQueryDialectRenderable(elemMatch.ElementPredicate),
            MongoConstantExpression { Value: bool } => true,
            _ => false
        };

    /// <summary>
    /// ANDs two filter documents into one when keys are distinct non-operators (or same-field operator documents
    /// don't overlap); otherwise a flattened <c>$and</c> array.
    /// </summary>
    private static BsonDocument CombineAnd(BsonDocument left, BsonDocument right)
    {
        var clauses = new List<BsonDocument>();
        AddAndOperand(clauses, left);
        AddAndOperand(clauses, right);

        var merged = new BsonDocument();
        foreach (var clause in clauses)
        {
            if (clause.ElementCount != 1 || clause.GetElement(0).Name.StartsWith('$'))
                return new BsonDocument("$and", new BsonArray(clauses));

            var element = clause.GetElement(0);
            if (!merged.Contains(element.Name))
            {
                merged.Add(element);
                continue;
            }

            // Same field twice (x > a && x < b): merge to { x: { $gt: a, $lt: b } } when possible, else $and.
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
                return false;
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

    // Flat $or array, matching driver-LINQ rendering.
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
