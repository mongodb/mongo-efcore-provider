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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>Mql.Exists</c>, <c>Mql.IsMissing</c> and <c>Mql.IsNullOrMissing</c> over a
/// stored element (a scalar, a primitive collection, an owned reference or an owned collection).
/// </summary>
/// <remarks>
/// Presence is a storage-level fact, so the target may be value-converted or non-default-represented: only its path is
/// used. A target that is not a bare element (a computed expression) declines.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateFieldPresence(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (call is not { Method: { IsStatic: true, DeclaringType: var declaring, Name: var name }, Arguments: [var target] }
            || declaring != typeof(Mql)
            || name is not (nameof(Mql.Exists) or nameof(Mql.IsMissing) or nameof(Mql.IsNullOrMissing))
            || !TryResolvePresenceTarget(target, out var path, out var mayBeArray))
        {
            return false;
        }

        var kind = name switch
        {
            nameof(Mql.Exists) => MongoFieldPresence.Exists,
            nameof(Mql.IsMissing) => MongoFieldPresence.Missing,
            _ => MongoFieldPresence.NullOrMissing
        };

        result = new MongoFieldPresenceExpression(path, kind, mayBeArray);
        return true;
    }

    private bool TryResolvePresenceTarget(Expression target, [NotNullWhen(true)] out string? path, out bool mayBeArray)
    {
        path = null;
        mayBeArray = false;

        // Nav-expansion wraps an owned entity in auto-include layers, and an owned collection in a
        // MaterializeCollectionNavigationExpression; neither changes which element is addressed.
        var node = target.RemoveConvert();
        while (node is IncludeExpression include)
            node = include.EntityExpression.RemoveConvert();

        if (node is MaterializeCollectionNavigationExpression materialized)
            node = materialized.Subquery;

        node = Unwrap(UnwrapAsQueryable(node));

        if (TryResolveMember(node, out var property, out var propertyPath, out var propertyIsOuter))
        {
            if (propertyIsOuter)
                return false;

            path = propertyPath;
            mayBeArray = property.ClrType != typeof(string) && property.ClrType.TryGetEnumerableElementType() is not null;
            return true;
        }

        if (TryResolveOwnedReferenceNavigationPath(node, out var referencePath, out _, out var referenceIsOuter))
        {
            if (referenceIsOuter)
                return false;

            path = referencePath;
            return true;
        }

        if (TryResolveOwnedCollectionPath(node, out var collectionPath, out _, out var collectionIsOuter) && !collectionIsOuter)
        {
            path = collectionPath;
            mayBeArray = true;
            return true;
        }

        return false;
    }
}
