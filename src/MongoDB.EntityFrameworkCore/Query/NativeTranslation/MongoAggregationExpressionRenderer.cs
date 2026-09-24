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

using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> subtree to a MongoDB
/// <em>aggregation expression</em> (the body that sits inside <c>{ $expr: … }</c>).
/// Used only for subtrees that have no correct query-dialect rendering (field-to-field
/// comparisons, arithmetic operands); the query renderer wraps the result in <c>$expr</c>.
/// </summary>
internal static class MongoAggregationExpressionRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> to an aggregation-expression <see cref="BsonValue"/>
    /// (the body that sits inside <c>{ $expr: … }</c>).
    /// </summary>
    /// <param name="node">The root <see cref="MongoExpression"/> subtree to render.</param>
    /// <param name="placeholders">
    /// Receives one entry per <see cref="MongoParameterExpression"/> encountered.
    /// Each entry's corresponding sentinel is embedded in the returned <see cref="BsonValue"/>.
    /// </param>
    /// <param name="elementVariable">
    /// The <c>$filter</c>/<c>$map</c> <c>as</c> variable name currently in scope, or <see langword="null"/> at
    /// the document root. When non-null, a field reference renders as <c>"$$" + elementVariable + "." + path</c>
    /// instead of <c>"$" + path</c> — the enclosing document is no longer addressable as <c>$path</c> once a
    /// <see cref="MongoFilteredSizeExpression"/>'s <c>$filter</c> has bound the element to a variable. Every
    /// pre-existing call site omits this (it defaults to <see langword="null"/>), which is what keeps their
    /// emitted MQL byte-identical.
    /// </param>
    /// <returns>
    /// A <see cref="BsonValue"/> representing the aggregation-expression body.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type or operator not handled by this renderer.
    /// </exception>
    public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
        => node switch
        {
            MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
            // NullSafe wraps a MISSING element the same as an explicitly-stored null (see the node's own
            // remarks) — needed for an owned-nav null-equality check, where $expr's own $eq does not
            // otherwise treat "missing" and "null" alike the way the ordinary query dialect does.
            MongoElementRefExpression { NullSafe: true } nullSafeElementRef
                => new BsonDocument("$ifNull", new BsonArray { FieldRef(nullSafeElementRef.Path, elementVariable), BsonNull.Value }),
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            // Always at document root, REGARDLESS of elementVariable — see the node's own remarks.
            MongoOuterFieldExpression outer => FieldRef(outer.ElementName, elementVariable: null),
            // EF-322: the ONLY consumer today is a Select-side join-scope null-check ternary
            // (`ti.Inner != null ? ti.Inner.City : null`) whose Test is rendered here as the MongoConditionalExpression's
            // "if" — see NativeJoinScopeProjectionBinder.TryBindConditionalProjection. LookupAlias is a plain top-level
            // field name (the $lookup's own "as"), so it renders through the same FieldRef helper as any other field.
            //
            // Wrapped in $ifNull, mirroring the NullSafe MongoElementRefExpression arm just above and for the
            // EXACT SAME reason: after a $lookup + $unwind(preserveNullAndEmptyArrays: true), an unmatched row's
            // LookupAlias field is genuinely MISSING (unset), not an explicit BSON null — and $eq/$ne in the
            // aggregation-EXPRESSION dialect (unlike the query dialect's {field: null}) do NOT treat missing and
            // null alike; BSON's Missing type sorts strictly below Null, so a bare $ne against an unmatched row's
            // missing field evaluates true (wrongly reporting "present"). MEASURED against a real server: without
            // the $ifNull normalization, an unmatched left-joined row's null-check comes back true and the
            // ternary picks the WRONG (IfTrue) branch, itself reading a now-missing nested field, which $project
            // then drops from the output entirely rather than emitting the intended IfFalse value — silently
            // wrong data, not a translation failure. $ifNull(missing, null) => null and $ifNull(<doc>, null) =>
            // <doc>, so the wrapped comparison is correct for both the matched and unmatched cases.
            // Always at document root, REGARDLESS of elementVariable — a $lookup alias is always a ROOT-level
            // field by construction (this node is never produced/read inside a $filter/$map element scope),
            // exactly like the MongoOuterFieldExpression arm above (final-review fix, M4).
            MongoLookupNullCheckExpression lookupNullCheck
                => new BsonDocument(lookupNullCheck.IsNotNull ? "$ne" : "$eq",
                    new BsonArray
                    {
                        new BsonDocument("$ifNull", new BsonArray { FieldRef(lookupNullCheck.LookupAlias, elementVariable: null), BsonNull.Value }),
                        BsonNull.Value
                    }),
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
            MongoSizeExpression size => RenderSize(size, elementVariable),
            // EF-322: $avg/$max/$min/$sum as an ARRAY-expression operator (over the $addToSet accumulator's
            // own output field), not a $group accumulator — see the node's own remarks.
            MongoArrayReduceExpression arrayReduce
                => new BsonDocument(arrayReduce.OperatorName, FieldRef(arrayReduce.FieldName, elementVariable)),
            MongoFilteredSizeExpression filtered => RenderFilteredSize(filtered, placeholders, elementVariable),
            MongoInExpression inExpr => RenderIn(inExpr, placeholders, elementVariable),
            MongoComputedInExpression computedIn => RenderComputedIn(computedIn, placeholders, elementVariable),
            MongoUnaryExpression unary => RenderUnary(unary, placeholders, elementVariable),
            MongoConvertExpression convert
                => new BsonDocument(
                    MongoConvertExpression.ToOperatorFor(convert.Type)
                        ?? throw new NativeTranslationNotSupportedException(
                            $"MQL has no conversion operator for '{convert.Type.Name}'. A convert to an "
                            + "unrenderable target should have been declined at translate time."),
                    Render(convert.Operand, placeholders, elementVariable)),
            MongoConditionalExpression conditional
                => new BsonDocument("$cond", new BsonDocument
                {
                    { "if", Render(conditional.Test, placeholders, elementVariable) },
                    { "then", Render(conditional.IfTrue, placeholders, elementVariable) },
                    { "else", Render(conditional.IfFalse, placeholders, elementVariable) }
                }),
            MongoCoalesceExpression coalesce
                => new BsonDocument("$ifNull", new BsonArray
                {
                    Render(coalesce.Left, placeholders, elementVariable),
                    Render(coalesce.Right, placeholders, elementVariable)
                }),
            MongoDateTimeOffsetLocalExpression local
                => new BsonDocument("$dateAdd", new BsonDocument
                {
                    { "startDate", FieldRef(local.Operand.ElementName + ".DateTime", elementVariable) },
                    { "unit", "minute" },
                    { "amount", FieldRef(local.Operand.ElementName + ".Offset", elementVariable) }
                }),
            MongoDatePartExpression datePart => RenderDatePart(datePart, placeholders, elementVariable),
            MongoDateAddExpression dateAdd => RenderDateAdd(dateAdd, placeholders, elementVariable),
            MongoStringIndexOfExpression indexOf
                => new BsonDocument("$indexOfCP", new BsonArray
                {
                    Render(indexOf.Haystack, placeholders, elementVariable),
                    Render(indexOf.Needle, placeholders, elementVariable)
                }),
            MongoStringLengthExpression length
                => new BsonDocument("$strLenCP", Render(length.Operand, placeholders, elementVariable)),
            MongoMathExpression math => RenderMath(math, placeholders, elementVariable),
            MongoQuantifierExpression quantifier => RenderQuantifier(quantifier, placeholders, elementVariable),
            // A constructed nested sub-document leaf (EF-447, `new Book { Id = e.Id, Title = e.Title }`).
            // Each member renders through this SAME Render call, recursively, so a nested field ref renders as
            // "$ElementName" (an aggregation-expression field path) exactly like any other computed leaf value —
            // never a bare unprefixed name, which $project would otherwise misread as an inclusion flag.
            MongoDocumentConstructionExpression construction
                => new BsonDocument(construction.Members.Select(
                    m => new BsonElement(m.MemberName, Render(m.Value, placeholders, elementVariable)))),
            MongoConcatExpression concat
                => new BsonDocument("$concat",
                    new BsonArray(concat.Operands.Select(o => Render(o, placeholders, elementVariable)))),
            // EF-322 (Include_collection_with_conditional_order_by): admits ANY Term shape, not just a
            // field-to-field one. Every CanRender caller (see its own remarks) is already inside an
            // $expr/$addFields/$sort/quantifier scope with no $regularExpression alternative available — a
            // top-level $match predicate never reaches this renderer at all, since a constant/parameter-term
            // regex is handled entirely by the separate query-dialect path (MongoQueryLanguageRenderer) before
            // it would. So there is no fallback disposition being preserved by declining a constant/parameter
            // term here; it must render exactly like the field-to-field case.
            MongoRegexExpression regex => RenderRegexAsExpr(regex, placeholders, elementVariable),
            // A constructed-tuple comparison operand (EF-322 follow-up) — a literal MQL array of the tuple's
            // per-member values, each rendered through this SAME Render call exactly like any other computed
            // value (a field ref, a constant, a nested computation). Unlike MongoValueListExpression (which
            // is deliberately NOT top-level-renderable — see its own remarks), this node exists ONLY as a
            // top-level $eq/$ne operand, so it is wired into the ordinary dispatch rather than a dedicated
            // $in-only helper.
            MongoTupleExpression tuple
                => new BsonArray(tuple.Elements.Select(e => Render(e, placeholders, elementVariable))),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Returns whether <see cref="Render"/> would render <paramref name="node"/> without throwing.
    /// </summary>
    /// <remarks>
    /// <b>This method and <see cref="Render"/> must be changed together.</b> It is the aggregation-dialect
    /// counterpart of <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>, and exists for the same
    /// reason: a caller that builds a node the renderer cannot express turns a clean translate-time decline
    /// into a render-time throw.
    /// <para>
    /// <b>Scope (EF-365).</b> The sole caller is
    /// <c>NativeSlotPopulator.TryTranslateComputedSortKey</c>, where the gate is load-bearing because a
    /// computed sort key's <c>$set</c> body has no fallback disposition of its own. It is NOT a general
    /// "decline anything unrenderable" rule: the filtered-count branch of
    /// <see cref="MongoExpressionTranslator"/> deliberately does <em>not</em> consult it, because for that
    /// shape a render-time throw is caught by <c>TryBuildPipeline</c> and lands on a working driver-LINQ
    /// fallback, while a translate-time decline lands on a hard <c>InvalidOperationException</c> in every
    /// query mode. Before adding a call to this method, check which of those two dispositions the caller
    /// actually has.
    /// </para>
    /// <para>
    /// <b>EF-413:</b> <c>MongoInExpression</c> (client-collection <c>Contains</c>, rendered as the array-form
    /// <c>{ $in: [needle, haystack] }</c>, negation via an enclosing <c>$not</c>) and
    /// <c>MongoUnaryExpression{Not}</c> (rendered as <c>{ $not: [ &lt;operand&gt; ] }</c> over any operand this
    /// method itself admits) both have aggregation-dialect arms now, so a client-collection <c>Contains</c> or
    /// a unary <c>Not</c> in a computed sort key (gated via <c>NativeSlotPopulator.TryTranslateComputedSortKey</c>)
    /// goes native instead of declining to fallback.
    /// </para>
    /// </remarks>
    public static bool CanRender(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression or MongoLookupNullCheckExpression => true,
            MongoConstantExpression or MongoParameterExpression => true,
            // EF-396 (review fix): $and/$or evaluate a BARE operand by TRUTHINESS, not by CLR boolean value —
            // the same hazard the Not arm below already guards against for its own bare-field operand. Without
            // this dedicated arm, the generic MongoBinaryExpression arm's CanRender(Left)/CanRender(Right) would
            // recurse into the unconditional `MongoFieldExpression => true` case above and admit a
            // value-converted/non-default-represented bool field used bare inside &&/||, which renders
            // successfully but answers the WRONG boolean (e.g. a HasConversion<string>() bool stored as "N",
            // a non-empty — therefore truthy — string regardless of the CLR value false). See
            // CanRenderLogicalOperand for the exact rule; a comparison-result operand (x.Age > 5) is unaffected
            // and safe, since a comparison already produces a genuine computed boolean.
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } logical
                => CanRenderLogicalOperand(logical.Left) && CanRenderLogicalOperand(logical.Right),
            MongoBinaryExpression binary
                => IsRenderableOperator(binary.Operator) && CanRender(binary.Left) && CanRender(binary.Right),
            MongoSizeExpression => true,
            MongoFilteredSizeExpression filtered => CanRender(filtered.ElementPredicate),
            MongoInExpression inExpr => CanRenderInValues(inExpr.Values),
            MongoComputedInExpression computedIn => CanRender(computedIn.Needle) && CanRenderInValues(computedIn.Values),
            // A bare-field operand must ALSO be default-serialized — mirrors RenderUnary's own render-time
            // guard (EF-413 review fix), so the two can never disagree: CanRender=true must mean Render
            // actually succeeds AND answers correctly, not merely "doesn't throw". Checked via
            // TryGetBareFieldProperty (final-review fix), which matches BOTH MongoFieldExpression and
            // MongoOuterFieldExpression — a bare outer-scoped bool under Not has the exact same
            // truthiness hazard as an ordinary bare field.
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not } unary
                => CanRender(unary.Operand)
                    && !MongoExpressionTranslator.IsUnsafeTruthinessRoot(unary.Operand, out _),
            MongoConvertExpression convert
                => MongoConvertExpression.ToOperatorFor(convert.Type) is not null && CanRender(convert.Operand),
            MongoConditionalExpression conditional
                => CanRender(conditional.Test) && CanRender(conditional.IfTrue) && CanRender(conditional.IfFalse),
            MongoCoalesceExpression coalesce => CanRender(coalesce.Left) && CanRender(coalesce.Right),
            MongoDateTimeOffsetLocalExpression local => CanRender(local.Operand),
            MongoDatePartExpression datePart => CanRender(datePart.Operand),
            MongoDateAddExpression dateAdd => CanRender(dateAdd.StartDate) && CanRender(dateAdd.Amount),
            MongoStringIndexOfExpression indexOf => CanRender(indexOf.Haystack) && CanRender(indexOf.Needle),
            MongoStringLengthExpression length => CanRender(length.Operand),
            MongoMathExpression math => math.Operands.All(CanRender),
            MongoQuantifierExpression quantifier => CanRender(quantifier.ArrayPath) && CanRender(quantifier.ElementPredicate),
            MongoConcatExpression concat => concat.Operands.All(CanRender),
            // Answers true unconditionally for StartsWith/EndsWith/Contains — this relies on RenderRegexAsExpr's
            // switch over MongoRegexKind staying exhaustive for those 3 members (its `_` arm throws and is
            // otherwise unreachable). Adding a new MongoRegexKind member requires adding it to BOTH that switch
            // and here at the same time, or this would wrongly admit a Kind that Render then throws on. Admits
            // ANY Term shape for those 3 (EF-322) — see the matching comment on Render's arm above for why a
            // constant/parameter term has no fallback disposition to preserve at any of this method's call
            // sites.
            // `Like` is EXCLUDED here deliberately: it has no $expr rendering (a LIKE pattern needs
            // wildcard-to-regex conversion, not a literal substring search like $indexOfCP), and
            // MongoExpressionTranslator.Like.cs only ever produces one in a $match-dialect position — but if
            // that ever changes (e.g. a future negated-Like-inside-a-projection shape), this must decline
            // rather than let RenderRegexAsExpr's default throw arm surface as a crash instead of a graceful
            // decline-to-fallback.
            MongoRegexExpression { Kind: MongoRegexKind.Like } => false,
            MongoRegexExpression regex => CanRender(regex.Field) && CanRender(regex.Term),
            MongoTupleExpression tuple => tuple.Elements.All(CanRender),
            // EF-322 follow-up: a constructed nested sub-document leaf (mirrors Render's own arm above).
            // Previously missing — Render already had an arm for it, so this used to be a "renderer wider
            // than classifier" gap; closing it lets a computed-needle Contains whose item is a composite
            // anonymous-type tuple (`ids.Contains(new { Id1 = ..., Id2 = ... })`) go native.
            MongoDocumentConstructionExpression construction => construction.Members.All(m => CanRender(m.Value)),
            _ => false
        };

    // A logical (&&/||) operand is TRUTHINESS-tested by $and/$or when it is a bare stored value, so a bare
    // field operand must ALSO be default-serialized (mirrors the Not arm above and RenderUnary's own
    // render-time guard, EF-413). A nested AndAlso/OrElse recurses back through this same check for ITS OWN
    // operands (so `!(a && (b && c))` is checked all the way down); anything else — a comparison result, a
    // constant, a parameter — is a genuine computed/opaque value and falls through to the ordinary CanRender.
    private static bool CanRenderLogicalOperand(MongoExpression node)
        => node switch
        {
            // TryGetBareFieldProperty (final-review fix) matches BOTH MongoFieldExpression and
            // MongoOuterFieldExpression — a bare outer-scoped bool used as an &&/|| operand is truthiness-
            // tested exactly the same way an ordinary bare field is; missing the outer-scoped sibling here
            // was the CRITICAL finding from EF-421's final review.
            MongoFieldExpression or MongoOuterFieldExpression
                => !MongoExpressionTranslator.IsUnsafeTruthinessRoot(node, out _),
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } nested
                => CanRenderLogicalOperand(nested.Left) && CanRenderLogicalOperand(nested.Right),
            _ => CanRender(node)
        };

    private static bool CanRenderInValues(MongoExpression values)
        => values is MongoConstantExpression { Value: System.Collections.IEnumerable } or MongoParameterExpression
            or MongoValueListExpression;

    // Exactly the operators RenderBinary's own switch maps below — every MongoBinaryOperator member, as it
    // happens (RenderBinary has no unmapped member today), but this must be re-checked against RenderBinary's
    // switch whenever either changes, not assumed to track the enum automatically.
    private static bool IsRenderableOperator(MongoBinaryOperator op)
        => op is MongoBinaryOperator.Equal
            or MongoBinaryOperator.NotEqual
            or MongoBinaryOperator.LessThan
            or MongoBinaryOperator.LessThanOrEqual
            or MongoBinaryOperator.GreaterThan
            or MongoBinaryOperator.GreaterThanOrEqual
            or MongoBinaryOperator.AndAlso
            or MongoBinaryOperator.OrElse
            or MongoBinaryOperator.Add
            or MongoBinaryOperator.Subtract
            or MongoBinaryOperator.Multiply
            or MongoBinaryOperator.Divide
            or MongoBinaryOperator.IntegerDivide
            or MongoBinaryOperator.Modulo;

    // Inside a $filter's cond the enclosing document is no longer addressable as "$path" — the element is bound to
    // a variable, so a field of it is "$$<var>.<path>". elementVariable is null everywhere else, which is what
    // keeps every pre-existing call site's emitted MQL byte-identical.
    private static BsonValue FieldRef(string path, string? elementVariable)
        => elementVariable is null ? "$" + path : "$$" + elementVariable + "." + path;

    // MongoDB's $dayOfWeek returns 1 (Sunday)..7 (Saturday); .NET's DayOfWeek enum is 0 (Sunday)..6 (Saturday).
    // The subtraction is mandatory, not defensive — omitting it silently shifts every day of the week by one.
    private static BsonValue RenderDatePart(MongoDatePartExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var operand = Render(node.Operand, placeholders, elementVariable);
        return node.Part switch
        {
            MongoDatePart.Year => new BsonDocument("$year", operand),
            MongoDatePart.Month => new BsonDocument("$month", operand),
            MongoDatePart.Day => new BsonDocument("$dayOfMonth", operand),
            MongoDatePart.Hour => new BsonDocument("$hour", operand),
            MongoDatePart.Minute => new BsonDocument("$minute", operand),
            MongoDatePart.Second => new BsonDocument("$second", operand),
            MongoDatePart.Millisecond => new BsonDocument("$millisecond", operand),
            MongoDatePart.DayOfYear => new BsonDocument("$dayOfYear", operand),
            MongoDatePart.DayOfWeek
                => new BsonDocument("$subtract", new BsonArray { new BsonDocument("$dayOfWeek", operand), 1 }),
            MongoDatePart.Date => new BsonDocument("$dateTrunc", new BsonDocument { { "date", operand }, { "unit", "day" } }),
            _ => throw new NativeTranslationNotSupportedException($"Unhandled {nameof(MongoDatePart)} '{node.Part}'.")
        };
    }

    private static BsonValue RenderMath(MongoMathExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        BsonValue Arg(int i) => Render(node.Operands[i], placeholders, elementVariable);

        return node.Function switch
        {
            MongoMathFunction.Abs => new BsonDocument("$abs", Arg(0)),
            MongoMathFunction.Ceiling => new BsonDocument("$ceil", Arg(0)),
            MongoMathFunction.Floor => new BsonDocument("$floor", Arg(0)),
            MongoMathFunction.Exp => new BsonDocument("$exp", Arg(0)),
            MongoMathFunction.Sqrt => new BsonDocument("$sqrt", Arg(0)),
            MongoMathFunction.Truncate => new BsonDocument("$trunc", Arg(0)),
            MongoMathFunction.Round => new BsonDocument("$round", Arg(0)),
            MongoMathFunction.RoundDigits => new BsonDocument("$round", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Ln => new BsonDocument("$ln", Arg(0)),
            MongoMathFunction.Log10 => new BsonDocument("$log10", Arg(0)),
            MongoMathFunction.Log2 => new BsonDocument("$log", new BsonArray { Arg(0), 2 }),
            MongoMathFunction.LogNewBase => new BsonDocument("$log", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Sign => new BsonDocument("$switch", new BsonDocument
            {
                {
                    "branches", new BsonArray
                    {
                        new BsonDocument { { "case", new BsonDocument("$gt", new BsonArray { Arg(0), 0 }) }, { "then", 1 } },
                        new BsonDocument { { "case", new BsonDocument("$lt", new BsonArray { Arg(0), 0 }) }, { "then", -1 } }
                    }
                },
                { "default", 0 }
            }),
            MongoMathFunction.Pow => new BsonDocument("$pow", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Atan2 => new BsonDocument("$atan2", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Max => new BsonDocument("$max", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Min => new BsonDocument("$min", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.DegreesToRadians => new BsonDocument("$degreesToRadians", Arg(0)),
            MongoMathFunction.RadiansToDegrees => new BsonDocument("$radiansToDegrees", Arg(0)),
            MongoMathFunction.Acos => new BsonDocument("$acos", Arg(0)),
            MongoMathFunction.Acosh => new BsonDocument("$acosh", Arg(0)),
            MongoMathFunction.Asin => new BsonDocument("$asin", Arg(0)),
            MongoMathFunction.Asinh => new BsonDocument("$asinh", Arg(0)),
            MongoMathFunction.Atan => new BsonDocument("$atan", Arg(0)),
            MongoMathFunction.Atanh => new BsonDocument("$atanh", Arg(0)),
            MongoMathFunction.Cos => new BsonDocument("$cos", Arg(0)),
            MongoMathFunction.Cosh => new BsonDocument("$cosh", Arg(0)),
            MongoMathFunction.Sin => new BsonDocument("$sin", Arg(0)),
            MongoMathFunction.Sinh => new BsonDocument("$sinh", Arg(0)),
            MongoMathFunction.Tan => new BsonDocument("$tan", Arg(0)),
            MongoMathFunction.Tanh => new BsonDocument("$tanh", Arg(0)),
            _ => throw new NativeTranslationNotSupportedException(
                $"Unhandled {nameof(MongoMathFunction)} '{node.Function}'.")
        };
    }

    private static BsonValue RenderDateAdd(MongoDateAddExpression node, PlaceholderTable placeholders, string? elementVariable)
        => new BsonDocument("$dateAdd", new BsonDocument
        {
            { "startDate", Render(node.StartDate, placeholders, elementVariable) },
            { "unit", UnitName(node.Unit) },
            { "amount", Render(node.Amount, placeholders, elementVariable) }
        });

    private static string UnitName(MongoDateAddUnit unit)
        => unit switch
        {
            MongoDateAddUnit.Year => "year",
            MongoDateAddUnit.Month => "month",
            MongoDateAddUnit.Day => "day",
            MongoDateAddUnit.Hour => "hour",
            MongoDateAddUnit.Minute => "minute",
            MongoDateAddUnit.Second => "second",
            MongoDateAddUnit.Millisecond => "millisecond",
            _ => throw new NativeTranslationNotSupportedException($"Unhandled {nameof(MongoDateAddUnit)} '{unit}'.")
        };

    // A missing or explicitly-null array makes $size a hard server error that aborts the whole aggregate, so an
    // EMBEDDED array path is wrapped in $ifNull (count 0 — what LINQ answers for a missing embedded array). A
    // $lookup output alias is always an array, so that path keeps the plain form and its committed spec
    // baselines stay byte-identical. See MongoSizeExpression's remarks.
    private static BsonValue RenderSize(MongoSizeExpression size, string? elementVariable)
        => size.NullSafe
            ? new BsonDocument("$size",
                new BsonDocument("$ifNull", new BsonArray { FieldRef(size.FieldName, elementVariable), new BsonArray() }))
            : new BsonDocument("$size", FieldRef(size.FieldName, elementVariable));

    private static BsonValue RenderFilteredSize(
        MongoFilteredSizeExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Each nesting level needs its own variable name. Deriving it from the enclosing one ("e", "ee", "eee")
        // keeps them distinct without threading a counter, and keeps every name lowercase-initial, as the
        // server requires of a $filter `as` name.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        // Final-review fix: $filter's own "cond" is evaluated by TRUTHINESS (same hazard as $and/$or/$not),
        // but a BARE ElementPredicate root (e.g. Count(p => b.Flag), no comparison/Not/&&/|| wrapping it) skips
        // every OTHER truthiness guard in this file — RenderBinary's CheckLogicalOperandSerialization only
        // fires when Render actually dispatches into an AndAlso/OrElse node, and RenderUnary's guard only fires
        // for a Not — neither runs when the WHOLE predicate is nothing but a bare field. Checked here,
        // render-time (not translate-time), per this call's own established EF-413 design: a translate-time
        // decline hard-fails this shape in EVERY mode (no alternate representation for a filtered count),
        // while this render-time throw is caught by TryBuildPipeline as a graceful driver-LINQ fallback.
        CheckBooleanRootSerialization(node.ElementPredicate);

        return new BsonDocument("$size",
            new BsonDocument("$filter", new BsonDocument
            {
                // $ifNull is MANDATORY: $filter over a missing or explicitly-null array is a hard server error
                // that aborts the whole aggregate command. [] yields 0, which is what LINQ answers for a missing
                // array.
                { "input", new BsonDocument("$ifNull", new BsonArray { FieldRef(node.ArrayPath, elementVariable), new BsonArray() }) },
                { "as", variable },
                { "cond", Render(node.ElementPredicate, placeholders, variable) }
            }));
    }

    private static BsonValue RenderQuantifier(
        MongoQuantifierExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Same nesting-variable convention as RenderFilteredSize: each nesting level gets its own $map `as`
        // name, derived from the enclosing one, so nested quantifiers/filters never collide.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        // Final-review fix (CRITICAL — silent wrong-data risk): $anyElementTrue/$allElementsTrue evaluate every
        // mapped-to "in" result by TRUTHINESS, exactly like $and/$or/$not. A BARE ElementPredicate root (e.g.
        // `b.Posts.Any(p => b.Flag)`, with no comparison/Not/&&/|| around it) is the one shape none of this
        // file's other truthiness guards catch: the translate-time CanRender check at this node's construction
        // site (MongoExpressionTranslator.TranslateNode's quantifier arm) admits ANY bare field unconditionally
        // via CanRender's own top arm, and a bare field's Render (FieldRef) has no guard of its own — so a
        // value-converted/non-default-represented bare bool (e.g. HasConversion<string>() storing "False", a
        // non-empty — therefore truthy — string) would otherwise render successfully and silently answer the
        // WRONG boolean for every row. Checked here as render-time defense-in-depth (mirroring RenderUnary's/
        // RenderBinary's own pattern for the same hazard).
        CheckBooleanRootSerialization(node.ElementPredicate);

        var map = new BsonDocument("$map", new BsonDocument
        {
            // $ifNull is MANDATORY, same reasoning as RenderFilteredSize/RenderSize: $map over a missing or
            // explicitly-null array is a hard server error. [] yields a $map result of [], and
            // $anyElementTrue/$allElementsTrue over [] answer false/true respectively — exactly LINQ's
            // Any/All-over-an-empty-sequence semantics.
            { "input", new BsonDocument("$ifNull", new BsonArray { Render(node.ArrayPath, placeholders, elementVariable), new BsonArray() }) },
            { "as", variable },
            { "in", Render(node.ElementPredicate, placeholders, variable) }
        });

        var op = node.Kind == MongoExpressionTranslator.MongoQuantifierKind.All ? "$allElementsTrue" : "$anyElementTrue";
        return new BsonDocument(op, map);
    }

    // Mirrors the C# driver's own LINQ v3 aggregation-expression translation for string.StartsWith/Contains/
    // EndsWith (StartsWithContainsOrEndsWithMethodToAggregationExpressionTranslator.CreateAst) exactly, so a
    // field-to-field term (no query-dialect form — MongoDB's $regularExpression pattern must be a literal, not
    // another field) renders byte-identically to the pre-existing driver-LINQ fallback for this shape. No
    // $ifNull guarding: the driver's own translation has none either, so matching it is parity, not a new
    // behavior — see this feature's own design notes. (This byte-identity claim is for the UN-negated test only:
    // the negated case still diverges in SHAPE from the fallback — native emits `$expr:{"$not":[...]}}` where
    // the old fallback emitted `$nor:[{"$expr":...}]` — logically identical but not byte-for-byte, which per this
    // project's AGENTS.md is not a contract concern since MQL shape is not contract.)
    private static BsonValue RenderRegexAsExpr(MongoRegexExpression regex, PlaceholderTable placeholders, string? elementVariable)
    {
        var field = Render(regex.Field, placeholders, elementVariable);
        var term = Render(regex.Term, placeholders, elementVariable);

        BsonValue test = regex.Kind switch
        {
            MongoRegexKind.StartsWith
                => new BsonDocument("$eq", new BsonArray { new BsonDocument("$indexOfCP", new BsonArray { field, term }), 0 }),
            MongoRegexKind.Contains
                => new BsonDocument("$gte", new BsonArray { new BsonDocument("$indexOfCP", new BsonArray { field, term }), 0 }),
            MongoRegexKind.EndsWith => RenderEndsWithAsExpr(field, term),
            _ => throw new NativeTranslationNotSupportedException($"Unsupported {nameof(MongoRegexKind)} '{regex.Kind}'.")
        };

        return regex.Negated ? new BsonDocument("$not", new BsonArray { test }) : test;
    }

    // start = strLenCP(field) - strLenCP(term); true iff start >= 0 AND indexOfCP(field, term, start) == start.
    // Bound ONCE via $let (EF-322 Task 2 review fix — the driver's own CreateAst binds it via
    // AstExpression.Let/UseVarIfNotSimple, not by re-rendering it twice), so this is byte-identical to the
    // pre-existing driver-LINQ fallback's own MQL for this shape rather than merely logically equivalent to
    // it. The driver's OUTER $let (which would bind the string/substring operands themselves) always collapses
    // away for this call site specifically: UseVarIfNotSimple only introduces a variable for a NON-simple
    // operand, and `field`/`term` here are always simple field paths — never a computed sub-expression — so
    // only the inner "start" $let ever survives to be rendered.
    private static BsonValue RenderEndsWithAsExpr(BsonValue field, BsonValue term)
    {
        var start = new BsonDocument("$subtract", new BsonArray
        {
            new BsonDocument("$strLenCP", field),
            new BsonDocument("$strLenCP", term)
        });

        var test = new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$gte", new BsonArray { "$$start", 0 }),
            new BsonDocument("$eq", new BsonArray
            {
                new BsonDocument("$indexOfCP", new BsonArray { field, term, "$$start" }),
                "$$start"
            })
        });

        return new BsonDocument("$let", new BsonDocument
        {
            { "vars", new BsonDocument("start", start) },
            { "in", test }
        });
    }

    // Shared render-time guard for any position where MongoDB evaluates a WHOLE sub-expression's result by
    // TRUTHINESS (a $filter's own "cond", or a quantifier's $map "in") rather than via an operator that
    // produces a genuine computed boolean. Unlike CheckLogicalOperandSerialization (which the AndAlso/OrElse
    // dispatch in RenderBinary already applies to ITS OWN Left/Right whenever one is actually rendered), a
    // BARE field root reaches neither RenderBinary nor RenderUnary at all — Render's own dispatch just returns
    // its raw field ref — so this is the one guard that must be invoked explicitly, by the two callers whose
    // whole ElementPredicate sits in such a position. Nested AndAlso/OrElse/Not underneath need no extra
    // recursion here: when Render actually descends into one, RenderBinary/RenderUnary already re-check their
    // own operands using the identical TryGetBareFieldProperty rule.
    private static void CheckBooleanRootSerialization(MongoExpression node)
    {
        if (MongoExpressionTranslator.IsUnsafeTruthinessRoot(node, out var property))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render '{property.Name}' as a bare boolean predicate root: it does not use default "
                + "BSON serialization, and this position is evaluated by truthiness, which would answer the "
                + "wrong boolean.");
        }
    }

    // Aggregation-dialect $in: { $in: [needle, haystack] }. Negated form wraps in $not (an array-form operator,
    // unlike the query dialect's { field: { $nin: ... } }).
    private static BsonValue RenderIn(MongoInExpression inExpr, PlaceholderTable placeholders, string? elementVariable)
    {
        var needle = FieldRef(inExpr.Field.ElementName, elementVariable);
        var haystack = RenderInValues(inExpr.Values, placeholders);
        var inDoc = new BsonDocument("$in", new BsonArray { needle, haystack });
        return inExpr.Negated ? new BsonDocument("$not", new BsonArray { inDoc }) : inDoc;
    }

    // The computed-needle sibling of RenderIn above: the needle renders through the ordinary recursive
    // Render (a $concat, a field, etc.) rather than FieldRef-by-element-name, since there is no single
    // field path to key on.
    private static BsonValue RenderComputedIn(
        MongoComputedInExpression computedIn, PlaceholderTable placeholders, string? elementVariable)
    {
        var needle = Render(computedIn.Needle, placeholders, elementVariable);
        var haystack = RenderInValues(computedIn.Values, placeholders);
        var inDoc = new BsonDocument("$in", new BsonArray { needle, haystack });
        return computedIn.Negated ? new BsonDocument("$not", new BsonArray { inDoc }) : inDoc;
    }

    private static BsonValue RenderInValues(MongoExpression values, PlaceholderTable placeholders)
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
                // A null ForSerialization means there is no backing IProperty — reached only via a
                // COMPUTED needle's values (TranslateInValuesRaw), so RawElementType carries the needle's CLR
                // type instead; pick a default (representation-less) serializer for it. An ordinary bare-field
                // MongoInExpression never reaches this null branch — TranslateInValues always supplies a real
                // property.
                var elementSerializer = parameter.ForSerialization is not null
                    ? BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization).Serializer
                    : parameter.RawElementType is not null
                        ? BsonSerializerFactory.CreateTypeSerializer(parameter.RawElementType)
                        : StringSerializer.Instance;
                return parameter.ExtractEntityKeyFromArrayElements
                    ? placeholders.CreateEntityKeyArrayPlaceholder(parameter.Name, parameter.ForSerialization!, elementSerializer)
                    : placeholders.CreateArrayPlaceholder(parameter.Name, elementSerializer);
            }
            case MongoValueListExpression list:
            {
                var array = new BsonArray();
                foreach (var element in list.Elements)
                    array.Add(MongoValueRenderer.RenderValue(element, placeholders));
                return array;
            }
            default:
                throw new NativeTranslationNotSupportedException(
                    $"MongoAggregationExpressionRenderer cannot render 'in' values of type '{values.GetType().Name}'.");
        }
    }

    // Aggregation-dialect Not: { $not: [ <expr> ] }. Renderable for any operand CanRender admits — unlike the
    // query dialect's RenderUnary, there is no query-native/bare-field special case here: this renderer only ever
    // runs inside $expr, where $not's array-form operator applies uniformly to every renderable operand.
    private static BsonValue RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders, string? elementVariable)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException($"Unsupported unary operator '{unary.Operator}'.");

        // EF-413 (review fix): $not is TRUTHINESS-based (only false/null/0/undefined are falsy), so negating a
        // value-converted/non-default-represented bool FIELD directly would render successfully but answer the
        // WRONG boolean — e.g. a HasConversion<string>() bool stored as "True"/"False", both non-empty (truthy)
        // strings regardless of the CLR value. Refuse to render rather than silently answer wrong.
        //
        // Deliberately narrow — only a BARE FIELD operand is checked, not any operand AllFieldsDefaultSerialized
        // would walk (a comparison, say). $not over a $eq/$gt/etc. RESULT is always safe regardless of the
        // comparison's own operands' serialization: that result is a genuine computed boolean, not a raw stored
        // value being truthiness-tested, so a recursive check here would over-decline an already-correct shape
        // (e.g. `!(x.A == x.B)` for two non-default-serialized fields is fine — $eq compares converted forms
        // for EQUALITY, which is representation-agnostic, and $not then negates that real boolean correctly).
        //
        // This is a RENDER-time throw, deliberately not a translate-time decline: it is the only gate for a
        // filtered collection count's element predicate (MongoExpressionTranslator's count branch is
        // deliberately gate-free — see its own remarks), where a translate-time null return instead hard-fails
        // the WHOLE leaf in every query mode. Throwing here is caught by
        // MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline for Native/DriverLinq (a graceful
        // driver-LINQ fallback) and surfaces as-is under NativeOnly. For a computed SORT KEY, this operand
        // shape is already declined earlier and more cheaply by
        // NativeSlotPopulator.TryTranslateComputedSortKey's CanRender/AllFieldsDefaultSerialized gate, so this
        // check never actually fires on that path — it exists here for the position that has no such gate.
        // TryGetBareFieldProperty (final-review fix) matches BOTH MongoFieldExpression and
        // MongoOuterFieldExpression — a bare OUTER-scoped bool under Not has the exact same truthiness hazard
        // as an ordinary bare field; missing that sibling here was one of the three (really four) sites the
        // EF-421 final review found checking MongoFieldExpression alone.
        if (MongoExpressionTranslator.IsUnsafeTruthinessRoot(unary.Operand, out var notProperty))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render 'Not' over '{notProperty.Name}': it does not use default BSON "
                + "serialization, and a raw-field $not would answer the wrong boolean.");
        }

        return new BsonDocument("$not", new BsonArray { Render(unary.Operand, placeholders, elementVariable) });
    }

    private static BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders, string? elementVariable)
    {
        // EF-396 (review fix): render-time defense-in-depth, mirroring RenderUnary's own bare-field guard
        // (EF-413). Some callers — MongoExpressionTranslator's filtered-count element-predicate path is
        // deliberately gate-free (see RenderUnary's remarks) — invoke Render directly without going through
        // CanRender first, so the correctness check must also live here, not only in CanRenderLogicalOperand.
        // Nested AndAlso/OrElse operands recurse into this same check naturally, because Render's own
        // dispatch routes any nested MongoBinaryExpression back through RenderBinary.
        if (binary.Operator is MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse)
        {
            CheckLogicalOperandSerialization(binary.Left);
            CheckLogicalOperandSerialization(binary.Right);
        }

        // C# integer division truncates toward zero; MQL's $divide always yields a double. $trunc over the
        // $divide is what reconciles them — and it is not merely cosmetic: without it an integral projection
        // member fails to DESERIALIZE (FormatException, "Truncation resulted in data loss"), and an integral
        // comparison answers off-by-one for any non-exact quotient. See MongoBinaryOperator.IntegerDivide for
        // why the integral-ness decision is made at translate time rather than from the operands here.
        if (binary.Operator == MongoBinaryOperator.IntegerDivide)
        {
            return new BsonDocument("$trunc",
                new BsonDocument("$divide", new BsonArray
                {
                    Render(binary.Left, placeholders, elementVariable),
                    Render(binary.Right, placeholders, elementVariable)
                }));
        }

        var op = binary.Operator switch
        {
            MongoBinaryOperator.Equal => "$eq",
            MongoBinaryOperator.NotEqual => "$ne",
            MongoBinaryOperator.LessThan => "$lt",
            MongoBinaryOperator.LessThanOrEqual => "$lte",
            MongoBinaryOperator.GreaterThan => "$gt",
            MongoBinaryOperator.GreaterThanOrEqual => "$gte",
            MongoBinaryOperator.AndAlso => "$and",
            MongoBinaryOperator.OrElse => "$or",
            MongoBinaryOperator.Add => "$add",
            MongoBinaryOperator.Subtract => "$subtract",
            MongoBinaryOperator.Multiply => "$multiply",
            MongoBinaryOperator.Divide => "$divide",
            MongoBinaryOperator.Modulo => "$mod",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported aggregation operator '{binary.Operator}'.")
        };

        var left = Render(binary.Left, placeholders, elementVariable);
        var right = Render(binary.Right, placeholders, elementVariable);
        return new BsonDocument(op, new BsonArray { left, right });
    }

    private static void CheckLogicalOperandSerialization(MongoExpression operand)
    {
        // TryGetBareFieldProperty (final-review fix) matches BOTH MongoFieldExpression and
        // MongoOuterFieldExpression — see RenderUnary's and CanRenderLogicalOperand's matching comments above.
        if (MongoExpressionTranslator.IsUnsafeTruthinessRoot(operand, out var logicalProperty))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render '{logicalProperty.Name}' as a bare logical (&&/||) operand: it does not use "
                + "default BSON serialization, and $and/$or evaluate operands by truthiness, which would "
                + "answer the wrong boolean.");
        }
    }
}
