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
/// Binds a <c>MongoQueryableExtensions.VectorSearch</c> call into <see cref="MongoSelectDefinition.VectorSearch"/>;
/// the native counterpart of the driver-LINQ bridge's <c>ProcessVectorSearch</c>.
/// </summary>
/// <remarks>
/// The gate treats a bound slot as both "native" and "the lowerer has a <c>$vectorSearch</c> to emit", so native
/// without the stage (rows silently in insertion order, not score order) is unreachable. Declines leave the query
/// unmutated so the driver-LINQ fallback still sees <c>VectorSearch</c> in the captured chain.
/// </remarks>
internal static class NativeVectorSearchBinder
{
    /// <summary>
    /// Attempts to record <paramref name="call"/> — a <c>VectorSearch</c> method call — as the native
    /// <see cref="MongoSelectDefinition.VectorSearch"/> anchor of <paramref name="mongoQ"/>.
    /// </summary>
    /// <param name="mongoQ">The query expression being built. Mutated only on success.</param>
    /// <param name="call">The <c>VectorSearch</c> call, as re-inserted by the preprocessor.</param>
    /// <returns>
    /// <see langword="true"/> when the slot was bound; <see langword="false"/> (no mutation) for an already-bound
    /// slot, an untranslatable pre-filter, or a vector/limit/options argument that is neither a query parameter nor
    /// a constant.
    /// </returns>
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

        // Vector, limit and options are resolved per execution at Build time; decline now any shape that
        // MongoPipelineFactory.ResolveVectorSearchArgument can't resolve, rather than throw later.
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
