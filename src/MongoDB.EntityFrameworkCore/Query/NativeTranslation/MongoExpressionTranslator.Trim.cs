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

using System.Linq;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>string.Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> and
/// their <c>char</c>/<c>char[]</c>-arg overloads.
/// </summary>
/// <remarks>
/// The driver-LINQ v3 provider only translates the <c>char[]</c>-arg overload of
/// <c>TrimStart</c>/<c>TrimEnd</c> and the zero-arg overload of <c>Trim</c> (confirmed empirically via
/// `StringTranslationsMongoTest`'s throwaway scoping run) — every other combination here is genuinely new
/// capability, not a native conversion of existing fallback behavior.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateTrim(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
            return false;

        MongoTrimSide side;
        switch (call.Method.Name)
        {
            case nameof(string.Trim):
                side = MongoTrimSide.Both;
                break;
            case nameof(string.TrimStart):
                side = MongoTrimSide.Start;
                break;
            case nameof(string.TrimEnd):
                side = MongoTrimSide.End;
                break;
            default:
                return false;
        }

        if (!TryTranslateValue(call.Object, out var source))
            return false;

        MongoExpression? chars = null;
        if (call.Arguments.Count == 1)
        {
            switch (call.Arguments[0])
            {
                case ConstantExpression { Value: char ch }:
                    chars = new MongoConstantExpression(ch.ToString(), forSerialization: null);
                    break;
                case ConstantExpression { Value: char[] chArray }:
                    chars = new MongoConstantExpression(new string(chArray), forSerialization: null);
                    break;
                case NewArrayExpression { Expressions: var elements }
                    when elements.All(e => e is ConstantExpression { Value: char }):
                    chars = new MongoConstantExpression(
                        new string(elements.Select(e => (char)((ConstantExpression)e).Value!).ToArray()),
                        forSerialization: null);
                    break;
                default:
                    // A parameterized/computed char[] has no compile-time string to build; decline rather
                    // than guess (no placeholder-substitution path exists for a computed `chars` operand,
                    // same reasoning as the correlated-reducer leaf's constant-only predicate gate).
                    return false;
            }
        }
        else if (call.Arguments.Count > 1)
        {
            return false;
        }

        result = new MongoTrimExpression(source, side, chars);
        return true;
    }
}
