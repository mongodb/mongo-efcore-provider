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
/// Binds a <c>MongoQueryableExtensions.VectorSearch</c> call into
/// <see cref="MongoSelectDefinition.VectorSearch"/> — the native translator's counterpart to the driver-LINQ
/// bridge's <c>ProcessVectorSearch</c>.
/// </summary>
/// <remarks>
/// <b>Binding the slot is what opens both gates.</b> The compile-time gate reads
/// <c>ContainsVectorSearch(CapturedExpression) &amp;&amp; Select.VectorSearch is null</c>, so a bound slot
/// simultaneously means "the disposition is Native" and "the lowerer has a <c>$vectorSearch</c> stage to
/// emit". The dangerous state — native route with no stage emitted, which would silently return rows in
/// insertion order instead of score order — is unreachable: this binder either binds the slot or the caller
/// marks the query non-representable.
/// <para>
/// Every decline path leaves the query expression unmutated, so a declined query falls back to driver-LINQ
/// with <c>VectorSearch</c> still in the captured chain, where it executes correctly.
/// </para>
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
    /// <see langword="true"/> when the slot was bound; <see langword="false"/> (with no mutation) when the
    /// shape is not natively representable — an already-bound slot, a pre-filter the native predicate
    /// translator declines, or a query-vector/limit/options argument that is neither an EF query parameter nor
    /// a constant.
    /// </returns>
    internal static bool TryBind(MongoQueryExpression mongoQ, MethodCallExpression call)
    {
        // Fail closed on a second VectorSearch on the same query. Not reachable through the public API (the
        // extension is rooted on a DbSet), but binding twice would silently drop the first anchor.
        if (mongoQ.Select.VectorSearch is not null)
        {
            return false;
        }

        var entityType = mongoQ.CollectionExpression.EntityType;
        var propertyLambda = call.Arguments[1].UnwrapLambdaFromQuote();

        // Arguments[2] is either a quoted pre-filter lambda or the Constant(null) the extension method emits
        // when no pre-filter was supplied — the same test the driver-LINQ bridge's ProcessVectorSearch makes.
        MongoExpression? preFilter = null;
        if (call.Arguments[2] is UnaryExpression)
        {
            var preFilterLambda = call.Arguments[2].UnwrapLambdaFromQuote();
            if (!new MongoExpressionTranslator(entityType).TryTranslate(preFilterLambda.Body, out preFilter))
            {
                // The native predicate set is narrower than the bridge's. A pre-filter outside it declines
                // gracefully: driver-LINQ runs the whole query, correctly, and only NativeOnly throws.
                return false;
            }
        }

        // The query vector, the limit and the options are resolved per execution, at Build time. Only the two
        // node shapes MongoPipelineFactory.ResolveVectorSearchArgument can resolve are admitted here; anything
        // else declines now rather than throwing later.
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

    // The argument shapes MongoPipelineFactory's deferred slot can resolve at Build time: an EF query
    // parameter (looked up in that execution's parameter values) or a plain constant. Kept in lockstep with
    // MongoPipelineFactory.ResolveVectorSearchArgument, whose "anything else" arm is unreachable precisely
    // because of this check.
    private static bool IsResolvableArgument(Expression argument)
        => argument is ConstantExpression || NativeQueryParameter.TryGetQueryParameterName(argument, out _);
}
