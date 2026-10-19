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
/// <see cref="MongoExpressionTranslator"/> — <c>root is T</c> (<see cref="TypeBinaryExpression"/>),
/// scoped to a NON-hierarchy root entity type.
/// </summary>
/// <remarks>
/// Sibling of <c>MongoExpressionTranslator.EntityType.cs</c>'s <c>root.GetType() == typeof(T)</c> handling —
/// same reasoning, different C# syntax. Without this, every <c>c is T</c> predicate over the query root fell
/// back to driver-LINQ.
/// <para>
/// <b>Scope, deliberately narrow.</b> Only a root entity type with NO hierarchy (no base type and no directly
/// derived types) is handled here — for such a type, the query root can only ever be exactly that CLR type,
/// so <c>root is T</c> collapses to a constant <see langword="true"/>/<see langword="false"/>, needing no
/// discriminator field at all. A TPH hierarchy needs a genuine discriminator-value predicate (mirroring
/// <c>TryBuildDiscriminatorPredicate</c>, used for <c>OfType&lt;T&gt;</c>) and is out of scope here — it
/// declines and keeps falling back.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)
    {
        result = null;

        if (SelfParam is null)
            return false;

        if (!ReferenceEquals(Unwrap(typeBinary.Expression), SelfParam))
            return false;

        // SelfParam can be a projected/accumulator value in a Distinct or GroupBy-aggregate scope, not the
        // root entity (e.g. a Where after Select(...).Distinct()) — _entityType.ClrType is only meaningful
        // when SelfParam genuinely denotes the root, so decline otherwise.
        if (SelfParam.Type != _entityType.ClrType)
            return false;

        // Hierarchy types need a real discriminator predicate, not a compile-time constant — decline and let
        // this keep falling back to driver-LINQ (see the type's own remarks).
        if (_entityType.BaseType is not null || _entityType.GetDirectlyDerivedTypes().Any())
            return false;

        var matches = typeBinary.TypeOperand.IsAssignableFrom(_entityType.ClrType);
        result = new MongoConstantExpression(matches, forSerialization: null);
        return true;
    }
}
