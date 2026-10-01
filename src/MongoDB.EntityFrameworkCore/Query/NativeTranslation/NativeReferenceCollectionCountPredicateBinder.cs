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
/// Translates a comparison between an unfiltered reference-collection-nav <c>Count</c>/<c>LongCount</c>
/// (<c>c.Orders.Count &gt; 2</c>) and a translatable value into a native <c>$lookup</c> + <c>$size</c>
/// comparison. Called from <see cref="NativeSlotPopulator"/>'s <c>Where</c> arm after
/// <see cref="MongoExpressionTranslator.TryTranslate"/> declines (it only handles embedded collections).
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

        if (predicateBody is not BinaryExpression binary
            || MongoExpressionTranslator.MapComparisonOperator(binary.NodeType) is not { } comparisonOperator)
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

        // Commit point: all gates passed, so apply what TryBuildReferenceCollectionCountLookup staged.
        if (lookupToStamp is not null)
            lookupToStamp.IsBareCountSizeSource = true;

        foreach (var lookup in pendingLookups)
            mongoQ.AddLookup(lookup);

        // PipelineOps are lowered before AppendLookupStages, so a $match reading the $lookup array via $size
        // would run before the array exists. This flips ActiveOps to PostJoinOps, emitted right after the
        // lookups. It's a dedicated flag (not JoinInnerAccessConfirmed) so Route can decline the query if a later
        // set op, projected Distinct/keyed GroupBy, or Join attaches — those lowering branches don't flush
        // PostJoinOps where this predicate needs it. See ReferenceCollectionCountPredicateConfirmed.
        mongoQ.Select.MarkReferenceCollectionCountPredicateConfirmed();

        var leftNode = ReferenceEquals(countSide, binary.Left) ? (MongoExpression)sizeExpression : otherNode;
        var rightNode = ReferenceEquals(countSide, binary.Left) ? otherNode : (MongoExpression)sizeExpression;
        result = new MongoBinaryExpression(comparisonOperator, leftNode, rightNode);
        return true;
    }
}
