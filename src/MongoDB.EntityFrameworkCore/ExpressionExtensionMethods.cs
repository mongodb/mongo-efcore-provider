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
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore;

internal static class ExpressionExtensionMethods
{
    internal static T GetConstantValue<T>(this Expression expression)
        => expression is ConstantExpression constantExpression
            ? (T)constantExpression.Value!
            : throw new InvalidOperationException();

    internal static LambdaExpression UnwrapLambdaFromQuote(this Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);
}
