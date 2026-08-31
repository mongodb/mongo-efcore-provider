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
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            // Always at document root, REGARDLESS of elementVariable — see the node's own remarks.
            MongoOuterFieldExpression outer => FieldRef(outer.ElementName, elementVariable: null),
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
            MongoSizeExpression size => RenderSize(size, elementVariable),
            MongoFilteredSizeExpression filtered => RenderFilteredSize(filtered, placeholders, elementVariable),
            MongoInExpression inExpr => RenderIn(inExpr, placeholders, elementVariable),
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
            MongoDateTimeOffsetLocalExpression local
                => new BsonDocument("$dateAdd", new BsonDocument
                {
                    { "startDate", FieldRef(local.Operand.ElementName + ".DateTime", elementVariable) },
                    { "unit", "minute" },
                    { "amount", FieldRef(local.Operand.ElementName + ".Offset", elementVariable) }
                }),
            MongoDatePartExpression datePart => RenderDatePart(datePart, placeholders, elementVariable),
            MongoQuantifierExpression quantifier => RenderQuantifier(quantifier, placeholders, elementVariable),
            // A constructed nested sub-document leaf (EF-447, `new Book { Id = e.Id, Title = e.Title }`).
            // Each member renders through this SAME Render call, recursively, so a nested field ref renders as
            // "$ElementName" (an aggregation-expression field path) exactly like any other computed leaf value —
            // never a bare unprefixed name, which $project would otherwise misread as an inclusion flag.
            MongoDocumentConstructionExpression construction
                => new BsonDocument(construction.Members.Select(
                    m => new BsonElement(m.MemberName, Render(m.Value, placeholders, elementVariable)))),
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
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression => true,
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
            // A bare-field operand must ALSO be default-serialized — mirrors RenderUnary's own render-time
            // guard (EF-413 review fix), so the two can never disagree: CanRender=true must mean Render
            // actually succeeds AND answers correctly, not merely "doesn't throw". Checked via
            // TryGetBareFieldProperty (final-review fix), which matches BOTH MongoFieldExpression and
            // MongoOuterFieldExpression — a bare outer-scoped bool under Not has the exact same
            // truthiness hazard as an ordinary bare field.
            MongoUnaryExpression { Operator: MongoUnaryOperator.Not } unary
                => CanRender(unary.Operand)
                    && (!MongoExpressionTranslator.TryGetBareFieldProperty(unary.Operand, out _)
                        || MongoExpressionTranslator.AllFieldsDefaultSerialized(unary.Operand)),
            MongoConvertExpression convert
                => MongoConvertExpression.ToOperatorFor(convert.Type) is not null && CanRender(convert.Operand),
            MongoConditionalExpression conditional
                => CanRender(conditional.Test) && CanRender(conditional.IfTrue) && CanRender(conditional.IfFalse),
            MongoDateTimeOffsetLocalExpression local => CanRender(local.Operand),
            MongoDatePartExpression datePart => CanRender(datePart.Operand),
            MongoQuantifierExpression quantifier => CanRender(quantifier.ArrayPath) && CanRender(quantifier.ElementPredicate),
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
                => MongoExpressionTranslator.AllFieldsDefaultSerialized(node),
            MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } nested
                => CanRenderLogicalOperand(nested.Left) && CanRenderLogicalOperand(nested.Right),
            _ => CanRender(node)
        };

    private static bool CanRenderInValues(MongoExpression values)
        => values is MongoConstantExpression { Value: System.Collections.IEnumerable } or MongoParameterExpression;

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
        if (MongoExpressionTranslator.TryGetBareFieldProperty(node, out var property)
            && !MongoExpressionTranslator.AllFieldsDefaultSerialized(node))
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
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization!);
                return placeholders.CreateArrayPlaceholder(parameter.Name, info.Serializer);
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
        if (MongoExpressionTranslator.TryGetBareFieldProperty(unary.Operand, out var notProperty)
            && !MongoExpressionTranslator.AllFieldsDefaultSerialized(unary.Operand))
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
        if (MongoExpressionTranslator.TryGetBareFieldProperty(operand, out var logicalProperty)
            && !MongoExpressionTranslator.AllFieldsDefaultSerialized(operand))
        {
            throw new NativeTranslationNotSupportedException(
                $"Cannot render '{logicalProperty.Name}' as a bare logical (&&/||) operand: it does not use "
                + "default BSON serialization, and $and/$or evaluate operands by truthiness, which would "
                + "answer the wrong boolean.");
        }
    }
}
