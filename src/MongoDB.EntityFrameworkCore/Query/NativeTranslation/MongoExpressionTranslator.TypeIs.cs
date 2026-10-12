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
using MongoDB.EntityFrameworkCore.Extensions; // IsInHierarchy()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>root is T</c> for a root entity type with no hierarchy.
/// </summary>
/// <remarks>
/// Sibling of <c>MongoExpressionTranslator.EntityType.cs</c>. With no base or derived types the root can only be
/// exactly that CLR type, so <c>root is T</c> folds to a constant. A TPH hierarchy takes the discriminator test of
/// <see cref="MongoDiscriminatorPredicate"/>, shared with <c>OfType&lt;T&gt;()</c>.
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

        // A hierarchy needs a discriminator predicate, not a constant.
        if (_entityType.IsInHierarchy())
            return TryTranslateHierarchyTypeTest(typeBinary.TypeOperand, exact: false, negated: false, out result);

        var matches = typeBinary.TypeOperand.IsAssignableFrom(_entityType.ClrType);
        result = new MongoConstantExpression(matches, forSerialization: null);
        return true;
    }

    /// <summary>
    /// <c>root is T</c> (<paramref name="exact"/> false: <c>T</c> and its subtypes) or <c>root.GetType() ==/!= typeof(T)</c>
    /// (<paramref name="exact"/> true) over a TPH hierarchy, as a discriminator test shared with <c>OfType&lt;T&gt;()</c>
    /// (<see cref="MongoDiscriminatorPredicate"/>).
    /// </summary>
    private bool TryTranslateHierarchyTypeTest(Type operand, bool exact, bool negated, out MongoExpression? result)
    {
        result = null;

        // Root-level, one-scope only: an element ($filter/$map) or flattened Distinct scope reads a different document,
        // and the discriminator field path is the document root's.
        if (_outerParam is not null || _innerPrefix is not null || DistinctAliasScope is not null)
            return false;

        // Every row is _entityType or a subtype, so a supertype operand (object, an unmapped CLR base) is always true.
        if (!exact && operand.IsAssignableFrom(_entityType.ClrType))
        {
            result = new MongoConstantExpression(true, forSerialization: null);
            return true;
        }

        var operandType = _entityType.Model.FindEntityType(operand);
        if (operandType is null || !ReferenceEquals(operandType.GetRootType(), _entityType.GetRootType()))
        {
            // Rows are always some mapped member of this hierarchy, so GetType() == typeof(NotInHierarchy) is false. For
            // `is`, a non-entity operand (an interface some subtype implements) can't be decided here: decline.
            if (!exact)
                return false;

            result = new MongoConstantExpression(negated, forSerialization: null);
            return true;
        }

        return MongoDiscriminatorPredicate.TryBuild(operandType, includeDerived: !exact, negated, out result);
    }
}
