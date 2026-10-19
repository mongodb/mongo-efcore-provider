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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Keeps every call <see cref="NonDeterministicCalls.IsNonDeterministic"/> recognizes in the query tree. EF's default
/// filter only protects <c>Random.Next</c> and <c>Guid.NewGuid</c>; it turns <c>Random.NextDouble</c>,
/// <c>NextSingle</c>, <c>NextInt64</c> and <c>Guid.CreateVersion7</c> into query parameters, evaluated once per
/// execution and shared by every row, before the provider's own check could see them (EF-464).
/// </summary>
internal sealed class MongoEvaluatableExpressionFilter(EvaluatableExpressionFilterDependencies dependencies)
    : EvaluatableExpressionFilter(dependencies)
{
    public override bool IsEvaluatableExpression(Expression expression, IModel model)
        => !(expression is MethodCallExpression call && NonDeterministicCalls.IsNonDeterministic(call))
           && base.IsEvaluatableExpression(expression, model);
}
