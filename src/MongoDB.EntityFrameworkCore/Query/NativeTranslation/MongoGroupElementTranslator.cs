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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates a GroupBy key part, accumulator operand, or element predicate for <see cref="NativeGroupByBinder"/>.
/// Over a plain collection this is the root-entity <see cref="MongoExpressionTranslator"/>. Over a native join
/// scope (<see cref="MongoSelectDefinition.GroupByJoinScope"/>) a body over the join's
/// <c>TransparentIdentifier(Outer, Inner)</c> element resolves each hop chain to one scope via
/// <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>.
/// </summary>
/// <remarks>
/// In join mode a body may reference only the single transparent-identifier element parameter. Any other
/// parameter declines: the root translator resolves members by name against the root entity, so an
/// entity-typed parameter from a join whose result selector already projected one side would silently read the
/// wrong field. Parameter-free bodies (constants, captured parameters) use the root translator.
/// </remarks>
internal sealed class MongoGroupElementTranslator
{
    private readonly MongoExpressionTranslator _rootTranslator;
    private readonly MongoJoinScope? _joinScope;

    internal MongoGroupElementTranslator(IEntityType rootEntityType, MongoJoinScope? joinScope, MongoGrouping? priorGrouping)
    {
        _rootTranslator = new MongoExpressionTranslator(rootEntityType);

        // A GroupBy over a prior finalized grouping resolves against that stage's flattened alias, never the
        // entity (see MongoExpressionTranslator.DistinctAliasScope). TranslateGroupBy never enables join mode then.
        if (priorGrouping != null)
            _rootTranslator.DistinctAliasScope = priorGrouping;

        _joinScope = priorGrouping == null ? joinScope : null;
    }

    internal bool IsJoinScoped => _joinScope != null;

    /// <summary>Whether the grouped join scope has any left-outer level (a row whose inner side may be unmatched).</summary>
    internal bool HasLeftOuterJoinLevel
    {
        get
        {
            if (_joinScope == null)
                return false;

            foreach (var level in _joinScope.Levels)
            {
                if (level.IsLeftOuter)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// In join mode, whether <paramref name="body"/> reads a join level whose row may be absent for a group element:
    /// a left-outer level, or any level chained after one (conservatively). An unmatched outer row leaves such a
    /// value missing, so a group whose elements are all unmatched reduces to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Not a standalone pre-check: returns <see langword="false"/> for a body that has more than one free parameter
    /// or doesn't re-root to a single scope, which is safe only because every such body also fails
    /// <see cref="TryTranslateValue"/>/<see cref="TryTranslate"/>. Call it only after a successful translation of
    /// the same body.
    /// </remarks>
    internal bool MayReadAnUnmatchedJoinSide(Expression body)
    {
        if (_joinScope == null)
            return false;

        var parameters = FreeParameters.Of(body);
        if (parameters.Count != 1
            || !NativeJoinScopeTranslator.TryResolveSingleScopeIndex(_joinScope, parameters[0], body, out var scopeIndex))
            return false;

        for (var i = 0; i < scopeIndex; i++)
        {
            if (_joinScope.Levels[i].IsLeftOuter)
                return true;
        }

        return false;
    }

    /// <summary>
    /// In join mode, whether <paramref name="translated"/> (the translation of <paramref name="body"/>) may read an
    /// unmatched join side (see <see cref="MayReadAnUnmatchedJoinSide"/>) through an operator that doesn't turn the
    /// missing value into EF's null. EF null-propagates over the DefaultIfEmpty null
    /// (<c>(decimal?)Math.Max(o.Total, 7)</c> is null), but on the server the side is missing and, e.g., <c>$max</c>
    /// ignores it and <c>Math.Sign</c>'s <c>$switch</c> orders it below 0. Such a value must decline.
    /// </summary>
    /// <remarks>
    /// Same calling contract as <see cref="MayReadAnUnmatchedJoinSide"/>: only after a successful translation of
    /// <paramref name="body"/>. The operator check is an allow-list (<see cref="PropagatesMissingAsNull"/>), so a
    /// node kind added later fails closed.
    /// </remarks>
    internal bool MayMiscomputeAnUnmatchedJoinSide(Expression body, MongoExpression translated)
        => !PropagatesMissingAsNull(translated) && MayReadAnUnmatchedJoinSide(body);

    /// <summary>
    /// Whether every operator in <paramref name="node"/> answers what EF answers when a field it reads is missing:
    /// null for a propagating operator (arithmetic, <c>$toX</c>, math, date parts, <c>$trim</c>,
    /// <c>$replaceAll</c>, <c>$indexOfCP</c> over a missing input, and the render-time null-guarded
    /// <c>$substrCP</c>/<c>$strLenCP</c>), the right operand for <c>$ifNull</c>, which is C#'s <c>??</c>, or
    /// <c>""</c> for C#'s concatenation of null (the operand-coalescing <c>$concat</c>). Anything not listed,
    /// including every condition (declined separately, see <c>NativeGroupByBinder</c>'s <c>ConditionFinder</c>),
    /// answers <see langword="false"/>.
    /// </summary>
    internal static bool PropagatesMissingAsNull(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression
                or MongoConstantExpression or MongoParameterExpression => true,
            MongoBinaryExpression
            {
                Operator: MongoBinaryOperator.Add or MongoBinaryOperator.Subtract or MongoBinaryOperator.Multiply
                or MongoBinaryOperator.Divide or MongoBinaryOperator.IntegerDivide or MongoBinaryOperator.Modulo
            } arithmetic => PropagatesMissingAsNull(arithmetic.Left) && PropagatesMissingAsNull(arithmetic.Right),
            MongoConvertExpression convert => PropagatesMissingAsNull(convert.Operand),
            MongoMathExpression math => IsNullPropagatingMathFunction(math.Function) && math.Operands.All(PropagatesMissingAsNull),
            MongoDatePartExpression datePart => PropagatesMissingAsNull(datePart.Operand),
            MongoCoalesceExpression coalesce => PropagatesMissingAsNull(coalesce.Left) && PropagatesMissingAsNull(coalesce.Right),
            MongoTrimExpression trim => PropagatesMissingAsNull(trim.Source) && (trim.Chars is null || PropagatesMissingAsNull(trim.Chars)),
            MongoReplaceExpression replace
                => PropagatesMissingAsNull(replace.Input) && PropagatesMissingAsNull(replace.Find)
                   && PropagatesMissingAsNull(replace.Replacement),
            MongoStringIndexOfExpression indexOf
                => PropagatesMissingAsNull(indexOf.Haystack) && PropagatesMissingAsNull(indexOf.Needle)
                   && (indexOf.Start is null || PropagatesMissingAsNull(indexOf.Start)),
            // $substrCP/$strLenCP are null-guarded at render (MongoAggregationExpressionRenderer.NullPropagating),
            // but only over the string: a missing start/length is still a server error, so those must be values.
            MongoSubstringExpression substring
                => PropagatesMissingAsNull(substring.Source) && IsValue(substring.Start)
                   && (substring.Length is null || IsValue(substring.Length)),
            MongoStringLengthExpression length => PropagatesMissingAsNull(length.Operand),
            // Each possibly-null operand is coalesced to "" at render, which is EF's concatenation of its null.
            MongoConcatExpression concat => concat.Operands.All(PropagatesMissingAsNull),
            _ => false
        };

    private static bool IsValue(MongoExpression node) => node is MongoConstantExpression or MongoParameterExpression;

    // Not Max/Min ($max/$min skip a missing operand, answering the other one) or Sign (its $switch orders a missing
    // operand below 0, answering -1). Also MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead's test for
    // an operator that absorbs a null behind a non-nullable type.
    internal static bool IsNullPropagatingMathFunction(MongoMathFunction function)
        => function is MongoMathFunction.Abs or MongoMathFunction.Ceiling or MongoMathFunction.Floor
            or MongoMathFunction.Exp or MongoMathFunction.Sqrt or MongoMathFunction.Truncate or MongoMathFunction.Round
            or MongoMathFunction.RoundDigits or MongoMathFunction.Ln or MongoMathFunction.Log10 or MongoMathFunction.Log2
            or MongoMathFunction.LogNewBase or MongoMathFunction.DegreesToRadians or MongoMathFunction.RadiansToDegrees
            or MongoMathFunction.Acos or MongoMathFunction.Acosh or MongoMathFunction.Asin or MongoMathFunction.Asinh
            or MongoMathFunction.Atan or MongoMathFunction.Atanh or MongoMathFunction.Cos or MongoMathFunction.Cosh
            or MongoMathFunction.Sin or MongoMathFunction.Sinh or MongoMathFunction.Tan or MongoMathFunction.Tanh
            or MongoMathFunction.Pow or MongoMathFunction.Atan2;

    internal bool TryTranslateValue(Expression body, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(body, valueMode: true, out result);

    internal bool TryTranslate(Expression body, [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateCore(body, valueMode: false, out result);

    internal bool TryTranslateField(Expression body, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;

        // NativeJoinScopeTranslator has no field mode, so a distinct accumulator over the join element declines.
        if (_joinScope != null && FreeParameters.Of(body).Count > 0)
            return false;

        return _rootTranslator.TryTranslateField(body, out result);
    }

    private bool TryTranslateCore(Expression body, bool valueMode, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (_joinScope == null)
        {
            return valueMode
                ? _rootTranslator.TryTranslateValue(body, out result)
                : _rootTranslator.TryTranslate(body, out result);
        }

        var parameters = FreeParameters.Of(body);
        if (parameters.Count == 0)
        {
            return valueMode
                ? _rootTranslator.TryTranslateValue(body, out result)
                : _rootTranslator.TryTranslate(body, out result);
        }

        if (parameters.Count != 1 || !parameters[0].Type.IsTransparentIdentifierType())
            return false;

        return NativeJoinScopeTranslator.TryTranslateSingleScope(_joinScope, parameters[0], body, valueMode, out result);
    }

    // Parameters referenced by `body` but not declared by a lambda inside it.
    private sealed class FreeParameters : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _found = [];
        private readonly HashSet<ParameterExpression> _declared = [];

        internal static IReadOnlyList<ParameterExpression> Of(Expression body)
        {
            var visitor = new FreeParameters();
            visitor.Visit(body);
            return visitor._found;
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            foreach (var p in node.Parameters)
                _declared.Add(p);
            return base.VisitLambda(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_declared.Contains(node) && !_found.Contains(node))
                _found.Add(node);
            return node;
        }
    }
}
