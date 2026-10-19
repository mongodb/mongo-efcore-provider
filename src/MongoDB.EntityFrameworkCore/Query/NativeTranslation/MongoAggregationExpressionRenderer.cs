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
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

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
    /// <param name="node">The expression to render.</param>
    /// <param name="placeholders">Receives one entry per <see cref="MongoParameterExpression"/>.</param>
    /// <param name="elementVariable">
    /// The <c>$filter</c>/<c>$map</c> <c>as</c> variable in scope, or <see langword="null"/> at the document root.
    /// When set, field refs render as <c>"$$var.path"</c>.
    /// </param>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type or operator this renderer doesn't handle.
    /// </exception>
    public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
        => node switch
        {
            MongoFieldExpression { NullSafe: true } nullSafeField
                => IfNull(FieldRef(nullSafeField.ElementName, elementVariable), BsonNull.Value),
            MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
            // Treats a missing element like null (needed for owned-nav null checks: $expr's $eq doesn't).
            MongoElementRefExpression { NullSafe: true } nullSafeElementRef
                => IfNull(FieldRef(nullSafeElementRef.Path, elementVariable), BsonNull.Value),
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            // Always at document root, regardless of elementVariable.
            MongoOuterFieldExpression outer => FieldRef(outer.ElementName, elementVariable: null),
            // Join-scope null-check ternary (NativeJoinScopeProjectionBinder.TryBindConditionalProjection).
            // $ifNull-wrapped: after $lookup + $unwind(preserveNullAndEmptyArrays) an unmatched row's alias is
            // missing, and aggregation $eq/$ne don't equate missing with null, so a bare $ne would answer "present".
            // Always root-level: a $lookup alias is never inside a $filter/$map element scope.
            MongoLookupNullCheckExpression lookupNullCheck
                => new BsonDocument(lookupNullCheck.IsNotNull ? "$ne" : "$eq",
                    new BsonArray
                    {
                        IfNull(FieldRef(lookupNullCheck.LookupAlias, elementVariable: null), BsonNull.Value),
                        BsonNull.Value
                    }),
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            // $and short-circuits, so a following $toX never sees a non-number.
            MongoNumericTypeBracketExpression bracket
                => new BsonDocument("$isNumber", Render(bracket.Field, placeholders, elementVariable)),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
            MongoSizeExpression size => RenderSize(size, elementVariable),
            MongoFieldPresenceExpression presence => RenderFieldPresence(presence, elementVariable),
            // Array-expression $avg/$max/$min/$sum over an $addToSet output, not a $group accumulator.
            MongoArrayReduceExpression arrayReduce
                => new BsonDocument(arrayReduce.OperatorName, FieldRef(arrayReduce.FieldName, elementVariable)),
            MongoFilteredSizeExpression filtered => RenderFilteredSize(filtered, placeholders, elementVariable),
            MongoMappedArrayReduceExpression mappedReduce => RenderMappedArrayReduce(mappedReduce, placeholders, elementVariable),
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
                => IfNull(
                    RenderBranch(coalesce.Left, placeholders, elementVariable),
                    RenderBranch(coalesce.Right, placeholders, elementVariable)),
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
            // Code points, not UTF-16 code units (a surrogate pair counts as 1), same as driver-LINQ; see
            // MongoExpressionTranslator.TryMatchStringLength.
            MongoStringLengthExpression length
                => RenderLength(length, placeholders, elementVariable),
            MongoMathExpression math => RenderMath(math, placeholders, elementVariable),
            MongoTrimExpression trim => RenderTrim(trim, placeholders, elementVariable),
            MongoSubstringExpression substring => RenderSubstring(substring, placeholders, elementVariable),
            // $cmp orders missing below null, where string.Compare(null, null) is 0, so a stored field compared with
            // an operand that may be null reads missing as null. Unlike $eq/$ne this applies inside element scopes too
            // (that exception only agrees with driver-LINQ's element-scope equality).
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
                    { "replacement", IfNull(RenderBranch(replace.Replacement, placeholders, elementVariable), "") }
                }),
            MongoStringFirstOrLastExpression firstOrLast
                => RenderStringFirstOrLast(firstOrLast, placeholders, elementVariable, nullForNullSource: false),
            MongoQuantifierExpression quantifier => RenderQuantifier(quantifier, placeholders, elementVariable),
            // Members go through RenderBranch so constants/parameters get $literal-wrapped; otherwise MongoDB reads a
            // bare number/bool as an inclusion flag and a "$"-prefixed string as a field path.
            MongoDocumentConstructionExpression construction
                => new BsonDocument(construction.Members.Select(
                    m => new BsonElement(m.MemberName, RenderBranch(m.Value, placeholders, elementVariable)))),
            // $concat answers null if any operand is null or missing; C# (and EF) concatenation reads null as "".
            MongoConcatExpression concat
                => new BsonDocument("$concat",
                    new BsonArray(concat.Operands.Select(o => ConcatOperandMayBeNull(o)
                        ? IfNull(RenderOperand(o, placeholders, elementVariable), "")
                        : RenderOperand(o, placeholders, elementVariable)))),
            // Any Term shape: callers are in aggregation scopes with no $regularExpression alternative.
            MongoRegexExpression regex => RenderRegexAsExpr(regex, placeholders, elementVariable),
            // Only ever a top-level $eq/$ne operand, unlike MongoValueListExpression.
            MongoTupleExpression tuple
                => new BsonArray(tuple.Elements.Select(e => RenderOperand(e, placeholders, elementVariable))),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Whether <see cref="Render"/> would render <paramref name="node"/> without throwing; change both together.
    /// </summary>
    /// <remarks>
    /// Only consult it where a render-time throw has no fallback (e.g. <c>NativeSlotPopulator.TryTranslateComputedSortKey</c>).
    /// The filtered-count branch of <see cref="MongoExpressionTranslator"/> deliberately doesn't: its render-time throw
    /// is caught by <c>TryBuildPipeline</c> and falls back to driver-LINQ, whereas a translate-time decline hard-fails
    /// in every mode.
    /// </remarks>
    public static bool CanRender(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression or MongoLookupNullCheckExpression => true,
            MongoNumericTypeBracketExpression => true,
            MongoConstantExpression or MongoParameterExpression => true,
            // $and/$or test a bare operand by truthiness, so a value-converted bool field would render but answer
            // wrong (see CanRenderLogicalOperand).
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } logical
                => CanRenderLogicalOperand(logical.Left) && CanRenderLogicalOperand(logical.Right),
            MongoBinaryExpression binary
                => IsRenderableOperator(binary.Operator) && CanRender(binary.Left) && CanRender(binary.Right),
            MongoSizeExpression => true,
            MongoArrayReduceExpression => true,
            MongoFieldPresenceExpression => true,
            MongoFilteredSizeExpression filtered => CanRender(filtered.ElementPredicate),
            MongoMappedArrayReduceExpression mappedReduce => CanRender(mappedReduce.ElementExpression),
            MongoInExpression inExpr => MongoValueRenderer.IsRenderableInValues(inExpr.Values),
            MongoComputedInExpression computedIn
                => CanRender(computedIn.Needle) && MongoValueRenderer.IsRenderableInValues(computedIn.Values),
            // A bare-field operand must be default-serialized, matching RenderUnary's render-time guard.
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not } unary
                => CanRender(unary.Operand)
                    && !MongoExpressionTranslator.IsUnsafeTruthinessRoot(unary.Operand, out _),
            MongoUnaryExpression { Operator: MongoUnaryOperator.BitNot } bitNot => CanRender(bitNot.Operand),
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
            // Relies on RenderRegexAsExpr's switch staying exhaustive; a new MongoRegexKind must be added to both.
            // Like renders only via $regexMatch with a constant/parameter term (RenderLiteralRegexAsExpr); any other
            // term must decline here rather than crash there.
            MongoRegexExpression { Kind: MongoRegexKind.Like, Term: not (MongoConstantExpression { Value: string } or MongoParameterExpression) } => false,
            // Exact has only a constant string term (a parameter could be null per execution); field-to-field throws.
            MongoRegexExpression { Kind: MongoRegexKind.Exact } exact
                => exact.Term is MongoConstantExpression { Value: string },
            // Pattern: a constant string, string parameter or string field term (EF-247); $regexMatch accepts all three.
            MongoRegexExpression { Kind: MongoRegexKind.Pattern } pattern
                => CanRender(pattern.Field)
                   && (pattern.Term is MongoConstantExpression { Value: string } or MongoParameterExpression or MongoFieldExpression)
                   && CanRender(pattern.Term),
            MongoRegexExpression regex => CanRender(regex.Field) && CanRender(regex.Term),
            MongoTupleExpression tuple => tuple.Elements.All(CanRender),
            MongoDocumentConstructionExpression construction => construction.Members.All(m => CanRender(m.Value)),
            _ => false
        };

    // $and/$or truthiness-test a bare stored operand, so a bare field operand must be default-serialized
    // (mirrors the Not arm and RenderUnary).
    private static bool CanRenderLogicalOperand(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoOuterFieldExpression
                => !MongoExpressionTranslator.IsUnsafeTruthinessRoot(node, out _),
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } nested
                => CanRenderLogicalOperand(nested.Left) && CanRenderLogicalOperand(nested.Right),
            _ => CanRender(node)
        };

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
            or MongoBinaryOperator.Modulo
            or MongoBinaryOperator.BitAnd
            or MongoBinaryOperator.BitOr
            or MongoBinaryOperator.BitXor;

    private static BsonValue FieldRef(string path, string? elementVariable)
        => elementVariable is null ? "$" + path : "$$" + elementVariable + "." + path;

    // Per-level names ("e", "ee", ...) stay distinct without a counter and lowercase-initial, as the server requires.
    private static string NestedElementVariable(string? elementVariable)
        => elementVariable is null ? "e" : elementVariable + "e";

    internal static BsonDocument IfNull(BsonValue value, BsonValue replacement)
        => new("$ifNull", new BsonArray { value, replacement });

    /// <summary>
    /// <see cref="Render"/>, with a constant/parameter <paramref name="node"/> <c>$literal</c>-wrapped, for every value
    /// position where a bare value would be misread (<c>$cond</c>/<c>$ifNull</c> branch, <c>$project</c>/<c>$set</c>
    /// field, <c>$group</c> <c>_id</c> part or accumulator operand).
    /// </summary>
    /// <remarks>
    /// An unwrapped "$"-prefixed string is read as a field path (<c>OrderBy(x => "$Label")</c> would sort by that
    /// field), and <c>$project</c> reads a bare number/bool as an inclusion flag (<c>Select(x => 0)</c> would abort).
    /// <c>MongoPipelineFactory.SubstituteValue</c> still finds a sentinel inside <c>{ "$literal": &lt;sentinel&gt; }</c>.
    /// </remarks>
    internal static BsonValue RenderBranch(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
    {
        var rendered = Render(node, placeholders, elementVariable);
        return node is MongoConstantExpression or MongoParameterExpression
            ? new BsonDocument("$literal", rendered)
            : rendered;
    }

    // A constant is wrapped only when its BSON would otherwise be evaluated as an expression (string, array,
    // document), so numeric operands stay bare; a parameter's value is only known per execution, so always wrapped.
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
            // The driver's shape: milliseconds since midnight, read back through
            // BsonSerializerFactory.TimeOfDayMillisecondsSerializer.
            MongoDatePart.TimeOfDay => new BsonDocument("$dateDiff", new BsonDocument
            {
                { "startDate", new BsonDocument("$dateTrunc", new BsonDocument { { "date", operand }, { "unit", "day" } }) },
                { "endDate", operand },
                { "unit", "millisecond" }
            }),
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
    // zero-arg overload renders an explicit "chars".
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

    // No length means "to the end". $strLenCP is a server error over a null/missing source, so it measures
    // { $ifNull: [source, ""] }.
    private static BsonValue RenderSubstring(MongoSubstringExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var source = RenderBranch(node.Source, placeholders, elementVariable);
        var start = Render(node.Start, placeholders, elementVariable);
        var length = node.Length is not null
            ? Render(node.Length, placeholders, elementVariable)
            : new BsonDocument("$subtract", new BsonArray
            {
                new BsonDocument("$strLenCP", IfNull(source, "")), start
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

    // Narrower than MayBeNull for two shapes that can't be null: $toString of a non-null constant, and string.Join's
    // elements already coalesced to a constant. Not $toString of a non-nullable field: it may be missing (an
    // unmatched join side), answering null.
    private static bool ConcatOperandMayBeNull(MongoExpression operand)
        => operand is not (MongoConvertExpression { Operand: MongoConstantExpression { Value: not null } }
               or MongoCoalesceExpression { Right: MongoConstantExpression { Value: not null } })
           && MayBeNull(operand);

    // C#/EF null propagation for a member-style string call (s.Substring(0, 1) is null for a null s) whose server
    // operator doesn't propagate null/missing: $substrCP answers "", $strLenCP and a $indexOfCP needle are server
    // errors. Wraps in a lazy $cond answering null when any operand that MayBeNull is null or missing. Operands'
    // rendered values are cloned, not re-rendered, so a parameter keeps a single placeholder-table entry. $trim,
    // $replaceAll, $toString and a $indexOfCP haystack already propagate and aren't wrapped.
    private static BsonValue NullPropagating(BsonValue rendered, params (MongoExpression Node, BsonValue Rendered)[] operands)
    {
        var tests = new BsonArray();
        foreach (var (node, operand) in operands)
        {
            if (MayBeNull(node))
                tests.Add(new BsonDocument("$eq", new BsonArray
                {
                    IfNull(operand.DeepClone(), BsonNull.Value), BsonNull.Value
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

    // Every code point char.IsWhiteSpace accepts. Built from char values because U+2028/U+2029 are line terminators
    // even inside a C# string literal (CS1010).
    private static readonly string DotNetWhitespaceChars = new(
    [
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', ' ', '\u0085', ' ', ' ',
        ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ',
        (char)0x2028 /* LINE SEPARATOR */, (char)0x2029 /* PARAGRAPH SEPARATOR */, ' ', ' ', '　'
    ]);

    // A negative $substrCP start (Last on an empty string) is a server error, so both kinds gate on strLenCP == 0 via
    // $cond. Both branches yield a one-char string (the empty branch is "\0", not Int32 0) so a comparison against
    // '\0' never crosses BSON type brackets.
    // nullForNullSource: a comparison operand answers null for a null/missing source, as EF's comparison over a null
    // receiver does; elsewhere a null source behaves like empty and yields '\0'.
    private static BsonValue RenderStringFirstOrLast(
        MongoStringFirstOrLastExpression node, PlaceholderTable placeholders, string? elementVariable, bool nullForNullSource)
    {
        // $strLenCP errors on a null/missing source, so coalesce to "" first.
        var rawSource = RenderOperand(node.Source, placeholders, elementVariable);
        var source = IfNull(rawSource, "");
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
    // LINQ answers). A $lookup alias is always an array and keeps the plain form.
    private static BsonValue RenderSize(MongoSizeExpression size, string? elementVariable)
        => size.NullSafe
            ? new BsonDocument("$size",
                IfNull(FieldRef(size.FieldName, elementVariable), new BsonArray()))
            : new BsonDocument("$size", FieldRef(size.FieldName, elementVariable));

    // Presence by $type, which tells a missing element from a null one; a bare comparison to null can't.
    private static BsonValue RenderFieldPresence(MongoFieldPresenceExpression presence, string? elementVariable)
    {
        var type = new BsonDocument("$type", FieldRef(presence.Path, elementVariable));
        var nullOrMissing = new BsonDocument("$in", new BsonArray { type, new BsonArray { "null", "missing" } });
        return presence.Kind switch
        {
            MongoFieldPresence.Exists => new BsonDocument("$ne", new BsonArray { type, "missing" }),
            MongoFieldPresence.Missing => new BsonDocument("$eq", new BsonArray { type, "missing" }),
            MongoFieldPresence.NullOrMissing => nullOrMissing,
            _ => new BsonDocument("$not", new BsonArray { nullOrMissing })
        };
    }

    private static BsonValue RenderFilteredSize(
        MongoFilteredSizeExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var variable = NestedElementVariable(elementVariable);

        // $filter's cond is truthiness-tested, and a bare-field predicate bypasses the AndAlso/OrElse/Not guards.
        // Checked at render time: a translate-time decline would hard-fail in every mode, whereas this throw is
        // caught by TryBuildPipeline as a driver-LINQ fallback.
        CheckBooleanRootSerialization(node.ElementPredicate);

        return new BsonDocument("$size",
            new BsonDocument("$filter", new BsonDocument
            {
                // $filter over a missing or null array is a server error.
                { "input", IfNull(FieldRef(node.ArrayPath, elementVariable), new BsonArray()) },
                { "as", variable },
                { "cond", Render(node.ElementPredicate, placeholders, variable) }
            }));
    }

    private static BsonValue RenderMappedArrayReduce(
        MongoMappedArrayReduceExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var variable = NestedElementVariable(elementVariable);

        return new BsonDocument(node.OperatorName, new BsonDocument("$map", new BsonDocument
        {
            // $map over a missing or null array is a server error; [] reduces to 0 ($sum) or null, as LINQ answers.
            { "input", IfNull(FieldRef(node.ArrayPath, elementVariable), new BsonArray()) },
            { "as", variable },
            { "in", Render(node.ElementExpression, placeholders, variable) }
        }));
    }

    private static BsonValue RenderQuantifier(
        MongoQuantifierExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var variable = NestedElementVariable(elementVariable);

        // $anyElementTrue/$allElementsTrue truthiness-test each "in" result, and a bare-field predicate passes
        // CanRender unchecked, so a value-converted bool (stored as "False", truthy) would answer wrong. Guard here.
        CheckBooleanRootSerialization(node.ElementPredicate);

        var map = new BsonDocument("$map", new BsonDocument
        {
            // $map over a missing or null array is a server error; [] gives false/true, matching LINQ Any/All.
            { "input", IfNull(Render(node.ArrayPath, placeholders, elementVariable), new BsonArray()) },
            { "as", variable },
            { "in", Render(node.ElementPredicate, placeholders, variable) }
        });

        var op = node.Kind == MongoExpressionTranslator.MongoQuantifierKind.All ? "$allElementsTrue" : "$anyElementTrue";
        return new BsonDocument(op, map);
    }

    // Constant/parameter terms render as $regexMatch (RenderLiteralRegexAsExpr). Field-to-field terms mirror the
    // driver's StartsWithContainsOrEndsWithMethodToAggregationExpressionTranslator.CreateAst, with no $ifNull guarding.
    private static BsonValue RenderRegexAsExpr(MongoRegexExpression regex, PlaceholderTable placeholders, string? elementVariable)
    {
        var field = Render(regex.Field, placeholders, elementVariable);

        // Never the $toLower fold below for these: $toLower only folds ASCII, so an OrdinalIgnoreCase term would miss
        // a non-ASCII match ("École" vs "école"), whereas the regex "i" option is Unicode-correct.
        if (regex.Kind == MongoRegexKind.Exact
            || (regex.Kind is not (MongoRegexKind.IsMatch or MongoRegexKind.Pattern)
                && regex.Term is MongoConstantExpression { Value: string } or MongoParameterExpression))
            return RenderLiteralRegexAsExpr(regex, field, placeholders);

        // Pattern: options come from PatternOptions verbatim, never CaseInsensitive/$toLower.
        if (regex.Kind == MongoRegexKind.Pattern)
        {
            var patternTerm = RenderOperand(regex.Term, placeholders, elementVariable);
            var patternMatch = new BsonDocument("$regexMatch",
                new BsonDocument { { "input", field }, { "regex", patternTerm }, { "options", regex.PatternOptions } });
            return NotIf(regex.Negated, patternMatch);
        }

        var term = RenderOperand(regex.Term, placeholders, elementVariable);

        // $indexOfCP/$strLenCP have no case-insensitive option, so fold both operands through $toLower (ASCII-only;
        // unreachable today, as the translator declines case-insensitive field-to-field). IsMatch has its own "options".
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
            // Roles are swapped (see MongoRegexKind.IsMatch): Field feeds "regex", Term feeds "input".
            MongoRegexKind.IsMatch => new BsonDocument("$regexMatch", new BsonDocument
            {
                { "input", term },
                { "regex", field },
                { "options", regex.CaseInsensitive ? "i" : "" }
            }),
            _ => throw new NativeTranslationNotSupportedException($"Unsupported {nameof(MongoRegexKind)} '{regex.Kind}'.")
        };

        return NotIf(regex.Negated, test);
    }

    // Matches MongoQueryLanguageRenderer.RenderRegex's constant/parameter handling (including Like's "is"). A
    // field-to-field Exact term has no dialect form (the translator never builds one) and throws rather than
    // silently folding through $toLower.
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
        return NotIf(regex.Negated, regexMatch);
    }

    // True iff start >= 0 and indexOfCP(field, term, start) == start, where start = strLenCP(field) - strLenCP(term),
    // bound once via $let as the driver's CreateAst does.
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

    // Throws if node is a bare non-default-serialized bool (e.g. stored as "True"/"False", both truthy) in a position
    // that tests it by truthiness.
    private static void ThrowIfUnsafeTruthinessRoot(MongoExpression node, string position, string reason)
    {
        if (MongoExpressionTranslator.IsUnsafeTruthinessRoot(node, out var property))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render '{property.Name}' as {position}: it does not use default BSON serialization, and "
                + $"{reason}, which would answer the wrong boolean.");
        }
    }

    // For positions that truthiness-test a whole sub-expression ($filter cond, quantifier $map "in"). A bare-field
    // root reaches neither RenderBinary nor RenderUnary, so these callers must invoke it; nested AndAlso/OrElse/Not
    // re-check their own operands.
    private static void CheckBooleanRootSerialization(MongoExpression node)
        => ThrowIfUnsafeTruthinessRoot(node, "a bare boolean predicate root", "this position is evaluated by truthiness");

    // { $in: [needle, haystack] }; negation wraps in $not (the query dialect has $nin, this one doesn't).
    private static BsonValue RenderIn(MongoInExpression inExpr, PlaceholderTable placeholders, string? elementVariable)
    {
        var needle = FieldRef(inExpr.Field.ElementName, elementVariable);
        var haystack = new BsonDocument("$literal", MongoValueRenderer.RenderInValues(inExpr.Values, placeholders));
        return NotIf(inExpr.Negated, new BsonDocument("$in", new BsonArray { needle, haystack }));
    }

    // Computed-needle variant of RenderIn.
    private static BsonValue RenderComputedIn(
        MongoComputedInExpression computedIn, PlaceholderTable placeholders, string? elementVariable)
    {
        var needle = Render(computedIn.Needle, placeholders, elementVariable);
        var haystack = new BsonDocument("$literal", MongoValueRenderer.RenderInValues(computedIn.Values, placeholders));
        return NotIf(computedIn.Negated, new BsonDocument("$in", new BsonArray { needle, haystack }));
    }

    private static BsonValue NotIf(bool negated, BsonValue test)
        => negated ? new BsonDocument("$not", new BsonArray { test }) : test;

    private static BsonValue RenderUnary(MongoUnaryExpression unary, PlaceholderTable placeholders, string? elementVariable)
    {
        if (unary.Operator == MongoUnaryOperator.BitNot)
            return new BsonDocument("$bitNot", Render(unary.Operand, placeholders, elementVariable));

        if (unary.Operator != MongoUnaryOperator.Not)
            throw new NativeTranslationNotSupportedException($"Unsupported unary operator '{unary.Operator}'.");

        // $not is truthiness-based, so negating a value-converted bool field directly would answer wrong. Only a bare
        // field is checked: $not over a comparison result is always correct.
        // Render-time rather than translate-time: this is the only gate for a filtered count's element predicate,
        // where a translate-time decline hard-fails in every mode, while this throw is caught by TryBuildPipeline
        // (driver-LINQ fallback) and surfaces as-is under NativeOnly.
        ThrowIfUnsafeTruthinessRoot(unary.Operand, "a 'Not' operand", "a raw-field $not evaluates it by truthiness");

        return new BsonDocument("$not", new BsonArray { Render(unary.Operand, placeholders, elementVariable) });
    }

    private static BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders, string? elementVariable)
    {
        // Render-time counterpart of CanRenderLogicalOperand, for callers that skip CanRender (the filtered-count path).
        if (binary.Operator is MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse)
        {
            CheckLogicalOperandSerialization(binary.Left);
            CheckLogicalOperandSerialization(binary.Right);
        }

        // C# integer division truncates; $divide yields a double. Without $trunc an integral member fails to
        // deserialize and integral comparisons are off by one. See MongoBinaryOperator.IntegerDivide.
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
            MongoBinaryOperator.BitAnd => "$bitAnd",
            MongoBinaryOperator.BitOr => "$bitOr",
            MongoBinaryOperator.BitXor => "$bitXor",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported aggregation operator '{binary.Operator}'.")
        };

        var left = binary.IsComparison
            ? RenderComparisonOperand(binary.Left, placeholders, elementVariable)
            : Render(binary.Left, placeholders, elementVariable);
        var right = binary.IsComparison
            ? RenderComparisonOperand(binary.Right, placeholders, elementVariable)
            : Render(binary.Right, placeholders, elementVariable);

        // Document root only: inside a $filter/$map element scope, missing-vs-null stays distinguished, matching
        // driver-LINQ (pinned by NativeOwnedCollectionFilteredCountTests).
        if (binary.Operator is MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual && elementVariable is null)
        {
            left = MissingAsNullWhenComparedToNull(binary.Left, binary.Right, left);
            right = MissingAsNullWhenComparedToNull(binary.Right, binary.Left, right);
        }

        var rendered = new BsonDocument(op, new BsonArray { left, right });

        // Relational null-ordering guard (see LowerRelationalOperand), also applied in element scopes. Reuses the
        // operand's rendered value (cloned) rather than re-rendering, so a parameter keeps a single placeholder entry.
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

    // A string FirstOrDefault()/LastOrDefault() comparison operand renders null (not '\0') for a null/missing
    // receiver, as C#/EF compare the null: `== '\0'` is false, `!= '\0'` true. Negation stays exact.
    private static BsonValue RenderComparisonOperand(
        MongoExpression node, PlaceholderTable placeholders, string? elementVariable)
        => node is MongoStringFirstOrLastExpression firstOrLast
            ? RenderStringFirstOrLast(firstOrLast, placeholders, elementVariable, nullForNullSource: true)
            : RenderOperand(node, placeholders, elementVariable);

    // $eq/$ne don't equate a missing field with null (the query dialect and .NET do), so `x.S == null` would be false
    // for a missing S. Wrap a bare stored field in $ifNull against a null constant or a parameter (null only known
    // per execution). Computed operands already yield null for a missing input.
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
            ? IfNull(rendered, BsonNull.Value)
            : rendered;

    // The "less" side of a relational comparison: Left of </<=, Right of >/>=; null for any other operator.
    // The aggregation dialect orders null/missing below every value, so `$lt: [null, 5]` and `$gte: [null, null]` are
    // true where C# answers false for any null operand. Only the less side can make the operator wrongly true, so
    // RenderBinary conjoins `$gt: [lower, null]` when that side may be null. Done here rather than at each
    // construction site so every aggregation-dialect comparison gets it. Negation stays exact: negators $not-wrap
    // relational operators and the guard is inside the wrapped node.
    private static MongoExpression? LowerRelationalOperand(MongoBinaryExpression binary)
        => binary.Operator switch
        {
            MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual => binary.Left,
            MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual => binary.Right,
            _ => null
        };

    /// <summary>
    /// Whether <paramref name="node"/> may evaluate to null or missing in the aggregation dialect, judged
    /// conservatively from its CLR type and null-propagating operators. A query parameter always may (its value is
    /// only known at execution).
    /// </summary>
    internal static bool MayBeNull(MongoExpression node)
        => node switch
        {
            MongoConstantExpression constant => constant.Value is null,
            MongoFieldExpression { NullSafe: true } or MongoElementRefExpression { NullSafe: true } => true,
            MongoElementRefExpression { ThrowsOnNull: true } => true,
            MongoParameterExpression => true,
            MongoBinaryExpression { IsArithmetic: true } arithmetic
                => IsNullableClrType(arithmetic.Type) || MayBeNull(arithmetic.Left) || MayBeNull(arithmetic.Right),
            MongoConvertExpression convert => IsNullableClrType(convert.Type) || MayBeNull(convert.Operand),
            MongoDatePartExpression datePart => MayBeNull(datePart.Operand),
            MongoMathExpression math => IsNullableClrType(math.Type) || math.Operands.Any(MayBeNull),
            // Null-guarded or null-propagating over a possibly-null string (see NullPropagating).
            MongoStringLengthExpression length => MayBeNull(length.Operand),
            MongoStringIndexOfExpression indexOf => MayBeNull(indexOf.Haystack) || MayBeNull(indexOf.Needle),
            MongoConcatExpression => false,
            // Only its comparison-operand form is ever null (see RenderComparisonOperand).
            MongoStringFirstOrLastExpression firstOrLast => MayBeNull(firstOrLast.Source),
            _ => IsNullableClrType(node.Type)
        };

    /// <summary>
    /// <see cref="MayBeNull"/>, except that a query parameter is judged by its CLR type (a captured non-nullable
    /// <c>int</c> is never null).
    /// </summary>
    internal static bool MayBeNullOperand(MongoExpression operand)
        => operand is MongoParameterExpression { ValueType: { } valueType } ? IsNullableClrType(valueType) : MayBeNull(operand);

    /// <summary>
    /// Whether <paramref name="node"/>, whose CLR type is a non-nullable value type, may nonetheless evaluate to null
    /// (<c>$strLenCP</c>/<c>$indexOfCP</c> over a possibly-null string, directly or through arithmetic, a cast, a math
    /// function or a conditional branch). Read back as that type the null would silently become <c>default(T)</c>
    /// where EF throws, so a group key, <c>$min</c>/<c>$max</c>/<c>$avg</c> or <c>$push</c> operand declines and a
    /// projection leaf throws or declines (see <see cref="ClassifyNonNullableValueRead"/>).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ClassifyNonNullableValueRead"/>, doesn't count a date operator over a nullable date or an
    /// operator over a nullable stored value (<c>x.Score.Value + 1</c>): declining those group keys and accumulators
    /// would move native grouped queries onto the fallback, and a group key has no throw-on-null read yet. Their null
    /// still reads as <c>0</c> (EF-461).
    /// </remarks>
    internal static bool MayBeNullBehindNonNullableType(MongoExpression node)
        => WalkNullBehindNonNullableType(node, [], dateParts: false).MayBeNull;

    // Whether a value may be null behind its non-nullable type, and whether an operator between that null and the value
    // may absorb it into a non-null answer (Decline). One walker answers both so they can't cover different nodes: a
    // node one saw and the other didn't would classify a hidden Math.Max as ThrowOnNull (a silent wrong value).
    private readonly record struct NullBehindNonNullable(bool MayBeNull, bool MayAbsorb)
    {
        public static NullBehindNonNullable operator |(NullBehindNonNullable a, NullBehindNonNullable b)
            => new(a.MayBeNull || b.MayBeNull, a.MayAbsorb || b.MayAbsorb);
    }

    // nonNull: operands a conditional's test has proven non-null on the branch being inspected. dateParts: whether a
    // date part over a nullable date counts. missingFields: whether a non-nullable property's field counts too, as in a
    // malformed document that omits it (projection leaves only; see NonNullableValueRead.ThrowOnMalformedNull).
    private static NullBehindNonNullable WalkNullBehindNonNullableType(
        MongoExpression node, IReadOnlyList<MongoExpression> nonNull, bool dateParts, bool missingFields = false)
        => node switch
        {
            MongoStringLengthExpression length => new(MayBeNullUnlessProven(length.Operand, nonNull), false),
            MongoStringIndexOfExpression indexOf
                => new(MayBeNullUnlessProven(indexOf.Haystack, nonNull) || MayBeNullUnlessProven(indexOf.Needle, nonNull), false),
            // $min/$max/$avg over an empty array answer null with nothing to absorb it, so a non-nullable result throws
            // on read like LINQ's Min/Max/Average over an empty sequence. $sum of an empty array is 0.
            // ElementRef: an upstream alias the emit side flagged MongoProjection.ThrowsOnNull.
            MongoArrayReduceExpression { OperatorName: "$min" or "$max" or "$avg" } => new(true, false),
            MongoMappedArrayReduceExpression { OperatorName: "$min" or "$max" or "$avg" } => new(true, false),
            MongoElementRefExpression { ThrowsOnNull: true } => new(true, false),
            MongoBinaryExpression { IsArithmetic: true } arithmetic
                => WalkNullBehindNonNullableType(arithmetic.Left, nonNull, dateParts, missingFields)
                   | WalkNullBehindNonNullableType(arithmetic.Right, nonNull, dateParts, missingFields),
            MongoConvertExpression convert => WalkNullBehindNonNullableType(convert.Operand, nonNull, dateParts, missingFields),
            // Date operators answer null for a null date, which a non-nullable read would take as 0 / DateTime.MinValue
            // where EF throws. $dateAdd is also null for a null amount; a DateTimeOffset's local reconstruction is a
            // $dateAdd over the nullable stored offset.
            MongoDatePartExpression datePart when dateParts
                => new NullBehindNonNullable(MayBeNullUnlessProven(datePart.Operand, nonNull), false)
                   | WalkNullBehindNonNullableType(datePart.Operand, nonNull, dateParts, missingFields),
            MongoDateAddExpression dateAdd when dateParts
                => new NullBehindNonNullable(
                       MayBeNullUnlessProven(dateAdd.StartDate, nonNull) || DateAddAmountMayBeNull(dateAdd.Amount, nonNull), false)
                   | WalkNullBehindNonNullableType(dateAdd.StartDate, nonNull, dateParts, missingFields)
                   | WalkNullBehindNonNullableType(dateAdd.Amount, nonNull, dateParts, missingFields),
            MongoDateTimeOffsetLocalExpression local when dateParts
                => new NullBehindNonNullable(MayBeNullUnlessProven(local.Operand, nonNull), false)
                   | WalkNullBehindNonNullableType(local.Operand, nonNull, dateParts, missingFields),
            // A nullable stored value under a non-nullable operator (`x.Score!.Value + 1`, with `.Value` peeled to the
            // field): the operator propagates its null, which a non-nullable read would take as 0 where EF throws.
            // A non-nullable property's field answers false (so `x.A + x.B` is not flagged) unless missingFields.
            // Projection leaves only (dateParts): group keys and accumulators are unchanged.
            MongoFieldExpression or MongoOuterFieldExpression when dateParts
                => new NullBehindNonNullable(MayBeNullUnlessProven(node, nonNull, missingFields), false),
            MongoMathExpression math => WalkMath(math, nonNull, dateParts, missingFields),
            // Either branch may be the value read, each under what the test proves on it
            // (`s == null ? a : b` makes s non-null in b).
            MongoConditionalExpression conditional
                => WalkNullBehindNonNullableType(conditional.IfTrue, [.. nonNull, .. ProvenNonNullWhen(conditional.Test, true)], dateParts, missingFields)
                   | WalkNullBehindNonNullableType(conditional.IfFalse, [.. nonNull, .. ProvenNonNullWhen(conditional.Test, false)], dateParts, missingFields),
            // `x.Score ?? x.Rank`: a missing non-nullable fallback field (or an operator over one) is the leaf's
            // malformed-document value. Only with missingFields: group keys and accumulators are unchanged.
            MongoCoalesceExpression coalesce when missingFields
                => WalkNullBehindNonNullableType(coalesce.Right, nonNull, dateParts, missingFields),
            _ => default
        };

    // $max/$min skip a null operand and Sign's $switch orders it below 0 (MongoGroupElementTranslator
    // .IsNullPropagatingMathFunction), so over a possibly-null operand they may answer non-null where EF throws.
    private static NullBehindNonNullable WalkMath(
        MongoMathExpression math, IReadOnlyList<MongoExpression> nonNull, bool dateParts, bool missingFields)
    {
        var operands = default(NullBehindNonNullable);
        foreach (var operand in math.Operands)
            operands |= WalkNullBehindNonNullableType(operand, nonNull, dateParts, missingFields);

        return MongoGroupElementTranslator.IsNullPropagatingMathFunction(math.Function)
            ? operands
            : operands with { MayAbsorb = operands.MayAbsorb || operands.MayBeNull };
    }

    // A primary-key field is never null in a stored document. Only the root document's own key (element "_id",
    // unprefixed): a join's inner-scope field is a prefixed path, and under a left-outer join that sub-document is
    // missing for an unmatched row. Deliberately local here, not in MayBeNull, which also drives render-time $ifNull
    // guards. missingFields: a stored field counts whatever its property's nullability, except the key and proven operands.
    private static bool MayBeNullUnlessProven(
        MongoExpression operand, IReadOnlyList<MongoExpression> nonNull, bool missingFields = false)
        => (MayBeNull(operand) || missingFields && operand is MongoFieldExpression or MongoOuterFieldExpression)
           && !(operand is MongoFieldExpression { NullSafe: false, ElementName: "_id" } field
                 && field.Property.IsPrimaryKey())
           && !nonNull.Any(proven => IsSameStoredValue(proven, operand));

    // A parameter amount is judged by its CLR type (`AddDays(days)` over a captured non-nullable `days` is never
    // null), although MayBeNull counts every parameter as possibly null.
    private static bool DateAddAmountMayBeNull(MongoExpression amount, IReadOnlyList<MongoExpression> nonNull)
        => amount is MongoParameterExpression { ValueType: { } valueType }
            ? IsNullableClrType(valueType)
            : MayBeNullUnlessProven(amount, nonNull);

    // The operands that `test` answering `outcome` proves non-null: only a null comparison of a stored value, combined
    // through ||/&&/! the way that preserves the proof (a false `a || b` makes both false; a true `a && b` both true).
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

    private static bool IsSameStoredValue(MongoExpression a, MongoExpression b)
        => (a, b) switch
        {
            (MongoFieldExpression fa, MongoFieldExpression fb) => fa.ElementName == fb.ElementName,
            (MongoOuterFieldExpression oa, MongoOuterFieldExpression ob) => oa.ElementName == ob.ElementName,
            (MongoElementRefExpression ea, MongoElementRefExpression eb) => ea.Path == eb.Path,
            _ => false
        };

    /// <summary>
    /// <see cref="MayBeNullBehindNonNullableType(MongoExpression)"/>, only when <paramref name="readType"/> is a
    /// non-nullable value type.
    /// </summary>
    internal static bool ReadsNullAsDefault(Type readType, MongoExpression node)
        => readType.IsValueType && Nullable.GetUnderlyingType(readType) is null && MayBeNullBehindNonNullableType(node);

    /// <summary>
    /// How a projection leaf read back as <paramref name="readType"/> handles a null behind the non-nullable type
    /// (<see cref="ReadsNullAsDefault"/>, plus a date operator over a nullable date and an operator over a nullable
    /// stored value such as <c>x.Score.Value + 1</c>):
    /// <see cref="NonNullableValueRead.Plain"/> when there is none or the leaf is a bare field (read through its
    /// property's binding); <see cref="NonNullableValueRead.ThrowOnNull"/> when every operator between that null and the
    /// value propagates it, so reading as <c>T?</c> and throwing is exactly EF's "Nullable object must have a value.";
    /// <see cref="NonNullableValueRead.Decline"/> when an operator may absorb it into a non-null answer where EF
    /// throws (<c>Math.Max</c>: <c>$max</c>/<c>$min</c> skip a null, <c>Math.Sign</c>'s <c>$switch</c> orders it below 0).
    /// </summary>
    /// <remarks>
    /// The one call a projection binder makes for a value leaf; its answer is both the gate and the read side's
    /// <c>MongoProjection.ThrowsOnNull</c>.
    /// </remarks>
    internal static NonNullableValueRead ClassifyNonNullableValueRead(Type readType, MongoExpression node)
    {
        if (!readType.IsValueType || Nullable.GetUnderlyingType(readType) is not null)
            return NonNullableValueRead.Plain;

        // A bare property leaf is read through the property's binding, which already handles its null; flagging it
        // would only make later operators (Distinct, set ops, groups) decline it.
        if (node is MongoFieldExpression or MongoOuterFieldExpression)
            return NonNullableValueRead.Plain;

        var walk = WalkNullBehindNonNullableType(node, [], dateParts: true);
        if (walk.MayBeNull)
            return walk.MayAbsorb ? NonNullableValueRead.Decline : NonNullableValueRead.ThrowOnNull;

        // A computation over a non-nullable field (`x.Rank + 1`) answers null when a malformed document omits it. The
        // read side alone reads it strictly; the emit side and later operators treat it as Plain (flagging would
        // decline Distinct/Min/Max/group keys over every `x.A + x.B`).
        if (!WalkNullBehindNonNullableType(node, [], dateParts: true, missingFields: true).MayBeNull)
            return NonNullableValueRead.Plain;

        // Where the leaf selects the omitted field itself (`x.Rank > 1 ? -1 : x.Rank`), the server answers MISSING
        // rather than null, which driver-LINQ's deserializer read as default.
        return MayAnswerMissing(readType, node) == MissingAnswer.Missing
            ? NonNullableValueRead.DefaultOnMalformedMissing
            : NonNullableValueRead.ThrowOnMalformedNull;
    }

    /// <summary>
    /// How a bare stored field <paramref name="node"/>, grouped on by a projected Distinct and read back as
    /// <paramref name="readType"/>, treats a malformed document. <see cref="ClassifyNonNullableValueRead"/> says
    /// <see cref="NonNullableValueRead.Plain"/> for a bare field, but a Distinct key is read from the <c>$group</c>
    /// output, where a lone key turns MISSING into null.
    /// <see cref="NonNullableValueRead.DefaultOnMalformedMissing"/> when read as the field's own type (including identity
    /// casts the driver drops); <see cref="NonNullableValueRead.ThrowOnMalformedNull"/> under a widening the translator
    /// dropped (<c>(long)x.Rank</c>), which the driver renders as <c>$toLong</c>;
    /// <see cref="NonNullableValueRead.Plain"/> for a nullable read, a <c>bool</c> (<see cref="DriverReadsNullAsDefault"/>),
    /// the primary key and anything that isn't a bare field. A value-converted field is marked too when read as its own
    /// type (the flatten is read through the property, so the converter applies); under a dropped widening it stays
    /// <see cref="NonNullableValueRead.Plain"/>, since a strict read is a generic read the converter would defeat. A
    /// marked converted key no longer combines with a plain projected operand in a set op (it declines).
    /// </summary>
    internal static NonNullableValueRead ClassifyMalformedFieldRead(Type readType, MongoExpression node)
    {
        if (!readType.IsValueType
            || Nullable.GetUnderlyingType(readType) is not null
            || DriverReadsNullAsDefault(readType)
            || node is not (MongoFieldExpression or MongoOuterFieldExpression)
            || IsRootPrimaryKey(node))
            return NonNullableValueRead.Plain;

        var missingAnswer = MayAnswerMissing(readType, node);
        if (!MongoExpressionTranslator.AllFieldsDefaultSerialized(node))
            return missingAnswer == MissingAnswer.Missing && ReadsConvertedDistinctKeyThroughProperty(readType, node)
                ? NonNullableValueRead.DefaultOnMalformedMissing
                : NonNullableValueRead.Plain;

        return missingAnswer == MissingAnswer.Missing
            ? NonNullableValueRead.DefaultOnMalformedMissing
            : NonNullableValueRead.ThrowOnMalformedNull;
    }

    /// <summary>
    /// Whether a marked projected-Distinct key over the value-converted bare field <paramref name="node"/>, read back as
    /// <paramref name="readType"/>, is read through the field's property (so its converter applies). The read side
    /// (<c>MongoProjectionBindingRemovingExpressionVisitor.TryCreateThrowOnMalformedNullAliasRead</c>) calls this too.
    /// </summary>
    internal static bool ReadsConvertedDistinctKeyThroughProperty(Type readType, MongoExpression node)
        => node is MongoFieldExpression field
           && !MongoExpressionTranslator.AllFieldsDefaultSerialized(field)
           && field.Property.ClrType == readType;

    /// <summary>
    /// Whether driver-LINQ's deserializer reads an explicit BSON null as <c>default</c> rather than throwing: only the
    /// driver's <c>BooleanSerializer</c> does (every other value serializer throws <c>FormatException</c>).
    /// </summary>
    internal static bool DriverReadsNullAsDefault(Type readType)
        => readType == typeof(bool);

    /// <summary>
    /// Whether a join-scope projection leaf <paramref name="node"/> read back as <paramref name="readType"/> is a bare
    /// stored scalar of a non-nullable property read as its own type (<c>o.Customer!.Rank</c>), which the Projection
    /// route reads through that property like a root bare leaf (D-F10): a MISSING element (an unmatched optional
    /// reference, or a document omitting the field) reads <c>default</c> as driver-LINQ did; an explicit null throws,
    /// except a <c>bool</c> (<see cref="DriverReadsNullAsDefault"/>). The single predicate behind
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.TryCreateJoinScopeBareScalarRead</c>.
    /// </summary>
    internal static bool ReadsJoinScopeBareScalarThroughProperty(Type readType, MongoExpression node)
        => readType.IsValueType
           && Nullable.GetUnderlyingType(readType) is null
           && node is MongoFieldExpression field
           && field.Property.ClrType == readType;

    /// <summary>
    /// <paramref name="classified"/> (for a projected Distinct key of type <paramref name="classifiedType"/>) when
    /// read back as <paramref name="readType"/>. Read as a wider type (<c>(long)(c ? -1 : x.Rank)</c>, whose widening the
    /// translator unwrapped where driver-LINQ renders <c>$toLong</c>), a
    /// <see cref="NonNullableValueRead.DefaultOnMalformedMissing"/> MISSING is driver-LINQ's null, so it reads strictly.
    /// </summary>
    internal static NonNullableValueRead ReclassifyMalformedReadAs(NonNullableValueRead classified, Type classifiedType, Type readType)
        => classified == NonNullableValueRead.DefaultOnMalformedMissing
           && classifiedType.UnwrapNullableType() != readType.UnwrapNullableType()
           && !MongoExpressionTranslator.IsEnumUnderlyingRelabel(classifiedType, readType)
            ? NonNullableValueRead.ThrowOnMalformedNull
            : classified;

    /// <summary>
    /// Whether <paramref name="node"/>, read back as <paramref name="readType"/>, may be MISSING on the server where
    /// driver-LINQ's rendering is null (a field under a dropped widening; see <see cref="MayAnswerMissing"/>).
    /// </summary>
    internal static bool MayAnswerUnfaithfulMissing(Type readType, MongoExpression node)
        => !IsRootPrimaryKey(node) && MayAnswerMissing(readType.UnwrapNullableType(), node) == MissingAnswer.Unfaithful;

    private static bool IsRootPrimaryKey(MongoExpression node)
        => node is MongoFieldExpression { NullSafe: false, ElementName: "_id" } key && key.Property.IsPrimaryKey();

    private enum MissingAnswer
    {
        // An operator or $toX over the field answers null instead.
        Never,

        // MISSING exactly where driver-LINQ's rendering of the same leaf is.
        Missing,

        // MISSING natively where driver-LINQ answers null: under a widening cast the translator unwrapped
        // (TryTranslateValue) but the driver renders as $toX.
        Unfaithful
    }

    // A stored field reached through the value positions of $cond (either branch) and $ifNull (the fallback) may be
    // MISSING; any other operator answers null for a missing operand. A field whose type isn't the read type sits under
    // a widening the translator dropped, which driver-LINQ renders as $toX (answering null), so its native MISSING must
    // not read as default. One unfaithful position makes the whole leaf strict: the read can't tell which branch ran.
    private static MissingAnswer MayAnswerMissing(Type readType, MongoExpression node)
        => node switch
        {
            MongoConditionalExpression conditional
                => Combine(MayAnswerMissing(readType, conditional.IfTrue), MayAnswerMissing(readType, conditional.IfFalse)),
            MongoCoalesceExpression coalesce => MayAnswerMissing(readType, coalesce.Right),
            // An enum read as its underlying type is a bare relabel, which the driver doesn't render as $toX either.
            MongoFieldExpression or MongoOuterFieldExpression
                => node.Type.UnwrapNullableType() == readType
                   || MongoExpressionTranslator.IsEnumUnderlyingRelabel(node.Type, readType)
                    ? MissingAnswer.Missing
                    : MissingAnswer.Unfaithful,
            _ => MissingAnswer.Never
        };

    private static MissingAnswer Combine(MissingAnswer a, MissingAnswer b)
        => a == MissingAnswer.Unfaithful || b == MissingAnswer.Unfaithful ? MissingAnswer.Unfaithful
            : a == MissingAnswer.Missing || b == MissingAnswer.Missing ? MissingAnswer.Missing
            : MissingAnswer.Never;

    private static bool IsNullableClrType(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static void CheckLogicalOperandSerialization(MongoExpression operand)
        => ThrowIfUnsafeTruthinessRoot(
            operand, "a bare logical (&&/||) operand", "$and/$or evaluate operands by truthiness");
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
    Decline,

    /// <summary>
    /// Never null over well-formed documents, but a computation over a non-nullable field (<c>x.Rank + 1</c>) answers
    /// null when a document omits it. Emit-side callers treat it as <see cref="Plain"/>; only the <c>Projection</c>-route
    /// read side acts on it, reading as <c>T?</c> and throwing EF's "Nullable object must have a value." on null.
    /// </summary>
    ThrowOnMalformedNull,

    /// <summary>
    /// As <see cref="ThrowOnMalformedNull"/>, but the leaf may select the omitted field itself (a <c>$cond</c> branch or
    /// <c>$ifNull</c> fallback), for which the server answers MISSING rather than null, as driver-LINQ's rendering does.
    /// The read side reads MISSING as <c>default(T)</c> and throws on null. Emit-side callers treat it as <see cref="Plain"/>.
    /// </summary>
    DefaultOnMalformedMissing
}
