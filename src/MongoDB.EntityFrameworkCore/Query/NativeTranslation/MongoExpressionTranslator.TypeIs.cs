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
/// <see cref="MongoExpressionTranslator"/> — <c>root is T</c> for a root entity type with no hierarchy.
/// </summary>
/// <remarks>
/// Sibling of <c>MongoExpressionTranslator.EntityType.cs</c>. With no base or derived types the root can only be
/// exactly that CLR type, so <c>root is T</c> folds to a constant. TPH hierarchies would need a discriminator
/// predicate (as <c>TryBuildDiscriminatorPredicate</c> does for <c>OfType&lt;T&gt;</c>) and decline.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null)
            return false;

        // In a Distinct/GroupBy-aggregate scope SelfParam may be a projected value, not the root entity; the
        // constant fold is only valid for the root. See IsSelfParamTheEntity.
        if (!IsSelfParamTheEntity(Unwrap(typeBinary.Expression)))
            return false;

        // Hierarchy types need a discriminator predicate, not a constant; decline.
        if (_entityType.BaseType is not null || _entityType.GetDirectlyDerivedTypes().Any())
            return false;

        var matches = typeBinary.TypeOperand.IsAssignableFrom(_entityType.ClrType);
        result = new MongoConstantExpression(matches, forSerialization: null);
        return true;
    }
}
