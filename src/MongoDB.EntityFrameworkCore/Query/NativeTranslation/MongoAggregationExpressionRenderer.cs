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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a <see cref="MongoExpression"/> subtree to an aggregation expression (the body inside
/// <c>{ $expr: … }</c>), for subtrees with no correct query-dialect rendering.
/// </summary>
internal static class MongoAggregationExpressionRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> to an aggregation-expression <see cref="BsonValue"/>.
    /// </summary>
    /// <param name="node">The root <see cref="MongoExpression"/> subtree to render.</param>
    /// <param name="placeholders">
    /// Receives one entry per <see cref="MongoParameterExpression"/>; its sentinel is embedded in the result.
    /// </param>
    /// <param name="elementVariable">
    /// The <c>$filter</c>/<c>$map</c> <c>as</c> variable in scope, or <see langword="null"/> at the document root.
    /// When set, field refs render as <c>"$$var.path"</c>, since the element is no longer addressable as
    /// <c>$path</c>.
    /// </param>
    /// <returns>
    /// The aggregation-expression body.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type or operator this renderer doesn't handle.
    /// </exception>
    public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
        => node switch
        {
            // See MongoFieldExpression.NullSafe.
            MongoFieldExpression { NullSafe: true } nullSafeField
                => new BsonDocument("$ifNull", new BsonArray { FieldRef(nullSafeField.ElementName, elementVariable), BsonNull.Value }),
            MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
            // Treats a missing element like null (needed for owned-nav null checks: $expr's $eq doesn't).
            MongoElementRefExpression { NullSafe: true } nullSafeElementRef
                => new BsonDocument("$ifNull", new BsonArray { FieldRef(nullSafeElementRef.Path, elementVariable), BsonNull.Value }),
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            // Always at document root, regardless of elementVariable.
            MongoOuterFieldExpression outer => FieldRef(outer.ElementName, elementVariable: null),
            // Used for a Select-side join-scope null-check ternary (`ti.Inner != null ? ti.Inner.City : null`;
            // see NativeJoinScopeProjectionBinder.TryBindConditionalProjection).
            //
            // $ifNull-wrapped: after $lookup + $unwind(preserveNullAndEmptyArrays), an unmatched row's alias is
            // missing, and aggregation $eq/$ne don't equate missing with null, so a bare $ne answers "present"
            // and the ternary picks the wrong branch — silently wrong data. Always root-level: a $lookup alias
            // is never inside a $filter/$map element scope.
            MongoLookupNullCheckExpression lookupNullCheck
                => new BsonDocument(lookupNullCheck.IsNotNull ? "$ne" : "$eq",
                    new BsonArray
                    {
                        new BsonDocument("$ifNull", new BsonArray { FieldRef(lookupNullCheck.LookupAlias, elementVariable: null), BsonNull.Value }),
                        BsonNull.Value
                    }),
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            // Aggregation form of the query dialect's { field: { $type: "number" } }; see
            // MongoNumericTypeBracketExpression. $and short-circuits, so a following $toX never sees a non-number.
            MongoNumericTypeBracketExpression bracket
                => new BsonDocument("$isNumber", Render(bracket.Field, placeholders, elementVariable)),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
            MongoSizeExpression size => RenderSize(size, elementVariable),
            // Array-expression $avg/$max/$min/$sum over an $addToSet output, not a $group accumulator.
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
                    { "then", RenderBranch(conditional.IfTrue, placeholders, elementVariable) },
                    { "else", RenderBranch(conditional.IfFalse, placeholders, elementVariable) }
                }),
            MongoCoalesceExpression coalesce
                => new BsonDocument("$ifNull", new BsonArray
                {
                    RenderBranch(coalesce.Left, placeholders, elementVariable),
                    RenderBranch(coalesce.Right, placeholders, elementVariable)
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
            MongoStringIndexOfExpression indexOf => RenderIndexOf(indexOf, placeholders, elementVariable),
            // Code points, not UTF-16 code units: a surrogate pair counts as 1, not 2 as in .NET. Same as
            // driver-LINQ; see MongoExpressionTranslator.TryMatchStringLength. $strLenCP of null/missing is a
            // server error; EF answers null.
            MongoStringLengthExpression length
                => RenderLength(length, placeholders, elementVariable),
            MongoMathExpression math => RenderMath(math, placeholders, elementVariable),
            MongoTrimExpression trim => RenderTrim(trim, placeholders, elementVariable),
            MongoSubstringExpression substring => RenderSubstring(substring, placeholders, elementVariable),
            // $cmp orders missing below null, where string.Compare(null, null) is 0, so a stored field compared with
            // an operand that may be null reads missing as null. Unlike $eq/$ne this applies inside a $filter/$map
            // element scope too. The $eq/$ne exception exists only to agree with driver-LINQ's element-scope equality;
            // here the wrap changes nothing but a missing-vs-null pair, which .NET (reading both as null) compares as 0.
            MongoStringCompareExpression cmp
                => new BsonDocument("$cmp", new BsonArray
                {
                    MissingAsNullWhenOtherMayBeNull(cmp.Left, cmp.Right, RenderBranch(cmp.Left, placeholders, elementVariable)),
                    MissingAsNullWhenOtherMayBeNull(cmp.Right, cmp.Left, RenderBranch(cmp.Right, placeholders, elementVariable))
                }),
            MongoReplaceExpression replace
                => new BsonDocument("$replaceAll", new BsonDocument
                {
                    { "input", RenderBranch(replace.Input, placeholders, elementVariable) },
                    { "find", RenderBranch(replace.Find, placeholders, elementVariable) },
                    { "replacement", new BsonDocument("$ifNull", new BsonArray
                        { RenderBranch(replace.Replacement, placeholders, elementVariable), "" }) }
                }),
            MongoStringFirstOrLastExpression firstOrLast
                => RenderStringFirstOrLast(firstOrLast, placeholders, elementVariable, nullForNullSource: false),
            MongoQuantifierExpression quantifier => RenderQuantifier(quantifier, placeholders, elementVariable),
            // Constructed nested sub-document (`new Book { Id = e.Id, Title = e.Title }`). Members go through
            // RenderBranch so constants/parameters get $literal-wrapped as at top level; otherwise MongoDB reads a
            // bare number/bool as an inclusion flag and a "$"-prefixed string as a field path.
            MongoDocumentConstructionExpression construction
                => new BsonDocument(construction.Members.Select(
                    m => new BsonElement(m.MemberName, RenderBranch(m.Value, placeholders, elementVariable)))),
            // $concat answers null if any operand is null or missing; C# (and EF) concatenation reads null as "".
            MongoConcatExpression concat
                => new BsonDocument("$concat",
                    new BsonArray(concat.Operands.Select(o => ConcatOperandMayBeNull(o)
                        ? new BsonDocument("$ifNull", new BsonArray { RenderOperand(o, placeholders, elementVariable), "" })
                        : RenderOperand(o, placeholders, elementVariable)))),
            // Any Term shape: callers are all in aggregation scopes with no $regularExpression alternative
            // (top-level $match regexes are rendered by MongoQueryLanguageRenderer instead).
            MongoRegexExpression regex => RenderRegexAsExpr(regex, placeholders, elementVariable),
            // Constructed-tuple comparison operand: an MQL array of the per-member values. Only ever a top-level
            // $eq/$ne operand, unlike MongoValueListExpression.
            MongoTupleExpression tuple
                => new BsonArray(tuple.Elements.Select(e => RenderOperand(e, placeholders, elementVariable))),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Returns whether <see cref="Render"/> would render <paramref name="node"/> without throwing.
    /// </summary>
    /// <remarks>
    /// Must be changed together with <see cref="Render"/>. Aggregation-dialect counterpart of
    /// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>: lets callers decline at translate time instead
    /// of throwing at render time.
    /// <para>
    /// Only consult it where a render-time throw has no fallback (e.g.
    /// <c>NativeSlotPopulator.TryTranslateComputedSortKey</c>). The filtered-count branch of
    /// <see cref="MongoExpressionTranslator"/> deliberately doesn't: its render-time throw is caught by
    /// <c>TryBuildPipeline</c> and falls back to driver-LINQ, whereas a translate-time decline hard-fails in every
    /// mode.
    /// </para>
    /// </remarks>
    public static bool CanRender(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression or MongoLookupNullCheckExpression => true,
            MongoNumericTypeBracketExpression => true,
            MongoConstantExpression or MongoParameterExpression => true,
            // $and/$or test a bare operand by truthiness, so a value-converted bool field (e.g. stored as "N",
            // a truthy string) would render but answer wrong. See CanRenderLogicalOperand; comparison-result
            // operands are safe.
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } logical
                => CanRenderLogicalOperand(logical.Left) && CanRenderLogicalOperand(logical.Right),
            MongoBinaryExpression binary
                => IsRenderableOperator(binary.Operator) && CanRender(binary.Left) && CanRender(binary.Right),
            MongoSizeExpression => true,
            MongoFilteredSizeExpression filtered => CanRender(filtered.ElementPredicate),
            MongoInExpression inExpr => CanRenderInValues(inExpr.Values),
            MongoComputedInExpression computedIn => CanRender(computedIn.Needle) && CanRenderInValues(computedIn.Values),
            // A bare-field operand (MongoFieldExpression or MongoOuterFieldExpression) must be default-serialized,
            // matching RenderUnary's render-time guard, so CanRender=true means Render answers correctly.
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
            MongoStringIndexOfExpression indexOf => CanRender(indexOf.Haystack) && CanRender(indexOf.Needle)
                && (indexOf.Start is null || CanRender(indexOf.Start)),
            MongoStringLengthExpression length => CanRender(length.Operand),
            MongoMathExpression math => math.Operands.All(CanRender),
            MongoTrimExpression trim => CanRender(trim.Source) && (trim.Chars is null || CanRender(trim.Chars)),
            MongoSubstringExpression s => CanRender(s.Source) && CanRender(s.Start) && (s.Length is null || CanRender(s.Length)),
            MongoReplaceExpression replace => CanRender(replace.Input) && CanRender(replace.Find) && CanRender(replace.Replacement),
            MongoStringCompareExpression cmp => CanRender(cmp.Left) && CanRender(cmp.Right),
            MongoStringFirstOrLastExpression firstOrLast => CanRender(firstOrLast.Source),
            MongoQuantifierExpression quantifier => CanRender(quantifier.ArrayPath) && CanRender(quantifier.ElementPredicate),
            MongoConcatExpression concat => concat.Operands.All(CanRender),
            // Relies on RenderRegexAsExpr's switch staying exhaustive for StartsWith/EndsWith/Contains/IsMatch;
            // a new MongoRegexKind must be added to both. Any Term shape is admitted (see Render's arm). IsMatch's
            // Term is always a constant string (TryTranslateRegexIsMatch). Like renders only via $regexMatch with a
            // constant/parameter term (RenderLiteralRegexAsExpr); any other term must decline here rather than
            // crash in RenderRegexAsExpr.
            MongoRegexExpression { Kind: MongoRegexKind.Like, Term: not (MongoConstantExpression { Value: string } or MongoParameterExpression) } => false,
            // The translator only builds Exact with a constant string term (a parameter could be null per
            // execution); RenderLiteralRegexAsExpr throws for field-to-field, so CanRender must agree.
            MongoRegexExpression { Kind: MongoRegexKind.Exact } exact
                => exact.Term is MongoConstantExpression { Value: string },
            // Pattern (Regex.IsMatch(field, constantPattern)): the translator only builds it with a constant
            // string term (no placeholder support for raw patterns), so CanRender mirrors that exactly.
            MongoRegexExpression { Kind: MongoRegexKind.Pattern } pattern
                => pattern.Term is MongoConstantExpression { Value: string },
            MongoRegexExpression regex => CanRender(regex.Field) && CanRender(regex.Term),
            MongoTupleExpression tuple => tuple.Elements.All(CanRender),
            // Constructed nested sub-document (mirrors Render's arm), e.g. a composite anonymous-type needle in
            // `ids.Contains(new { Id1 = ..., Id2 = ... })`.
            MongoDocumentConstructionExpression construction => construction.Members.All(m => CanRender(m.Value)),
            _ => false
        };

    // $and/$or truthiness-test a bare stored operand, so a bare field operand must be default-serialized
    // (mirrors the Not arm and RenderUnary). Nested AndAlso/OrElse recurse; comparisons, constants and
    // parameters fall through to CanRender.
    private static bool CanRenderLogicalOperand(MongoExpression node)
        => node switch
        {
            // Covers MongoOuterFieldExpression too — an outer-scoped bool has the same truthiness hazard.
            MongoFieldExpression or MongoOuterFieldExpression
                => !MongoExpressionTranslator.IsUnsafeTruthinessRoot(node, out _),
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } nested
                => CanRenderLogicalOperand(nested.Left) && CanRenderLogicalOperand(nested.Right),
            _ => CanRender(node)
        };

    private static bool CanRenderInValues(MongoExpression values)
        => values is MongoConstantExpression { Value: System.Collections.IEnumerable } or MongoParameterExpression
            or MongoValueListExpression;

    // Must match RenderBinary's switch (currently every MongoBinaryOperator); re-check when either changes.
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

    // Inside a $filter/$map the element is bound to a variable, so its fields are "$$<var>.<path>".
    private static BsonValue FieldRef(string path, string? elementVariable)
        => elementVariable is null ? "$" + path : "$$" + elementVariable + "." + path;

    // A $cond/$ifNull branch gets the same $literal wrap as a top-level projected constant/parameter: an
    // unwrapped "$"-prefixed string is read as a field path, so `g.Key ?? "$Year"` would return null (or a
    // "$"-prefixed parameter value could throw).
    private static BsonValue RenderBranch(MongoExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var rendered = Render(node, placeholders, elementVariable);
        return node is MongoConstantExpression or MongoParameterExpression
            ? new BsonDocument("$literal", rendered)
            : rendered;
    }

    // An operand position that can hold a string. A constant is wrapped only when its BSON would otherwise be
    // evaluated as an expression (a string as a field path, an array/document element-wise), so numeric operands
    // stay bare; a parameter's value is only known per execution, so it is always wrapped.
    private static BsonValue RenderOperand(MongoExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var rendered = Render(node, placeholders, elementVariable);
        return node is MongoParameterExpression
               || (node is MongoConstantExpression && rendered is BsonString or BsonArray or BsonDocument)
            ? new BsonDocument("$literal", rendered)
            : rendered;
    }

    // $dayOfWeek is 1 (Sunday)..7; .NET DayOfWeek is 0..6. Omitting the subtraction shifts every day by one.
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

    // MongoDB's default $trim whitespace set differs from char.IsWhiteSpace (e.g. it includes U+0000), so the
    // zero-arg overload (Chars is null) renders an explicit "chars" with .NET's whitespace set.
    private static BsonValue RenderTrim(MongoTrimExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var op = node.Side switch
        {
            MongoTrimSide.Both => "$trim",
            MongoTrimSide.Start => "$ltrim",
            MongoTrimSide.End => "$rtrim",
            _ => throw new NativeTranslationNotSupportedException($"Unhandled {nameof(MongoTrimSide)} '{node.Side}'.")
        };

        var spec = new BsonDocument("input", RenderOperand(node.Source, placeholders, elementVariable));
        spec.Add(
            "chars",
            node.Chars is not null
                ? RenderOperand(node.Chars, placeholders, elementVariable)
                : DotNetWhitespaceChars);

        return new BsonDocument(op, spec);
    }

    // No length ⇒ "to the end": start subtracted from the source's own code-point length. $strLenCP is a server
    // error over a null/missing source (aborting the whole query), so it measures { $ifNull: [source, ""] }.
    private static BsonValue RenderSubstring(MongoSubstringExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var source = RenderBranch(node.Source, placeholders, elementVariable);
        var start = Render(node.Start, placeholders, elementVariable);
        var length = node.Length is not null
            ? Render(node.Length, placeholders, elementVariable)
            : new BsonDocument("$subtract", new BsonArray
            {
                new BsonDocument("$strLenCP", new BsonDocument("$ifNull", new BsonArray { source, "" })), start
            });
        return NullPropagating(
            new BsonDocument("$substrCP", new BsonArray { source, start, length }), (node.Source, source));
    }

    private static BsonValue RenderLength(MongoStringLengthExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var operand = RenderOperand(node.Operand, placeholders, elementVariable);
        return NullPropagating(new BsonDocument("$strLenCP", operand), (node.Operand, operand));
    }

    // $indexOfCP already answers null for a null/missing haystack, but a null/missing needle is a server error.
    private static BsonValue RenderIndexOf(MongoStringIndexOfExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var haystack = RenderOperand(node.Haystack, placeholders, elementVariable);
        var needle = RenderBranch(node.Needle, placeholders, elementVariable);
        var args = new BsonArray { haystack, needle };
        if (node.Start is not null)
            args.Add(Render(node.Start, placeholders, elementVariable));

        return NullPropagating(new BsonDocument("$indexOfCP", args), (node.Needle, needle));
    }

    // Narrower than MayBeNull for two operand shapes the translator builds that can't be null: $toString of a
    // non-null constant (a non-string `+` operand), and string.Join's elements, already coalesced to a non-null
    // constant. Not $toString of a non-nullable field: it may be missing (an unmatched join side), answering null.
    private static bool ConcatOperandMayBeNull(MongoExpression operand)
        => operand is not (MongoConvertExpression { Operand: MongoConstantExpression { Value: not null } }
               or MongoCoalesceExpression { Right: MongoConstantExpression { Value: not null } })
           && MayBeNull(operand);

    /// <summary>
    /// C#/EF null propagation for a member-style string call (<c>s.Substring(0, 1)</c> is null for a null
    /// <c>s</c>) whose server operator doesn't propagate a null or missing operand: <c>$substrCP</c> answers
    /// <c>""</c>, <c>$strLenCP</c> and a <c>$indexOfCP</c> needle are server errors. Wraps <paramref name="rendered"/>
    /// in <c>$cond</c> answering null when any listed operand that <see cref="MayBeNull"/> is null or missing
    /// (<c>$cond</c> is lazy, so the operator never sees it). Operands that can't be null leave the MQL unchanged.
    /// </summary>
    /// <remarks>
    /// Reuses each operand's rendered value (cloned) rather than rendering it again, so a parameter operand keeps a
    /// single placeholder-table entry. Operators that already propagate (<c>$trim</c>, <c>$replaceAll</c>,
    /// <c>$toString</c>, a <c>$indexOfCP</c> haystack) aren't wrapped.
    /// </remarks>
    private static BsonValue NullPropagating(BsonValue rendered, params (MongoExpression Node, BsonValue Rendered)[] operands)
    {
        var tests = new BsonArray();
        foreach (var (node, operand) in operands)
        {
            if (MayBeNull(node))
                tests.Add(new BsonDocument("$eq", new BsonArray
                {
                    new BsonDocument("$ifNull", new BsonArray { operand.DeepClone(), BsonNull.Value }), BsonNull.Value
                }));
        }

        return tests.Count == 0
            ? rendered
            : new BsonDocument("$cond", new BsonDocument
            {
                { "if", tests.Count == 1 ? tests[0] : new BsonDocument("$or", tests) },
                { "then", BsonNull.Value },
                { "else", rendered }
            });
    }

    /// <summary>
    /// Every code point <see cref="char.IsWhiteSpace(char)"/> accepts, used as the explicit <c>$trim</c>
    /// <c>"chars"</c> for zero-arg <c>Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c>. Built from <c>char</c> values
    /// because U+2028/U+2029 are line terminators even inside a C# string literal (<c>CS1010</c>).
    /// </summary>
    private static readonly string DotNetWhitespaceChars = new(
    [
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', ' ', '\u0085', ' ', ' ',
        ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ',
        (char)0x2028 /* LINE SEPARATOR */, (char)0x2029 /* PARAGRAPH SEPARATOR */, ' ', ' ', '　'
    ]);

    // Empty-safe char extraction (see MongoStringFirstOrLastExpression): a negative $substrCP start (Last on an
    // empty string) is a server error, so both kinds gate on strLenCP == 0 via $cond (which short-circuits).
    // Both branches yield a one-char string — the empty branch is "\0", not Int32 0 — so a comparison against
    // '\0' never crosses BSON type brackets. Source and its $strLenCP are rendered once and reused.
    //
    // nullForNullSource: a comparison operand (see RenderComparisonOperand) answers null for a null/missing source,
    // as EF's comparison over the null receiver does. Everywhere else (a projected value) a null source behaves like
    // empty and yields '\0'.
    private static BsonValue RenderStringFirstOrLast(
        MongoStringFirstOrLastExpression node, PlaceholderTable placeholders, string? elementVariable, bool nullForNullSource)
    {
        // $strLenCP errors on a null/missing source, so coalesce to "" first; null then behaves like empty
        // (a comparison operand is null-guarded on top; see nullForNullSource).
        var rawSource = RenderOperand(node.Source, placeholders, elementVariable);
        var source = new BsonDocument("$ifNull", new BsonArray { rawSource, "" });
        var length = new BsonDocument("$strLenCP", source);
        var isEmpty = new BsonDocument("$eq", new BsonArray { length, 0 });

        BsonValue start = node.Kind switch
        {
            MongoStringFirstOrLastKind.First => 0,
            MongoStringFirstOrLastKind.Last => new BsonDocument("$subtract", new BsonArray { length, 1 }),
            _ => throw new NativeTranslationNotSupportedException(
                $"Unhandled {nameof(MongoStringFirstOrLastKind)} '{node.Kind}'.")
        };

        var extract = new BsonDocument("$substrCP", new BsonArray { source, start, 1 });

        var rendered = new BsonDocument("$cond", new BsonDocument
        {
            { "if", isEmpty },
            { "then", "\0" },
            { "else", extract }
        });

        return nullForNullSource ? NullPropagating(rendered, (node.Source, rawSource)) : rendered;
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

    // $size over a missing or null array is a server error, so an embedded path is $ifNull-wrapped (count 0, as
    // LINQ answers). A $lookup alias is always an array and keeps the plain form. See MongoSizeExpression.
    private static BsonValue RenderSize(MongoSizeExpression size, string? elementVariable)
        => size.NullSafe
            ? new BsonDocument("$size",
                new BsonDocument("$ifNull", new BsonArray { FieldRef(size.FieldName, elementVariable), new BsonArray() }))
            : new BsonDocument("$size", FieldRef(size.FieldName, elementVariable));

    private static BsonValue RenderFilteredSize(
        MongoFilteredSizeExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Per-level variable names ("e", "ee", "eee") stay distinct without a counter and lowercase-initial, as
        // the server requires of an `as` name.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        // $filter's cond is truthiness-tested, and a bare-field predicate (Count(p => b.Flag)) bypasses the
        // AndAlso/OrElse/Not guards. Checked at render time: a translate-time decline would hard-fail in every
        // mode, whereas this throw is caught by TryBuildPipeline as a driver-LINQ fallback.
        CheckBooleanRootSerialization(node.ElementPredicate);

        return new BsonDocument("$size",
            new BsonDocument("$filter", new BsonDocument
            {
                // $filter over a missing or null array is a server error; [] yields 0, as LINQ answers.
                { "input", new BsonDocument("$ifNull", new BsonArray { FieldRef(node.ArrayPath, elementVariable), new BsonArray() }) },
                { "as", variable },
                { "cond", Render(node.ElementPredicate, placeholders, variable) }
            }));
    }

    private static BsonValue RenderQuantifier(
        MongoQuantifierExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Per-level `as` names, as in RenderFilteredSize.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        // $anyElementTrue/$allElementsTrue truthiness-test each "in" result, and a bare-field predicate
        // (`b.Posts.Any(p => b.Flag)`) passes CanRender unchecked, so a value-converted bool (stored as "False",
        // truthy) would silently answer wrong for every row. Guard at render time.
        CheckBooleanRootSerialization(node.ElementPredicate);

        var map = new BsonDocument("$map", new BsonDocument
        {
            // $map over a missing or null array is a server error; over [] $anyElementTrue/$allElementsTrue
            // answer false/true, matching LINQ Any/All on an empty sequence.
            { "input", new BsonDocument("$ifNull", new BsonArray { Render(node.ArrayPath, placeholders, elementVariable), new BsonArray() }) },
            { "as", variable },
            { "in", Render(node.ElementPredicate, placeholders, variable) }
        });

        var op = node.Kind == MongoExpressionTranslator.MongoQuantifierKind.All ? "$allElementsTrue" : "$anyElementTrue";
        return new BsonDocument(op, map);
    }

    // Constant/parameter terms render as $regexMatch (RenderLiteralRegexAsExpr). Field-to-field terms, which have
    // no regex form ($regularExpression needs a literal pattern), mirror the driver's
    // StartsWithContainsOrEndsWithMethodToAggregationExpressionTranslator.CreateAst, with no $ifNull guarding.
    private static BsonValue RenderRegexAsExpr(MongoRegexExpression regex, PlaceholderTable placeholders, string? elementVariable)
    {
        var field = Render(regex.Field, placeholders, elementVariable);

        // A constant/parameter term (and Exact always) renders via $regexMatch with the same BSON regex the query
        // dialect uses, never the $toLower fold below: $toLower only folds ASCII, so an OrdinalIgnoreCase term
        // would silently miss a non-ASCII match (e.g. "École" vs "école"), whereas the regex "i" option is
        // Unicode-correct. $regexMatch is false for a null/missing input, matching the query dialect.
        if (regex.Kind == MongoRegexKind.Exact
            || (regex.Kind is not (MongoRegexKind.IsMatch or MongoRegexKind.Pattern)
                && regex.Term is MongoConstantExpression { Value: string } or MongoParameterExpression))
            return RenderLiteralRegexAsExpr(regex, field, placeholders);

        // Pattern (Regex.IsMatch(field, constantPattern)): options come from PatternOptions verbatim, never
        // CaseInsensitive/$toLower — must precede the fold below, which never applies to it (CaseInsensitive is
        // always false for Pattern) but is handled explicitly for clarity, matching Exact.
        if (regex.Kind == MongoRegexKind.Pattern)
        {
            var patternTerm = RenderOperand(regex.Term, placeholders, elementVariable);
            var patternMatch = new BsonDocument("$regexMatch",
                new BsonDocument { { "input", field }, { "regex", patternTerm }, { "options", regex.PatternOptions } });
            return regex.Negated ? new BsonDocument("$not", new BsonArray { patternMatch }) : patternMatch;
        }

        var term = RenderOperand(regex.Term, placeholders, elementVariable);

        // $indexOfCP/$strLenCP have no case-insensitive option, so an OrdinalIgnoreCase term folds both
        // operands through $toLower (ASCII-only; unreachable for field-to-field today, which the translator declines
        // when case-insensitive). IsMatch is excluded: $regexMatch has its own "options".
        if (regex.CaseInsensitive && regex.Kind != MongoRegexKind.IsMatch)
        {
            field = new BsonDocument("$toLower", field);
            term = new BsonDocument("$toLower", term);
        }

        BsonValue test = regex.Kind switch
        {
            MongoRegexKind.StartsWith
                => new BsonDocument("$eq", new BsonArray { new BsonDocument("$indexOfCP", new BsonArray { field, term }), 0 }),
            MongoRegexKind.Contains
                => new BsonDocument("$gte", new BsonArray { new BsonDocument("$indexOfCP", new BsonArray { field, term }), 0 }),
            MongoRegexKind.EndsWith => RenderEndsWithAsExpr(field, term),
            // Regex.IsMatch(constantInput, fieldPattern): roles are swapped (see MongoRegexKind.IsMatch), so
            // Field feeds $regexMatch's "regex" and Term its "input". $regexMatch accepts a field-valued regex,
            // which $regularExpression does not.
            MongoRegexKind.IsMatch => new BsonDocument("$regexMatch", new BsonDocument
            {
                { "input", term },
                { "regex", field },
                { "options", regex.CaseInsensitive ? "i" : "" }
            }),
            _ => throw new NativeTranslationNotSupportedException($"Unsupported {nameof(MongoRegexKind)} '{regex.Kind}'.")
        };

        return regex.Negated ? new BsonDocument("$not", new BsonArray { test }) : test;
    }

    // $regexMatch with a literal BSON regex: a constant term bakes the pattern now; a parameter becomes a regex
    // placeholder resolved per execution, matching MongoQueryLanguageRenderer.RenderRegex's constant/parameter
    // handling (including Like's "is"). A field-to-field Exact term has no query- or expression-dialect form (the
    // translator never builds one; see MongoExpressionTranslator.StringEquals.cs) and throws rather than silently
    // folding through $toLower.
    private static BsonValue RenderLiteralRegexAsExpr(MongoRegexExpression regex, BsonValue field, PlaceholderTable placeholders)
    {
        var caseInsensitive = regex.Kind == MongoRegexKind.Like || regex.CaseInsensitive;
        BsonValue regexValue = regex.Term switch
        {
            MongoConstantExpression { Value: string literal } => new BsonRegularExpression(
                MongoRegexPatternBuilder.BuildPattern(literal, regex.Kind), caseInsensitive ? "is" : "s"),
            MongoParameterExpression parameter =>
                placeholders.CreateRegexPlaceholder(parameter.Name, regex.Kind, caseInsensitive),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer cannot render a field-to-field {nameof(MongoRegexKind)}."
                + $"{nameof(MongoRegexKind.Exact)} term.")
        };

        var regexMatch = new BsonDocument("$regexMatch", new BsonDocument { { "input", field }, { "regex", regexValue } });
        return regex.Negated ? new BsonDocument("$not", new BsonArray { regexMatch }) : regexMatch;
    }

    // start = strLenCP(field) - strLenCP(term); true iff start >= 0 and indexOfCP(field, term, start) == start.
    // "start" is bound once via $let, matching the driver's CreateAst output.
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

    // Guard for positions that truthiness-test a whole sub-expression ($filter cond, quantifier $map "in"). A
    // bare-field root reaches neither RenderBinary nor RenderUnary, so these callers must invoke it explicitly;
    // nested AndAlso/OrElse/Not re-check their own operands.
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
        var haystack = new BsonDocument("$literal", RenderInValues(inExpr.Values, placeholders));
        var inDoc = new BsonDocument("$in", new BsonArray { needle, haystack });
        return inExpr.Negated ? new BsonDocument("$not", new BsonArray { inDoc }) : inDoc;
    }

    // Computed-needle variant of RenderIn: the needle renders through Render (no single field path).
    private static BsonValue RenderComputedIn(
        MongoComputedInExpression computedIn, PlaceholderTable placeholders, string? elementVariable)
    {
        var needle = Render(computedIn.Needle, placeholders, elementVariable);
        var haystack = new BsonDocument("$literal", RenderInValues(computedIn.Values, placeholders));
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
                // No backing property: only for a computed needle (TranslateInValuesRaw), so use a default
                // serializer for RawElementType.
                var elementSerializer = parameter.ForSerialization is not null
                    ? BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization).Serializer
                    : parameter.RawElementType is { } rawElementType
                        ? typeof(ITuple).IsAssignableFrom(rawElementType)
                            // The driver's own Tuple/ValueTuple serializers write one BSON array per tuple, matching how
                            // a MongoTupleExpression needle renders; BsonSerializerFactory.CreateTypeSerializer would fall
                            // through to a class-map (document) serializer for a tuple type.
                            ? BsonSerializer.LookupSerializer(rawElementType)
                            : BsonSerializerFactory.CreateTypeSerializer(rawElementType)
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

    // Aggregation-dialect Not: { $not: [ <expr> ] }, for any operand CanRender admits (always inside $expr).
    private static BsonValue RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders, string? elementVariable)
    {
        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException($"Unsupported unary operator '{unary.Operator}'.");

        // $not is truthiness-based, so negating a value-converted bool field directly (e.g. stored as
        // "True"/"False", both truthy) would answer wrong; refuse to render. Only a bare field (including
        // MongoOuterFieldExpression) is checked: $not over a comparison result is always correct.
        //
        // Render-time rather than translate-time: this is the only gate for a filtered count's element predicate,
        // where a translate-time decline hard-fails in every mode, while this throw is caught by TryBuildPipeline
        // (driver-LINQ fallback) and surfaces as-is under NativeOnly. Computed sort keys are already declined
        // earlier by NativeSlotPopulator.TryTranslateComputedSortKey.
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
        // Render-time counterpart of CanRenderLogicalOperand, for callers that skip CanRender (the filtered-count
        // path; see RenderUnary). Nested AndAlso/OrElse recurse through RenderBinary.
        if (binary.Operator is MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse)
        {
            CheckLogicalOperandSerialization(binary.Left);
            CheckLogicalOperandSerialization(binary.Right);
        }

        // C# integer division truncates; $divide yields a double. Without $trunc an integral member fails to
        // deserialize ("Truncation resulted in data loss") and integral comparisons are off by one. See
        // MongoBinaryOperator.IntegerDivide for why integral-ness is decided at translate time.
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

        // A comparison operand can be a string; arithmetic and logical operands never are.
        var isComparison = binary.Operator is MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual
            or MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual
            or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual;
        var left = isComparison
            ? RenderComparisonOperand(binary.Left, placeholders, elementVariable)
            : Render(binary.Left, placeholders, elementVariable);
        var right = isComparison
            ? RenderComparisonOperand(binary.Right, placeholders, elementVariable)
            : Render(binary.Right, placeholders, elementVariable);

        // Document root only: inside a $filter/$map element scope, missing-vs-null stays distinguished, matching
        // driver-LINQ (pinned by NativeOwnedCollectionFilteredCountTests.Filtered_count_null_check_diverges_...).
        if (binary.Operator is MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual && elementVariable is null)
        {
            left = MissingAsNullWhenComparedToNull(binary.Left, binary.Right, left);
            right = MissingAsNullWhenComparedToNull(binary.Right, binary.Left, right);
        }

        var rendered = new BsonDocument(op, new BsonArray { left, right });

        // Relational null-ordering guard (disjoint from the ==/!= missing-as-null rule above). Element-scoped
        // predicates ($filter cond, quantifier $map "in") are guarded too: the reused operand is already rendered
        // against the element variable ($$e.<field>), and a missing element field fails `$gt: [missing, null]` just
        // as a missing document field does.
        //
        // The guard reuses the operand's final rendered value (cloned, so the two positions don't share one
        // instance) rather than rendering it again, so a parameter operand keeps a single placeholder-table entry.
        return LowerRelationalOperand(binary) is { } lower && MayBeNull(lower)
            ? new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$gt", new BsonArray
                {
                    (ReferenceEquals(lower, binary.Left) ? left : right).DeepClone(), BsonNull.Value
                }),
                rendered
            })
            : rendered;
    }

    // A comparison operand that is a string FirstOrDefault()/LastOrDefault() renders null (not '\0') for a
    // null/missing receiver (see RenderStringFirstOrLast), as C#/EF compare the null: `== '\0'` is false, `!= '\0'`
    // true, and the relational null guard below makes `<` false. Negation stays exact: $eq/$ne partition the null
    // form too, and a negated relational comparison $not-wraps the guarded node.
    private static BsonValue RenderComparisonOperand(
        MongoExpression node, PlaceholderTable placeholders, string? elementVariable)
        => node is MongoStringFirstOrLastExpression firstOrLast
            ? RenderStringFirstOrLast(firstOrLast, placeholders, elementVariable, nullForNullSource: true)
            : RenderOperand(node, placeholders, elementVariable);

    // $eq/$ne don't equate a missing field with null (the query dialect's { field: null } does, and so does .NET,
    // which reads both as null), so `x.S == null` would be false for a missing S. Wrap a bare stored field in
    // { $ifNull: [ field, null ] } against a null constant, or a parameter (null only known per execution).
    // Computed operands already yield null (not missing) for a missing input.
    private static BsonValue MissingAsNullWhenComparedToNull(MongoExpression operand, MongoExpression other, BsonValue rendered)
        => other is MongoConstantExpression { Value: null } or MongoParameterExpression
            ? MissingAsNull(operand, rendered)
            : rendered;

    // $cmp counterpart: missing and null only compare differently against an operand that may itself be null.
    private static BsonValue MissingAsNullWhenOtherMayBeNull(MongoExpression operand, MongoExpression other, BsonValue rendered)
        => MayBeNull(other) ? MissingAsNull(operand, rendered) : rendered;

    private static BsonValue MissingAsNull(MongoExpression operand, BsonValue rendered)
        => operand is MongoFieldExpression { NullSafe: false } or MongoOuterFieldExpression
               or MongoElementRefExpression { NullSafe: false, Path: not MongoElementRefExpression.WholeRootDocumentPath }
            ? new BsonDocument("$ifNull", new BsonArray { rendered, BsonNull.Value })
            : rendered;

    // The operand on the "less" side of a relational comparison: the Left of </<=, the Right of >/>=. Null for any
    // other operator.
    //
    // The aggregation dialect orders null (and missing) below every value, so `$lt: [null, 5]`, `$lte: [null, null]`,
    // `$gt: [5, null]` and `$gte: [null, null]` are all true, where C# lifted comparison semantics answer false for
    // any null operand. Only the less side can make a relational operator wrongly true (a non-null lower side never
    // orders below a null or missing greater side), so RenderBinary conjoins `$gt: [lower, null]` (true exactly when
    // the lower operand is neither null nor missing; missing orders below null) whenever that side may be null. Structural here rather than at each
    // construction site, so every aggregation-dialect comparison (HAVING $match, $cond inside $group, $project
    // ternaries, $expr fall-through) gets it. Negation stays exact: every negator $not-wraps a relational operator,
    // and the guard is inside the wrapped node, so the complement of the guarded pair is exactly C#'s !(a < b).
    private static MongoExpression? LowerRelationalOperand(MongoBinaryExpression binary)
        => binary.Operator switch
        {
            MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual => binary.Left,
            MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual => binary.Right,
            _ => null
        };

    /// <summary>
    /// Whether <paramref name="node"/> may evaluate to null or missing in the aggregation dialect, judged
    /// conservatively from its CLR type (a nullable value type or a reference type) and from null-propagating
    /// operators over such an operand. A non-null constant never does; a query parameter always may (its value is
    /// only known at execution).
    /// </summary>
    internal static bool MayBeNull(MongoExpression node)
        => node switch
        {
            MongoConstantExpression constant => constant.Value is null,
            // Rendered as $ifNull: [field, null], so it is null (never missing) when absent.
            MongoFieldExpression { NullSafe: true } or MongoElementRefExpression { NullSafe: true } => true,
            // A read of an upstream alias holding a null-propagated value behind a non-nullable type.
            MongoElementRefExpression { ThrowsOnNull: true } => true,
            MongoParameterExpression => true,
            // Arithmetic ($add/$subtract/..., $trunc of $divide) propagates a null operand.
            MongoBinaryExpression { Operator: not (MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse
                or MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual or MongoBinaryOperator.LessThan
                or MongoBinaryOperator.LessThanOrEqual or MongoBinaryOperator.GreaterThan
                or MongoBinaryOperator.GreaterThanOrEqual) } arithmetic
                => IsNullableClrType(arithmetic.Type) || MayBeNull(arithmetic.Left) || MayBeNull(arithmetic.Right),
            // $toX, $year/$month/..., and the math operators all return null for a null input.
            MongoConvertExpression convert => IsNullableClrType(convert.Type) || MayBeNull(convert.Operand),
            MongoDatePartExpression datePart => MayBeNull(datePart.Operand),
            MongoMathExpression math => IsNullableClrType(math.Type) || math.Operands.Any(MayBeNull),
            // Null-guarded or null-propagating over a possibly-null string (see NullPropagating), so null where
            // EF answers null.
            MongoStringLengthExpression length => MayBeNull(length.Operand),
            MongoStringIndexOfExpression indexOf => MayBeNull(indexOf.Haystack) || MayBeNull(indexOf.Needle),
            // Every possibly-null operand is coalesced to "".
            MongoConcatExpression => false,
            // Only its comparison-operand form is ever null (see RenderComparisonOperand); elsewhere it's '\0'.
            MongoStringFirstOrLastExpression firstOrLast => MayBeNull(firstOrLast.Source),
            _ => IsNullableClrType(node.Type)
        };

    /// <summary>
    /// Whether <paramref name="node"/>, whose CLR type is a non-nullable value type, may nonetheless evaluate to null:
    /// <c>$strLenCP</c>/<c>$indexOfCP</c> over a possibly-null string (null-guarded, see <see cref="NullPropagating"/>),
    /// directly or through arithmetic, a cast, a math function or a conditional branch. Read back as that non-nullable type, the null would
    /// silently become <c>default(T)</c> (a <c>0</c> length) where EF throws, so a group key, <c>$min</c>/<c>$max</c>/
    /// <c>$avg</c> or <c>$push</c> operand declines, and a projection leaf throws on null or declines (see
    /// <see cref="ClassifyNonNullableValueRead"/>); a nullable cast (<c>(int?)s.Length</c>) reads the null and stays
    /// native. Filters are unaffected: the relational null guard covers them.
    /// </summary>
    internal static bool MayBeNullBehindNonNullableType(MongoExpression node)
        => WalkNullBehindNonNullableType(node, []).MayBeNull;

    // What one walk of a value finds about a null behind its non-nullable type: whether it may be null at all
    // (MayBeNullBehindNonNullableType), and whether an operator between that null and the value may absorb it into a
    // non-null answer (ClassifyNonNullableValueRead's Decline). One walker answers both so they can't cover different
    // nodes: a node one saw and the other didn't would classify a hidden Math.Max as ThrowOnNull (a silent wrong value).
    private readonly record struct NullBehindNonNullable(bool MayBeNull, bool MayAbsorb)
    {
        public static NullBehindNonNullable operator |(NullBehindNonNullable a, NullBehindNonNullable b)
            => new(a.MayBeNull || b.MayBeNull, a.MayAbsorb || b.MayAbsorb);
    }

    // nonNull: operands a conditional's test has proven non-null on the branch being inspected
    // (`s == null ? 0 : s.Length` reads Length only where s isn't null).
    private static NullBehindNonNullable WalkNullBehindNonNullableType(MongoExpression node, IReadOnlyList<MongoExpression> nonNull)
        => node switch
        {
            MongoStringLengthExpression length => new(MayBeNullUnlessProven(length.Operand, nonNull), false),
            MongoStringIndexOfExpression indexOf
                => new(MayBeNullUnlessProven(indexOf.Haystack, nonNull) || MayBeNullUnlessProven(indexOf.Needle, nonNull), false),
            // An upstream alias (a Distinct's flattened key, a bare Select's `_v`, a set-op operand) the emit side
            // flagged MongoProjection.ThrowsOnNull: the same possibly-null value, read by reference.
            MongoElementRefExpression { ThrowsOnNull: true } => new(true, false),
            MongoBinaryExpression { Operator: not (MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse
                or MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual or MongoBinaryOperator.LessThan
                or MongoBinaryOperator.LessThanOrEqual or MongoBinaryOperator.GreaterThan
                or MongoBinaryOperator.GreaterThanOrEqual) } arithmetic
                => WalkNullBehindNonNullableType(arithmetic.Left, nonNull) | WalkNullBehindNonNullableType(arithmetic.Right, nonNull),
            MongoConvertExpression convert => WalkNullBehindNonNullableType(convert.Operand, nonNull),
            MongoMathExpression math => WalkMath(math, nonNull),
            // Either branch may be the value read, each under what the test proves on it: `s == null ? a : b` and
            // `string.IsNullOrEmpty(s) ? a : b` make s non-null in b; `s != null ? a : b` makes it non-null in a.
            MongoConditionalExpression conditional
                => WalkNullBehindNonNullableType(conditional.IfTrue, [.. nonNull, .. ProvenNonNullWhen(conditional.Test, true)])
                   | WalkNullBehindNonNullableType(conditional.IfFalse, [.. nonNull, .. ProvenNonNullWhen(conditional.Test, false)]),
            _ => default
        };

    // $max/$min skip a null operand and Sign's $switch orders it below 0 (MongoGroupElementTranslator
    // .IsNullPropagatingMathFunction), so over a possibly-null operand they may answer non-null where EF throws.
    private static NullBehindNonNullable WalkMath(MongoMathExpression math, IReadOnlyList<MongoExpression> nonNull)
    {
        var operands = default(NullBehindNonNullable);
        foreach (var operand in math.Operands)
            operands |= WalkNullBehindNonNullableType(operand, nonNull);

        return MongoGroupElementTranslator.IsNullPropagatingMathFunction(math.Function)
            ? operands
            : operands with { MayAbsorb = operands.MayAbsorb || operands.MayBeNull };
    }

    // A primary-key field is never null in a stored document, so its Length/IndexOf is not null behind the non-nullable
    // type. Only the root document's own key (element "_id", unprefixed): a join's inner-scope field is a prefixed path
    // ("inner._id"), and under a left-outer join that sub-document is missing for an unmatched row, so it may be null.
    // Deliberately local to this predicate, not in MayBeNull: MayBeNull also drives the render-time $ifNull guards.
    private static bool MayBeNullUnlessProven(MongoExpression operand, IReadOnlyList<MongoExpression> nonNull)
        => MayBeNull(operand)
           && !(operand is MongoFieldExpression { NullSafe: false, ElementName: "_id" } field
                 && field.Property.IsPrimaryKey())
           && !nonNull.Any(proven => IsSameStoredValue(proven, operand));

    // The operands that `test` answering `outcome` proves non-null. Structural: only a null comparison of a stored
    // value, combined through ||/&&/! the way that preserves the proof (a false `a || b` makes both false; a true
    // `a && b` makes both true).
    private static IEnumerable<MongoExpression> ProvenNonNullWhen(MongoExpression test, bool outcome)
        => test switch
        {
            MongoBinaryExpression { Operator: MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual } comparison
                when (comparison.Operator == MongoBinaryOperator.NotEqual) == outcome
                     && NullComparedOperand(comparison) is { } operand
                => [operand],
            MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } orElse when !outcome
                => ProvenNonNullWhen(orElse.Left, false).Concat(ProvenNonNullWhen(orElse.Right, false)),
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } andAlso when outcome
                => ProvenNonNullWhen(andAlso.Left, true).Concat(ProvenNonNullWhen(andAlso.Right, true)),
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not } negation => ProvenNonNullWhen(negation.Operand, !outcome),
            _ => []
        };

    private static MongoExpression? NullComparedOperand(MongoBinaryExpression comparison)
        => comparison.Right is MongoConstantExpression { Value: null } ? comparison.Left
            : comparison.Left is MongoConstantExpression { Value: null } ? comparison.Right
            : null;

    // The same stored value by document path (a field's NullSafe rendering is irrelevant to whether it is null).
    private static bool IsSameStoredValue(MongoExpression a, MongoExpression b)
        => (a, b) switch
        {
            (MongoFieldExpression fa, MongoFieldExpression fb) => fa.ElementName == fb.ElementName,
            (MongoOuterFieldExpression oa, MongoOuterFieldExpression ob) => oa.ElementName == ob.ElementName,
            (MongoElementRefExpression ea, MongoElementRefExpression eb) => ea.Path == eb.Path,
            _ => false
        };

    /// <summary>
    /// <see cref="MayBeNullBehindNonNullableType(MongoExpression)"/> for a value read back as <paramref name="readType"/>: true only
    /// when that type is a non-nullable value type.
    /// </summary>
    internal static bool ReadsNullAsDefault(Type readType, MongoExpression node)
        => readType.IsValueType && Nullable.GetUnderlyingType(readType) is null && MayBeNullBehindNonNullableType(node);

    /// <summary>
    /// How a projection leaf read back as <paramref name="readType"/> handles a null behind the non-nullable type
    /// (<see cref="ReadsNullAsDefault"/>): <see cref="NonNullableValueRead.Plain"/> when there is none;
    /// <see cref="NonNullableValueRead.ThrowOnNull"/> when every operator between that null and the leaf's value
    /// propagates it, so reading the leaf as <c>T?</c> and throwing on null is exactly EF's
    /// "Nullable object must have a value."; <see cref="NonNullableValueRead.Decline"/> when an operator may absorb it
    /// into a non-null answer where EF throws (<c>Math.Max(s.Length, t.Length)</c>: <c>$max</c>/<c>$min</c> skip a null
    /// operand, and <c>Math.Sign</c>'s <c>$switch</c> orders it below 0), which no read of the value can detect.
    /// </summary>
    /// <remarks>
    /// The one call a projection binder makes for a value leaf; its answer is both the gate and the read side's
    /// <c>MongoProjection.ThrowsOnNull</c>.
    /// </remarks>
    internal static NonNullableValueRead ClassifyNonNullableValueRead(Type readType, MongoExpression node)
    {
        if (!readType.IsValueType || Nullable.GetUnderlyingType(readType) is not null)
            return NonNullableValueRead.Plain;

        var walk = WalkNullBehindNonNullableType(node, []);
        return !walk.MayBeNull ? NonNullableValueRead.Plain
            : walk.MayAbsorb ? NonNullableValueRead.Decline
            : NonNullableValueRead.ThrowOnNull;
    }

    private static bool IsNullableClrType(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static void CheckLogicalOperandSerialization(MongoExpression operand)
    {
        // Matches MongoOuterFieldExpression too; see RenderUnary.
        if (MongoExpressionTranslator.IsUnsafeTruthinessRoot(operand, out var logicalProperty))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render '{logicalProperty.Name}' as a bare logical (&&/||) operand: it does not use "
                + "default BSON serialization, and $and/$or evaluate operands by truthiness, which would "
                + "answer the wrong boolean.");
        }
    }
}

/// <summary>
/// How a projection value read back as a non-nullable value type treats a server null; see
/// <see cref="MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead"/>.
/// </summary>
internal enum NonNullableValueRead
{
    /// <summary>The value is never null behind the non-nullable type: read it as usual.</summary>
    Plain,

    /// <summary>The value may be null and the null reaches it: read as <c>T?</c> and throw EF's exception on null.</summary>
    ThrowOnNull,

    /// <summary>An operator may absorb the null into a non-null answer where EF throws: decline.</summary>
    Decline
}
