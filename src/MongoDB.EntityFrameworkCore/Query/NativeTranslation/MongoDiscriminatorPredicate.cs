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

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// The discriminator test shared by <c>OfType&lt;T&gt;()</c>, <c>x is T</c> and <c>x.GetType() ==/!= typeof(T)</c>
/// over a TPH hierarchy. Values go through the discriminator property's serializer (converter/<c>BsonRepresentation</c>),
/// as the write path does. An abstract type has no discriminator value and contributes nothing, or
/// <c>$in: [null, ...]</c> would also match a document with no discriminator element.
/// </summary>
internal static class MongoDiscriminatorPredicate
{
    /// <summary>
    /// Builds the discriminator test for <paramref name="target"/> (and derived types when
    /// <paramref name="includeDerived"/>); <see langword="false"/> outside a TPH hierarchy. With no concrete type in
    /// scope the test is the constant <paramref name="negated"/>.
    /// </summary>
    internal static bool TryBuild(
        IEntityType target, bool includeDerived, bool negated, [NotNullWhen(true)] out MongoExpression? predicate)
    {
        predicate = null;
        var discriminatorProperty = target.FindDiscriminatorProperty();
        if (discriminatorProperty is null)
            return false;

        var types = includeDerived ? target.GetDerivedTypes().Prepend(target) : [target];
        var values = types
            .Where(t => !t.ClrType.IsAbstract)
            .Select(t => t.GetDiscriminatorValue())
            .Where(v => v is not null)
            .Distinct()
            .ToArray();

        if (values.Length == 0)
        {
            predicate = new MongoConstantExpression(negated, forSerialization: null);
            return true;
        }

        var field = new MongoFieldExpression(discriminatorProperty, discriminatorProperty.GetElementName());
        predicate = values.Length == 1
            ? new MongoBinaryExpression(
                negated ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal, field,
                new MongoConstantExpression(values[0], forSerialization: discriminatorProperty))
            : new MongoInExpression(
                field, new MongoConstantExpression(values, forSerialization: discriminatorProperty), negated);
        return true;
    }
}
