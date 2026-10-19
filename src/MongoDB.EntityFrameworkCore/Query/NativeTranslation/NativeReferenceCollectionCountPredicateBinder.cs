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
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Recognizes a relational/equality comparison whose one operand is an UNFILTERED reference-collection-nav
/// <c>Count</c>/<c>LongCount</c> (<c>c.Orders.Count &gt; 2</c>) and the other a translatable value, and
/// translates it into a native <c>$lookup</c> + <c>$size</c> comparison. Called from
/// <see cref="NativeSlotPopulator"/>'s <c>Where</c> arm, after the general
/// <see cref="MongoExpressionTranslator.TryTranslate"/> attempt has already declined (which it always will for
/// this shape — the general translator's owned-collection-count arm requires an EMBEDDED collection).
/// See docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md.
/// </summary>
internal static class NativeReferenceCollectionCountPredicateBinder
{
    internal static bool TryTranslate(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression predicateBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (predicateBody is not BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                    or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } binary)
        {
            return false;
        }

        Expression countSide;
        Expression otherSide;
        Expression whereArg;
        if (MongoExpressionTranslator.TryMatchCountExpression(binary.Left, out var leftWhereArg, out var leftPredicate)
            && leftPredicate is null)
        {
            countSide = binary.Left;
            otherSide = binary.Right;
            whereArg = leftWhereArg;
        }
        else if (MongoExpressionTranslator.TryMatchCountExpression(binary.Right, out var rightWhereArg, out var rightPredicate)
                 && rightPredicate is null)
        {
            countSide = binary.Right;
            otherSide = binary.Left;
            whereArg = rightWhereArg;
        }
        else
        {
            return false;
        }

        if (!NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation(
                mongoQ, outerParameter, whereArg, out var navigation))
        {
            return false;
        }

        var pendingLookups = new List<LookupExpression>();
        if (!NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup(
                mongoQ, navigation, pendingLookups, countSide.Type, out var lookupToStamp, out var sizeExpression))
        {
            return false;
        }

        var valueTranslator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        if (!valueTranslator.TryTranslateValue(otherSide, out var otherNode))
            return false;

        // Commit point: every gate above has passed, so it's now safe to stamp the lookup
        // TryBuildReferenceCollectionCountLookup staged rather than mutated directly (Query/AGENTS.md's
        // "a recognizer must not mutate then decline" invariant — see that method's own remarks).
        if (lookupToStamp is not null)
            lookupToStamp.IsBareCountSizeSource = true;

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);

        // MongoSelectLowerer emits Select.PipelineOps (arm's default AddPredicateConjunct target) BEFORE
        // AppendLookupStages — a $match reading this navigation's freshly-registered $lookup array via $size
        // would run before that array exists otherwise. MarkReferenceCollectionCountPredicateConfirmed flips
        // MongoSelectDefinition.ActiveOps to PostJoinOps, which the lowerer emits immediately after
        // AppendLookupStages in the ordinary (no set-op) lowering branch — the placement this predicate needs.
        // EF-322 final review (Critical 1/2): a DEDICATED flag, not JoinInnerAccessConfirmed — this predicate's
        // own $lookup is registered before it's known whether a LATER set operation, projected Distinct/keyed
        // GroupBy, or genuine Join will also attach to this select; each is lowered by a different
        // MongoSelectLowerer branch that does not flush PostJoinOps at the point this predicate needs (the
        // set-op branches never emit PostJoinOps at all; a later Join has its own paging-eligibility
        // interactions this predicate never exercised). Rather than teach every such branch a new emission
        // point, MongoSelectDefinition.Route retroactively declines the WHOLE combination via THIS flag, once
        // the full query shape is known — see ReferenceCollectionCountPredicateConfirmed's own remarks. A flag
        // shared with JoinInnerAccessConfirmed could not do this: it would either force Route to also decline
        // genuine (unrelated) join queries, or the query's own real JoinScope would defeat a check meant to
        // ask "did a LATER join attach to a select this predicate already confirmed".
        mongoQ.Select.MarkReferenceCollectionCountPredicateConfirmed();

        var leftNode = ReferenceEquals(countSide, binary.Left) ? (MongoExpression)sizeExpression : otherNode;
        var rightNode = ReferenceEquals(countSide, binary.Left) ? otherNode : (MongoExpression)sizeExpression;
        result = new MongoBinaryExpression(MapOperator(binary.NodeType), leftNode, rightNode);
        return true;
    }

    private static MongoBinaryOperator MapOperator(ExpressionType nodeType)
        => nodeType switch
        {
            ExpressionType.Equal => MongoBinaryOperator.Equal,
            ExpressionType.NotEqual => MongoBinaryOperator.NotEqual,
            ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => MongoBinaryOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => MongoBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => MongoBinaryOperator.LessThanOrEqual,
            _ => throw new System.NotSupportedException($"Unexpected comparison operator '{nodeType}'.")
        };
}
