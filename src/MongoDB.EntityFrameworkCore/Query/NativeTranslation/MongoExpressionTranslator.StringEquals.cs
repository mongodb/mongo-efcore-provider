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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/>: <c>a.Equals(b, StringComparison)</c> and
/// <c>string.Equals(a, b, StringComparison)</c>. Ordinal is <c>$eq</c>; OrdinalIgnoreCase is an anchored
/// case-insensitive regex against a non-null constant. A parameter declines (it may be null per execution, which has
/// no regex form); field-to-field declines (<c>$strcasecmp</c> is ASCII-only). Culture comparisons decline.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateStringEqualsWithComparison(
        MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (call.Method.Name != nameof(string.Equals) || call.Method.DeclaringType != typeof(string))
            return false;

        Expression left, right, comparisonArg;
        if (call.Object is not null && call.Arguments.Count == 2)
            (left, right, comparisonArg) = (call.Object, call.Arguments[0], call.Arguments[1]);
        else if (call.Object is null && call.Arguments.Count == 3)
            (left, right, comparisonArg) = (call.Arguments[0], call.Arguments[1], call.Arguments[2]);
        else
            return false;

        if (comparisonArg is not ConstantExpression { Value: StringComparison comparison })
            return false;

        switch (comparison)
        {
            case StringComparison.Ordinal:
                result = TranslateComparisonCore(left, right, ExpressionType.Equal);
                return result is not null;

            case StringComparison.OrdinalIgnoreCase:
                // Field on either side; the other must be a non-null string constant.
                if (!TryResolveMember(Unwrap(left), out var property, out var fieldPath, out var isOuter))
                {
                    (left, right) = (right, left);
                    if (!TryResolveMember(Unwrap(left), out property, out fieldPath, out isOuter))
                        return false;
                }

                if (isOuter || property.ClrType != typeof(string))
                    return false;

                if (TranslateValue(Unwrap(right), property) is not MongoConstantExpression { Value: string } term)
                    return false;

                result = new MongoRegexExpression(
                    new MongoFieldExpression(property, fieldPath), MongoRegexKind.Exact, term, negated: false,
                    caseInsensitive: true);
                return true;

            default:
                return false;
        }
    }
}
