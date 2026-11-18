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

using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Binds a <c>VectorSearch</c> call into <see cref="MongoSelectDefinition.VectorSearch"/>, the native counterpart of the
/// driver-LINQ bridge's <c>ProcessVectorSearch</c>. The gate treats a bound slot as both "native" and "has a
/// <c>$vectorSearch</c> to emit", so native without the stage (rows in insertion order) is unreachable. Declines leave the
/// query unmutated so the fallback still sees <c>VectorSearch</c>.
/// </summary>
internal static class NativeVectorSearchBinder
{
    /// <summary>Records <paramref name="call"/> as the native <c>VectorSearch</c> anchor of <paramref name="mongoQ"/> (mutated only on success).</summary>
    internal static bool TryBind(MongoQueryExpression mongoQ, MethodCallExpression call)
    {
        // Fail closed on a second VectorSearch (unreachable via the public API); rebinding would drop the first.
        if (mongoQ.Select.VectorSearch is not null)
        {
            return false;
        }

        var entityType = mongoQ.CollectionExpression.EntityType;
        var propertyLambda = call.Arguments[1].UnwrapLambdaFromQuote();

        // Arguments[2] is a quoted pre-filter lambda, or Constant(null) when none was supplied.
        MongoExpression? preFilter = null;
        if (call.Arguments[2] is UnaryExpression)
        {
            var preFilterLambda = call.Arguments[2].UnwrapLambdaFromQuote();
            if (!new MongoExpressionTranslator(entityType, preFilterLambda.Parameters[0]).TryTranslate(preFilterLambda.Body, out preFilter))
            {
                // The native predicate set is narrower than the bridge's; decline to driver-LINQ.
                return false;
            }
        }

        // Vector, limit and options are resolved at Build time; decline shapes MongoPipelineFactory.ResolveVectorSearchArgument can't resolve.
        if (!IsResolvableArgument(call.Arguments[3])
            || !IsResolvableArgument(call.Arguments[4])
            || !IsResolvableArgument(call.Arguments[5]))
        {
            return false;
        }

        mongoQ.Select.VectorSearch = new MongoVectorSearch(
            entityType,
            propertyLambda,
            preFilter,
            call.Arguments[3],
            call.Arguments[4],
            call.Arguments[5]);

        return true;
    }

    // An EF query parameter or a constant. Keep in lockstep with MongoPipelineFactory.ResolveVectorSearchArgument,
    // whose "anything else" arm is unreachable because of this check.
    private static bool IsResolvableArgument(Expression argument)
        => argument is ConstantExpression || NativeQueryParameter.TryGetQueryParameterName(argument, out _);
}
