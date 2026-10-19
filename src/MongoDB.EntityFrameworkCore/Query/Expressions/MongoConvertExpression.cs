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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// An explicit type conversion of <see cref="Operand"/> to <see cref="Type"/>, rendered in the
/// aggregation-expression dialect as one of MQL's four <c>$to…</c> operators.
/// </summary>
/// <remarks>
/// A narrowing or signed/unsigned cast changes the value, so the translator must not simply unwrap it. The admissible
/// set is bounded by MQL (<see cref="ToOperatorFor"/>): no <c>$toShort</c>/<c>$toUInt</c>/<c>$toFloat</c>, and the
/// driver throws for those too. Not query-dialect-renderable (<c>$expr</c> inside <c>$elemMatch</c> is a server error).
/// <c>Convert</c> and <c>ConvertChecked</c> both map here. An out-of-range value fails the whole aggregate; keep that
/// error and don't add <c>$convert</c>'s <c>onError</c>: an error-produced null would slip past
/// <see cref="MongoNumericTypeBracketExpression"/> (which tests the stored type) and silently move rows. Driver-LINQ
/// drops the cast instead (see <c>BREAKING-CHANGES.md</c>).
/// </remarks>
internal sealed class MongoConvertExpression(MongoExpression operand, Type clrType) : MongoExpression
{
    public MongoExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;

    /// <summary>
    /// The MQL conversion operator for <paramref name="clrType"/>, or <see langword="null"/> when MQL can't express
    /// it. The single definition of the admissible set.
    /// </summary>
    /// <remarks>
    /// <c>$toString</c> exists only for string-concatenation operand coercion
    /// (<c>MongoExpressionTranslator.TranslateStringConcat</c>); no C# cast compiles to <c>Convert(x, string)</c>.
    /// </remarks>
    public static string? ToOperatorFor(Type clrType)
    {
        var target = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return target == typeof(int) ? "$toInt"
            : target == typeof(long) ? "$toLong"
            : target == typeof(double) ? "$toDouble"
            : target == typeof(decimal) ? "$toDecimal"
            : target == typeof(string) ? "$toString"
            : null;
    }
}
